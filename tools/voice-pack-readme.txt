MUTA for TuneLab — 嫣汐1.0 音源包（{ID}）
=====================================================================

这个文件夹是一个“重打包”的 MUTA 音源：采样库来自 MUTA 1.0，声学模型取自
已安装的 MUTA 2.0，插件里显示为“嫣汐1.0”，可以与 2.0 的“嫣汐”并存。

After extraction you should see:

    {ID}\{ID}.voice    HTS acoustic model (from MUTA 2.0)
    {ID}\{ID}.inf      unit index, MUTA 1.0 revision (version 100)
    {ID}\{ID}.bnk      recorded sample bank (MUTA 1.0)
    {ID}\{ID}.chr      display name, GBK: name={NAME}

安装 / Install
---------------------------------------------------------------------
1. 解压本压缩包，得到 {ID} 文件夹（放在哪里都可以，路径最好全英文）。
2. 打开 TuneLab：设置 → 扩展 → MUTA for TuneLab → “额外音源目录”，
   添加 {ID} 的上一级目录；也可以直接添加 {ID} 文件夹本身。
   TuneLab: Settings → Extensions → MUTA for TuneLab → "Extra voice directories",
   add the folder that contains {ID} (or {ID} itself).
3. 重新打开音源列表（或重启 TuneLab），选择“{NAME}”。
4. 语言仍按 part 设置成中文或日文；音色来自这个 1.0 采样库。

说明 / Notes
---------------------------------------------------------------------
* 1.0 的原始 yanxi.voice 其实是 222 MB 的采样库，不是 HTS 模型，所以插件不能
  直接读一个未处理的 1.0 安装目录；本包已经把它改名成 .bnk 并配上 2.0 的模型。
  The 1.0 "voice" file is the sample bank, not a model; this pack renames it to
  .bnk and adds the 2.0 model, which is what the plugin expects.
* 声学模型（时间、音高轮廓）来自 2.0，音色（MGC/BAP 帧）来自 1.0，所以听起来是
  1.0 的嫣汐，但长短、连音、滑音按 2.0 模型走。
* 音源版权归 MUTA / 嫣汐 的权利人所有。请只在你已经合法拥有 MUTA 1.0（含嫣汐）
  和 MUTA 2.0 的前提下使用，不要单独再分发采样数据。
  The voice data belongs to its authors. Use it only if you own MUTA 1.0 and 2.0.
