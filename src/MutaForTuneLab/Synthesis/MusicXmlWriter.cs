using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace MutaForTuneLab.Synthesis;

/// <summary>A note for the Japanese chain: seconds, pitch, and the lyric as written.</summary>
public readonly record struct ScoreNote(double Start, double Duration, int Midi, string Lyric);

/// <summary>
/// Writes the note list as MusicXML, which is the only score format Sinsy reads.
///
/// The grid is fixed (480 divisions per quarter, tempo 120) rather than following the
/// project: labels come out in frames, and the chain converts seconds on its own. What
/// matters is that the ratio between notes survives, and a fixed grid keeps that
/// deterministic.
///
/// Two rules are not cosmetic, they are what makes Sinsy keep every note:
///
///   * every measure is exactly full. A measure whose notes add up to more than its time
///     signature is "repaired" by Sinsy, which drops the overflow -- measured: four one
///     second notes written into one 4/4 measure came back as two notes;
///   * a note never crosses a barline. The measure length is chosen from the longest note
///     in the block, so no tie is needed and no note is cut.
///
/// A rest opens the score and another closes it: Sinsy warns without them and the first
/// and last phones lose their context.
/// </summary>
public static class MusicXmlWriter
{
    public const int Divisions = 480;
    public const double Tempo = 120.0;

    /// <summary>How much silence the score puts before the first note, in seconds.</summary>
    public const double LeadSeconds = 0.5;

    static readonly string[] Steps = { "C", "C", "D", "D", "E", "F", "F", "G", "G", "A", "A", "B" };
    static readonly int[] Alters = { 0, 1, 0, 1, 0, 0, 1, 0, 1, 0, 1, 0 };

    public static string Write(IReadOnlyList<ScoreNote> notes, double totalSeconds)
    {
        double beat = 60.0 / Tempo;
        long ToTicks(double seconds) => (long)Math.Round(seconds / beat * Divisions);

        // One measure holds the whole block. A note must never cross a barline: the splitter
        // can cut a rest anywhere, but cutting a note produces two notes with the same lyric,
        // and Sinsy sings it twice -- measured on a two-note phrase (ka, ki): six phones
        // instead of four, ka ki ki.
        double span = LeadSeconds + Math.Max(0.0, totalSeconds);
        int beatsPerMeasure = Math.Max(4, (int)Math.Ceiling(span / beat) + 1);
        long measureTicks = (long)beatsPerMeasure * Divisions;

        var events = new List<Event>();
        long cursor = ToTicks(LeadSeconds);
        // The score has to open with a rest: Sinsy warns otherwise and the first phone loses
        // its left context.
        events.Add(new Event(cursor, true, default));
        foreach (var note in notes)
        {
            long start = ToTicks(LeadSeconds + note.Start);
            if (start > cursor)
                events.Add(new Event(start - cursor, true, default));
            long end = Math.Max(start + 1, ToTicks(LeadSeconds + note.Start + note.Duration));
            events.Add(new Event(end - Math.Max(cursor, start), false, note));
            cursor = Math.Max(cursor, end);
        }

        // The score covers the block even when the last note ends before it does.
        long wanted = ToTicks(LeadSeconds + Math.Max(0.0, totalSeconds));
        long remaining = Math.Max(measureTicks, wanted - cursor);
        long fill = remaining % measureTicks;
        events.Add(new Event(remaining + (fill == 0 ? 0 : measureTicks - fill), true, default));

        var measures = Split(events, measureTicks);

        var builder = new StringBuilder();
        builder.AppendLine("<?xml version=\"1.0\" encoding=\"UTF-8\"?>");
        builder.AppendLine("<!DOCTYPE score-partwise PUBLIC \"-//Recordare//DTD MusicXML 2.0 Partwise//EN\" \"http://www.musicxml.org/dtds/partwise.dtd\">");
        builder.AppendLine("<score-partwise version=\"2.0\" xml:lang=\"ja\">");
        builder.AppendLine("  <part-list>");
        builder.AppendLine("    <score-part id=\"P1\"><part-name>Voice</part-name></score-part>");
        builder.AppendLine("  </part-list>");
        builder.AppendLine("  <part id=\"P1\">");

        for (int index = 0; index < measures.Count; index++)
        {
            builder.AppendLine($"    <measure number=\"{index + 1}\">");
            if (index == 0)
            {
                builder.AppendLine("      <attributes>");
                builder.AppendLine($"        <divisions>{Divisions}</divisions>");
                builder.AppendLine("        <key><fifths>0</fifths></key>");
                builder.AppendLine($"        <time><beats>{beatsPerMeasure}</beats><beat-type>4</beat-type></time>");
                builder.AppendLine("        <clef><sign>G</sign><line>2</line></clef>");
                builder.AppendLine("      </attributes>");
                builder.AppendLine($"      <direction><sound tempo=\"{Tempo.ToString(CultureInfo.InvariantCulture)}\"/></direction>");
            }

            foreach (var item in measures[index])
            {
                if (item.Ticks <= 0)
                    continue;
                if (item.IsRest)
                {
                    AppendRest(builder, item.Ticks);
                    continue;
                }
                AppendNote(builder, item.Note, item.Ticks);
            }
            builder.AppendLine("    </measure>");
        }

        builder.AppendLine("  </part>");
        builder.AppendLine("</score-partwise>");
        return builder.ToString();
    }

    readonly record struct Event(long Ticks, bool IsRest, ScoreNote Note);

    /// <summary>Cuts the event stream at every barline; a rest may be split, a note may not.</summary>
    static List<List<Event>> Split(List<Event> events, long measureTicks)
    {
        var measures = new List<List<Event>>();
        var current = new List<Event>();
        long used = 0;

        foreach (var item in events)
        {
            long remaining = item.Ticks;
            while (remaining > 0)
            {
                long room = measureTicks - used;
                long take = Math.Min(room, remaining);
                current.Add(item with { Ticks = take });
                used += take;
                remaining -= take;
                if (used == measureTicks)
                {
                    measures.Add(current);
                    current = new List<Event>();
                    used = 0;
                }
            }
        }

        if (current.Count > 0)
        {
            current.Add(new Event(measureTicks - used, true, default));
            measures.Add(current);
        }
        return measures;
    }

    static void AppendRest(StringBuilder builder, long ticks)
    {
        builder.Append("      <note><rest/>");
        builder.Append("<duration>").Append(ticks).Append("</duration>");
        AppendType(builder, ticks);
        builder.AppendLine("</note>");
    }

    static void AppendNote(StringBuilder builder, ScoreNote note, long ticks)
    {
        int midi = Math.Clamp(note.Midi, 0, 127);
        var (step, alter, octave) = Spell(midi);
        builder.AppendLine("      <note>");
        builder.Append("        <pitch><step>").Append(step).Append("</step>");
        if (alter != 0)
            builder.Append("<alter>").Append(alter).Append("</alter>");
        builder.Append("<octave>").Append(octave).Append("</octave></pitch>");
        builder.AppendLine();
        builder.Append("        <duration>").Append(ticks).Append("</duration>");
        AppendType(builder, ticks);
        builder.AppendLine();
        builder.AppendLine("        <lyric number=\"1\">");
        builder.AppendLine("          <syllabic>single</syllabic>");
        builder.Append("          <text>").Append(Escape(note.Lyric)).Append("</text>");
        builder.AppendLine();
        builder.AppendLine("        </lyric>");
        builder.AppendLine("      </note>");
    }

    /// <summary>Note type from the duration, when it is a plain note value.</summary>
    static void AppendType(StringBuilder builder, long ticks)
    {
        string? type = ticks switch
        {
            _ when ticks % 1920 == 0 && ticks / 1920 == 1 => "whole",
            _ when ticks % 960 == 0 && ticks / 960 == 1 => "half",
            _ when ticks % 480 == 0 && ticks / 480 == 1 => "quarter",
            _ when ticks % 240 == 0 && ticks / 240 == 1 => "eighth",
            _ when ticks % 120 == 0 && ticks / 120 == 1 => "16th",
            _ => null,
        };
        if (type is not null)
            builder.Append("<type>").Append(type).Append("</type>");
    }

    static (string Step, int Alter, int Octave) Spell(int midi)
    {
        int index = midi % 12;
        int octave = midi / 12 - 1;
        return (Steps[index], Alters[index], octave);
    }

    static string Escape(string text)
    {
        if (string.IsNullOrEmpty(text))
            return "a";
        return text.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");
    }
}
