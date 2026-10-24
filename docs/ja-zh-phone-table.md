# 日语音素 → MUTA bank 音素 对照表（第二版）

依据：`yanxi.inf` 的真实录音过渡段（`current` 与 `right` 的共现），不是纸面近似。

状态：`exact` = 声韵与实际录音一致；`proxy` = 换用更近的声母/韵母但过渡段真实存在；
`hard-join` = 该组合在 bank 里没有过渡段，靠引擎回退拼接。

| 统计 | 数量 |
| --- | ---: |
| proxy | 67 |
| exact | 51 |
| hard-join | 45 |

## 逐项

| 日语音素 | 类别 | bank 起始 | bank 韵母 | 状态 |
| --- | --- | --- | --- | --- |
| N | nasal | — | en | exact |
| a | vowel | — | a | exact |
| i | vowel | — | i | exact |
| u | vowel | — | u | exact |
| e | vowel | — | ei | exact |
| o | vowel | — | o | exact |
| ka | plain | k | a | exact |
| ku | plain | k | u | exact |
| ke | plain | k | ei | hard-join |
| ko | plain | k | o | hard-join |
| ga | plain | g | a | exact |
| gu | plain | g | u | exact |
| ge | plain | g | ei | exact |
| go | plain | g | o | hard-join |
| ha | plain | h | a | exact |
| hu | plain | h | u | exact |
| he | plain | h | ei | exact |
| ho | plain | h | o | hard-join |
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
| no | plain | n | o | hard-join |
| fa | plain | f | a | exact |
| fi | plain | f | i | hard-join |
| fu | plain | f | u | exact |
| fe | plain | f | ei | exact |
| fo | plain | f | o | exact |
| sa | plain | s | a | exact |
| si | plain | s | i | hard-join |
| su | plain | s | u | exact |
| se | plain | s | ei | hard-join |
| so | plain | s | o | hard-join |
| za | plain | z | a | exact |
| zi | plain | z | i | hard-join |
| zu | plain | z | u | exact |
| ze | plain | z | ei | exact |
| zo | plain | z | o | hard-join |
| ta | plain | t | a | exact |
| ti | plain | t | i | exact |
| tu | plain | t | u | exact |
| te | plain | t | ei | hard-join |
| to | plain | t | o | hard-join |
| da | plain | d | a | exact |
| di | plain | d | i | exact |
| du | plain | d | u | exact |
| de | plain | d | ei | exact |
| do | plain | d | o | hard-join |
| ra | plain | l | a | proxy |
| ri | plain | l | i | proxy |
| ru | plain | l | u | proxy |
| re | plain | l | ei | proxy |
| ro | plain | l | o | hard-join |
| wa | plain | w | a | hard-join |
| wi | plain | w | i | hard-join |
| wu | plain | w | u | exact |
| we | plain | w | ei | hard-join |
| wo | plain | w | o | hard-join |
| ya | plain | y | a | hard-join |
| yi | plain | y | i | exact |
| yu | plain | y | u | hard-join |
| ye | plain | y | ei | hard-join |
| yo | plain | y | o | hard-join |
| tsa | plain | c | a | proxy |
| tsi | plain | c | i | hard-join |
| tsu | plain | c | u | proxy |
| tse | plain | c | ei | hard-join |
| tso | plain | c | o | hard-join |
| cha | plain | q | a | hard-join |
| chi | plain | q | i | proxy |
| chu | plain | q | u | hard-join |
| che | plain | q | ei | hard-join |
| cho | plain | q | o | hard-join |
| sha | plain | x | a | hard-join |
| shi | plain | x | i | proxy |
| shu | plain | x | u | hard-join |
| she | plain | x | ei | hard-join |
| sho | plain | x | o | hard-join |
| ja | plain | q | a | hard-join |
| ji | plain | q | i | proxy |
| ju | plain | q | u | hard-join |
| je | plain | q | ei | hard-join |
| jo | plain | q | o | hard-join |
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
| byo | palatalized | b | ie | proxy |
| pya | palatalized | p | ia | hard-join |
| pyi | palatalized | p | i | proxy |
| pyu | palatalized | p | iu | hard-join |
| pye | palatalized | p | ie | proxy |
| pyo | palatalized | p | ie | proxy |
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
| sha | palatalized | x | ia | proxy |
| shi | palatalized | x | i | proxy |
| shu | palatalized | x | iu | proxy |
| she | palatalized | x | ie | proxy |
| sho | palatalized | x | iu | proxy |
| cha | palatalized | q | ia | proxy |
| chi | palatalized | q | i | proxy |
| chu | palatalized | q | iu | proxy |
| che | palatalized | q | ie | proxy |
| cho | palatalized | q | iu | proxy |
| ja | palatalized | j | ia | proxy |
| ji | palatalized | j | i | exact |
| ju | palatalized | j | iu | proxy |
| je | palatalized | j | ie | proxy |
| jo | palatalized | j | iu | proxy |

## 生成说明

本表由 `build/gen-table2.py` 从 `yanxi.inf` 的实际录音索引生成，
判据是**过渡段是否存在**：声母组的 `right` 或韵母组的 `left` 命中即算存在（双向查）。

`hard-join` 的那一批不是漏配，而是**普通话里根本没有这个音节**：
`ko / ro / si / se / so / te / to / do / yo / wa / wo / zi / zo …`
日语有、中文没有，任何跨语言映射都只能靠引擎按音类回退。
其中 `k/g/h + i 系` 更特殊：き是 `ky+i`，所以走的是拗音行而不是 `k+i`。

**N** 取 `en`（无起始）：中文 `en` 的前半极短、后半是稳定鼻音，拉长时最接近独立拨音。