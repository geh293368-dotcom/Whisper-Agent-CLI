using System.Net;
using WhisperDesktop.Modern.Services;

static void Require(bool condition, string message)
{
    if (!condition)
        throw new InvalidOperationException(message);
}

var source = new[]
{
    new SourceSegment(TimeSpan.Zero, TimeSpan.FromMilliseconds(200), "  first   line "),
    new SourceSegment(TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(150), " second line "),
    new SourceSegment(TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(3), "   "),
};

IReadOnlyList<SubtitleCue> cues = SubtitlePipeline.Build(source);
Require(cues.Count == 2, "blank segments must be removed");
Require(cues[0].Text == "first line", "whitespace must be normalized");
Require(cues[1].Begin >= cues[0].End + TimeSpan.FromMilliseconds(20), "cue timestamps must not overlap");
Require(cues[1].End - cues[1].Begin >= TimeSpan.FromMilliseconds(800), "short cues need a readable duration");

var dupSource = new[]
{
    new SourceSegment(TimeSpan.Zero, TimeSpan.FromMilliseconds(100), "Hello world"),
    new SourceSegment(TimeSpan.FromMilliseconds(150), TimeSpan.FromMilliseconds(150), "Hello world"), // duplicate and zero-duration
    new SourceSegment(TimeSpan.FromMilliseconds(300), TimeSpan.FromMilliseconds(310), "Goodbye world")
};
IReadOnlyList<SubtitleCue> dupCues = SubtitlePipeline.Build(dupSource);
Require(dupCues.Count == 2, "duplicate segments must be merged");
Require(dupCues[0].Text == "Hello world", "merged text must match");
Require(dupCues[0].Begin == TimeSpan.Zero, "merged start time must be correct");
Require(dupCues[0].End == TimeSpan.FromMilliseconds(950), "merged end time must incorporate corrected zero-duration segment");
Require(dupCues[1].Text == "Goodbye world", "distinct segment must remain");

var chinese = SubtitlePipeline.Build(
    [new SourceSegment(TimeSpan.Zero, TimeSpan.FromSeconds(12), "这是一个很长的中文字幕，用来验证标点断句和自动换行不会破坏UTF8字符。")],
    new SubtitleOptions(MaxCharactersPerLine: 8, MaxLines: 2));
Require(chinese.Count >= 2, "long subtitles must split into multiple cues");

string srt = SubtitlePipeline.RenderSubRip([new SubtitleCue(TimeSpan.Zero, TimeSpan.FromMilliseconds(150), "hello")]);
string vtt = SubtitlePipeline.RenderWebVtt([new SubtitleCue(TimeSpan.Zero, TimeSpan.FromMilliseconds(150), "hello")]);
Require(srt.Contains("00:00:00,000 --> 00:00:00,150"), "SRT timestamp format is invalid");
Require(vtt.StartsWith("WEBVTT\r\n\r\n", StringComparison.Ordinal), "WebVTT header is missing");

Require(GeminiModelClient.ClassifyStatusCode(HttpStatusCode.Unauthorized) == GeminiErrorCategory.UserConfiguration, "401 must be classified as configuration error");
Require(GeminiModelClient.ClassifyStatusCode((HttpStatusCode)429) == GeminiErrorCategory.RateLimited, "429 must be classified as rate limited");
Require(GeminiModelClient.ClassifyStatusCode(HttpStatusCode.BadGateway) == GeminiErrorCategory.TemporaryService, "502 must be classified as temporary service error");
Require(!GeminiModelClient.IsRetryable(GeminiErrorCategory.UserConfiguration), "configuration errors must not be retried");
Require(GeminiModelClient.IsRetryable(GeminiErrorCategory.RateLimited), "rate limits must be retried");
Require(GeminiModelClient.IsRetryable(GeminiErrorCategory.Timeout), "timeouts must be retried");

string fixtureRoot = GetFixtureRoot();
Require(Directory.Exists(fixtureRoot), "AI subtitle regression fixture directory is missing");
Require(!Directory.EnumerateFiles(fixtureRoot, "*.mp4").Any(), "regression fixtures must not include large media files");
foreach (string name in new[] { "03 地面效果3", "05 空中效果1", "07 爆点制作" })
{
    string originalPath = Path.Combine(fixtureRoot, $"{name}.srt");
    string optimizedPath = Path.Combine(fixtureRoot, $"{name}.optimized.srt");
    string markdownReportPath = Path.Combine(fixtureRoot, $"{name}.ai-report.md");
    string jsonReportPath = Path.Combine(fixtureRoot, $"{name}.ai-report.json");
    Require(File.Exists(originalPath), $"missing original subtitle fixture: {name}");
    Require(File.Exists(optimizedPath), $"missing optimized subtitle fixture: {name}");
    Require(File.Exists(markdownReportPath), $"missing markdown AI report fixture: {name}");
    Require(File.Exists(jsonReportPath), $"missing JSON AI report fixture: {name}");

    IReadOnlyList<ParsedSubtitleCue> original = AiSubtitleOptimizationService.ParseSubRip(File.ReadAllText(originalPath));
    IReadOnlyList<ParsedSubtitleCue> optimized = AiSubtitleOptimizationService.ParseSubRip(File.ReadAllText(optimizedPath));
    Require(original.Count > 0, $"original subtitle fixture must parse: {name}");
    Require(original.Count == optimized.Count, $"optimized subtitle must preserve cue count: {name}");
}

string batchReportPath = Path.Combine(fixtureRoot, "ai-batch-report-20260616-134614.md");
Require(File.Exists(batchReportPath), "batch AI report fixture is missing");

string tempRoot = Path.Combine(Path.GetTempPath(), "WhisperDesktop-ModernUiTests", Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(tempRoot);
try
{
    string subtitlePath = Path.Combine(tempRoot, "sample.srt");
    string originalContent = AiSubtitleOptimizationService.RenderSubRip(
        [new ParsedSubtitleCue(1, TimeSpan.Zero, TimeSpan.FromSeconds(1), "original text")]);
    string optimizedContent = AiSubtitleOptimizationService.RenderSubRip(
        [new ParsedSubtitleCue(1, TimeSpan.Zero, TimeSpan.FromSeconds(1), "optimized text")]);
    File.WriteAllText(subtitlePath, originalContent);

    AiSubtitleWriteResult preserved = AiSubtitleOptimizationService.WriteOptimizedSubtitle(
        subtitlePath,
        optimizedContent,
        expectedCueCount: 1,
        outputPolicy: AiSubtitleOutputPolicy.PreserveOriginal);
    Require(File.Exists(preserved.OutputPath), "preserve policy must write an optimized subtitle");
    Require(preserved.BackupPath is null, "preserve policy must not create a backup");
    Require(File.ReadAllText(subtitlePath).Contains("original text"), "preserve policy must keep the original subtitle");

    AiSubtitleWriteResult overwritten = AiSubtitleOptimizationService.WriteOptimizedSubtitle(
        subtitlePath,
        optimizedContent,
        expectedCueCount: 1,
        outputPolicy: AiSubtitleOutputPolicy.OverwriteWithBackup);
    Require(overwritten.OutputPath == subtitlePath, "overwrite policy must return the original subtitle path");
    Require(overwritten.BackupPath is not null && File.Exists(overwritten.BackupPath), "overwrite policy must create a backup");
    string backupPath = overwritten.BackupPath!;
    Require(File.ReadAllText(subtitlePath).Contains("optimized text"), "overwrite policy must replace the subtitle");
    Require(File.ReadAllText(backupPath).Contains("original text"), "overwrite backup must contain the original subtitle");

    File.WriteAllText(subtitlePath, originalContent);
    string existingOptimizedPath = Path.Combine(tempRoot, "sample.optimized.srt");
    File.WriteAllText(existingOptimizedPath, optimizedContent);
    bool failedSafely = false;
    try
    {
        _ = AiSubtitleOptimizationService.WriteOptimizedSubtitle(
            subtitlePath,
            "not a valid srt",
            expectedCueCount: 1,
            outputPolicy: AiSubtitleOutputPolicy.PreserveOriginal);
    }
    catch (InvalidOperationException)
    {
        failedSafely = true;
    }
    Require(failedSafely, "invalid optimized subtitle must fail validation");
    Require(File.ReadAllText(subtitlePath).Contains("original text"), "failed preserve write must keep the original subtitle");
    Require(File.ReadAllText(existingOptimizedPath).Contains("optimized text"), "failed preserve write must keep the previous optimized subtitle");
    Require(!Directory.EnumerateFiles(tempRoot, "*.tmp").Any(), "failed subtitle write must clean temporary files");
}
finally
{
    Directory.Delete(tempRoot, recursive: true);
}

string batchTempRoot = Path.Combine(Path.GetTempPath(), "WhisperDesktop-AiBatchTests", Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(batchTempRoot);
try
{
    string badMedia = Path.Combine(batchTempRoot, "bad.mp4");
    string badSubtitle = Path.ChangeExtension(badMedia, ".srt");
    string goodMedia = Path.Combine(batchTempRoot, "good.mp4");
    string goodSubtitle = Path.ChangeExtension(goodMedia, ".srt");
    File.WriteAllText(badMedia, string.Empty);
    File.WriteAllText(goodMedia, string.Empty);
    File.WriteAllText(badSubtitle, AiSubtitleOptimizationService.RenderSubRip(
    [
        new ParsedSubtitleCue(1, TimeSpan.Zero, TimeSpan.FromSeconds(1), "bad one"),
        new ParsedSubtitleCue(2, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), "bad two"),
    ]));
    File.WriteAllText(goodSubtitle, AiSubtitleOptimizationService.RenderSubRip(
    [
        new ParsedSubtitleCue(1, TimeSpan.Zero, TimeSpan.FromSeconds(1), "good one"),
        new ParsedSubtitleCue(2, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), "good two"),
    ]));

    var fakeGemini = new FakeGeminiModelClient();
    var aiService = new AiSubtitleOptimizationService(fakeGemini, [TimeSpan.Zero]);
    AiSubtitleBatchReport batchReport = aiService.OptimizeAndEvaluateAsync(
        [badMedia, goodMedia],
        apiKey: "test-key",
        model: GeminiModelClient.DefaultModel,
        languageName: "中文",
        terminologyHint: string.Empty,
        outputPolicy: AiSubtitleOutputPolicy.PreserveOriginal,
        progress: null,
        CancellationToken.None).GetAwaiter().GetResult();

    Require(batchReport.FileCount == 1, "batch report must count successful files");
    Require(batchReport.FailedCount == 1, "batch report must include failed files");
    Require(batchReport.Failures[0].DisplayName == "bad", "failed file name must be reported");
    Require(batchReport.Failures[0].ErrorCategory == GeminiErrorCategory.Content, "missing Gemini indexes must be reported as content errors");
    Require(batchReport.Failures[0].RetryCount == 3, "content errors must retry three times before failing");
    Require(fakeGemini.BadOptimizeCalls == 4, "bad file must run initial request plus three retries");
    Require(fakeGemini.GoodOptimizeCalls == 2, "good file must retry once after a transient rate limit");
    Require(batchReport.Reports[0].Statistics.RetryCount == 1, "successful file statistics must include retry count");
    Require(File.Exists(Path.Combine(batchTempRoot, "good.optimized.srt")), "successful file must write optimized subtitle");
    Require(!File.Exists(Path.Combine(batchTempRoot, "bad.optimized.srt")), "failed file must not write optimized subtitle");
    Require(File.ReadAllText(badSubtitle).Contains("bad one"), "failed file must keep original subtitle");
}
finally
{
    Directory.Delete(batchTempRoot, recursive: true);
}

Console.WriteLine("Modern UI subtitle tests passed");

static string GetFixtureRoot()
{
    string fromOutput = Path.GetFullPath(Path.Combine(
        AppContext.BaseDirectory,
        "..",
        "..",
        "..",
        "..",
        "Fixtures",
        "AiSubtitleRegression"));
    if (Directory.Exists(fromOutput))
        return fromOutput;

    return Path.GetFullPath(Path.Combine(
        Directory.GetCurrentDirectory(),
        "Tools",
        "ModernUiTests",
        "Fixtures",
        "AiSubtitleRegression"));
}

sealed class FakeGeminiModelClient : GeminiModelClient
{
    public int BadOptimizeCalls { get; private set; }
    public int GoodOptimizeCalls { get; private set; }

    public override Task<SubtitleChunkOptimizationResult> OptimizeSubtitleChunkAsync(
        string apiKey,
        string model,
        IReadOnlyList<SubtitleTextItem> items,
        string languageName,
        string terminologyHint,
        CancellationToken cancellationToken)
    {
        if (items.Any(item => item.Text.Contains("bad", StringComparison.OrdinalIgnoreCase)))
        {
            BadOptimizeCalls++;
            return Task.FromResult(new SubtitleChunkOptimizationResult(
                [new SubtitleOptimizedTextItem(items[0].Index, items[0].Text + " changed", null)],
                "missing index"));
        }

        GoodOptimizeCalls++;
        if (GoodOptimizeCalls == 1)
        {
            throw new GeminiRequestException(
                GeminiErrorCategory.RateLimited,
                "test rate limit",
                retryable: true,
                HttpStatusCode.TooManyRequests);
        }

        return Task.FromResult(new SubtitleChunkOptimizationResult(
            items.Select(item => new SubtitleOptimizedTextItem(item.Index, item.Text + " ok", null)).ToArray(),
            "optimized",
            new GeminiUsage(10, 5, 0, 15)));
    }

    public override Task<SubtitleQualityEvaluation> EvaluateSubtitleQualityAsync(
        string apiKey,
        string model,
        string fileName,
        IReadOnlyList<SubtitleComparisonItem> items,
        string languageName,
        CancellationToken cancellationToken)
    {
        return Task.FromResult(new SubtitleQualityEvaluation(
            OverallScoreBefore: 70,
            OverallScoreAfter: 90,
            Scores: new SubtitleQualityScores(90, 90, 90, 90, 90),
            Summary: "test summary",
            Improvements: ["test improvement"],
            Risks: [],
            Examples: [],
            Usage: new GeminiUsage(10, 5, 0, 15)));
    }
}
