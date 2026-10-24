# MUTA for TuneLab

TuneLab 的 MUTA 歌声合成引擎。中文与日文双链路，用 worldline 声码器出声
（默认 R1.1 连续噪声；R2 为可选档，需要你自己提供声码器模型）。

**本包不含 MUTA 的任何二进制，也不带 MUTA 音源。** `.voice/.inf/.bnk` 从你机器上
已安装的 MUTA 目录读取。请先装 MUTA；或者用
「设置 → 扩展 → MUTA → 额外音源目录」指向存放音源文件夹的位置。

日文链路自包含：Sinsy 的音素表与 HTS Voice "NIT SONG070 F001" 模型随包分发，
日文声库 `sinsy-jp` 用**这个模型自己的音色**演唱，不与中文音源混用。

## 音源

中文音源 = 一个文件夹，里面有 `<名字>.voice`、`<名字>.inf`、`<名字>.bnk`。
默认搜索 `Program Files (x86)\Muta\voice\song\*\`。

日文音源 `sinsy-jp` 随包提供，装好即可用，不需要额外文件。

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
