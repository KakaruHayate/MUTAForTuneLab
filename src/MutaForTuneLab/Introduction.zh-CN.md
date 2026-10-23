# MUTA for TuneLab

TuneLab 的 MUTA 歌声合成引擎。中文与日文双链路，用 WORLD / worldline 声码器出声。

**本包不含 MUTA 的任何二进制，也不带 MUTA 音源。** `.voice/.inf/.bnk` 从你机器上
已安装的 MUTA 目录读取。请先装 MUTA；或者用
「设置 → 扩展 → MUTA → 额外音源目录」指向存放音源文件夹的位置。
日文链路所需的日文模型随包分发（它只提供音素时长与 F0，音色仍来自 MUTA 音源）。

## 音源

一个音源 = 一个文件夹，里面有 `<名字>.voice`、`<名字>.inf`、`<名字>.bnk`。
默认搜索 `Program Files (x86)\Muta\voice\song\*\`。

## 参数

可编辑曲线（part 级、逐帧）：能量、音色、沙哑（作用在声学模型输出上），
以及性别、张力、气声、发声度（逐帧直传声码器）。
音高与颤音走 TuneLab 自己的音高 / 偏差双通道，不另建曲线。

part 设置里还有「换采样（选块音区）」——决定一个音符从 bank 里挑哪个录音音高的单元，
以及声码器模式。

## 状态

早期阶段。目前引擎能列出音源并声明参数面；合成正在按阶段移植，见仓库 README 的路线图。

## 致谢

声音本身是用户自己的，本包不含 MUTA 的任何内容。合成链路建在这些人的工作上：
worldline（作者 StAkira，MIT）、WORLD（Masanori Morise，修改版 BSD）、
HTS 引擎与 Sinsy（名古屋工业大学，修改版 BSD）、
以及 HTS Voice "NIT SONG070 F001"（名古屋工业大学，CC BY 3.0）。
完整许可见同目录的 THIRD-PARTY-NOTICES.md。
