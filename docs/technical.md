# 技术说明

本文面向维护者，说明插件如何把 MUTA 音源接入 TuneLab：分层、原生库、数据流和关键约束。
使用说明见 [README](../README.zh-CN.md)。

## 1. 总体结构

```
TuneLab host
  │  IVoiceSynthesisSession / snapshot
  ▼
MutaVoiceEngine / MutaSynthesisSession   (C#, host 适配、分块、缓存、发布)
  │
  ▼
MutaRenderer                             (一块的完整合成)
  ├─ LabelBuilder (中文) / MutaSinsy (日文)   → full-context labels
  ├─ muta_hts.dll (HTS + WORLD 参数)          → MGC / LF0 / BAP
  ├─ BankRenderer + .inf/.bnk                 → 用录音替换频谱
  ├─ F0Mechanism                              → 音高轮廓
  └─ WorldlineVocoder                         → 波形
       ├─ worldline.dll (R1.1)                → 48 kHz
       └─ R2Vocoder (ONNX, 可选)              → 44.1 kHz
```

托管层只依赖 TuneLab SDK 的会话/快照接口；原生层通过 P/Invoke 暴露 C 接口，不反向依赖宿主。

## 2. 仓库布局

| 路径 | 内容 |
| --- | --- |
| `src/MutaForTuneLab` | 托管插件、声明、会话、渲染链 |
| `src/MutaForTuneLab/Synthesis` | 标签、bank、F0、声码器、输出门 |
| `src/native/hts` | `muta_hts.dll` 包装与 HTS 构建配置；WORLD 声码器用 stub 关闭 |
| `src/native/worldline` | `worldline.dll` 构建配置 |
| `src/native/sinsy` | `muta_sinsy.dll`，日文标签前端 |
| `third_party` | HTS、WORLD、worldline、Sinsy 的源码与许可 |
| `tests` | 单元测试、渲染/标签/银行/声码器探针 |
| `tools` | 构建、打包、冒烟、音源校验 |
| `assets` | 中日音素映射表 |
| `docs` | 本文件、实现说明、音素表 |

## 3. 原生库

### muta_hts.dll

- HTS 引擎 + 薄 C 包装（`muta_hts.c`）。
- 只生成参数流（MCP/LF0/BAP），不生成 HTS 自身的波形：`WORLD_Vocoder_*` 是显式 stub，
  并把 `wave_length` 初始化为 0。HTS 的 `HTS_GStreamSet_create_WORLD` 仍会走一遍分配，
  因此 stub 必须完整初始化结构体，不能留栈垃圾。
- 每次合成使用独立引擎，销毁前 `HTS_Engine_refresh`，再 `HTS_Engine_clear`。
- 标签时间戳、总时长和 token 长度在托管侧预校验；HTS 的 `HTS_calloc` 先判空再清零。
- 进程级命名互斥锁 + 托管锁串行化所有原生合成，避免两份程序集同时驱动 HTS。

### worldline.dll

- R1.1 连续噪声合成；输入功率谱、非周期性、F0 和表情曲线。
- 输出长度由整数 hop 派生周期计算，托管缓冲区使用同一公式。
- 频谱 bin 数必须等于 `fft_size/2+1`，否则拒绝，避免原生越界读。

### muta_sinsy.dll

- Sinsy 的标签前端：把内存中的乐谱转成 full-context labels。
- 进程级单例转换器；不写临时文件。
- 返回长度协议：缓冲不足返回负数，托管侧不会读未填充内存。

## 4. 数据格式

- `.voice`：HTS 声学模型；可能是编码过的容器，加载时解码到缓存目录。
- `.inf`：bank 索引。解析前按文件长度约束头计数，并限制 MGC/BAP order；每条记录的
  bank 区间与 `.bnk` 长度对账。
- `.bnk`：按需 seek，不整文件加载；录音帧以 float32 存储。

## 5. 块与缓存

- 块在 `Plan()` 中按音符间隔、块长和音符数规划；同一份音符得到同一组块。
- 每块有 `Key`（首音符 id + 数量）和 `Signature`（音符字段、锁定音素、曲线版本、part 版本）。
- 曲线编辑用 `RangeModified(start,end)` 只递增相交块的版本；part 设置递增全局版本。
- 已渲染块按 `Key` 缓存 `IAudioSegment`；块重组时清理旧产品。
- HTS 声学模型和 bank 资产按文件路径 + 长度 + mtime 缓存。

## 6. 内存与限制

- 一块的频谱链约为 `帧数 × 1025 × 8` 的十余份；渲染入口按帧数估算峰值并与可用内存比较。
- 单块硬上限 60 秒（12000 帧）；超过时该块失败，不进入原生大分配。
- 长音在 60 秒内按单块渲染；暂不做流式或跨块拼接。
- 原生的 HTS 分配失败会走 `HTS_error`；因此上限和预算是第一道保护。

## 7. 构建与打包

```powershell
powershell -ExecutionPolicy Bypass -File tools\build-native.ps1
powershell -ExecutionPolicy Bypass -File tools\build.ps1 -TuneLabDir <TuneLab>
powershell -ExecutionPolicy Bypass -File tools\pack-tlx.ps1
```

- `MutaLite=true` 时移除 R2 编译、ONNX 依赖和 `mel.onnx`。
- 打包会检查原生 DLL 是否存在，避免产出不能加载的包。
- `tools/smoke.ps1` 做一次无头安装/渲染检查；`tests/UnitTests` 覆盖标签、连音、滑音、
  换气、bank 和声码器边界。

## 8. 测试

- `tests/UnitTests`：纯托管断言，不依赖宿主。
- `tests/*Probe`：单模块探针（标签、HTS、bank、F0、频谱、声码器、R2）。
- `tools/smoke.ps1`：临时数据目录中安装扩展并渲染参考工程。

## 9. 约束与后续方向

- 仅 Windows x64；依赖 TuneLab 的 voice SDK 1.1。
- 长音只支持到 60 秒；更大的音符需要分块/流式方案。
- R2 的模型和 yaml 由用户提供，插件只校验尺寸并做回退。
- 日文依赖随包的 Sinsy 表和 NIT SONG070 模型；音色仍来自 MUTA bank。
