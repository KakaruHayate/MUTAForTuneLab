using System;
using System.Collections.Generic;
using MutaForTuneLab.Assets;

namespace MutaForTuneLab.Synthesis;

/// <summary>
/// Turns a parsed <c>.inf</c> into the phone-keyed groups the assembly loop asks for.
///
/// The two index revisions answer the same question differently:
///
/// <list type="bullet">
/// <item><b>2.0</b> records a phone name per row plus the names of its left and right
/// neighbours, so the group key is simply the phone and the row already describes its own
/// context. Several groups can share a name across the file, hence the merge.</item>
/// <item><b>1.0</b> has one name per row and that name <i>is</i> the context: a recorded
/// syllable is <c>initial_nucleus</c> (<c>b_a</c>), a recording of a boundary is
/// <c>final-initial</c> (<c>a-b</c>), and a sustained vowel is the final alone (<c>a</c>).
/// The rows carry no left/right names at all, so this method puts them back before the
/// selector sees them.</item>
/// </list>
///
/// The renderer wants two slots per note: a consonant that runs into the vowel, and the
/// vowel body. The 1.0 bank supplies exactly those two halves — the consonant half from
/// <c>initial_nucleus</c> (its first segment is the consonant, the rest is the vowel onset)
/// or from a <c>final-initial</c> boundary, and the body from the bare final, which is a
/// long recording of that final at many pitches.
/// </summary>
public static class BankLayout
{
    /// <summary>Groups for whichever index revision the file turned out to be.</summary>
    public static IReadOnlyDictionary<string, List<InfItem>> Build(InfFile inf)
        => inf.Version < 200 ? BuildV1(inf) : BuildV2(inf);

    static Dictionary<string, List<InfItem>> BuildV2(InfFile inf)
    {
        var groups = new Dictionary<string, List<InfItem>>(StringComparer.Ordinal);
        foreach (var group in inf.Groups)
        {
            if (groups.TryGetValue(group.Name, out var existing))
                existing.AddRange(group.Items);
            else
                groups[group.Name] = new List<InfItem>(group.Items);
        }
        return groups;
    }

    // A nasal coda is a unit of its own in the 1.0 index ("n-r", "g-b"), but the label front
    // end names the whole final ("an", "ang"). Alias the coda units so a syllable that ends
    // in one still finds its transition.
    static readonly string[] FinalsEndingInN = ["an", "en", "in", "un", "vn", "ian", "uan"];
    static readonly string[] FinalsEndingInG = ["ang", "eng", "ing", "ong", "iang", "uang", "iong", "ueng"];

    // Finals whose right-context class differs from their nucleus' class: the selector's
    // class test would then never match the recorded onset. Register the same unit once more
    // under each of those finals so an exact match exists.
    static readonly Dictionary<string, string[]> NucleusAlternates = new(StringComparer.Ordinal)
    {
        ["i"] = ["ib", "if"],
        ["o"] = ["ou", "ong"],
    };

    static Dictionary<string, List<InfItem>> BuildV1(InfFile inf)
    {
        var groups = new Dictionary<string, List<InfItem>>(StringComparer.Ordinal);
        foreach (var group in inf.Groups)
        {
            foreach (var item in group.Items)
            {
                string key = item.Current;
                int separator = key.IndexOfAny(['_', '-']);
                if (separator <= 0 || separator >= key.Length - 1)
                {
                    // A bare final: the sustained vowel.
                    Add(groups, key, WithContext(item, "sil", "sil", consonant: false));
                    continue;
                }

                string head = key[..separator];
                string tail = key[(separator + 1)..];
                if (key[separator] == '_')
                {
                    // initial_nucleus: a syllable; the consonant runs into that nucleus.
                    Add(groups, head, WithContext(item, "sil", tail, consonant: true));
                    if (NucleusAlternates.TryGetValue(tail, out var alternates))
                        foreach (string final in alternates)
                            Add(groups, head, WithContext(item, "sil", final, consonant: true));
                }
                else
                {
                    // final-initial: a boundary; the same recording serves every final that
                    // ends in the same coda when the head is a bare coda letter.
                    foreach (string left in FinalAliases(head))
                        Add(groups, tail, WithContext(item, left, string.Empty, consonant: true));
                }
            }
        }
        return groups;
    }

    static IEnumerable<string> FinalAliases(string head)
    {
        yield return head;
        if (head == "n")
        {
            foreach (string final in FinalsEndingInN)
                yield return final;
        }
        else if (head == "g")
        {
            foreach (string final in FinalsEndingInG)
                yield return final;
        }
    }

    static void Add(Dictionary<string, List<InfItem>> groups, string phone, InfItem item)
    {
        if (!groups.TryGetValue(phone, out var list))
            groups[phone] = list = [];
        list.Add(item);
    }

    /// <summary>
    /// A copy of the row with the context it will be scored under. The class fields of a
    /// 1.0 record are MUTA's own codes, which use a different scale from the ones the
    /// selector computes; replacing them keeps a mixed bank comparable.
    /// </summary>
    static InfItem WithContext(InfItem source, string left, string right, bool consonant)
    {
        var values = (int[])source.Values.Clone();
        values[0] = consonant ? BankContext.FirstLeftClass(left) : BankContext.LeftClass(left);
        values[1] = consonant ? BankContext.RightClass(right) : BankContext.LeftClass(right);
        return new InfItem { Current = source.Current, Left = left, Right = right, Values = values };
    }
}
