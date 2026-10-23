using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MutaForTuneLab.Assets;
using TuneLab.Foundation;
using TuneLab.SDK;

namespace MutaForTuneLab;

/// <summary>
/// 每条 MidiPart 一个会话。阶段 A 只把契约跑通：音素/参数/音高都是空的，
/// 不产生待合成区间，也不改任何用户数据。
///
/// 真正的实现（分块、脏标记、快照、音频段）按计划阶段 G 填入；这里保留
/// 订阅与 Dispose 的形状，避免后面改结构。
/// </summary>
public sealed class MutaSynthesisSession : IVoiceSynthesisSession
{
    readonly IVoiceSynthesisContext mContext;
    readonly MutaVoice mVoice;

    readonly ActionEvent mStatusChanged = new();
    readonly ActionEvent mPhonemesChanged = new();
    readonly ActionEvent mPitchChanged = new();
    readonly ActionEvent mParametersChanged = new();

    public MutaSynthesisSession(IVoiceSynthesisContext context, MutaVoice voice)
    {
        mContext = context;
        mVoice = voice;
    }

    public string DefaultLyric => "a";

    public IActionEvent StatusChanged => mStatusChanged;

    public IActionEvent SynthesizedPhonemesChanged => mPhonemesChanged;

    public IActionEvent SynthesizedPitchChanged => mPitchChanged;

    public IActionEvent SynthesizedParametersChanged => mParametersChanged;

    // 阶段 A：没有任何产物。空 map / 空音高是合法值（= 还没算完）。
    public IReadOnlyMap<string, SynthesizedSyllable> SynthesizedPhonemes { get; } =
        new Map<string, SynthesizedSyllable>();

    public SynthesizedPitch SynthesizedPitch { get; } = new() { Segments = [] };

    public IReadOnlyMap<string, SynthesizedParameter> SynthesizedParameters { get; } =
        new Map<string, SynthesizedParameter>();

    /// <summary>
    /// 状态带：整个 part 报 Pending。宿主据此把区段画成"没合成"。
    /// 没有音符时返回空表（不是 0 长度的段）。
    /// </summary>
    public IReadOnlyList<SynthesisStatusSegment> Status
    {
        get
        {
            var first = mContext.Notes.First;
            var last = mContext.Notes.Last;
            if (first is null || last is null)
                return [];
            double start = first.StartTime.Value;
            double end = last.EndTime.Value;
            if (!(end > start))
                return [];
            return [new SynthesisStatusSegment { StartTime = start, EndTime = end, Status = SynthesisSegmentStatus.Pending }];
        }
    }

    /// <summary>阶段 A 不声称有任何脏块，宿主因此不会调 SynthesizeNext。</summary>
    public SynthesisRange? GetNextPendingSynthesisRange(double startTime, double endTime) => null;

    public Task SynthesizeNext(double startTime, double endTime, CancellationToken cancellation = default)
        => Task.CompletedTask;

    /// <summary>不做 melisma：每个 note 都是内容。</summary>
    public bool IsContinuation(IVoiceSynthesisNote note) => false;

    public void Dispose()
    {
    }

    internal MutaVoice Voice => mVoice;
}
