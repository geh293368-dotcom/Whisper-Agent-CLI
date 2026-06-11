using Whisper;

namespace WhisperDesktop.Modern.Services;

public interface ITranscriptionEngine : IDisposable
{
    bool IsModelLoaded { get; }

    Task LoadModelAsync(
        string path,
        IProgress<double>? progress,
        CancellationToken cancellationToken);

    Task<string> TranscribeAsync(
        string inputPath,
        string outputFolder,
        eLanguage language,
        bool translate,
        OutputFormat format,
        IProgress<double>? progress,
        Action<string>? liveText,
        CancellationToken cancellationToken);
}
