namespace MutaForTuneLab;

using TuneLab.SDK;

/// <summary>
/// 极简本地化：宿主把当前语言放在 <c>TuneLabContext.Global.Language</c>（如 "zh-CN"），
/// 文本翻译由插件自己做（宿主不查表）。这里只覆盖本插件用到的词。
/// </summary>
public static class L
{
    static bool IsChinese =>
        TuneLabContext.Global.Language.StartsWith("zh", StringComparison.OrdinalIgnoreCase);

    public static string Tr(string english, string chinese) => IsChinese ? chinese : english;
}
