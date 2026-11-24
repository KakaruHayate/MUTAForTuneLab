# 技术说明

维护者文档；使用说明见 [README](../README.zh-CN.md)。

## 分层

```
TuneLab host
  │  IVoiceSynthesisSession / snapshot
  ▼
MutaVoiceEngine / MutaSynthesisSession   宿主适配、分块、缓存
  ▼
MutaRenderer                             一块的完整合成
  ├─ LabelBuilder / MutaSinsy            full-context 标签
  ├─ muta_hts.dll                        HTS 参数（MGC / LF0 / BAP）
  ├─ BankLayout + BankRenderer          用录音替换频谱（.inf / .bnk）
  ├─ F0Mechanism                         音高轮廓
  └─ WorldlineVocoder / R2Vocoder        波形
```

托管层只依赖 TuneLab SDK 的会话/快照接口；原生层通过 P/Invoke 暴露 C 接口。

## 原生库

| 库 | 作用 |
| --- | --- |
| `muta_hts.dll` | HTS 引擎 + 薄 C 包装，只产参数流，不产波形；每次合成独立引擎，进程内串行 |
| `worldline.dll` | 48 kHz 连续噪声合成 |
| `muta_sinsy.dll` | 日文 full-context 标签，进程内单例 |

## 数据格式

- `.voice`：HTS 声学模型；可能是编码容器，加载时解码到缓存目录。
- `.inf`：音源索引，两版共存：
  - **revision 200**（MUTA 2.0）：6 × int32 头；每行"当前 + 左 + 右 + 11 × int32"
    （类别、帧数、prefix/core/suffix、音高码、左右边界音高、bank 偏移与字节数）。
  - **revision 100**（MUTA 1.0 / 0.2.alpha）：10 × int32 头；每行"单元名 + 9 × int32"
    （类别、音高码、帧数、首段帧数、左右边界音高、偏移、字节数）。单元名就是上下文：
    `b_a` 音节、`a-b` 边界、`a` 纯韵母，没有三段划分。
  - 两版都由 `Assets/InfFile` 解析成同一份 11 字段视图，再由 `Synthesis/BankLayout`
    变成"音素 → 单元列表"。
- `.bnk`：按需 seek 的 float32 录音帧，先整段 MGC 后整段 BAP。
- `.chr`：GBK 的 `name=`，音源显示名。

## 块与缓存

- `Plan()` 是纯函数：按音符间隔、块长和音符数切块，同一份音符得到同一组块。
- 每块有 Key 与 Signature（音符字段、锁定音素、曲线版本、part 版本）。
- 曲线编辑只失效相交块；part 设置递增全局版本。
- HTS 模型与 bank 按路径 + 长度 + mtime 缓存，文件变化自动失效。

## 构建与打包

```powershell
powershell -ExecutionPolicy Bypass -File tools\build-native.ps1
powershell -ExecutionPolicy Bypass -File tools\build.ps1 -TuneLabDir <TuneLab>   # -Lite 为便携版
powershell -ExecutionPolicy Bypass -File tools\pack-tlx.ps1
```

CI 在 Windows / Linux / macOS 上各跑一次 `build-native.ps1` 并各产出一个 `.tlx`；三个平台的
原生库都用系统工具链构建（MSVC / gcc / clang），输出分别是 `.dll` / `.so` / `.dylib`。

## 限制

- 单块上限 60 秒，超过直接失败，不做流式。
- R2 的模型由用户提供；缺失或配置错误时回退 R1.1。
