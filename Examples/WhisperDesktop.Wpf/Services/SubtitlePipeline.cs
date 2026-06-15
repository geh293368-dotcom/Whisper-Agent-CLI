using System.Text;
using System.Text.RegularExpressions;

namespace WhisperDesktop.Modern.Services;

public readonly record struct SourceSegment(TimeSpan Begin, TimeSpan End, string Text);
public readonly record struct SubtitleCue(TimeSpan Begin, TimeSpan End, string Text);

public sealed record SubtitleOptions(
    int MaxCharactersPerLine = 20,
    int MaxLines = 2,
    int MinimumDurationMs = 800,
    int MaximumDurationMs = 7000,
    int MinimumGapMs = 20);

public static partial class SubtitlePipeline
{
    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();

    private static string GetComparisonText(string text)
    {
        var sb = new StringBuilder();
        foreach (char c in text)
        {
            if (char.IsLetterOrDigit(c))
                sb.Append(char.ToLowerInvariant(c));
        }
        return sb.ToString();
    }

    public static IReadOnlyList<SubtitleCue> Build(
        IEnumerable<SourceSegment> source,
        SubtitleOptions? options = null)
    {
        options ??= new SubtitleOptions();
        var result = new List<SubtitleCue>();
        var previousEnd = TimeSpan.Zero - TimeSpan.FromMilliseconds(options.MinimumGapMs);
        int maxCueCharacters = options.MaxCharactersPerLine * Math.Max(1, options.MaxLines);

        var mergedSource = new List<SourceSegment>();
        foreach (SourceSegment segment in source)
        {
            string text = Normalize(segment.Text);
            if (text.Length == 0)
                continue;

            TimeSpan begin = segment.Begin < TimeSpan.Zero ? TimeSpan.Zero : segment.Begin;
            TimeSpan end = segment.End > begin
                ? segment.End
                : begin + TimeSpan.FromMilliseconds(options.MinimumDurationMs);

            if (mergedSource.Count > 0)
            {
                var last = mergedSource[mergedSource.Count - 1];
                if (GetComparisonText(last.Text) == GetComparisonText(segment.Text))
                {
                    mergedSource[mergedSource.Count - 1] = new SourceSegment(last.Begin, end > last.Begin ? end : last.End, last.Text);
                    continue;
                }
            }
            mergedSource.Add(new SourceSegment(begin, end, segment.Text));
        }

        foreach (SourceSegment segment in mergedSource)
        {
            string text = Normalize(segment.Text);
            if (text.Length == 0)
                continue;

            List<string> chunks = Split(text, maxCueCharacters);
            TimeSpan sourceBegin = segment.Begin;
            TimeSpan sourceEnd = segment.End;
            double durationMs = (sourceEnd - sourceBegin).TotalMilliseconds;
            int totalCharacters = chunks.Sum(c => Math.Max(1, c.EnumerateRunes().Count()));
            int consumed = 0;

            foreach (string chunk in chunks)
            {
                int characters = Math.Max(1, chunk.EnumerateRunes().Count());
                var begin = sourceBegin + TimeSpan.FromMilliseconds(durationMs * consumed / totalCharacters);
                consumed += characters;
                var end = sourceBegin + TimeSpan.FromMilliseconds(durationMs * consumed / totalCharacters);
                var earliestBegin = previousEnd + TimeSpan.FromMilliseconds(options.MinimumGapMs);
                if (begin < earliestBegin)
                    begin = earliestBegin;
                var minimumEnd = begin + TimeSpan.FromMilliseconds(options.MinimumDurationMs);
                if (end < minimumEnd)
                    end = minimumEnd;
                var maximumEnd = begin + TimeSpan.FromMilliseconds(options.MaximumDurationMs);
                if (end > maximumEnd)
                    end = maximumEnd;

                result.Add(new SubtitleCue(begin, end, Wrap(chunk, options.MaxCharactersPerLine)));
                previousEnd = end;
            }
        }
        return result;
    }

    public static string RenderText(IReadOnlyList<SubtitleCue> cues, bool timestamps)
    {
        var builder = new StringBuilder();
        foreach (SubtitleCue cue in cues)
        {
            if (timestamps)
                builder.Append('[').Append(Format(cue.Begin, '.')).Append(" --> ")
                    .Append(Format(cue.End, '.')).Append("]  ");
            builder.AppendLine(cue.Text);
        }
        return builder.ToString();
    }

    public static string RenderSubRip(IReadOnlyList<SubtitleCue> cues)
    {
        var builder = new StringBuilder();
        for (int i = 0; i < cues.Count; i++)
        {
            SubtitleCue cue = cues[i];
            builder.AppendLine((i + 1).ToString());
            builder.Append(Format(cue.Begin, ',')).Append(" --> ").AppendLine(Format(cue.End, ','));
            builder.AppendLine(cue.Text).AppendLine();
        }
        return builder.ToString();
    }

    public static string RenderWebVtt(IReadOnlyList<SubtitleCue> cues)
    {
        var builder = new StringBuilder("WEBVTT\r\n\r\n");
        foreach (SubtitleCue cue in cues)
        {
            builder.Append(Format(cue.Begin, '.')).Append(" --> ").AppendLine(Format(cue.End, '.'));
            builder.AppendLine(cue.Text).AppendLine();
        }
        return builder.ToString();
    }

    static string Normalize(string text) => WhitespaceRegex().Replace(text.Trim(), " ");

    static List<string> Split(string text, int maximumCharacters)
    {
        if (text.EnumerateRunes().Count() <= maximumCharacters)
            return [text];

        var result = new List<string>();
        var current = new StringBuilder();
        int count = 0;
        int preferredLength = -1;
        foreach (System.Text.Rune rune in text.EnumerateRunes())
        {
            current.Append(rune.ToString());
            count++;
            if (char.IsWhiteSpace((char)rune.Value) || "，。！？；：、,.!?;:".Contains(rune.ToString()))
                preferredLength = current.Length;
            if (count < maximumCharacters)
                continue;

            int splitAt = preferredLength > 0 ? preferredLength : current.Length;
            string chunk = Normalize(current.ToString(0, splitAt));
            if (chunk.Length > 0)
                result.Add(chunk);
            string remainder = current.ToString(splitAt, current.Length - splitAt).TrimStart();
            current.Clear().Append(remainder);
            count = remainder.EnumerateRunes().Count();
            preferredLength = -1;
        }
        string last = Normalize(current.ToString());
        if (last.Length > 0)
            result.Add(last);
        return result;
    }

    static string Wrap(string text, int maximumCharacters) =>
        string.Join(Environment.NewLine, Split(text, maximumCharacters));

    static string Format(TimeSpan value, char separator) =>
        $"{(int)value.TotalHours:00}:{value.Minutes:00}:{value.Seconds:00}{separator}{value.Milliseconds:000}";
}
