# MUTA for TuneLab

[![DeepWiki](https://deepwiki.com/badge.svg)](https://deepwiki.com/KakaruHayate/MUTAForTuneLab)
[![Latest release](https://img.shields.io/github/v/release/KakaruHayate/MUTAForTuneLab)](https://github.com/KakaruHayate/MUTAForTuneLab/releases)
[![License: MIT](https://img.shields.io/badge/license-MIT-blue.svg)](LICENSE)

一个让 MUTA 音源在 TuneLab 里唱歌的插件。它是独立实现：读取本机已安装 MUTA 的音源数据，
用自带的 worldline 声码器自己合成，不包含任何 MUTA 程序或音源文件。

[下载](https://github.com/KakaruHayate/MUTAForTuneLab/releases) · [English](README.md)

## 功能

- 同一个音源可以唱中文和日文，语言是 part 设置。
- 内置 worldline R1.1 声码器；R2 是可选的外部神经网络声码器。
- 可调参数：发音、辅音提前量、换采样音区偏移、语速、输出增益，以及能量、音色、沙哑、
  性别、张力、气声、发声七条自动化轨。
- 支持锁定音素、连音延音、手动换气、跨拍滑音。
- 增量渲染：只重算被改动过的块。

## 环境要求

| 组件 | 要求 |
| --- | --- |
| TuneLab | 2.1.0 或更新 |
| 系统 | Windows x64、Linux x64、macOS arm64 |
| 音源 | 已安装的 MUTA，或包含 `<名字>.voice`、`<名字>.inf`、`<名字>.bnk` 的目录 |

## 安装

1. 从 Releases 下载 `.tlx`，在 TuneLab 的扩展管理器里安装。
2. 重启 TuneLab。
3. 给 part 选择 MUTA 音源。音源不在 MUTA 自己的目录时，在
   **设置 → 扩展 → MUTA for TuneLab → 额外音源目录**里添加。

发布两个版本：**完整版**（R1.1 + R2）和**便携版**（只有 R1.1）。两者在 R1.1 下听感相同。

## 使用

1. 新建 voice part，选音源，设置 **Language**。
2. 填歌词：中文写拼音音节，日文写假名。
3. 编辑音符；拖动音素分界线可以锁定这个音符的音素。
4. 用 TuneLab 自己的工具画音高和颤音。
5. 用自动化轨和 part 设置调整表情。

## R2 声码器

R2 是可选的，只在完整版里有。把 **设置 → 扩展 → MUTA for TuneLab → R2 声码器目录**指向
一个含 `vocoder.yaml` 和它指定的 `.onnx` 模型的目录（44.1 kHz、hop 512、128 mel、fft 2048，
例如 OpenUtau 的 `pc_nsf_hifigan_44.1k_hop512_*` 包）。Windows 走 DirectML，Linux 和
macOS 走 WebGPU 插件 EP，取不到就回退 CPU。

## 构建

```powershell
powershell -ExecutionPolicy Bypass -File tools\build-native.ps1
powershell -ExecutionPolicy Bypass -File tools\build.ps1 -TuneLabDir <TuneLab 目录>
powershell -ExecutionPolicy Bypass -File tools\pack-tlx.ps1
```

测试：`dotnet run --project tests\UnitTests\UnitTests.csproj -c Release`。

## 许可

插件本体为 MIT，见 [LICENSE](LICENSE)。第三方组件保留自己的许可，列在
[THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md)。MUTA 是独立产品，本项目与它的作者无关。
