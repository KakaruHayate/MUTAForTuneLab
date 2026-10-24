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
/// The grid is fixed (divisions per quarter, tempo 120) rather than following the project:
/// labels come out in frames, and the chain converts seconds on its own. What matters is
/// that the ratio between notes survives, and a fixed grid keeps that deterministic.
///
/// Sinsy wants the phrase to begin and end with a rest; without one it warns and the first
/// and last phonemes lose their context, so both are added here.
/// </summary>
public static class MusicXmlWriter
{
    public const int Divisions = 480;
    public const double Tempo = 120.0;

    static readonly string[] Steps = { "C", "C", "D", "D", "E", "F", "F", "G", "G", "A", "A", "B" };
    static readonly int[] Alters = { 0, 1, 0, 1, 0, 0, 1, 0, 1, 0, 1, 0 };

    /// <summary>Whole rests of a quarter note on each side, in divisions.</summary>
    const int PadDivisions = Divisions;

    public static string Write(IReadOnlyList<ScoreNote> notes, double totalSeconds)
    {
        var builder = new StringBuilder();
        builder.AppendLine("<?xml version=\"1.0\" encoding=\"UTF-8\"?>");
        builder.AppendLine("<!DOCTYPE score-partwise PUBLIC \"-//Recordare//DTD MusicXML 2.0 Partwise//EN\" \"http://www.musicxml.org/dtds/partwise.dtd\">");
        builder.AppendLine("<score-partwise version=\"2.0\">");
        builder.AppendLine("  <part-list>");
        builder.AppendLine("    <score-part id=\"P1\"><part-name>Voice</part-name></score-part>");
        builder.AppendLine("  </part-list>");
        builder.AppendLine("  <part id=\"P1\">");
        builder.AppendLine("    <measure number=\"1\">");
        builder.AppendLine("      <attributes>");
        builder.AppendLine($"        <divisions>{Divisions}</divisions>");
        builder.AppendLine("        <key><fifths>0</fifths></key>");
        builder.AppendLine("        <time><beats>4</beats><beat-type>4</beat-type></time>");
        builder.AppendLine("        <clef><sign>G</sign><line>2</line></clef>");
        builder.AppendLine("      </attributes>");
        builder.AppendLine($"      <sound tempo=\"{Tempo.ToString(CultureInfo.InvariantCulture)}\"/>");

        AppendRest(builder, PadDivisions);

        double cursor = 0.0;
        foreach (var note in notes)
        {
            // A gap is written as a rest so the following note lands where it should.
            double gap = note.Start - cursor;
            int gapDivisions = SecondsToDivisions(gap);
            if (gapDivisions > 0)
                AppendRest(builder, gapDivisions);

            int divisions = Math.Max(1, SecondsToDivisions(note.Duration));
            int midi = Math.Clamp(note.Midi, 0, 127);
            var (step, alter, octave) = Spell(midi);
            builder.AppendLine("      <note>");
            builder.Append("        <pitch><step>").Append(step).Append("</step>");
            if (alter != 0)
                builder.Append("<alter>").Append(alter).Append("</alter>");
            builder.Append("<octave>").Append(octave).Append("</octave></pitch>");
            builder.AppendLine();
            builder.Append("        <duration>").Append(divisions).Append("</duration>");
            builder.AppendLine();
            builder.AppendLine("        <type>quarter</type>");
            builder.AppendLine("        <lyric number=\"1\">");
            builder.AppendLine("          <syllabic>single</syllabic>");
            builder.Append("          <text>").Append(Escape(note.Lyric)).Append("</text>");
            builder.AppendLine();
            builder.AppendLine("        </lyric>");
            builder.AppendLine("      </note>");

            cursor = Math.Max(cursor, note.Start + note.Duration);
        }

        // Trailing rest: whatever is left, with a floor of one beat.
        int tail = SecondsToDivisions(Math.Max(0.0, totalSeconds - cursor));
        AppendRest(builder, Math.Max(PadDivisions, tail));

        builder.AppendLine("    </measure>");
        builder.AppendLine("  </part>");
        builder.AppendLine("</score-partwise>");
        return builder.ToString();
    }

    static void AppendRest(StringBuilder builder, int divisions)
    {
        builder.Append("      <note><rest/>");
        builder.Append("<duration>").Append(divisions).Append("</duration>");
        builder.AppendLine("<type>quarter</type></note>");
    }

    static int SecondsToDivisions(double seconds)
    {
        if (seconds <= 0.0)
            return 0;
        double quarters = seconds / (60.0 / Tempo);
        return (int)Math.Round(quarters * Divisions);
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
