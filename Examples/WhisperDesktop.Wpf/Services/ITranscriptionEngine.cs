using Whisper;

namespace WhisperDesktop.Modern.Services;

public readonly record struct TranscriptionSegment(TimeSpan Begin, TimeSpan End, string Text);
public readonly record struct TermCorrection(string Source, string Target);
public readonly record struct TranscriptionResult(string? OutputPath, int SegmentCount, int CorrectionCount);

public interface ITranscriptionEngine : IDisposable
{
    bool IsModelLoaded { get; }

    Task LoadModelAsync(
        string path,
        IProgress<double>? progress,
        CancellationToken cancellationToken);

    Task<TranscriptionResult> TranscribeAsync(
        string inputPath,
        string outputFolder,
        eLanguage language,
        bool translate,
        OutputFormat format,
        string? initialPrompt,
        IReadOnlyList<TermCorrection> corrections,
        IProgress<double>? progress,
        Action<TranscriptionSegment>? liveSegment,
        CancellationToken cancellationToken);
}
