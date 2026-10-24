using System;
using System.Collections.Generic;
using System.Linq;
using MutaForTuneLab.Assets;
using TuneLab.Foundation;
using TuneLab.SDK;

namespace MutaForTuneLab;

/// <summary>
/// MUTA voice 引擎入口（manifest 的 engine id = "MUTA"，不可变——会写进工程文件）。
///
/// 职责边界：
///   · 声库目录：从用户机器上已安装的 MUTA 读音源（插件不打包音源）；
///   · 声明面：automation 轨 / part / note / 音素 schema（纯函数、静态）；
///   · 每条 part 建一个会话。
///
/// 阶段 A：目录扫描 + 契约跑通（不合成）。标签/F0/bank/声码器按计划逐阶段填入。
/// </summary>
public sealed class MutaVoiceEngine : IVoiceSynthesisEngine, IExtensionSettings
{
    const string KeyVoiceDirs = "voice_dirs";
    const string KeyR2VocoderDir = "r2_vocoder_dir";

    readonly OrderedMap<string, VoiceSourceInfo> mInfos = new();
    IReadOnlyList<VoiceSourceLayoutItem> mLayout = [];
    IReadOnlyList<MutaVoice> mVoices = [];
    PropertyObject mSettings = PropertyObject.Empty;

    // —— 引擎生命周期 ——

    public void Init()
    {
        Rescan();
    }

    public void Destroy()
    {
        mVoices = [];
    }

    /// <summary>必须立即返回、不得阻塞（宿主与 UI 同步读）。扫描结果在 Init/Rescan 时缓存。</summary>
    public IReadOnlyOrderedMap<string, VoiceSourceInfo> VoiceSourceInfos => mInfos;

    public IReadOnlyList<VoiceSourceLayoutItem> VoiceSourceLayout => mLayout;

    /// <summary>每条 part 一个会话；voiceId 已烘在 context 里。</summary>
    public IVoiceSynthesisSession CreateSession(IVoiceSynthesisContext context)
    {
        var voice = mVoices.FirstOrDefault(v => string.Equals(v.Id, context.VoiceId, StringComparison.OrdinalIgnoreCase))
                    ?? throw new ArgumentException($"unknown MUTA voice: {context.VoiceId}");
        return new MutaSynthesisSession(context, voice);
    }

    // —— 声明面（引擎级、纯函数）——

    public IReadOnlyOrderedMap<PropertyKey, AutomationConfig> GetAutomationConfigs(IVoiceSynthesisPartPropertyContext context)
        => Declarations.AutomationConfigs;

    public IReadOnlyOrderedMap<PropertyKey, AutomationConfig> GetSynthesizedParameterConfigs(IVoiceSynthesisPartPropertyContext context)
        => Declarations.ReadbackConfigs;

    public ObjectConfig GetPartPropertyConfig(IVoiceSynthesisPartPropertyContext context)
        => Declarations.PartConfig;

    public ObjectConfig GetNotePropertyConfig(IVoiceSynthesisNotePropertyContext context)
        => Declarations.NoteConfig;

    /// <summary>
    /// per-phoneme 声明：键 = 核相对 slot（0 = 核，&lt;0 = 引导辅音，&gt;0 = 核后）。
    /// 本引擎所有 slot 授同一份 schema（只有一条"时长缩放"）。
    /// </summary>
    public IReadOnlyMap<int, ObjectConfig> GetPhonemePropertyConfigs(IVoiceSynthesisNotePropertyContext context)
    {
        var map = new Map<int, ObjectConfig>();
        foreach (int slot in context.Notes.UnionSlots())
            map.Add(slot, Declarations.PhonemeConfig);
        return map;
    }

    // —— 扩展设置：音源目录 / R2 声码器目录 ——

    public ObjectConfig GetSettingsConfig(IExtensionSettingsContext context)
    {
        var properties = new OrderedMap<PropertyKey, IControllerConfig>
        {
            {
                (KeyVoiceDirs, L.Tr("Extra voice directories", "额外音源目录")),
                DirListConfig(context.Settings, KeyVoiceDirs)
            },
            {
                (KeyR2VocoderDir, L.Tr("R2 vocoder folder (pc_nsf_hifigan_…)", "R2 声码器目录（pc_nsf_hifigan_…）")),
                TextBoxConfig.Create()
            },
        };
        return ObjectConfig.Create(properties);
    }

    /// <summary>
    /// 目录列表：每行一个路径的文本框，+ 追加空行。行数按当前已存值算——
    /// 缺席（从未配置）= 0 行、不 seed（默认目录本就隐式生效）。
    /// （2.0.0 SDK 没有 PathPickerConfig，那是 sdk-version 1.1。）
    /// </summary>
    static ListConfig DirListConfig(PropertyObject settings, string key)
    {
        int count = settings.GetValue(key, PropertyArray.Empty).Count;
        var elements = Enumerable.Range(0, count)
            .Select(_ => (IControllerConfig)TextBoxConfig.Create())
            .ToList();
        return ListConfig.Create(elements, [new AddableElement(TextBoxConfig.Create(), L.Tr("Directory", "目录"))]);
    }

    public void ApplySettings(PropertyObject settings)
    {
        mSettings = settings;
        Rescan();
    }

    // —— 扫描 ——

    /// <summary>The packaged Japanese voice, or null when its files are missing.</summary>
    static MutaVoice? BuiltInJapaneseVoice()
    {
        string voiceFile = Synthesis.MutaSinsy.VoiceFile;
        string dictionary = Synthesis.MutaSinsy.DictionaryDirectory;
        if (!File.Exists(voiceFile) || !Directory.Exists(dictionary))
            return null;

        return new MutaVoice(
            Id: "sinsy-jp",
            Directory: Path.GetDirectoryName(Path.GetDirectoryName(dictionary)) ?? "",
            VoiceFile: voiceFile,
            InfFile: "",
            BankFile: "",
            DisplayName: "NIT SONG070 F1（日语）",
            BankBytes: 0);
    }

    void Rescan()
    {
        var logger = TuneLabContext.Global.GetLogger();
        var roots = new List<string>();
        roots.AddRange(VoiceLibrary.DefaultRoots());
        roots.AddRange(ConfiguredDirs(KeyVoiceDirs));

        mVoices = VoiceLibrary.Scan(roots);

        // The Japanese voice ships with the plugin: it is a plain HTS voice with Sinsy's
        // label builder in front, so it needs no MUTA installation and no bank.
        if (BuiltInJapaneseVoice() is { } japanese)
            mVoices = mVoices.Append(japanese).ToList();

        mInfos.Clear();
        var layoutItems = new List<VoiceSourceLayoutItem>();
        foreach (var voice in mVoices)
        {
            mInfos.Add(voice.Id, new VoiceSourceInfo
            {
                Name = string.IsNullOrWhiteSpace(voice.DisplayName) ? voice.Id : voice.DisplayName,
                Description = $"MUTA · {voice.Id} · {voice.BankMegabytes:F0} MB",
            });
            layoutItems.Add(VoiceSourceLayoutItem.Voice(voice.Id));
        }

        // 全部平铺；同名音源在多个根下出现时取先到的一份（Scan 已去重）。
        mLayout = layoutItems;

        if (mVoices.Count == 0)
            logger.Warning("MUTA: no voice bank found. Install MUTA, or add its song voice folder under Settings > Extensions > MUTA.");
        else
            logger.Info($"MUTA: {mVoices.Count} voice bank(s) found.");
    }

    IEnumerable<string> ConfiguredDirs(string key)
    {
        foreach (var v in mSettings.GetValue(key, PropertyArray.Empty))
            if (v.ToString(out var s) && !string.IsNullOrWhiteSpace(s))
                yield return s.Trim();
    }
}
