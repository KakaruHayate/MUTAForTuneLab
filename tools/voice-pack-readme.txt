MUTA for TuneLab — 嫣汐1.0（{ID}）
=====================================

解压后是一个音源目录：

    {ID}\{ID}.voice    MUTA 2.0 的声学模型
    {ID}\{ID}.inf      MUTA 1.0 的音源索引
    {ID}\{ID}.bnk      MUTA 1.0 的采样库
    {ID}\{ID}.chr      显示名（GBK）：name={NAME}

安装
----
1. 解压到任意目录（路径尽量用英文）。
2. TuneLab → 设置 → 扩展 → MUTA for TuneLab → 额外音源目录，添加 {ID} 的上一级目录。
3. 重新打开音源列表，选择「{NAME}」。

Install
-------
1. Extract anywhere.
2. TuneLab → Settings → Extensions → MUTA for TuneLab → Extra voice directories: add the
   folder that contains {ID}.
3. Reopen the voice list and pick {NAME}.

说明
----
- 1.0 的 yanxi.voice 是采样库而不是模型，所以这个包把它改名成 .bnk，并配上 2.0 的模型。
  时间和音高来自 2.0 模型，音色来自 1.0 采样。
- 音源版权归原作者所有。请只在你已拥有 MUTA 1.0 和 2.0 的前提下使用，不要再分发采样数据。

Notes
-----
- MUTA 1.0 stores its samples in a file named .voice, so this pack renames it to .bnk and
  adds the 2.0 model. Timing and pitch come from the model, timbre from the 1.0 samples.
- The voice data belongs to its authors. Use it only if you own MUTA 1.0 and 2.0, and do
  not redistribute the samples.
