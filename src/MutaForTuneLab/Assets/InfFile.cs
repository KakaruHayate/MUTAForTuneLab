using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace MutaForTuneLab.Assets;

/// <summary>一条 bank 上下文：当前音素 + 左右上下文 + 11 个整数字段。</summary>
public sealed class InfItem
{
    public required string Current { get; init; }
    public required string Left { get; init; }
    public required string Right { get; init; }
    public required int[] Values { get; init; }

    // 字段位置由 .inf 格式固定；values[0]/[1] 是左右类别，末尾两个是 bank 偏移与长度。
    public int FrameCount => Values[2];
    public int PrefixFrames => Values[3];
    public int CoreFrames => Values[4];
    public int SuffixFrames => Values[5];
    public int PitchCode => Values[6];
    public int MidiNote => Values[6] + 12;
    public int LeftPitchCode => Values[7];
    public int RightPitchCode => Values[8];
    public int BankOffset => Values[^2];
    public int BankSize => Values[^1];

    public double PitchHz => 440.0 * Math.Pow(2.0, (PitchCode - 57) / 12.0);
    public double LeftPitchHz => 440.0 * Math.Pow(2.0, (LeftPitchCode - 57) / 12.0);
    public double RightPitchHz => 440.0 * Math.Pow(2.0, (RightPitchCode - 57) / 12.0);
}

public sealed class InfGroup
{
    public required string Name { get; init; }
    public required int DeclaredCount { get; init; }
    public required List<InfItem> Items { get; init; }
}

/// <summary>
/// MUTA 音源的 <c>.inf</c> 上下文索引。两种修订版共用文件骨架，只有头长度与记录字段数不同：
///
/// 2.0（version 200）：
///   第 1 行  6 × int32（version, item_count, mgc_order, bap_order, final_count, group_count）
///   组区     group_count × (名字 + int32 容量)，行尾 0x0A
///   记录区   item_count × (当前 + 左 + 右 + 11 × int32)，行尾 0x0A
///   尾块区   final_count × (3 × int32)，行尾 0x0A
///
/// 1.0 / 0.2.alpha（version 100）：
///   第 1 行  10 × int32（前六个同 2.0，后四个是标志位）
///   组区     同上
///   记录区   item_count × (单元名 + 9 × int32)，行尾 0x0A
///   尾块区   同上
///
/// 记录字段的对应关系（1.0 → 本类的 11 字段视图）：
///   [0] 左类别  [1] 右类别  [2] 音高码  [3] 总帧数  [4] 首段帧数
///   [5] 左边界音高  [6] 右边界音高  [7] bank 偏移  [8] bank 字节数
/// 旧版没有 prefix/core/suffix 三段划分：首段就是 prefix 或 core，取决于单元名。
/// 判定写在 <see cref="MakeV1Item"/> 里，分组->音素的映射在 <see cref="BankLayout"/>。
///
/// 注意 `decode_byte` 必须作用在**整个文件**上：它同时覆盖字符串与所有整数，
/// 只解字符串会把整数字段搞坏。
/// </summary>
public sealed class InfFile
{
    public int Version { get; private init; }
    public int ItemCount { get; private init; }
    public int MgcOrder { get; private init; }
    public int BapOrder { get; private init; }
    public int FinalCount { get; private init; }
    public int GroupCount { get; private init; }
    public required IReadOnlyList<InfGroup> Groups { get; init; }
    /// <summary>尾部 PCM 块：(采样数, bank 内起始字节, 字节数)。</summary>
    public required IReadOnlyList<(int Count, int Start, int Size)> Final { get; init; }

    public int TotalItems
    {
        get
        {
            int total = 0;
            foreach (var group in Groups)
                total += group.Items.Count;
            return total;
        }
    }

    public static InfFile Load(string path) => ParseDecoded(DecodeAll(File.ReadAllBytes(path)), path);

    /// <summary>
    /// The revision an index declares, read from the first four bytes only. Used by the
    /// scanner, which must not read a 12 MB index just to reject a folder.
    /// </summary>
    public static int PeekVersion(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            Span<byte> head = stackalloc byte[4];
            stream.ReadExactly(head);
            return BinaryPrimitives.ReadInt32LittleEndian(
                [DecodeByte(head[0]), DecodeByte(head[1]), DecodeByte(head[2]), DecodeByte(head[3])]);
        }
        catch (Exception)
        {
            return 0;
        }
    }

    /// <summary>
    /// Parses an already-decoded index. Public so probes and tests can build a synthetic
    /// index in memory instead of shipping a bank-sized file.
    /// </summary>
    public static InfFile ParseDecoded(byte[] data, string path)
    {
        // The version is the first int32 and is the only trustworthy discriminator: a v100
        // header line is 40 bytes, a v200 one is 24, and both may contain 0x0A bytes.
        int version = data.Length >= 4 ? BinaryPrimitives.ReadInt32LittleEndian(data) : 0;
        return version < 200 ? ParseV1(data, path) : ParseV2(data, path);
    }

    /// <summary>
    /// The byte transform MUTA applies to a whole .inf before writing it; it is a
    /// bijection over all 256 values, so this is its inverse.
    /// </summary>
    public static byte DecodeByte(byte value) => value switch
    {
        < 0x28 => value,
        0x2C or 0x64 => value,
        >= 0x28 and < 0x40 => (byte)(value + 0x38),
        >= 0x40 and < 0x50 => (byte)(value + 0x10),
        >= 0x50 and < 0x60 => (byte)(value - 0x10),
        >= 0x60 and < 0x78 => (byte)(value - 0x38),
        _ => value,
    };

    public static byte[] DecodeAll(byte[] data)
    {
        var result = new byte[data.Length];
        for (int i = 0; i < data.Length; i++)
            result[i] = DecodeByte(data[i]);
        return result;
    }

    static InfFile ParseV2(byte[] data, string path)
    {
        var cursor = new Cursor(data, path);

        var headerBytes = cursor.Token(0x0A);
        if (headerBytes.Length < 24)
            throw new InvalidDataException($"{path}: short header line ({headerBytes.Length} bytes)");

        Span<int> header = stackalloc int[6];
        for (int i = 0; i < 6; i++)
            header[i] = BinaryPrimitives.ReadInt32LittleEndian(headerBytes.AsSpan(i * 4, 4));
        for (int i = 0; i < 6; i++)
            if (header[i] < 0)
                throw new InvalidDataException($"{path}: negative header field");

        // Header counts are file-controlled. Without these bounds a 25-byte file can make us
        // commit hundreds of MB before the truncated parse throws, and that runs per block.
        int groupCount = header[5];
        if (groupCount > data.Length / 8)
            throw new InvalidDataException($"{path}: {groupCount} groups in {data.Length} bytes");

        var capacities = new List<(string Name, int Declared)>(groupCount);
        for (int i = 0; i < groupCount; i++)
            capacities.Add((cursor.Text(), cursor.I32()));
        cursor.Expect(0x0A);

        var byName = new Dictionary<string, List<InfItem>>(StringComparer.Ordinal);
        foreach (var (name, _) in capacities)
            byName[name] = [];

        int itemCount = header[1];
        if (itemCount > data.Length / 16)
            throw new InvalidDataException($"{path}: {itemCount} items in {data.Length} bytes");
        if (header[2] <= 0 || header[2] > 512 || header[3] <= 0 || header[3] > 512)
            throw new InvalidDataException(
                $"{path}: implausible orders mgc={header[2]} bap={header[3]}");
        for (int i = 0; i < itemCount; i++)
        {
            string current = cursor.Text();
            string left = cursor.Text();
            string right = cursor.Text();
            var values = new int[11];
            for (int k = 0; k < 11; k++)
                values[k] = cursor.I32();
            if (!byName.TryGetValue(current, out var bucket))
                throw new InvalidDataException($"{path}: unknown group '{current}' at item {i}");
            bucket.Add(new InfItem { Current = current, Left = left, Right = right, Values = values });
            cursor.Expect(0x0A);
        }

        int finalCount = header[4];
        if (finalCount > data.Length / 8)
            throw new InvalidDataException($"{path}: {finalCount} final records in {data.Length} bytes");
        var final = new List<(int, int, int)>(finalCount);
        for (int i = 0; i < finalCount; i++)
        {
            final.Add((cursor.I32(), cursor.I32(), cursor.I32()));
            cursor.Expect(0x0A);
        }

        if (cursor.Position != data.Length)
            throw new InvalidDataException(
                $"{path}: parser stopped at 0x{cursor.Position:X}, file size is 0x{data.Length:X}");

        var groups = new List<InfGroup>(groupCount);
        foreach (var (name, declared) in capacities)
        {
            var items = byName[name];
            if (items.Count > declared)
                throw new InvalidDataException(
                    $"{path}: group '{name}' has {items.Count} items but declares capacity {declared}");
            groups.Add(new InfGroup { Name = name, DeclaredCount = declared, Items = items });
        }

        int sum = 0;
        foreach (var group in groups)
            sum += group.Items.Count;
        if (sum != itemCount)
            throw new InvalidDataException($"{path}: parsed group counts do not sum to {itemCount}");

        return new InfFile
        {
            Version = header[0],
            ItemCount = header[1],
            MgcOrder = header[2],
            BapOrder = header[3],
            FinalCount = header[4],
            GroupCount = header[5],
            Groups = groups,
            Final = final,
        };
    }

    /// <summary>
    /// MUTA 1.0 / 0.2.alpha index: a 10-int header and 9-field records whose name is the
    /// unit's context key ("b_a", "a-b", "a") rather than a phone name.
    /// </summary>
    static InfFile ParseV1(byte[] data, string path)
    {
        var cursor = new Cursor(data, path);

        var headerBytes = cursor.Fixed(40);
        cursor.Expect(0x0A);

        Span<int> header = stackalloc int[6];
        for (int i = 0; i < 6; i++)
            header[i] = BinaryPrimitives.ReadInt32LittleEndian(headerBytes.AsSpan(i * 4, 4));
        for (int i = 0; i < 6; i++)
            if (header[i] < 0)
                throw new InvalidDataException($"{path}: negative header field");

        int groupCount = header[5];
        if (groupCount > data.Length / 8)
            throw new InvalidDataException($"{path}: {groupCount} groups in {data.Length} bytes");

        var capacities = new List<(string Name, int Declared)>(groupCount);
        for (int i = 0; i < groupCount; i++)
            capacities.Add((cursor.Text(), cursor.I32()));
        cursor.Expect(0x0A);

        var byName = new Dictionary<string, List<InfItem>>(StringComparer.Ordinal);
        foreach (var (name, _) in capacities)
            byName[name] = [];

        int itemCount = header[1];
        if (itemCount > data.Length / 16)
            throw new InvalidDataException($"{path}: {itemCount} items in {data.Length} bytes");
        if (header[2] <= 0 || header[2] > 512 || header[3] <= 0 || header[3] > 512)
            throw new InvalidDataException(
                $"{path}: implausible orders mgc={header[2]} bap={header[3]}");

        int skipped = 0;
        for (int i = 0; i < itemCount; i++)
        {
            string key = cursor.Text();
            var raw = cursor.Fixed(9 * 4);
            cursor.Expect(0x0A);
            if (!byName.TryGetValue(key, out var bucket))
                throw new InvalidDataException($"{path}: unknown group '{key}' at item {i}");
            if (MakeV1Item(key, raw) is { } item)
                bucket.Add(item);
            else
                skipped++;
        }

        int finalCount = header[4];
        if (finalCount > data.Length / 8)
            throw new InvalidDataException($"{path}: {finalCount} final records in {data.Length} bytes");
        var final = new List<(int, int, int)>(finalCount);
        for (int i = 0; i < finalCount; i++)
        {
            final.Add((cursor.I32(), cursor.I32(), cursor.I32()));
            cursor.Expect(0x0A);
        }

        if (cursor.Position != data.Length)
            throw new InvalidDataException(
                $"{path}: parser stopped at 0x{cursor.Position:X}, file size is 0x{data.Length:X}");

        var groups = new List<InfGroup>(groupCount);
        foreach (var (name, declared) in capacities)
        {
            var items = byName[name];
            if (items.Count > declared)
                throw new InvalidDataException(
                    $"{path}: group '{name}' has {items.Count} items but declares capacity {declared}");
            groups.Add(new InfGroup { Name = name, DeclaredCount = declared, Items = items });
        }

        int sum = 0;
        foreach (var group in groups)
            sum += group.Items.Count;
        if (sum + skipped != itemCount)
            throw new InvalidDataException($"{path}: parsed group counts do not sum to {itemCount}");

        return new InfFile
        {
            Version = header[0],
            ItemCount = header[1],
            MgcOrder = header[2],
            BapOrder = header[3],
            FinalCount = header[4],
            GroupCount = header[5],
            Groups = groups,
            Final = final,
        };
    }

    /// <summary>
    /// Turns one 1.0 record into the eleven-field view the rest of the plugin reads.
    ///
    /// The old index has no prefix/core/suffix split, only a "first segment" length. What
    /// that segment is follows from how the unit is named:
    /// <list type="bullet">
    /// <item><c>initial_nucleus</c> (b_a): a syllable; the first segment is the consonant.</item>
    /// <item><c>final-initial</c> (a-b): a boundary; the first segment is the previous vowel.</item>
    /// <item>anything else (a, iang): a bare final; the whole unit is the vowel.</item>
    /// </list>
    /// A segment is never allowed to collapse to zero frames, or the stretcher would divide
    /// by an empty source. Returns null for the empty record some 1.0 banks carry (zh_o in
    /// the shipped Yanxi index).
    /// </summary>
    static InfItem? MakeV1Item(string key, ReadOnlySpan<byte> raw)
    {
        Span<int> v = stackalloc int[9];
        for (int i = 0; i < 9; i++)
            v[i] = BinaryPrimitives.ReadInt32LittleEndian(raw.Slice(i * 4, 4));

        int frames = v[3];
        if (frames <= 0)
            return null;

        int first = Math.Clamp(v[4], 0, frames);
        int prefix, core, suffix;
        if (key.Contains('_'))
        {
            core = Math.Clamp(first, 1, frames);
            prefix = 0;
            suffix = frames - core;
        }
        else if (key.Contains('-'))
        {
            core = Math.Clamp(frames - first, 1, frames);
            prefix = frames - core;
            suffix = 0;
        }
        else
        {
            core = frames;
            prefix = 0;
            suffix = 0;
        }

        return new InfItem
        {
            Current = key,
            Left = string.Empty,
            Right = string.Empty,
            Values =
            [
                v[0], v[1],
                frames, prefix, core, suffix,
                v[2], v[5], v[6],
                v[7], v[8],
            ],
        };
    }

    /// <summary>
    /// 校验每条记录的 bank 区间与 .bnk 文件对得上（长度、越界）。
    /// 返回每条记录期望的字节布局：MGC[frames × mgc_order] 紧跟 BAP[frames × bap_order]。
    /// </summary>
    public void ValidateBank(long bankSize)
    {
        long vectorBytes = ((long)MgcOrder + BapOrder) * 4;
        foreach (var group in Groups)
        {
            foreach (var item in group.Items)
            {
                long expected = (long)item.FrameCount * vectorBytes;
                if (item.BankSize != expected)
                    throw new InvalidDataException(
                        $"{item.Current}/{item.Left}/{item.Right}: bank size {item.BankSize} != {expected}");
                if (item.BankOffset < 0 || item.BankOffset + (long)item.BankSize > bankSize)
                    throw new InvalidDataException(
                        $"{item.Current}/{item.Left}/{item.Right}: bank range " +
                        $"{item.BankOffset}+{item.BankSize} exceeds {bankSize}");
            }
        }
    }

    /// <summary>行式字节游标：token 会折叠连续分隔符，且只吃掉一个尾分隔符。</summary>
    sealed class Cursor
    {
        readonly byte[] mData;
        readonly string mPath;
        public int Position { get; private set; }

        public Cursor(byte[] data, string path)
        {
            mData = data;
            mPath = path;
        }

        public byte[] Token(byte separator)
        {
            while (Position < mData.Length && mData[Position] == separator)
                Position++;
            int start = Position;
            while (Position < mData.Length && mData[Position] != separator)
                Position++;
            var value = mData.AsSpan(start, Position - start).ToArray();
            if (Position < mData.Length)
                Position++;
            return value;
        }

        /// <summary>A fixed-size run with no separator, used for field blocks.</summary>
        public byte[] Fixed(int count)
        {
            if (Position + count > mData.Length)
                throw new InvalidDataException(
                    $"{mPath}: truncated {count}-byte field at 0x{Position:X}");
            var value = mData.AsSpan(Position, count).ToArray();
            Position += count;
            return value;
        }

        public string Text()
        {
            var raw = Token(0x20);
            int nul = Array.IndexOf(raw, (byte)0);
            if (nul >= 0)
                Array.Resize(ref raw, nul);
            foreach (byte b in raw)
                if (b >= 0x80)
                    throw new InvalidDataException($"{mPath}: non-ASCII byte 0x{b:X2} in text field");
            return Encoding.ASCII.GetString(raw);
        }

        public int I32()
        {
            if (Position + 4 > mData.Length)
                throw new InvalidDataException($"{mPath}: truncated i32 at 0x{Position:X}");
            int value = BinaryPrimitives.ReadInt32LittleEndian(mData.AsSpan(Position, 4));
            Position += 4;
            return value;
        }

        public void Expect(byte value)
        {
            if (Position >= mData.Length)
                throw new InvalidDataException($"{mPath}: expected 0x{value:X2} at end of file");
            byte actual = mData[Position];
            if (actual != value)
                throw new InvalidDataException(
                    $"{mPath}: expected 0x{value:X2} at 0x{Position:X}, found 0x{actual:X2}");
            Position++;
        }
    }
}
