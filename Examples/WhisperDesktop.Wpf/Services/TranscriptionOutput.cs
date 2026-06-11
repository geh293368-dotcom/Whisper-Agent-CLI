using System.IO;
using System.Text;

namespace WhisperDesktop.Modern.Services;

static class TranscriptionOutput
{
    public static string Write(
        IReadOnlyList<SourceSegment> segments,
        string inputPath,
        string outputFolder,
        OutputFormat format)
    {
        IReadOnlyList<SubtitleCue> cues = SubtitlePipeline.Build(segments);
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
        return outputPath;
    }
}
