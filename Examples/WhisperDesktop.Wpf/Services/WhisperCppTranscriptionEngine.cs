using System.Runtime.InteropServices;
using Whisper;

namespace WhisperDesktop.Modern.Services;

public sealed class WhisperCppTranscriptionEngine : ITranscriptionEngine
{
    readonly string libraryName;
    WhisperCppNative? native;
    WhisperCppNative.ModelHandle? model;
    iMediaFoundation? mediaFoundation;

    public bool IsModelLoaded => model is { IsInvalid: false, IsClosed: false };

    public WhisperCppTranscriptionEngine(string libraryName)
    {
        this.libraryName = libraryName;
    }

    public async Task LoadModelAsync(
        string path,
        IProgress<double>? progress,
        CancellationToken cancellationToken)
    {
        WhisperCppNative.ModelHandle loaded = await Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report(0.05);
            WhisperCppNative api = native ??= new WhisperCppNative(libraryName);
            WhisperCppNative.ModelHandle handle = api.LoadModel(path);
            if (handle.IsInvalid || !api.ModelReady(handle))
            {
                string error = handle.IsInvalid
                    ? "whisper.cpp did not return a model handle."
                    : api.GetError(handle);
                handle.Dispose();
                throw new InvalidOperationException(error);
            }
            if (cancellationToken.IsCancellationRequested)
            {
                handle.Dispose();
                cancellationToken.ThrowIfCancellationRequested();
            }
            return handle;
        }, cancellationToken);

        model?.Dispose();
        model = loaded;
        mediaFoundation ??= Library.initMediaFoundation();
        progress?.Report(1);
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
            using iAudioBuffer audio = mediaFoundation.loadAudioFile(inputPath);
            string languageCode = GetLanguageCode(language);

            WhisperCppNative.ProgressCallback progressCallback =
                (value, _) => progress?.Report(value / 100.0);
            WhisperCppNative.SegmentCallback segmentCallback = (_, _, text, _) =>
            {
                string? value = Marshal.PtrToStringUTF8(text);
                if (!string.IsNullOrWhiteSpace(value))
                    liveText?.Invoke(value.Trim());
            };
            WhisperCppNative.CancelCallback cancelCallback =
                _ => cancellationToken.IsCancellationRequested ? 1 : 0;

            int result = native!.Transcribe(
                model,
                audio.getPcmMono(),
                audio.countSamples(),
                languageCode,
                translate,
                progressCallback,
                segmentCallback,
                cancelCallback);

            cancellationToken.ThrowIfCancellationRequested();
            if (result != 0)
                throw new InvalidOperationException(native.GetError(model));

            int count = native.SegmentCount(model);
            var copied = new List<SourceSegment>(count);
            for (int index = 0; index < count; index++)
            {
                copied.Add(new SourceSegment(
                    TimeSpan.FromMilliseconds(native.SegmentBegin(model, index) * 10),
                    TimeSpan.FromMilliseconds(native.SegmentEnd(model, index) * 10),
                    native.SegmentText(model, index)));
            }

            return TranscriptionOutput.Write(copied, inputPath, outputFolder, format);
        }, cancellationToken);
    }

    static string GetLanguageCode(eLanguage language)
    {
        uint packed = (uint)language;
        Span<char> code = stackalloc char[4];
        int length = 0;
        while (packed != 0)
        {
            code[length++] = (char)(packed & 0xff);
            packed >>= 8;
        }
        return new string(code[..length]);
    }

    public void Dispose()
    {
        model?.Dispose();
        mediaFoundation?.Dispose();
        native?.Dispose();
        model = null;
        mediaFoundation = null;
        native = null;
    }
}
