using System.Collections.Generic;
using TuneLab.Foundation;
using TuneLab.SDK;

namespace MutaForTuneLab;

/// <summary>
/// 声明面（纯 schema，与具体 note 无关）。宿主在每次参数 commit 时调用并 diff 到 UI，
/// 所以这里只构造一次、之后复用——不能带任何状态。
///
/// 三条模型级曲线（Energy / Timbre / Husky）作用在 HTS 输出的 MGC/BAP 上；
/// 四条 worldline 曲线（Gender / Tension / Breathiness / Voicing）逐帧直传声码器。
/// 音高与颤音**不在这里**：它们走宿主内置的 Pitch / PitchDeviation 双通道。
/// </summary>
public static class Declarations
{
    // —— part 属性 ——
    public const string KeyLanguage = "language";
    public const string KeyBankPitchMode = "bank_pitch_mode";
    public const string KeyBankPitchOffset = "bank_pitch_offset";
    public const string KeyBankPitchNote = "bank_pitch_note";
    public const string KeyPhonemeSpeed = "phoneme_speed";
    public const string KeyVocoderMode = "vocoder_mode";
    public const string KeyOutputGain = "output_gain_db";

    // —— note / 音素属性 ——
    public const string KeyPronunciation = "pronunciation";
    public const string KeyPreutteranceMs = "preutterance_ms";
    public const string KeyPhonemeDurationScale = "duration_scale";

    // —— automation 轨 ——
    public const string KeyEnergy = "Energy";
    public const string KeyTimbre = "Timbre";
    public const string KeyHusky = "Husky";
    public const string KeyGender = "Gender";
    public const string KeyTension = "Tension";
    public const string KeyBreathiness = "Breathiness";
    public const string KeyVoicing = "Voicing";

    public const double EnergyMin = -36, EnergyMax = 24;
    public const double TimbreMin = -4, TimbreMax = 4;
    public const double HuskyMin = 0, HuskyMax = 40;
    public const double GenderDefault = 0.5, TensionDefault = 0.5, BreathinessDefault = 0.5, VoicingDefault = 1.0;

    /// <summary>可编辑 automation 轨（连续轨：给实数基线）。</summary>
    public static readonly IReadOnlyOrderedMap<PropertyKey, AutomationConfig> AutomationConfigs = BuildAutomations();

    /// <summary>回显轨：阶段 A/H 都不产出，先空着（空 map 是合法声明）。</summary>
    public static readonly IReadOnlyOrderedMap<PropertyKey, AutomationConfig> ReadbackConfigs =
        new OrderedMap<PropertyKey, AutomationConfig>();

    static OrderedMap<PropertyKey, AutomationConfig> BuildAutomations()
    {
        var map = new OrderedMap<PropertyKey, AutomationConfig>();
        // 模型级：作用在 MGC/BAP 上，与声码器无关。
        map.Add((KeyEnergy, L.Tr("Energy", "能量")), Continuous("#E5A573", 0, EnergyMin, EnergyMax));
        map.Add((KeyTimbre, L.Tr("Timbre", "音色")), Continuous("#E5C773", 0, TimbreMin, TimbreMax));
        map.Add((KeyHusky, L.Tr("Husky", "沙哑")), Continuous("#C27373", 0, HuskyMin, HuskyMax));
        // 声码器级：逐帧直传 worldline。
        map.Add((KeyGender, L.Tr("Gender", "性别")), Continuous("#73B5E5", GenderDefault, 0, 1));
        map.Add((KeyTension, L.Tr("Tension", "张力")), Continuous("#73E5C7", TensionDefault, 0, 1));
        map.Add((KeyBreathiness, L.Tr("Breathiness", "气声")), Continuous("#73A5E5", BreathinessDefault, 0, 1));
        map.Add((KeyVoicing, L.Tr("Voicing", "发声度")), Continuous("#9E9E9E", VoicingDefault, 0, 1));
        return map;
    }

    /// <summary>连续轨 = 给实数基线（<c>WithDefault</c> 是实数 ⇒ 处处有值）；分段轨给 NaN。</summary>
    static AutomationConfig Continuous(string color, double baseline, double min, double max)
        => AutomationConfig.Create(min, max).WithDefault(baseline).WithColor(color);

    /// <summary>part 面板：语言、换采样、语速、声码器模式、输出增益。</summary>
    public static ObjectConfig PartConfig { get; } = BuildPartConfig();

    static ObjectConfig BuildPartConfig()
    {
        var properties = new OrderedMap<PropertyKey, IControllerConfig>
        {
            {
                (KeyLanguage, L.Tr("Language", "语言")),
                // No "auto": lyrics reach the engine romanised, so a syllable like "ni"
                // belongs to both languages and there is nothing to detect. Chinese is the
                // default because every listed voice is a MUTA Chinese bank.
                ComboBoxConfig.Create(new List<ComboBoxItem>
                {
                    new(PropertyValue.Create("zh"), L.Tr("Chinese", "中文")),
                    new(PropertyValue.Create("ja"), L.Tr("Japanese", "日文")),
                }).WithDefault(PropertyValue.Create("zh"))
            },



            {
                (KeyPhonemeSpeed, L.Tr("Phoneme speed", "语速")),
                SliderConfig.Linear(1.0, 0.5, 2.0)
            },
            {
                (KeyVocoderMode, L.Tr("Vocoder", "声码器")),
                ComboBoxConfig.Create(new List<ComboBoxItem>
                {
                    // R1.1 (continuous noise) is the default: its noise is generated in the time
                    // domain instead of per frame, so there is no frame-boundary modulation, and
                    // it needs nothing installed.
                    new(PropertyValue.Create("worldline"), L.Tr("WORLDLINE R1.1 (built in)", "WORLDLINE R1.1（内置）")),
#if !MUTA_LITE
                    // R2 runs an external neural vocoder the user installs themselves; the package
                    // is not redistributable, so it is never bundled. Needs its folder in settings.
                    new(PropertyValue.Create("r2"), L.Tr("WORLDLINE R2 (external vocoder)", "WORLDLINE R2（外部声码器）")),
#endif
                }).WithDefault(PropertyValue.Create("worldline"))
            },
            {
                (KeyOutputGain, L.Tr("Output gain (dB)", "输出增益（dB）")),
                SliderConfig.Linear(0, -24, 12)
            },
        };
        return ObjectConfig.Create(properties);
    }

    /// <summary>note 面板：显式发音覆盖、辅音提前量、单音符换采样覆盖。</summary>
    public static ObjectConfig NoteConfig { get; } = BuildNoteConfig();

    static ObjectConfig BuildNoteConfig()
    {
        var properties = new OrderedMap<PropertyKey, IControllerConfig>
        {
            { (KeyPronunciation, L.Tr("Pronunciation override", "发音覆盖")), TextBoxConfig.Create() },
            { (KeyPreutteranceMs, L.Tr("Preutterance (ms)", "辅音提前量（ms）")), SliderConfig.Integer(0, -200, 200) },
            { (KeyBankPitchOffset, L.Tr("Bank pitch offset (semitones)", "音区偏移（半音）")), SliderConfig.Integer(0, -24, 24) },
        };
        return ObjectConfig.Create(properties);
    }

    /// <summary>音素属性：每个 slot 一套（阶段 A 只声明时长缩放）。</summary>
    public static ObjectConfig PhonemeConfig { get; } = BuildPhonemeConfig();

    static ObjectConfig BuildPhonemeConfig()
    {
        var properties = new OrderedMap<PropertyKey, IControllerConfig>
        {
            { (KeyPhonemeDurationScale, L.Tr("Duration scale", "时长缩放")), SliderConfig.Linear(1.0, 0.2, 3.0) },
        };
        return ObjectConfig.Create(properties);
    }

    public static ObjectConfig EmptyConfig { get; } =
        ObjectConfig.Create(new OrderedMap<PropertyKey, IControllerConfig>());
}
