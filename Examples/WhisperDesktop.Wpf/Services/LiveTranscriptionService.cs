using Whisper;

namespace WhisperDesktop.Modern.Services;

public sealed record LiveTranscriptionSegment(TimeSpan Begin, TimeSpan End, string Text);

public sealed class LiveTranscriptionService
{
    public async Task RunAsync(
        string modelPath,
        string captureEndpoint,
        eLanguage language,
        bool translate,
        IProgress<double>? modelProgress,
        Action<eCaptureStatus>? statusChanged,
        Action<LiveTranscriptionSegment>? segmentAdded,
        CancellationToken cancellationToken)
    {
        using iMediaFoundation mediaFoundation = Library.initMediaFoundation();
        using iModel model = await Library.loadModelAsync(
            modelPath,
            cancellationToken,
            pfnProgress: value => modelProgress?.Report(value));

        var captureParameters = new sCaptureParams(true)
        {
            minDuration = 2.0f,
            maxDuration = 8.0f,
            dropStartSilence = 0.30f,
            pauseDuration = 0.60f,
        };

        using iAudioCapture capture = mediaFoundation.openCaptureDevice(captureEndpoint, ref captureParameters);
        using Context context = model.createContext();
        context.parameters.language = language;
        context.parameters.setFlag(eFullParamsFlags.Translate, translate);
        context.parameters.setFlag(eFullParamsFlags.NoContext, true);
        context.parameters.setFlag(eFullParamsFlags.PrintRealtime, false);

        var transcriptionCallbacks = new SegmentCallbacks(cancellationToken, segmentAdded);
        var captureCallbacks = new SessionCallbacks(cancellationToken, statusChanged);
        await Task.Run(
            () => context.runCapture(capture, transcriptionCallbacks, captureCallbacks),
            cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
    }

    sealed class SegmentCallbacks(
        CancellationToken cancellationToken,
        Action<LiveTranscriptionSegment>? segmentAdded) : Callbacks
    {
        protected override bool onEncoderBegin(Context sender) => !cancellationToken.IsCancellationRequested;

        protected override void onNewSegment(Context sender, int countNew)
        {
            ReadOnlySpan<sSegment> segments = sender.results(eResultFlags.Timestamps).segments;
            int first = Math.Max(0, segments.Length - countNew);
            for (int index = first; index < segments.Length; index++)
            {
                sSegment segment = segments[index];
                string text = (segment.text ?? string.Empty).Trim();
                if (text.Length > 0)
                    segmentAdded?.Invoke(new LiveTranscriptionSegment(segment.time.begin, segment.time.end, text));
            }
        }
    }

    sealed class SessionCallbacks(
        CancellationToken cancellationToken,
        Action<eCaptureStatus>? statusChanged) : CaptureCallbacks
    {
        protected override bool shouldCancel(Context sender) => cancellationToken.IsCancellationRequested;

        protected override void captureStatusChanged(Context sender, eCaptureStatus status) =>
            statusChanged?.Invoke(status);
    }
}
