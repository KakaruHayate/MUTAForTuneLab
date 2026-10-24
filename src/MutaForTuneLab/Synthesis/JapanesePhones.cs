namespace MutaForTuneLab.Synthesis;

/// <summary>
/// Japanese phones rewritten onto the phones the MUTA bank records.
///
/// Sinsy supplies the labels, durations and F0; the bank supplies the timbre, and its phone
/// names are Chinese. The table is generated from the bank index itself
/// (docs/ja-zh-phone-table.md) so every entry either lands on a recorded transition or is
/// marked as one Mandarin cannot have, in which case the caller still passes the intended
/// target and the selector scores the nearest recorded unit.
/// </summary>
public static class JapanesePhones
{
    /// <summary>Sinsy phone pair to (bank onset, bank final); null onset means a bare vowel.</summary>
    static readonly Dictionary<string, (string? Onset, string Final)> sMap = new(StringComparer.Ordinal)
    {
        ["N"] = (null, "en"),
        ["a"] = (null, "a"),
        ["i"] = (null, "i"),
        ["u"] = (null, "u"),
        ["e"] = (null, "ie"),
        ["o"] = (null, "o"),
        ["ka"] = ("k", "a"),
        ["ku"] = ("k", "u"),
        ["ke"] = ("k", "e"),
        ["ko"] = ("k", "uo"),
        ["ga"] = ("g", "a"),
        ["gu"] = ("g", "u"),
        ["ge"] = ("g", "ei"),
        ["go"] = ("g", "uo"),
        ["ha"] = ("h", "a"),
        ["hu"] = ("h", "u"),
        ["he"] = ("h", "ei"),
        ["ho"] = ("h", "uo"),
        ["ba"] = ("b", "a"),
        ["bi"] = ("b", "i"),
        ["bu"] = ("b", "u"),
        ["be"] = ("b", "ie"),
        ["bo"] = ("b", "o"),   // hard-join
        ["pa"] = ("p", "a"),
        ["pi"] = ("p", "i"),
        ["pu"] = ("p", "u"),
        ["pe"] = ("p", "ie"),
        ["po"] = ("p", "o"),
        ["ma"] = ("m", "a"),
        ["mi"] = ("m", "i"),
        ["mu"] = ("m", "u"),
        ["me"] = ("m", "ie"),
        ["mo"] = ("m", "o"),
        ["na"] = ("n", "a"),
        ["ni"] = ("n", "i"),
        ["nu"] = ("n", "u"),
        ["ne"] = ("n", "ie"),
        ["no"] = ("n", "uo"),
        ["fa"] = ("f", "a"),
        ["fi"] = ("f", "i"),   // hard-join
        ["fu"] = ("f", "u"),
        ["fe"] = ("f", "ei"),
        ["fo"] = ("f", "o"),
        ["sa"] = ("s", "a"),
        ["si"] = ("s", "if"),
        ["su"] = ("s", "u"),
        ["se"] = ("s", "e"),
        ["so"] = ("s", "uo"),
        ["za"] = ("z", "a"),
        ["zi"] = ("z", "if"),
        ["zu"] = ("z", "u"),
        ["ze"] = ("z", "ei"),
        ["zo"] = ("z", "uo"),
        ["ta"] = ("t", "a"),
        ["ti"] = ("t", "i"),
        ["tu"] = ("t", "u"),
        ["te"] = ("t", "ie"),
        ["to"] = ("t", "uo"),
        ["da"] = ("d", "a"),
        ["di"] = ("d", "i"),
        ["du"] = ("d", "u"),
        ["de"] = ("d", "ie"),
        ["do"] = ("d", "uo"),
        ["ra"] = ("l", "a"),
        ["ri"] = ("l", "i"),
        ["ru"] = ("l", "u"),
        ["re"] = ("l", "ie"),
        ["ro"] = ("l", "uo"),
        ["tsa"] = ("c", "a"),
        ["tsi"] = ("c", "if"),
        ["tsu"] = ("c", "u"),
        ["tse"] = ("c", "e"),
        ["tso"] = ("c", "uo"),
        ["cha"] = ("q", "ia"),
        ["chi"] = ("q", "i"),
        ["chu"] = ("q", "iu"),
        ["che"] = ("q", "ie"),
        ["cho"] = ("q", "iao"),
        ["sha"] = ("x", "ia"),
        ["shi"] = ("x", "i"),
        ["shu"] = ("x", "iu"),
        ["she"] = ("x", "ie"),
        ["sho"] = ("x", "iao"),
        ["ja"] = ("j", "ia"),
        ["ji"] = ("j", "i"),
        ["ju"] = ("j", "iu"),
        ["je"] = ("j", "ie"),
        ["jo"] = ("j", "iao"),
        ["kya"] = ("q", "ia"),
        ["kyi"] = ("q", "i"),
        ["kyu"] = ("q", "iu"),
        ["kye"] = ("q", "ie"),
        ["kyo"] = ("q", "iao"),
        ["gya"] = ("j", "ia"),
        ["gyi"] = ("j", "i"),
        ["gyu"] = ("j", "iu"),
        ["gye"] = ("j", "ie"),
        ["gyo"] = ("j", "iao"),
        ["hya"] = ("x", "ia"),
        ["hyi"] = ("x", "i"),
        ["hyu"] = ("x", "iu"),
        ["hye"] = ("x", "ie"),
        ["hyo"] = ("x", "iao"),
        ["nya"] = ("n", "ia"),   // hard-join
        ["nyi"] = ("n", "i"),
        ["nyu"] = ("n", "iu"),
        ["nye"] = ("n", "ie"),
        ["nyo"] = ("n", "iao"),
        ["mya"] = ("m", "ia"),   // hard-join
        ["myi"] = ("m", "i"),
        ["myu"] = ("m", "iu"),
        ["mye"] = ("m", "ie"),
        ["myo"] = ("m", "iao"),
        ["bya"] = ("b", "ia"),   // hard-join
        ["byi"] = ("b", "i"),
        ["byu"] = ("b", "iu"),   // hard-join
        ["bye"] = ("b", "ie"),
        ["byo"] = ("b", "iao"),
        ["pya"] = ("p", "ia"),   // hard-join
        ["pyi"] = ("p", "i"),
        ["pyu"] = ("p", "iu"),   // hard-join
        ["pye"] = ("p", "ie"),
        ["pyo"] = ("p", "iao"),
        ["rya"] = ("l", "ia"),
        ["ryi"] = ("l", "i"),
        ["ryu"] = ("l", "iu"),
        ["rye"] = ("l", "ie"),
        ["ryo"] = ("l", "iao"),
        ["tya"] = ("q", "ia"),
        ["tyi"] = ("q", "i"),
        ["tyu"] = ("q", "iu"),
        ["tye"] = ("q", "ie"),
        ["tyo"] = ("q", "iao"),
        ["dya"] = ("j", "ia"),
        ["dyi"] = ("j", "i"),
        ["dyu"] = ("j", "iu"),
        ["dye"] = ("j", "ie"),
        ["dyo"] = ("j", "iao"),
        ["ya"] = ("y", "ia"),
        ["yu"] = ("y", "iu"),
        ["yo"] = ("y", "iao"),
        ["ye"] = ("y", "ie"),
        ["wa"] = ("w", "ua"),
        ["wo"] = ("w", "uo"),
        ["va"] = ("w", "ua"),
        ["vi"] = ("w", "i"),   // hard-join
        ["vu"] = ("w", "u"),
        ["ve"] = ("w", "ui"),
        ["vo"] = ("w", "uo"),
        ["ki"] = ("q", "i"),
        ["gi"] = ("j", "i"),
        ["hi"] = ("x", "i"),
    };

    /// <summary>
    /// Rewrites one mora. The caller passes the onset and the vowel it got from the kana table;
    /// the pair is looked up whole because the mapping is not separable -- a palatalised onset
    /// changes the vowel it takes.
    /// </summary>
    public static bool TryMap(string? onset, string vowel, out string? bankOnset, out string bankFinal)
    {
        string key = (onset ?? string.Empty) + vowel;
        if (sMap.TryGetValue(key, out var mapped))
        {
            bankOnset = mapped.Onset;
            bankFinal = mapped.Final;
            return true;
        }
        bankOnset = onset;
        bankFinal = vowel;
        return false;
    }
}
