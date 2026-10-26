using System;
using System.Collections.Generic;
using MutaForTuneLab.Assets;

namespace MutaForTuneLab.Synthesis;

/// <summary>
/// Bank context classification and selection.
///
/// Two different selectors sit behind the same question ("which recorded unit fits
/// this phone"), and they score differently: the consonant selector compares the left
/// phone by class and the right phone by name, the vowel selector compares both by
/// class. Both are weighted integer costs and both take the first minimum in .inf
/// order — a tie must not silently pick a different unit.
/// </summary>
public static class BankContext
{
    public const int LeftPhoneWeight = 1000;
    public const int RightPhoneWeight = 1000;
    public const int LeftClassWeight = 100;
    public const int RightClassWeight = 100;
    public const int PitchWeight = 20;
    public const int DurationWeight = 1;

    /// <summary>Hertz to the bank's pitch code (the code is MIDI − 12).</summary>
    public static int PitchCodeFor(double targetPitch)
        => (int)Math.Round(57.0 + 12.0 * Math.Log2(Math.Max(1.0, targetPitch) / 440.0),
                           MidpointRounding.ToEven);

    /// <summary>General left-context class.</summary>
    public static int LeftClass(string phone) => phone switch
    {
        "sil" or "sp" => 0,
        _ when StartsWithAny(phone, "ga", "go", "ge", "ger") || phone.StartsWith('w') || phone.StartsWith('y') => 1,
        _ when StartsWithAny(phone, "m", "n", "l", "r") => 2,
        _ when StartsWithAny(phone, "b", "p", "d", "t", "g", "k") => 3,
        _ when StartsWithAny(phone, "f", "h", "j", "q", "x") => 4,
        _ when StartsWithAny(phone, "zh", "ch", "sh", "z", "c", "s") => 5,
        _ => -1,
    };

    /// <summary>General right-context (final) class.</summary>
    public static int RightClass(string phone) => phone switch
    {
        "sil" or "sp" => 0,
        _ when phone.StartsWith('a') => 1,
        _ when StartsWithAny(phone, "ou", "ong") => 8,
        _ when phone.StartsWith('o') => 2,
        _ when StartsWithAny(phone, "ei") => 5,
        _ when phone.StartsWith('e') => 3,
        _ when StartsWithAny(phone, "if") => 9,
        _ when StartsWithAny(phone, "ib") => 10,
        _ when phone.StartsWith('i') => 4,
        _ when phone.StartsWith('u') => 6,
        _ when phone.StartsWith('v') => 7,
        _ => -1,
    };

    /// <summary>
    /// Left class used by the consonant selector. The two branches after the "last
    /// letter is n" test are unreachable in the original control flow; they are kept
    /// so the code reads like the specification rather than like a cleanup.
    /// </summary>
    public static int FirstLeftClass(string phone)
    {
        if (phone is "sil" or "sp")
            return 0;
        if (string.IsNullOrEmpty(phone))
            return -1;

        char last = phone[^1];
        if (last == 'a')
            return 1;
        if (last != 'n')
            return 12;
        if (last == 'g')
            return 13;
        // The order below is the specification. In particular the "last letter"
        // tests come before the compound-final names, which is why "iu" is class 7
        // and not class 11.
        if (phone is "o" or "uo")
            return 2;
        if (phone == "er")
            return 14;
        if (phone is "ei" or "ui")
            return 9;
        if (phone == "e")
            return 3;
        if (last == 'i')
            return 4;
        if (phone == "ib")
            return 5;
        if (phone == "if")
            return 6;
        if (last == 'u')
            return 7;
        if (last == 'v')
            return 8;
        if (phone is "ie" or "ve")
            return 10;
        if (phone is "ao" or "ou" or "iu" or "iao")
            return 11;
        return -1;
    }

    /// <summary>The consonant selector scores the right phone by name, not class.</summary>
    public static bool FirstLeftPhoneMismatch(string actual, string expected)
        => expected is "sil" or "sp" ? actual is not ("sil" or "sp") : actual != expected;

    public static bool PositiveRightPhoneMismatch(string actual, string expected)
        => expected is "sil" or "sp" ? actual is not ("sil" or "sp") : actual != expected;

    static int PitchCost(int targetCode, InfItem item) => Math.Abs(targetCode - item.PitchCode) * PitchWeight;

    /// <summary>Consonant (slot class 0) selection.</summary>
    public static InfItem? ChooseFirstItem(
        IReadOnlyList<InfItem> items, string previousPhone, string nextPhone, double targetPitch)
    {
        if (items.Count == 0)
            return null;
        if (items.Count == 1)
            return items[0];

        int targetCode = PitchCodeFor(targetPitch);
        int expectedLeft = FirstLeftClass(previousPhone);
        int expectedRight = RightClass(nextPhone);

        int Score(InfItem item)
        {
            int cost = 0;
            if (FirstLeftPhoneMismatch(item.Left, previousPhone))
                cost += LeftPhoneWeight;
            if (item.Right != nextPhone)
                cost += RightPhoneWeight;
            if (item.Values[0] != expectedLeft)
                cost += LeftClassWeight;
            if (item.Values[1] != expectedRight)
                cost += RightClassWeight;
            cost += PitchCost(targetCode, item);
            return cost;
        }

        InfItem best = items[0];
        int bestCost = Score(best);
        for (int i = 1; i < items.Count; i++)
        {
            int cost = Score(items[i]);
            if (cost < bestCost)   // strictly less: ties keep the earlier unit
            {
                best = items[i];
                bestCost = cost;
            }
        }
        return best;
    }

    /// <summary>Vowel (positive slot class) selection; it also scores duration.</summary>
    public static InfItem? ChoosePositiveItem(
        IReadOnlyList<InfItem> items, string previousPhone, string nextPhone,
        int targetCore, double targetPitch)
    {
        if (items.Count == 0)
            return null;
        if (items.Count == 1)
            return items[0];

        int expectedLeft = LeftClass(previousPhone);
        // Both contexts go through the general left classifier here; the final-vowel
        // classifier belongs only to the consonant path.
        int expectedRight = LeftClass(nextPhone);
        if (expectedLeft == -1 || expectedRight == -1)
            return null;

        int targetCode = PitchCodeFor(targetPitch);

        int Score(InfItem item)
        {
            int cost = 0;
            if (item.Left != previousPhone)
                cost += LeftPhoneWeight;
            if (PositiveRightPhoneMismatch(item.Right, nextPhone))
                cost += RightPhoneWeight;
            if (item.Values[0] != expectedLeft)
                cost += LeftClassWeight;
            if (item.Values[1] != expectedRight)
                cost += RightClassWeight;
            cost += PitchCost(targetCode, item);
            cost += Math.Abs(targetCore - item.CoreFrames) * DurationWeight;
            return cost;
        }

        InfItem best = items[0];
        int bestCost = Score(best);
        for (int i = 1; i < items.Count; i++)
        {
            int cost = Score(items[i]);
            if (cost < bestCost)
            {
                best = items[i];
                bestCost = cost;
            }
        }
        return best;
    }

    /// <summary>Dispatch by slot class. Breaks and unknown phones select nothing.</summary>
    public static InfItem? SelectNoteItem(
        IReadOnlyDictionary<string, List<InfItem>> groups, string phone,
        string previousPhone, string nextPhone, int targetCore, double targetPitch, int slotClass)
    {
        if (slotClass < 0 || !groups.TryGetValue(phone, out var items) || items.Count == 0)
            return null;
        return slotClass == 0
            ? ChooseFirstItem(items, previousPhone, nextPhone, targetPitch)
            : ChoosePositiveItem(items, previousPhone, nextPhone, targetCore, targetPitch);
    }

    static bool StartsWithAny(string phone, params string[] prefixes)
    {
        foreach (var prefix in prefixes)
            if (phone.StartsWith(prefix, StringComparison.Ordinal))
                return true;
        return false;
    }
}
