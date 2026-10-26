# MUTA for TuneLab

[![DeepWiki](https://deepwiki.com/badge.svg)](https://deepwiki.com/KakaruHayate/MUTAForTuneLab)
[![Latest release](https://img.shields.io/github/v/release/KakaruHayate/MUTAForTuneLab)](https://github.com/KakaruHayate/MUTAForTuneLab/releases)
[![License: MIT](https://img.shields.io/badge/license-MIT-blue.svg)](LICENSE)

一个让 **MUTA 音源**在 TuneLab 里唱歌的歌声合成扩展。它读取已安装 MUTA 音源的
`.voice`、`.inf`、`.bnk`，在 TuneLab 中合成中文或日文，并通过 **worldline** 声码器出声
（默认 R1.1，可选 R2）。

插件不包含任何 MUTA 程序、源码或音源：它是独立实现，只读取音源数据格式并驱动自己的
HTS + worldline 链路。

**[English](README.md)**

---

## 功能

### 演唱

- **一个音源唱中文和日文。** 语言是 part 设置：切换语言只换前端（拼音标签 / Sinsy 标签），
  不换音色。
- **MUTA 风格的声母时长。** 声母使用 MUTA 自己的时长表，并限制在音符的一半以内，而不是
  模型给出的更长状态时长；这是短音符能咬清字的原因。
- **连音/延音。** `-` 和 `+` 是乘客音符：链头的韵母跨过整个保持段，不产生第二次起音；
  编辑器显示的跨度和音频使用的是同一组数字。
- **跨拍滑音。** 音高变化从拍前约 60–90 ms 开始，在拍后约 30 ms 落定，而不是在拍点硬切。
- **手动换气。** 歌词为 `br` 或 `sp` 的音符是换气：优先取 bank 的 `.inf` 尾部 PCM，
  没有时用带通噪声，并按 MUTA 的相对电平混在演唱下方。
- **锁定音素。** 任意音符都可以锁定为明确的 leading/body 音素；引擎按这些符号演唱，
  bank 也会遵循它们的时长、拉伸权重和 body offset。拖动音素分界线会重渲染受影响的块。
- **音符级换采样音区偏移。** 每个音符可以用半音偏移选择 bank 里不同的录音音区；
  音符自身的音高不变。

### 编辑器集成

- **绝对音高优先。** 宿主音高通道有值的帧，F0 就取该值；没有画的帧保留音符/模型轮廓。
  颤音走宿主的偏差通道，按半音叠加。
- **七条自动化轨：** Energy、Timbre、Husky 作用在声学模型输出上；Gender、Tension、
  Breathiness、Voicing 直接进入声码器。另有语速和输出增益两个 part 设置。
- **音素几何**交给 TuneLab 自己的 `PhonemeLayout` 计算，编辑器里的音素带和实际音频
  使用同一组数字。
- **增量渲染。** 块是纯函数规划的并带缓存。改音符只失效对应块；改曲线只失效与编辑
  时间区间重叠的块；改 part 设置才全曲失效。
- **确定性噪声。** 噪声种子由音源和块起点决定，同一个工程每次渲染得到相同波形。

### 声码器

- **worldline R1.1**（默认，内置）：48 kHz 连续噪声合成，无需额外安装。
- **worldline R2**（可选，仅完整版）：用户自备的神经声码器，44.1 kHz，通过 ONNX Runtime
  运行；有 DirectML 时走 GPU，否则回退 CPU。见 [R2 声码器](#r2-声码器可选)。

---

## 环境要求

| 组件 | 要求 |
| --- | --- |
| TuneLab | 2.1.0 或更新（SDK 1.1） |
| 系统 | Windows x64 |
| 音源 | 已安装的 MUTA 歌曲音源，或包含 `<名字>.voice`、`<名字>.inf`、`<名字>.bnk` 的目录 |
| 构建 | .NET 8 SDK、CMake、Visual Studio 2022 C++ 工具 |

插件不自带音源。请单独安装 MUTA，或把包含音源的目录指给扩展。

## 安装

1. 从 [Releases](https://github.com/KakaruHayate/MUTAForTuneLab/releases) 下载
   **`MUTAForTuneLab-<版本>-win-x64.tlx`**（完整版）或
   **`...-win-x64-lite.tlx`**（仅 R1.1）。
2. 在 TuneLab 的扩展管理器中安装 `.tlx`。
3. 如果 TuneLab 正在运行，安装后重启一次。
4. 给 part 选择 MUTA 音源。音源不在默认 MUTA 目录时，在
   **设置 → 扩展 → MUTA for TuneLab → 音源目录**里添加。

### 完整版与便携版

| 版本 | 内容 | 大小 |
| --- | --- | --- |
| 完整版 | worldline R1.1 + R2 + ONNX Runtime + `mel.onnx` | 约 20 MB |
| 便携版 | 只有 worldline R1.1，不含 ONNX/GPU 部分 | 约 5 MB |

两者在 R1.1 下输出相同。

## 使用

1. 新建 voice part，选择 MUTA 音源。
2. 把 part 的 **Language** 设为中文或日文。歌词写对应语言的正常文本（中文拼音音节、
   日文假名），前端会给出音素。
3. 画音符和歌词；音素带显示实际会唱出的内容。
4. 拖动音素分界线会锁定该音符的音素并调整时长；双击音素可以就地改符号。
5. 音符属性里有 **发音覆盖**、**辅音提前量** 和 **换采样音区偏移（半音）**。
   偏移只决定从 bank 里挑哪段录音音区，不会移调音符。
6. 用 TuneLab 自己的音高和颤音工具画线；画过的帧使用绝对音高。
7. 用自动化轨、语速和输出增益调整表情。
8. 在 part 的 **Vocoder** 设置里选择声码器。

长音在 60 秒以内按单块渲染；超过 60 秒的块会给出明确失败信息，而不是耗尽内存后崩溃。
块失败会显示在编辑器状态里，不会静默错位。

## R2 声码器（可选）

R2 只在**完整版**中可用，需要你自备声码器包：

1. 准备一个目录，里面包含：
   - `vocoder.yaml`
   - yaml 里 `model:` 指向的 `.onnx` 模型
2. 模型需要和随包的 mel 前端兼容：44.1 kHz、hop 512、128 mel bins、fft 2048。
   例如 OpenUtau 的 `pc_nsf_hifigan_44.1k_hop512_*` 包。
3. `vocoder.yaml` 示例：

```yaml
name: pc_nsf_hifigan
model: pc_nsf_hifigan.onnx
sample_rate: 44100
hop_size: 512
fft_size: 2048
num_mel_bins: 128
```

4. TuneLab：**设置 → 扩展 → MUTA for TuneLab → R2 声码器目录**，选择该目录。
5. part 的 **Vocoder** 选择 *WORLDLINE R2 (external vocoder)*。

`mel.onnx` 随完整版放在扩展根目录，不要复制到声码器目录。R2 缺失或配置错误时，渲染
会回退 R1.1，并在日志中写明原因。

## 与 MUTA 原版的差异

这不是把 MUTA 原版重新打包，而是一个 TuneLab 原生引擎：读取 MUTA 音源数据，并复现
“让音源唱对”所需要的那部分行为。

| 方面 | MUTA 原版 | MUTA for TuneLab |
| --- | --- | --- |
| 宿主 | MUTA 编辑器/工程 | TuneLab 的音符、音高/颤音、自动化、音素带 |
| 工程文件 | MUTA 工程 | TuneLab 工程；不导入 MUTA 工程 |
| 音高 | MUTA 的音高编辑 | TuneLab 音高通道；画过的帧为绝对音高 |
| 音素编辑 | MUTA 的音素工具 | TuneLab 锁定音素；时长/权重/body offset 会进入 bank |
| 声码器 | MUTA 内置链路 | worldline R1.1 / 可选 R2 |
| 日文前端 | MUTA 的 | Sinsy 表 + NIT SONG070 提供标签/时序；音色仍来自你的 bank |
| 渲染 | 单引擎实例 | 增量分块、块缓存、确定性噪声、60 s 上限 |
| 二进制 | MUTA 程序 | 不含任何 MUTA 程序、源码或音源 |

刻意保持兼容的行为包括：声母时长表、槽位拼接与重叠规则、连音处理、换气电平和滑音形状。
实现全部独立完成；音源数据从你的安装目录读取。

已知限制：

- 仅 Windows x64。
- 单块上限 60 秒；更长会失败，暂不支持流式。
- R2 的音质完全取决于你提供的声码器包。
- 日文依赖随包的 Sinsy 字典/声线，不需要另装 Sinsy。

## 从源码构建

```powershell
git clone https://github.com/KakaruHayate/MUTAForTuneLab.git
cd MUTAForTuneLab

# 原生库：HTS 包装、worldline、Sinsy 标签器
powershell -ExecutionPolicy Bypass -File tools\build-native.ps1

# 托管层 + 包，指向 TuneLab 安装目录
powershell -ExecutionPolicy Bypass -File tools\build.ps1 -TuneLabDir "C:\Program Files\TuneLab"

# 生成 .tlx
powershell -ExecutionPolicy Bypass -File tools\pack-tlx.ps1
```

便携版：

```powershell
dotnet build src\MutaForTuneLab\MutaForTuneLab.csproj -c Release `
  -p:TuneLabDir="C:\Program Files\TuneLab" -p:MutaLite=true `
  -p:OutputPath=artifacts\package-lite\ -t:Rebuild
powershell -ExecutionPolicy Bypass -File tools\pack-tlx.ps1 `
  -Package artifacts\package-lite -Output artifacts\MUTAForTuneLab-lite.tlx
```

测试：

```powershell
dotnet run --project tests\UnitTests\UnitTests.csproj -c Release
powershell -ExecutionPolicy Bypass -File tools\smoke.ps1 -TuneLabDir "C:\Program Files\TuneLab"
```

## 文档

- [技术说明](docs/technical.md)
- [实现说明](docs/implementation.md)
- [中日音素对照表](docs/ja-zh-phone-table.md)
- [第三方声明](THIRD-PARTY-NOTICES.md)

## 许可

插件本体为 MIT；见 [LICENSE](LICENSE)。HTS、WORLD、worldline、Sinsy、NIT SONG070、
ONNX Runtime 等第三方组件保留各自许可，列在 [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md)。

MUTA 是独立产品，本项目与其作者无隶属或背书关系。
