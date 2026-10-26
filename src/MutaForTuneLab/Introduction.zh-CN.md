# MUTA for TuneLab

TuneLab 的 MUTA 歌声合成引擎。中文与日文双链路，用 worldline 声码器出声
（默认 R1.1 连续噪声；R2 为可选档，需要你自己提供声码器模型）。

**本包不含 MUTA 的任何二进制，也不带 MUTA 音源。** `.voice/.inf/.bnk` 从你机器上
已安装的 MUTA 目录读取。请先装 MUTA；或者用
「设置 → 扩展 → MUTA → 额外音源目录」指向存放音源文件夹的位置。

发布有两种包，装其中一个即可：

- **完整版**：内置 worldline R1.1，另可选 R2（外部神经声码器，需自备模型）；随包带 ONNX Runtime。
- **便携版**（`-lite`）：只有 worldline R1.1，不含 ONNX Runtime 与 R2，体积约为完整版的四分之一，
  也不会碰到任何 GPU 驱动问题。

本插件与 MUTA 官方**无关联**，也未获其背书。它是读取 MUTA 音源格式的独立实现：
不包含、也不再分发 MUTA 的任何程序或音源内容。

日文链路自包含**前端**：Sinsy 的音素表与 HTS Voice "NIT SONG070 F001" 模型随包分发，
但它们只提供标签与时序。**日文的音色同样来自你的 MUTA 音源**：同一个 bank，
把 part 的 `language` 设为 `ja`，就只换前端，不换录音。

## 音源

中文音源 = 一个文件夹，里面有 `<名字>.voice`、`<名字>.inf`、`<名字>.bnk`。
默认搜索 `Program Files (x86)\Muta\voice\song\*\`。

日文不需要额外音源：任意 MUTA 音源都可以唱日文，把 part 的 `language` 设为 `ja` 即可。

## 参数

可编辑曲线（part 级、逐帧）：能量、音色、沙哑（作用在声学模型输出上），
以及性别、张力、气声、发声度（逐帧直传声码器）。
音高与颤音走 TuneLab 自己的音高 / 偏差双通道，不另建曲线。

part 设置里还有「换采样（选块音区）」——`auto` 跟随音符、`offset` 按半音偏移、
`fixed` 钉在指定音区；它决定一个音符从 bank 里挑哪个录音音高的单元。
另有声码器档位、语速与输出增益。

## 状态

中文与日文两条链路都能完整出声，所有声明参数都真正作用到音频上，
同一工程重渲染得到同样的波形。

## 致谢

声音本身是用户自己的，本包不含 MUTA 的任何内容。合成链路建在这些人的工作上：
worldline（作者 StAkira，MIT）、WORLD（Masanori Morise，修改版 BSD）、
HTS 引擎与 Sinsy（名古屋工业大学，修改版 BSD）、
以及 HTS Voice "NIT SONG070 F001"（名古屋工业大学，CC BY 3.0）。
完整许可见同目录的 THIRD-PARTY-NOTICES.md。
