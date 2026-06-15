using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace WhisperDesktop.Modern.Services;

static class TranscriptionOutput
{
    public static TranscriptionResult Write(
        IReadOnlyList<SourceSegment> segments,
        string inputPath,
        string outputFolder,
        OutputFormat format,
        IReadOnlyList<TermCorrection> corrections)
    {
        int correctionCount = 0;
        IReadOnlyList<SourceSegment> correctedSegments = ApplyCorrections(segments, corrections, ref correctionCount);
        IReadOnlyList<SubtitleCue> cues = SubtitlePipeline.Build(correctedSegments);
        if (cues.Count == 0)
            return new TranscriptionResult(null, 0, correctionCount);

        string content = format switch
        {
            OutputFormat.Text => SubtitlePipeline.RenderText(cues, false),
            OutputFormat.TextWithTimestamps => SubtitlePipeline.RenderText(cues, true),
            OutputFormat.SubRip => SubtitlePipeline.RenderSubRip(cues),
            OutputFormat.WebVtt => SubtitlePipeline.RenderWebVtt(cues),
            _ => throw new ArgumentOutOfRangeException(nameof(format)),
        };

        Directory.CreateDirectory(outputFolder);
        string extension = format switch
        {
            OutputFormat.SubRip => ".srt",
            OutputFormat.WebVtt => ".vtt",
            _ => ".txt",
        };
        string outputPath = Path.Combine(outputFolder, Path.GetFileNameWithoutExtension(inputPath) + extension);
        File.WriteAllText(outputPath, content, new UTF8Encoding(true));
        return new TranscriptionResult(outputPath, cues.Count, correctionCount);
    }

    static IReadOnlyList<SourceSegment> ApplyCorrections(
        IReadOnlyList<SourceSegment> segments,
        IReadOnlyList<TermCorrection> corrections,
        ref int correctionCount)
    {
        if (corrections.Count == 0 || segments.Count == 0)
            return segments;

        var result = new List<SourceSegment>(segments.Count);
        foreach (SourceSegment segment in segments)
        {
            string text = segment.Text;
            foreach (TermCorrection correction in corrections)
            {
                if (!IsSafeSource(correction.Source) || string.Equals(correction.Source, correction.Target, StringComparison.OrdinalIgnoreCase))
                    continue;
                int replacements = 0;
                text = Regex.Replace(
                    text,
                    Regex.Escape(correction.Source),
                    match =>
                    {
                        replacements++;
                        return correction.Target;
                    },
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
                correctionCount += replacements;
            }
            result.Add(segment with { Text = text });
        }
        return result;
    }

    static bool IsSafeSource(string value)
    {
        string text = value.Trim();
        if (text.Length < 2)
            return false;
        bool hasAsciiLetter = text.Any(c => c <= 127 && char.IsLetter(c));
        return !hasAsciiLetter || text.Length >= 3;
    }
}
