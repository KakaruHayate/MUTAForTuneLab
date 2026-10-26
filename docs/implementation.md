# 实现说明

本文说明渲染链的具体实现和数据流；架构与构建见 [技术说明](technical.md)。

## 1. 会话生命周期

`MutaVoiceEngine` 为每个 part/voice 创建 `MutaSynthesisSession`。宿主通过：

- `GetNextPendingSynthesisRange(start,end)` 查询下一个需要合成的块；
- `SynthesizeNext(...)` 在同一块上提交；
- `Status` / `SynthesizedPhonemes` / `SynthesizedPitch` 读取状态和产物。

会话内部：

- `ConcurrentDictionary<string, Product>` 保存每块的音频片段和音素；
- `Product.Signature` 决定是否仍然有效；
- 渲染在线程池执行，结果回到数据线程后创建/替换 `IAudioSegment`；
- `Dispose` 取消在飞渲染并等待其结束，再释放片段。

## 2. 分块

`Plan()` 是纯函数：

1. 从 `mContext.Notes` 取当前音符；
2. 音符间隔超过 `GapSeconds`（0.4 s）就开新块；
3. 块跨度超过 `MaxBlockSeconds`（12 s）或音符数超过 240 时，在下一个内容音符前断开；
4. 连音乘客不能作为块首，避免块首没有可延续的链头；
5. 块边界落在静音中点，或前后各留 lead/tail。

块签名包括：

- 音符 id、起止、音高、歌词；
- 锁定音素的符号、时长、权重、`BodyOffset`；
- 该块的曲线版本；
- part 设置版本。

曲线编辑的 `RangeModified(start,end)` 只递增与区间相交的块版本；part 设置递增全局版本。
因此改一句歌词/曲线只会重跑对应块。

## 3. 中文标签

- `ChineseFrontend` 负责分词/多音字，产出 `SungSyllable`（声母/韵母、bank 名）。
- `LabelBuilder` 把音符展开成 slot：
  - 声母长度使用 MUTA 的定长表，并限制为音符的一半；
  - 韵母填满剩余时长；
  - 连音乘客不产生 slot，链头韵母延长；
  - 生成 full-context 标签，第三字段为 `p2^p1-current+n1=n2/...`。
- 标签在进入 HTS 前校验时间戳、总长和 token 长度。

## 4. 日文标签

- `MutaSinsy.Labels` 把内存乐谱写成 MusicXML 字符串，交给 Sinsy 前端；
- Sinsy 返回的标签包含 `pau` 和派生时间 `-1`；校验器对日文允许该哨兵；
- 音色仍来自 MUTA bank，Sinsy 只提供音素/时长/时序。

## 5. HTS 与 bank

1. `muta_hts_synthesize` 返回模型帧数；
2. 读取 MGC/LF0/BAP 三条流和每个 state 的时长；
3. `PhoneTimeline` 用 state 时长重建每个音素的实际帧位置；
4. `BankRenderer` 在需要时用录音上下文替换频谱：
   - 每个音符最多一个声母槽 + 一个韵母槽；
   - 按左/右上下文、音类、音高、时长选择录音；
   - 声母/韵母时长、重叠和扩展由 `BankAssembler` 处理；
   - 锁定音素时使用其符号、时长、权重和 `BodyOffset`；
   - 每音符 `BankPitchOffset` 只影响选中的录音音区；
5. 没有 bank 的音符保留模型频谱。

## 6. 音高

- `F0Mechanism` 先把模型 F0 弯到音符轮廓；
- 画过的绝对音高逐帧覆盖；
- 未画帧保留音符轮廓，并叠加 `PitchDeviation`；
- 最后把非有限/越界值归一化到 8–4000 Hz；
- 跨拍滑音使用前后帧的 S 曲线，起点在拍前、终点在拍后。

## 7. 声码器与输出

- `WorldlineVocoder` 把 MGC/BAP 转成 1025 bin 功率谱/非周期性；
- R1.1 用 `WorldSynthesisContinuousNoise` 生成 48 kHz；
- R2 先由 `mel.onnx` 求 mel，再交给用户的 ONNX 声码器（44.1 kHz）；
- 换气在声码器之后混入（`.inf` 尾部 PCM 或带通噪声），电平相对同块演唱 RMS；
- `OutputGate` 对静音帧做门控和短斜坡，最后软限幅；
- 输出按块写入 `IAudioSegment`。

## 8. 失败处理

- 标签数量/音素序列不一致：该块失败并记录原因，不静默移位；
- 块超过 60 秒或内存预算不足：失败并给出提示；
- R2 缺失/配置错误：回退 R1.1 并记录原因；
- 原生库缺失：扩展初始化时报错，不进入渲染。

## 9. 关键缓存

- `HtsVoice`：路径 + 长度 + mtime；
- `BankAssets`：`.inf`/`.bnk` 的路径、长度、mtime；
- 已渲染块：按 `Key` 保存片段；
- R2：按目录缓存 ONNX 会话。

修改音源文件或替换 bank 后，缓存按 mtime/长度自动失效。
