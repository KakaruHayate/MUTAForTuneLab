# 日语音素 → MUTA bank 音素 对照表（第三版）

依据：`yanxi.inf` 的真实录音索引，判据是**过渡段是否存在**——声母组记了该右邻，或韵母组记了该左邻。

中文会对同一个元音用不同拼写，本表按索引里的实际拼写取：

| 现象 | 拼写 |
| --- | --- |
| o 在多数声母后 | `uo`（火 h+uo、罗 l+uo） |
| i 在 s/z/c 后 | `if`（思/资/次） |
| i 在 zh/ch/sh/r 后 | `ib`（知/日） |
| 零声母的 y/w | 记作 `sp` + 韵母 |
| け/て/で | `e`（不是 `ei`） |

状态：`exact` 与索引一致；`proxy` 换了声母或韵母但过渡段真实存在；`hard-join` 无过渡段。

| 统计 | 数量 |
| --- | ---: |
| proxy | 88 |
| exact | 50 |
| hard-join | 8 |

## 逐项

| 日语音素 | 类别 | bank 起始 | bank 韵母 | 状态 |
| --- | --- | --- | --- | --- |
| N | nasal | — | en | proxy |
| a | vowel | — | a | exact |
| i | vowel | — | i | exact |
| u | vowel | — | u | exact |
| e | vowel | — | ei | exact |
| o | vowel | — | o | exact |
| ka | plain | k | a | exact |
| ku | plain | k | u | exact |
| ke | plain | k | e | proxy |
| ko | plain | k | uo | proxy |
| ga | plain | g | a | exact |
| gu | plain | g | u | exact |
| ge | plain | g | ei | exact |
| go | plain | g | uo | proxy |
| ha | plain | h | a | exact |
| hu | plain | h | u | exact |
| he | plain | h | ei | exact |
| ho | plain | h | uo | proxy |
| ba | plain | b | a | exact |
| bi | plain | b | i | exact |
| bu | plain | b | u | exact |
| be | plain | b | ei | exact |
| bo | plain | b | o | hard-join |
| pa | plain | p | a | exact |
| pi | plain | p | i | exact |
| pu | plain | p | u | exact |
| pe | plain | p | ei | exact |
| po | plain | p | o | exact |
| ma | plain | m | a | exact |
| mi | plain | m | i | exact |
| mu | plain | m | u | exact |
| me | plain | m | ei | exact |
| mo | plain | m | o | exact |
| na | plain | n | a | exact |
| ni | plain | n | i | exact |
| nu | plain | n | u | exact |
| ne | plain | n | ei | exact |
| no | plain | n | uo | proxy |
| fa | plain | f | a | exact |
| fi | plain | f | i | hard-join |
| fu | plain | f | u | exact |
| fe | plain | f | ei | exact |
| fo | plain | f | o | exact |
| sa | plain | s | a | exact |
| si | plain | s | if | exact |
| su | plain | s | u | exact |
| se | plain | s | e | proxy |
| so | plain | s | uo | proxy |
| za | plain | z | a | exact |
| zi | plain | z | if | exact |
| zu | plain | z | u | exact |
| ze | plain | z | ei | exact |
| zo | plain | z | uo | proxy |
| ta | plain | t | a | exact |
| ti | plain | t | i | exact |
| tu | plain | t | u | exact |
| te | plain | t | ie | proxy |
| to | plain | t | uo | proxy |
| da | plain | d | a | exact |
| di | plain | d | i | exact |
| du | plain | d | u | exact |
| de | plain | d | ei | exact |
| do | plain | d | uo | proxy |
| ra | plain | l | a | proxy |
| ri | plain | l | i | proxy |
| ru | plain | l | u | proxy |
| re | plain | l | ei | proxy |
| ro | plain | l | uo | proxy |
| tsa | plain | c | a | proxy |
| tsi | plain | c | if | proxy |
| tsu | plain | c | u | proxy |
| tse | plain | c | e | proxy |
| tso | plain | c | uo | proxy |
| cha | plain | q | ia | proxy |
| chi | plain | q | i | proxy |
| chu | plain | q | iu | proxy |
| che | plain | q | ie | proxy |
| cho | plain | q | iu | proxy |
| sha | plain | x | ia | proxy |
| shi | plain | x | i | proxy |
| shu | plain | x | iu | proxy |
| she | plain | x | ie | proxy |
| sho | plain | x | iu | proxy |
| ja | plain | j | ia | proxy |
| ji | plain | j | i | exact |
| ju | plain | j | iu | proxy |
| je | plain | j | ie | proxy |
| jo | plain | j | iu | proxy |
| kya | palatalized | q | ia | proxy |
| kyi | palatalized | q | i | proxy |
| kyu | palatalized | q | iu | proxy |
| kye | palatalized | q | ie | proxy |
| kyo | palatalized | q | iu | proxy |
| gya | palatalized | j | ia | proxy |
| gyi | palatalized | j | i | proxy |
| gyu | palatalized | j | iu | proxy |
| gye | palatalized | j | ie | proxy |
| gyo | palatalized | j | iu | proxy |
| hya | palatalized | x | ia | proxy |
| hyi | palatalized | x | i | proxy |
| hyu | palatalized | x | iu | proxy |
| hye | palatalized | x | ie | proxy |
| hyo | palatalized | x | iu | proxy |
| nya | palatalized | n | ia | hard-join |
| nyi | palatalized | n | i | proxy |
| nyu | palatalized | n | iu | proxy |
| nye | palatalized | n | ie | proxy |
| nyo | palatalized | n | iu | proxy |
| mya | palatalized | m | ia | hard-join |
| myi | palatalized | m | i | proxy |
| myu | palatalized | m | iu | proxy |
| mye | palatalized | m | ie | proxy |
| myo | palatalized | m | iu | proxy |
| bya | palatalized | b | ia | hard-join |
| byi | palatalized | b | i | proxy |
| byu | palatalized | b | iu | hard-join |
| bye | palatalized | b | ie | proxy |
| byo | palatalized | b | iao | proxy |
| pya | palatalized | p | ia | hard-join |
| pyi | palatalized | p | i | proxy |
| pyu | palatalized | p | iu | hard-join |
| pye | palatalized | p | ie | proxy |
| pyo | palatalized | p | iao | proxy |
| rya | palatalized | l | ia | proxy |
| ryi | palatalized | l | i | proxy |
| ryu | palatalized | l | iu | proxy |
| rye | palatalized | l | ie | proxy |
| ryo | palatalized | l | iu | proxy |
| tya | palatalized | q | ia | proxy |
| tyi | palatalized | q | i | proxy |
| tyu | palatalized | q | iu | proxy |
| tye | palatalized | q | ie | proxy |
| tyo | palatalized | q | iu | proxy |
| dya | palatalized | j | ia | proxy |
| dyi | palatalized | j | i | proxy |
| dyu | palatalized | j | iu | proxy |
| dye | palatalized | j | ie | proxy |
| dyo | palatalized | j | iu | proxy |
| ya | zero-initial | y | ia | proxy |
| yu | zero-initial | y | iu | proxy |
| yo | zero-initial | y | iu | proxy |
| ye | zero-initial | y | ie | proxy |
| wa | zero-initial | w | ua | proxy |
| ki | palatalized | q | i | proxy |
| gi | palatalized | j | i | proxy |
| hi | palatalized | x | i | proxy |
