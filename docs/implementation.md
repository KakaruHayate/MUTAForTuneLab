# MUTA for TuneLab — 实现说明

本文只说明插件**怎么实现**：分层、数据流、每个模块的职责、关键决策的技术理由。
不涉及开发过程、调试记录或待办事项。面向技术交流的完整叙述见 [technical.md](technical.md)。

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

`MutaVoice.HasBank` 由 `.bnk` 是否存在判定。**没有"日语声源"这一回事**：语言是
part 的属性（`language = zh | ja`），三个 bank 都能用两种前端唱。

### 2.2 语言不是音源

| 轴 | 取值 | 决定什么 |
|---|---|---|
| 音色 | `yanxi` / `hupo` / `weiyang` | 选哪套 `.inf`/`.bnk` |
| 语言 | `zh`（默认）/ `ja` | 用哪套前端与标签（`LabelBuilder` 或 Sinsy） |

两个轴正交：同一个 bank 切 `language` 只换前端，音色与选槽不变。宿主在把歌词交给引擎
之前就已经罗马化（汉字→拼音、假名→罗马字），所以**无法从文本反推语言**（`ni` 两属），
这也是语言必须显式声明、没有 auto 的原因。

日文的时序来自随包分发的 NIT SONG070 模型（`sinsy/voice/`），**只提供标签与时长**，
音色仍来自当前 bank。

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
- 日文：假名直接查 `KanaTable`；罗马字经表的反向索引转假名。语言由 part 属性
  `language` 决定（**没有 auto**，默认 `zh`）：宿主已经在引擎之前把歌词罗马化，
  `ni` 这种写法中文日文都成立，文本反推不可靠。
- ASCII 歌词按音素直传（罗马字工程即走此路）。
- **白名单**：每个音节必须能拆成该音源录音里真实存在的声母/韵母
  （中文用 `.inf` 的音素清单，日文用假名表）；不满足则退到该音源确实有的元音并记日志。
  这一步是必须的：原生气机不做校验，遇到它放不下的音素会越界。

### 3.2 标签

- 中文：`LabelBuilder` 生成 HTS 全上下文标签（音素、音节、词、音高、时值等字段）。
- 日文：`MusicXmlWriter` 把音符写成乐谱文本，**以内存字符串**交给 `muta_sinsy.dll`
  （Sinsy 的 reader 接受任意 `IReadableStream`，不落临时文件）；`MutaSinsy` 取回全上下文标签。

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
- 连音乘客（`-`/`+`）不选槽：链头韵母按“本音符 + 乘客帧数”拉伸并覆盖整段，避免在乘客处
  出现第二段录音的起音。
- 换气音符（`br`/`sp`）不选槽：中文标签留一个 `sp` 停顿、日文谱面留空，声音在声码器之后
  由 `BreathRenderer` 用 `.inf` finals（或带通噪声）按 −13 dB 混入。

**日文走同一套录音层**：`JapaneseMapping` 把 Sinsy 的音素映射成 bank 的音素，
映射**数据化**在随包的 `ja-zh-phone-map.tsv`（`jp_phone / bank_onset / bank_final / note`），
改表不用重编。定稿值就在表里（例：`kyo → k + iao`、`N → en`、`e → ei`），
缺精确组合时的回退由 `BankContext` 打分完成。

### 3.5 基频

`F0Mechanism` 分两种情况，目标逐帧决定：

- **用户画了音高线**（`Pitch` 在该帧有值）：该帧的 F0 直接取这条绝对音高（MIDI），
  不经过钳制与起音滑音——用户编辑的音高就是答案。
- **没画线**（NaN）：以模型输出的 F0 为载波，按音符音高做分段弯折（截尾均值、±0.22
  半音钳制、起音斜坡、连音延伸），保留 MUTA 的进音手感。相邻、相接且音高不同时，滑音
  跨在音符边界上：边界前 90 ms 起滑、边界后 30 ms 落点，按半音域 smoothstep 插值并保留
  载波微形状；该音符因此不再走自己的起音斜坡。

两条路径都叠加 `PitchDeviation`（颤音走这条加性通道）。回显与音频共用同一份逐帧 F0，
所以"看到的"就是"听到的"。

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

`OutputGate` 对标记为静音的帧做门控与短斜坡；随后软膝限幅（0.8 起膝、**0.9 封顶**——
宿主 48k→44.1k 重采样对饱和信号有数个百分点的过冲，0.95 的封顶实测会回到 0.9988）；
最后按块写入 `IAudioSegment`。

---

## 4. 会话与分块

宿主按"peek 下一块 → commit 同一块"驱动，因此分块必须是**纯函数**。

- 块以音符分组：间隔 > 0.4 s、或跨度 > 12 s、或音符数 > 240 则断开；
- 单块硬上限 12000 帧（60 s），超过时该块直接失败并在状态栏给出原因。
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
- **钉死音素**：快照里 leading/body 任一非空即用户钉死，几何原样上报（多 leading、
  多 body 都保留）；中文按钉死重拼拼音音节，日文按假名表反查（`k`+`y`+`a` → きゃ）。
- **连音**：歌词 `-` 且沿 `Previous` 回溯到内容 note 且沿途相接 → 乘客 note，不发音素，
  几何由宿主布置为透明；孤儿 `-` 退化成裸元音。`+` 是"当前词的下一个音节"，属于内容。
- 音高回显把每块逐帧 F0 转成 `(秒, 半音)` 折线，按 20 ms 抽稀。显示用的轮廓会把有声段内部
  不超过 250 ms 的无音高洞插值补齐（清辅音、载波空洞），真休止仍断开；音频 F0 不变。

---

## 6. 打包与依赖

`tools/pack-tlx.ps1` 产出 `artifacts/mutafortunelab.tlx`（28 条目，约 19.9 MB）：

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

另产出**便携版**（`-p:MutaLite=true`）：不引 ONNX Runtime、不带 `mel.onnx`、不编译
`R2Vocoder`（`IR2Vocoder` 留作 pipeline 的空参数），R2 也从设置与模式解析里隐藏。
包体 4.9 MB（完整版 19.9 MB），只走 worldline R1.1。

第三方组件与许可证见 `THIRD-PARTY-NOTICES.md`；日语音素映射是**运行时数据**，
见随包的 `ja-zh-phone-map.tsv`（改表不用重编；`docs/ja-zh-phone-table.md` 只是历史记录）。
