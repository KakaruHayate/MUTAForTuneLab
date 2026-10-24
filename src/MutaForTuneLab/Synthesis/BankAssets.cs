using System;
using System.Collections.Generic;
using System.IO;
using MutaForTuneLab.Assets;

namespace MutaForTuneLab.Synthesis;
/// <summary>
/// The recording bank: a flat float file plus the index that says which frames belong to
/// which context.
///
/// The file is not read whole — it is around 94 MB per voice and a song touches a fraction
/// of it. Items are read on demand and cached by identity, so the same syllable in the same
/// context costs one seek per session rather than one per note.
/// </summary>
public sealed class BankAssets : IBankSource
{
    /// <summary>Items kept before the cache is cleared. A song reuses contexts heavily.</summary>
    const int CacheLimit = 512;

    readonly string mBankPath;
    readonly Dictionary<InfItem, (double[][] Mgc, double[][] Bap)> mCache = new();

    public InfFile Info { get; }
    public IReadOnlyDictionary<string, List<InfItem>> Groups { get; }
    public int MgcOrder => Info.MgcOrder;
    public int BapOrder => Info.BapOrder;
    public int FrameBytes => (Info.MgcOrder + Info.BapOrder) * 4;

    BankAssets(InfFile info, string bankPath)
    {
        Info = info;
        mBankPath = bankPath;

        var groups = new Dictionary<string, List<InfItem>>(StringComparer.Ordinal);
        foreach (var group in info.Groups)
        {
            if (groups.TryGetValue(group.Name, out var existing))
                existing.AddRange(group.Items);
            else
                groups[group.Name] = new List<InfItem>(group.Items);
        }
        Groups = groups;
    }

    public static BankAssets Load(MutaVoice voice)
    {
        var info = InfFile.Load(voice.InfFile);
        info.ValidateBank(new FileInfo(voice.BankFile).Length);
        return new BankAssets(info, voice.BankFile);
    }

    /// <summary>The MGC and BAP frames of one recorded context.</summary>
    public (double[][] Mgc, double[][] Bap) Streams(InfItem item)
    {
        if (mCache.TryGetValue(item, out var cached))
            return cached;
        if (mCache.Count >= CacheLimit)
            mCache.Clear();

        int frames = item.FrameCount;
        int mgcOrder = Info.MgcOrder;
        int bapOrder = Info.BapOrder;
        int count = frames * (mgcOrder + bapOrder);

        var buffer = new double[count];
        using (var stream = new FileStream(mBankPath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            stream.Seek(item.BankOffset, SeekOrigin.Begin);
            var bytes = new byte[count * 4];
            int read = 0;
            while (read < bytes.Length)
            {
                int got = stream.Read(bytes, read, bytes.Length - read);
                if (got <= 0)
                    throw new InvalidDataException($"bank ends inside item {item.Current}");
                read += got;
            }
            // The file is float32; block-copying those bytes into a double[] would
            // reinterpret the bit patterns rather than convert them.
            var floats = new float[count];
            Buffer.BlockCopy(bytes, 0, floats, 0, bytes.Length);
            for (int i = 0; i < count; i++)
                buffer[i] = floats[i];
        }

        var mgc = new double[frames][];
        var bap = new double[frames][];
        for (int f = 0; f < frames; f++)
        {
            mgc[f] = new double[mgcOrder];
            Array.Copy(buffer, f * mgcOrder, mgc[f], 0, mgcOrder);
            bap[f] = new double[bapOrder];
            Array.Copy(buffer, frames * mgcOrder + f * bapOrder, bap[f], 0, bapOrder);
        }

        var result = (mgc, bap);
        mCache[item] = result;
        return result;
    }
}

