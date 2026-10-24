# MUTA for TuneLab — 实现说明

本文只说明插件**怎么实现**：分层、数据流、每个模块的职责、关键决策的技术理由。
不涉及开发过程、调试记录或待办事项。

---

## 1. 总体结构

插件是一个 net8.0 类库，向 TuneLab 注册一个 **voice 引擎**（引擎 id `MUTA`），
通过 manifest 的 `sdk-version: 1.1` 声明所需宿主版本（TuneLab ≥ 2.1.0，因为
2.0.0 没有 `IVoiceSynthesisSession`）。

```text
TuneLab 宿主
  │  IVoiceSynthesisEngine   —— 音源发现、参数声明
  │  IVoiceSynthesisSession  —— 分块、渲染、产物发布
  ▼
MutaForTuneLab.dll
  ├── Assets/      读取 MUTA 的音源文件（.voice / .inf / .bnk）与文本表
  ├── Native/      P/Invoke：muta_hts、worldline、muta_sinsy
  └── Synthesis/   合成链：标签 → 模型 → bank → 基频 → 声码器 → 输出
```

三个原生库由 `tools/build-native.ps1` 从固定源码用 MSVC 编译，随包分发；
包内不含任何 MUTA 的二进制。

---

## 2. 音源与数据

### 2.1 音源发现

`VoiceLibrary.Scan` 遍历 MUTA 安装目录下的 `voice/song/*`，每个目录按
`<name>.voice`（HTS 声学模型）、`<name>.inf`（录音索引）、`<name>.bnk`（录音参数）
识别为一个音源。**只读取，不复制、不打包**。

`MutaVoice.IsJapanese` 由 id 前缀判定；`HasBank` 由 `.bnk` 是否存在判定。

### 2.2 日语声源

内置音源 `sinsy-jp` 由两部分拼成：

- **前端与时长**：随包分发的 `muta_sinsy.dll`（Sinsy 的乐谱→全上下文标签）与
  NIT SONG070 声学模型；
- **音色**：**借用机器上已安装的第一个 MUTA 歌曲 bank**，与中文音源同源。

这不是"一份完整日语声库"，而是"日语时序 + 中文 bank 近似日语音色"。

### 2.3 文件格式

| 文件 | 内容 | 读取方 |
|---|---|---|
| `.voice` | HTS 声学模型（含 `OPTION[MCP]:ALPHA`、帧周期） | `HtsVoice` |
| `.inf` | 每段录音的 (左音素, 当前音素, 右音素, 帧数, 音高码, bank 偏移, 大小) | `InfFile` |
| `.bnk` | 按 `.inf` 偏移取出的 float32 参数：每段 MGC 50 维 + BAP 26 维 | `BankAssets`（按需读取） |
| `pin.dict` / `duo.dict` | 汉字 → 拼音 | `PinyinDictionary` |
| Sinsy `dic/` | 假名 → 音素表 | `KanaTable` |

`.inf` 与 `.bnk` 全程按需读取，不整文件加载；单个音源 94 MB 也不进内存。

---

## 3. 合成链

```text
歌词 + 音高
  → 音素化（PinyinDictionary / KanaTable → 音素序列）
  → 标签（LabelBuilder 或 Sinsy）
  → HTS 引擎（muta_hts）：状态时长、MGC、LF0、BAP
  → 【录音层】用 .inf/.bnk 选出录音单元，替换 MGC/BAP
  → 基频机制：把 F0 弯到用户写的音符上
  → 参数层：energy / timbre / husky
  → 频谱：MGC → 功率谱（α 弯折 + FFT），BAP → 每带非周期性权重
  → 声码器：worldline（R1.1 连续噪声，或 R2 外部模型）
  → 静音门 → 软限幅 → IAudioSegment
```

### 3.1 音素化

- 中文：汉字经 `duo.dict`（最长匹配）拆词，再经 `pin.dict` 转拼音音节；
  多音字取表中首选读音。
- 日文：假名直接查 `KanaTable`；罗马字经表的反向索引转假名。
- ASCII 歌词按音素直传（罗马字工程即走此路）。
- **白名单**：每个音节必须能拆成该音源录音里真实存在的声母/韵母
  （中文用 `.inf` 的音素清单，日文用假名表）；不满足则退到该音源确实有的元音并记日志。
  这一步是必须的：原生气机不做校验，遇到它放不下的音素会越界。

### 3.2 标签

- 中文：`LabelBuilder` 生成 HTS 全上下文标签（音素、音节、词、音高、时值等字段）。
- 日文：`MusicXmlWriter` 把音符写成乐谱，交给 `muta_sinsy.dll` 产出标签；
  `MutaSinsy.ContentWindow` 从中取出真正有内容的帧区间，去掉 Sinsy 自动加的首尾停顿。

### 3.3 声学模型

`muta_hts` 包装 HTS engine 的 API，只导出需要的入口：加载、合成、读三条流
（MGC/LF0/BAP）、状态时长、语速、GV 权重。C# 侧 `MutaHts` 做 P/Invoke，
按整块读取流数据再切帧。

引擎按 `.voice` 路径缓存复用；调用串行化（原生包装在合成期间使用进程级全局状态，
并发会写错引擎缓冲）。

### 3.4 录音层（bank）

当音源有 `.bnk` 时，`BankRenderer` 用录音参数**整体替换**模型输出的 MGC/BAP，
模型仍只提供基频。这是 MUTA 原版的分工，也是它"换采样"能力的基础。

每个音符按音素拆成声母槽与韵母槽，各自选一段录音：

- 选择器 `BankContext` 在 `.inf` 的音素组里按**音类加权**打分取最近项，
  因此缺少精确组合时仍能取到声学上最近的真实录音，而不是拼接。
- 写入时按目标帧数做三段伸缩（前缀/核心/后缀），并在元音与前一音符重叠处交叉淡化。

**日文走同一套录音层**：`JapanesePhones` 把 Sinsy 的音素对映射成 bank 的音素对
（含回退链，例如 `kyo → k + iu`），映射表由 bank 索引生成（`docs/ja-zh-phone-table.md`）。

### 3.5 基频

`F0Mechanism` 以模型给出的 F0 为基线，把每个音符的音高作为目标，做分段线性弯折，
再叠加宿主 `Pitch` / `PitchDeviation` 的偏移。连音、休止、起音斜坡都有对应处理。

### 3.6 参数层

| 参数 | 作用点 | 实现 |
|---|---|---|
| energy | 帧对数增益 | `mgc[f][0] += dB · ln10/20` |
| timbre | 频谱弯折因子 α | 逐帧 α（按 0.005 量化分桶，一次 FFT 覆盖同桶帧） |
| husky | 非周期性 | `bap[f][b] += dB` |
| gender / tension / breathiness / voicing | 声码器 | 逐帧数组直传 worldline |
| phoneme_speed | 时长 | HTS 引擎的 speed |
| output_gain_db | 输出 | 软限幅前的线性增益 |
| bank_pitch_mode/offset/note | 选块音区 | 决定请求录音的音高码，音频仍按音符自身音高播放 |

不画曲线时参数为空，渲染结果与接线前逐样本相同。

### 3.7 频谱与声码器

`MgcSpectrum` 把每帧 MGC 经 α 弯折后用 FFT 逆变换成 1025 点功率谱
（等价于离散余弦变换，但按桶批量执行）。
`BapExpand` 把 26 带（或 31 带）非周期性展开到 1025 bin 并转成权重。

`VocoderPipeline` 调用 `worldline`：

- **R1.1**（默认）：连续噪声模型，输入功率谱、谐波谱、非周期性、F0；
- **R2**（可选）：先由 `mel.onnx` 求 mel 谱，再交给用户自备的神经声码器；
  可用时走 DirectML，不可用回退 CPU。

没有非周期性的声源（BAP 全零）会得到一条默认曲线：低频周期、高频渐气声——
否则该声源会被当作全噪声合成。

### 3.8 输出

`OutputGate` 对标记为静音的帧做门控与短斜坡；随后软膝限幅（0.8 起）保证不削波；
最后按块写入 `IAudioSegment`。

---

## 4. 会话与分块

宿主按"peek 下一块 → commit 同一块"驱动，因此分块必须是**纯函数**。

- 块以音符分组：间隔 > 0.4 s、或跨度 > 30 s、或音符数 > 240 则断开。
- 边界落在两段之间的**静音中点**（间隔 ≤ 1 s 时），否则各留 50 ms 前置 / 150 ms 后置。
- 块签名 = 音符数据 + 曲线版本号 + 部件属性；任一变化即触发重渲。
- 曲线改动通过订阅 `RangeModified` / `Modified` 递增版本号，而不是比对采样值。
- 声码器噪声种子由 `(音源 id, 块起始时间)` 经 FNV-1a 派生：同块可复现，块间不同。
- 已完成的块缓存音频段与音素，编辑只重渲受影响的块。

产物发布（`SynthesizedPhonemes` / `SynthesizedPitch` / `Status`）都在数据线程换引用发布。

---

## 5. 音素几何与音高回显

- 音素几何交给宿主自己的 `PhonemeLayout.Resolve`：插件构造
  `PhonemeLayoutNote`（填充范围、leading/body 列表、`BodyOffset`），宿主返回绝对时间。
  这样"听到的"和"看到的"用同一份排布逻辑。
- `BodyOffset` 取声母时长，让辅音落在音符内部（MUTA 语义）。
- 音高回显把每块逐帧 F0 转成 `(秒, 半音)` 折线，休止处断开，按 20 ms 抽稀。

---

## 6. 打包与依赖

`tools/pack-tlx.ps1` 产出 `artifacts/mutafortunelab.tlx`（27 条目，约 19.9 MB）：

```text
manifest.json / Introduction*.md / THIRD-PARTY-NOTICES.md
MutaForTuneLab.dll + deps.json / runtimeconfig.json
muta_hts.dll        HTS 引擎包装
worldline.dll       WORLD 声码器（OpenUtau 的 worldline）
muta_sinsy.dll      Sinsy 标签前端
sinsy/dic/          假名表与词典配置
sinsy/voice/        NIT SONG070 声学模型
mel.onnx            R2 的 mel 前端
onnxruntime + DirectML
```

打包时剔除 `.pdb` 与 `DirectML.Debug.*`；不打包 R2 的声码器权重
（许可为 CC BY-NC-SA，由用户自备）。

第三方组件与许可证见 `THIRD-PARTY-NOTICES.md`；日语音素映射表见
`docs/ja-zh-phone-table.md`。
