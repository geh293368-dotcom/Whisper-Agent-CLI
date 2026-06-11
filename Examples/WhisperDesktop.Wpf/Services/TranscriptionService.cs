using System.Text;
using System.IO;
using Whisper;

namespace WhisperDesktop.Modern.Services;

public enum OutputFormat
{
    Text,
    TextWithTimestamps,
    SubRip,
    WebVtt,
}

public sealed class TranscriptionService : IDisposable
{
    iModel? model;
    iMediaFoundation? mediaFoundation;

    public bool IsModelLoaded => model is not null;

    public async Task LoadModelAsync(
        string path,
        IProgress<double>? progress,
        CancellationToken cancellationToken)
    {
        iModel loaded = await Library.loadModelAsync(
            path,
            cancellationToken,
            pfnProgress: value => progress?.Report(value));

        model?.Dispose();
        model = loaded;
        mediaFoundation ??= Library.initMediaFoundation();
    }

    public async Task<string> TranscribeAsync(
        string inputPath,
        string outputFolder,
        eLanguage language,
        bool translate,
        OutputFormat format,
        IProgress<double>? progress,
        Action<string>? liveText,
        CancellationToken cancellationToken)
    {
        if (model is null || mediaFoundation is null)
            throw new InvalidOperationException("请先加载 Whisper 模型。");

        return await Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            using Context context = model.createContext();
            context.parameters.language = language;
            context.parameters.setFlag(eFullParamsFlags.Translate, translate);
            context.parameters.setFlag(eFullParamsFlags.NoContext, true);
            context.parameters.setFlag(eFullParamsFlags.PrintRealtime, false);

            var callbacks = new UiCallbacks(cancellationToken, liveText);
            using iAudioReader reader = mediaFoundation.openAudioFile(inputPath);
            context.runFull(reader, pfnProgress: value => progress?.Report(value), callbacks: callbacks);
            cancellationToken.ThrowIfCancellationRequested();

            var copied = new List<SourceSegment>();
            foreach (sSegment segment in context.results(eResultFlags.Timestamps).segments)
            {
                copied.Add(new SourceSegment(
                    segment.time.begin,
                    segment.time.end,
                    segment.text ?? string.Empty));
            }

            IReadOnlyList<SubtitleCue> cues = SubtitlePipeline.Build(copied);
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
        }, cancellationToken);
    }

    public void Dispose()
    {
        model?.Dispose();
        mediaFoundation?.Dispose();
        model = null;
        mediaFoundation = null;
    }

    sealed class UiCallbacks(CancellationToken cancellationToken, Action<string>? liveText) : Callbacks
    {
        protected override bool onEncoderBegin(Context sender) => !cancellationToken.IsCancellationRequested;

        protected override void onNewSegment(Context sender, int countNew)
        {
            ReadOnlySpan<sSegment> segments = sender.results().segments;
            int first = Math.Max(0, segments.Length - countNew);
            for (int i = first; i < segments.Length; i++)
            {
                string? text = segments[i].text;
                if (!string.IsNullOrWhiteSpace(text))
                    liveText?.Invoke(text.Trim());
            }
        }
    }
}
