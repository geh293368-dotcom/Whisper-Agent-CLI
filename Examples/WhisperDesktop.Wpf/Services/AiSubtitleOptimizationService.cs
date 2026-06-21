using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace WhisperDesktop.Modern.Services;

internal sealed class AiSubtitleOptimizationService
{
    const int ChunkSize = 100;
    const int MaxGeminiRetryCount = 3;
    const decimal UsdToCny = 7.25m;
    static readonly TimeSpan[] DefaultGeminiRetryDelays =
    [
        TimeSpan.FromSeconds(2),
        TimeSpan.FromSeconds(5),
        TimeSpan.FromSeconds(10),
    ];
    readonly Func<IAiSubtitleModelClient> modelClientFactory;
    readonly IReadOnlyList<TimeSpan> geminiRetryDelays;

    static readonly JsonSerializerOptions ReportJsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public AiSubtitleOptimizationService(
        IAiSubtitleModelClient modelClient,
        IReadOnlyList<TimeSpan>? geminiRetryDelays = null)
        : this(() => modelClient, geminiRetryDelays)
    {
    }

    public AiSubtitleOptimizationService(
        Func<IAiSubtitleModelClient> modelClientFactory,
        IReadOnlyList<TimeSpan>? geminiRetryDelays = null)
    {
        this.modelClientFactory = modelClientFactory;
        this.geminiRetryDelays = geminiRetryDelays is { Count: > 0 }
            ? geminiRetryDelays
            : DefaultGeminiRetryDelays;
    }

    public async Task<AiSubtitleBatchReport> OptimizeAndEvaluateAsync(
        IReadOnlyList<string> inputPaths,
        string apiKey,
        string model,
        string languageName,
        string terminologyHint,
        AiSubtitleOutputPolicy outputPolicy,
        IProgress<AiSubtitleProgress>? progress,
        CancellationToken cancellationToken)
    {
        List<AiSubtitleInputPair> pairs = DiscoverPairs(inputPaths);
        if (pairs.Count == 0)
            throw new InvalidOperationException("没有找到可优化的同名 SRT 字幕。请先把视频和字幕放在同一目录，文件名保持一致。");

        IAiSubtitleModelClient modelClient = modelClientFactory();
        var reports = new List<AiSubtitleFileReport>();
        var failures = new List<AiSubtitleFailureReport>();
        GeminiUsage totalUsage = GeminiUsage.Empty;
        var startedAt = DateTimeOffset.Now;
        for (int i = 0; i < pairs.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            AiSubtitleInputPair pair = pairs[i];
            progress?.Report(new AiSubtitleProgress($"正在优化 {pair.DisplayName} ({i + 1}/{pairs.Count})", i, pairs.Count, null));
            try
            {
                AiSubtitleFileReport report = await OptimizeAndEvaluateFileAsync(
                    modelClient,
                    pair,
                    apiKey,
                    model,
                    languageName,
                    terminologyHint,
                    outputPolicy,
                    progress,
                    cancellationToken);
                reports.Add(report);
                totalUsage += report.Usage;
                progress?.Report(new AiSubtitleProgress($"完成 {pair.DisplayName}，评分 {report.OverallScoreBefore} -> {report.OverallScoreAfter}", i + 1, pairs.Count, report.ReportMarkdownPath));
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                AiSubtitleFailureReport failure = CreateFailureReport(pair, ex);
                failures.Add(failure);
                progress?.Report(new AiSubtitleProgress($"失败 {pair.DisplayName}：{failure.ErrorMessage}，已继续下一个文件", i + 1, pairs.Count, null));
            }
        }

        string batchDirectory = GetCommonDirectory(pairs.Select(pair => pair.SubtitlePath));
        string stamp = DateTimeOffset.Now.ToString("yyyyMMdd-HHmmss-fff");
        string batchJsonPath = Path.Combine(batchDirectory, $"ai-batch-report-{stamp}.json");
        string batchMarkdownPath = Path.Combine(batchDirectory, $"ai-batch-report-{stamp}.md");
        decimal costUsd = EstimateCostUsd(modelClient, model, totalUsage);
        var batch = new AiSubtitleBatchReport(
            GeneratedAt: startedAt,
            Model: model,
            Language: languageName,
            FileCount: reports.Count,
            Usage: totalUsage,
            EstimatedCostUsd: costUsd,
            EstimatedCostCny: costUsd * UsdToCny,
            Reports: reports,
            Failures: failures,
            ReportJsonPath: batchJsonPath,
            ReportMarkdownPath: batchMarkdownPath);

        File.WriteAllText(batchJsonPath, JsonSerializer.Serialize(batch, ReportJsonOptions), new UTF8Encoding(true));
        File.WriteAllText(batchMarkdownPath, RenderBatchMarkdown(batch), new UTF8Encoding(true));
        return batch;
    }

    public static List<AiSubtitleInputPair> DiscoverPairs(IEnumerable<string> inputPaths)
    {
        var result = new List<AiSubtitleInputPair>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string inputPath in inputPaths.Where(path => !string.IsNullOrWhiteSpace(path)))
        {
            string fullPath = Path.GetFullPath(inputPath);
            string extension = Path.GetExtension(fullPath);
            string? mediaPath = IsSubtitleExtension(extension) ? FindSiblingMedia(fullPath) : fullPath;
            string subtitlePath = IsSubtitleExtension(extension)
                ? fullPath
                : Path.ChangeExtension(fullPath, ".srt");
            if (mediaPath is null || !File.Exists(mediaPath) || !File.Exists(subtitlePath))
                continue;

            string key = Path.GetFullPath(subtitlePath);
            if (!seen.Add(key))
                continue;

            result.Add(new AiSubtitleInputPair(
                MediaPath: mediaPath,
                SubtitlePath: subtitlePath,
                DisplayName: Path.GetFileNameWithoutExtension(subtitlePath)));
        }

        return result;
    }

    async Task<AiSubtitleFileReport> OptimizeAndEvaluateFileAsync(
        IAiSubtitleModelClient modelClient,
        AiSubtitleInputPair pair,
        string apiKey,
        string model,
        string languageName,
        string terminologyHint,
        AiSubtitleOutputPolicy outputPolicy,
        IProgress<AiSubtitleProgress>? progress,
        CancellationToken cancellationToken)
    {
        var totalStopwatch = Stopwatch.StartNew();
        IReadOnlyList<ParsedSubtitleCue> originalCues = ParseSubRip(File.ReadAllText(pair.SubtitlePath));
        if (originalCues.Count == 0)
            throw new InvalidOperationException($"字幕文件为空或格式无法解析：{pair.SubtitlePath}");

        int textCharCount = CountTextCharacters(originalCues);
        var optimizedTexts = originalCues.ToDictionary(cue => cue.Index, cue => cue.Text);
        var chunkSummaries = new List<string>();
        GeminiUsage usage = GeminiUsage.Empty;
        int chunkCount = (int)Math.Ceiling(originalCues.Count / (double)ChunkSize);
        int retryCount = 0;
        var optimizeStopwatch = Stopwatch.StartNew();
        for (int offset = 0; offset < originalCues.Count; offset += ChunkSize)
        {
            cancellationToken.ThrowIfCancellationRequested();
            IReadOnlyList<ParsedSubtitleCue> chunk = originalCues.Skip(offset).Take(ChunkSize).ToArray();
            int chunkNumber = offset / ChunkSize + 1;
            progress?.Report(new AiSubtitleProgress($"正在优化 {pair.DisplayName}：第 {chunkNumber}/{chunkCount} 段", offset, originalCues.Count, null));
            AiSubtitleRequestResult<SubtitleChunkOptimizationResult> chunkResult = await ExecuteGeminiWithRetryAsync(
                async ct =>
                {
                    SubtitleChunkOptimizationResult result = await modelClient.OptimizeSubtitleChunkAsync(
                        apiKey,
                        model,
                        chunk.Select(cue => new SubtitleTextItem(cue.Index, cue.Text)).ToArray(),
                        languageName,
                        terminologyHint,
                        ct);
                    ValidateOptimizedChunkResult(chunk, result);
                    return result;
                },
                $"{pair.DisplayName} 第 {chunkNumber}/{chunkCount} 段",
                chunkNumber,
                progress,
                cancellationToken);
            retryCount += chunkResult.RetryCount;
            SubtitleChunkOptimizationResult optimizedChunk = chunkResult.Value;
            usage += optimizedChunk.Usage ?? GeminiUsage.Empty;
            if (!string.IsNullOrWhiteSpace(optimizedChunk.Summary))
                chunkSummaries.Add(optimizedChunk.Summary);

            foreach (SubtitleOptimizedTextItem item in optimizedChunk.Items)
            {
                if (optimizedTexts.ContainsKey(item.Index) && !string.IsNullOrWhiteSpace(item.Text))
                    optimizedTexts[item.Index] = NormalizeSubtitleText(item.Text);
            }
        }
        optimizeStopwatch.Stop();

        IReadOnlyList<ParsedSubtitleCue> optimizedCues = originalCues
            .Select(cue => cue with { Text = optimizedTexts.TryGetValue(cue.Index, out string? text) ? text : cue.Text })
            .ToArray();
        string optimizedContent = RenderSubRip(optimizedCues);
        ValidateRenderedSubRip(optimizedContent, originalCues.Count, pair.SubtitlePath);

        var comparisonItems = originalCues.Zip(optimizedCues, (before, after) => new SubtitleComparisonItem(
            before.Index,
            FormatTime(before.Begin, ','),
            FormatTime(before.End, ','),
            before.Text,
            after.Text)).ToArray();
        var evaluationStopwatch = Stopwatch.StartNew();
        AiSubtitleRequestResult<SubtitleQualityEvaluation> evaluationResult = await ExecuteGeminiWithRetryAsync(
            ct => modelClient.EvaluateSubtitleQualityAsync(
                apiKey,
                model,
                pair.DisplayName,
                comparisonItems,
                languageName,
                ct),
            $"{pair.DisplayName} 评分",
            null,
            progress,
            cancellationToken);
        evaluationStopwatch.Stop();
        retryCount += evaluationResult.RetryCount;
        SubtitleQualityEvaluation evaluation = evaluationResult.Value;
        usage += evaluation.Usage ?? GeminiUsage.Empty;

        int changed = comparisonItems.Count(item => !string.Equals(
            NormalizeForComparison(item.OriginalText),
            NormalizeForComparison(item.OptimizedText),
            StringComparison.Ordinal));
        decimal costUsd = EstimateCostUsd(modelClient, model, usage);
        AiSubtitleWriteResult writeResult = WriteOptimizedSubtitle(pair.SubtitlePath, optimizedContent, originalCues.Count, outputPolicy);
        totalStopwatch.Stop();
        var statistics = new AiSubtitleFileStatistics(
            TextCharCount: textCharCount,
            ChunkSize: ChunkSize,
            OptimizeRequestCount: chunkCount,
            EvaluationRequestCount: 1,
            TotalRequestCount: chunkCount + 1,
            OptimizeElapsedMs: optimizeStopwatch.ElapsedMilliseconds,
            EvaluationElapsedMs: evaluationStopwatch.ElapsedMilliseconds,
            TotalElapsedMs: totalStopwatch.ElapsedMilliseconds,
            CharsPerSecond: CalculateRate(textCharCount, totalStopwatch.Elapsed),
            CuesPerSecond: CalculateRate(originalCues.Count, totalStopwatch.Elapsed),
            RetryCount: retryCount);
        string reportJsonPath = Path.Combine(
            Path.GetDirectoryName(pair.SubtitlePath)!,
            $"{Path.GetFileNameWithoutExtension(pair.SubtitlePath)}.ai-report.json");
        string reportMarkdownPath = Path.Combine(
            Path.GetDirectoryName(pair.SubtitlePath)!,
            $"{Path.GetFileNameWithoutExtension(pair.SubtitlePath)}.ai-report.md");
        var report = new AiSubtitleFileReport(
            MediaPath: pair.MediaPath,
            OriginalSubtitlePath: pair.SubtitlePath,
            OptimizedSubtitlePath: writeResult.OutputPath,
            OutputPolicy: outputPolicy,
            BackupSubtitlePath: writeResult.BackupPath,
            ReportJsonPath: reportJsonPath,
            ReportMarkdownPath: reportMarkdownPath,
            CueCount: originalCues.Count,
            ChangedCueCount: changed,
            Statistics: statistics,
            OverallScoreBefore: evaluation.OverallScoreBefore,
            OverallScoreAfter: evaluation.OverallScoreAfter,
            Scores: evaluation.Scores,
            Summary: evaluation.Summary,
            Improvements: evaluation.Improvements,
            Risks: evaluation.Risks,
            Examples: evaluation.Examples,
            ChunkSummaries: chunkSummaries,
            Usage: usage,
            EstimatedCostUsd: costUsd,
            EstimatedCostCny: costUsd * UsdToCny);

        File.WriteAllText(reportJsonPath, JsonSerializer.Serialize(report, ReportJsonOptions), new UTF8Encoding(true));
        File.WriteAllText(reportMarkdownPath, RenderFileMarkdown(report), new UTF8Encoding(true));
        return report;
    }

    public static IReadOnlyList<ParsedSubtitleCue> ParseSubRip(string content)
    {
        var result = new List<ParsedSubtitleCue>();
        string normalized = content.Replace("\r\n", "\n").Replace('\r', '\n');
        string[] blocks = Regex.Split(normalized.Trim(), @"\n{2,}");
        foreach (string block in blocks)
        {
            string[] lines = block.Split('\n');
            if (lines.Length < 3 || !int.TryParse(lines[0].Trim(), out int index))
                continue;

            Match timeMatch = Regex.Match(lines[1], @"(?<begin>\d{2}:\d{2}:\d{2}[,.]\d{3})\s*-->\s*(?<end>\d{2}:\d{2}:\d{2}[,.]\d{3})");
            if (!timeMatch.Success ||
                !TryParseTime(timeMatch.Groups["begin"].Value, out TimeSpan begin) ||
                !TryParseTime(timeMatch.Groups["end"].Value, out TimeSpan end))
            {
                continue;
            }

            string text = NormalizeSubtitleText(string.Join('\n', lines.Skip(2)));
            if (text.Length == 0)
                continue;

            result.Add(new ParsedSubtitleCue(index, begin, end, text));
        }

        return result;
    }

    public static string RenderSubRip(IReadOnlyList<ParsedSubtitleCue> cues)
    {
        var builder = new StringBuilder();
        for (int i = 0; i < cues.Count; i++)
        {
            ParsedSubtitleCue cue = cues[i];
            builder.AppendLine((i + 1).ToString());
            builder.Append(FormatTime(cue.Begin, ',')).Append(" --> ").AppendLine(FormatTime(cue.End, ','));
            builder.AppendLine(cue.Text).AppendLine();
        }

        return builder.ToString();
    }

    static string RenderFileMarkdown(AiSubtitleFileReport report)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"# AI 字幕优化报告 - {Path.GetFileNameWithoutExtension(report.OriginalSubtitlePath)}");
        builder.AppendLine();
        builder.AppendLine($"- 字幕条数：{report.CueCount}");
        builder.AppendLine($"- 正文字符：{report.Statistics.TextCharCount}");
        builder.AppendLine($"- 请求次数：优化 {report.Statistics.OptimizeRequestCount} / 评分 {report.Statistics.EvaluationRequestCount} / 总计 {report.Statistics.TotalRequestCount}");
        builder.AppendLine($"- 重试次数：{report.Statistics.RetryCount}");
        builder.AppendLine($"- 耗时：总计 {FormatElapsed(report.Statistics.TotalElapsedMs)}，优化 {FormatElapsed(report.Statistics.OptimizeElapsedMs)}，评分 {FormatElapsed(report.Statistics.EvaluationElapsedMs)}");
        builder.AppendLine($"- 速度：{FormatRate(report.Statistics.CharsPerSecond)} 字/秒，{FormatRate(report.Statistics.CuesPerSecond)} 条/秒");
        builder.AppendLine($"- 修改条数：{report.ChangedCueCount}");
        builder.AppendLine($"- 总分：{report.OverallScoreBefore} -> {report.OverallScoreAfter}");
        builder.AppendLine($"- 输出策略：{FormatOutputPolicy(report.OutputPolicy)}");
        if (!string.IsNullOrWhiteSpace(report.BackupSubtitlePath))
            builder.AppendLine($"- 备份字幕：{report.BackupSubtitlePath}");
        builder.AppendLine($"- Token：输入 {report.Usage.PromptTokens} / 输出 {report.Usage.OutputTokens} / 总计 {report.Usage.TotalTokens}");
        builder.AppendLine($"- 估算成本：${report.EstimatedCostUsd:F6} / ¥{report.EstimatedCostCny:F4}");
        builder.AppendLine();
        builder.AppendLine("## 总结");
        builder.AppendLine(report.Summary);
        builder.AppendLine();
        AppendList(builder, "改进点", report.Improvements);
        AppendList(builder, "风险", report.Risks);
        builder.AppendLine("## 例子");
        foreach (SubtitleQualityExample example in report.Examples.Take(12))
        {
            builder.AppendLine($"- #{example.Index}: {example.Reason}");
            builder.AppendLine($"  - 原文：{example.Before}");
            builder.AppendLine($"  - 优化：{example.After}");
        }

        return builder.ToString();
    }

    static string RenderBatchMarkdown(AiSubtitleBatchReport report)
    {
        var builder = new StringBuilder();
        builder.AppendLine("# AI 字幕批量优化报告");
        builder.AppendLine();
        builder.AppendLine($"- 生成时间：{report.GeneratedAt:yyyy-MM-dd HH:mm:ss zzz}");
        builder.AppendLine($"- 模型：{report.Model}");
        builder.AppendLine($"- 文件数：{report.FileCount}");
        builder.AppendLine($"- 失败数：{report.FailedCount}");
        builder.AppendLine($"- Token：输入 {report.Usage.PromptTokens} / 输出 {report.Usage.OutputTokens} / 总计 {report.Usage.TotalTokens}");
        builder.AppendLine($"- 估算成本：${report.EstimatedCostUsd:F6} / ¥{report.EstimatedCostCny:F4}");
        builder.AppendLine();
        builder.AppendLine("| 文件 | 输出 | 字符 | 请求 | 重试 | 耗时 | 速度 | 修改条数 | 分数 | 成本 |");
        builder.AppendLine("|---|---|---:|---:|---:|---:|---:|---:|---:|---:|");
        foreach (AiSubtitleFileReport item in report.Reports)
        {
            builder.AppendLine($"| {Path.GetFileName(item.OriginalSubtitlePath)} | {FormatOutputPolicy(item.OutputPolicy)} | {item.Statistics.TextCharCount} | {item.Statistics.TotalRequestCount} | {item.Statistics.RetryCount} | {FormatElapsed(item.Statistics.TotalElapsedMs)} | {FormatRate(item.Statistics.CharsPerSecond)} 字/秒 | {item.ChangedCueCount}/{item.CueCount} | {item.OverallScoreBefore} -> {item.OverallScoreAfter} | ¥{item.EstimatedCostCny:F4} |");
        }

        if (report.Failures.Count > 0)
        {
            builder.AppendLine();
            builder.AppendLine("## 失败文件");
            builder.AppendLine();
            builder.AppendLine("| 文件 | 阶段 | 错误类型 | 分块 | 重试 | 错误 |");
            builder.AppendLine("|---|---|---|---:|---:|---|");
            foreach (AiSubtitleFailureReport failure in report.Failures)
            {
                string chunk = failure.FailedChunkIndex is null ? "-" : failure.FailedChunkIndex.Value.ToString();
                builder.AppendLine($"| {Path.GetFileName(failure.SubtitlePath)} | {failure.Stage} | {FormatGeminiErrorCategory(failure.ErrorCategory)} | {chunk} | {failure.RetryCount} | {EscapeMarkdownTable(TrimForReport(failure.ErrorMessage, 120))} |");
            }
        }

        return builder.ToString();
    }

    static void AppendList(StringBuilder builder, string title, IReadOnlyList<string> items)
    {
        builder.AppendLine($"## {title}");
        if (items.Count == 0)
        {
            builder.AppendLine("- 无");
        }
        else
        {
            foreach (string item in items)
                builder.AppendLine($"- {item}");
        }
        builder.AppendLine();
    }

    static string GetCommonDirectory(IEnumerable<string> paths)
    {
        string[] directories = paths
            .Select(path => Path.GetDirectoryName(Path.GetFullPath(path))!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return directories.Length == 1 ? directories[0] : Directory.GetCurrentDirectory();
    }

    static string? FindSiblingMedia(string subtitlePath)
    {
        string directory = Path.GetDirectoryName(subtitlePath)!;
        string name = Path.GetFileNameWithoutExtension(subtitlePath);
        foreach (string extension in new[] { ".mp4", ".mkv", ".mov", ".avi", ".mp3", ".wav", ".m4a", ".flac" })
        {
            string candidate = Path.Combine(directory, name + extension);
            if (File.Exists(candidate))
                return candidate;
        }

        return null;
    }

    static bool IsSubtitleExtension(string extension) =>
        string.Equals(extension, ".srt", StringComparison.OrdinalIgnoreCase);

    static bool TryParseTime(string value, out TimeSpan result)
    {
        result = TimeSpan.Zero;
        string normalized = value.Replace(',', '.');
        return TimeSpan.TryParseExact(normalized, @"hh\:mm\:ss\.fff", null, out result);
    }

    static string FormatTime(TimeSpan value, char separator) =>
        $"{(int)value.TotalHours:00}:{value.Minutes:00}:{value.Seconds:00}{separator}{value.Milliseconds:000}";

    static string NormalizeSubtitleText(string text) =>
        Regex.Replace(text.Trim(), @"[ \t]+", " ").Replace("\n ", "\n").Replace(" \n", "\n");

    static string NormalizeForComparison(string text) =>
        Regex.Replace(text, @"\s+", string.Empty);

    static int CountTextCharacters(IEnumerable<ParsedSubtitleCue> cues)
    {
        int count = 0;
        foreach (ParsedSubtitleCue cue in cues)
        {
            foreach (Rune rune in cue.Text.EnumerateRunes())
            {
                if (!Rune.IsWhiteSpace(rune))
                    count++;
            }
        }

        return count;
    }

    static double CalculateRate(int count, TimeSpan elapsed) =>
        elapsed.TotalSeconds <= 0 ? 0 : count / elapsed.TotalSeconds;

    async Task<AiSubtitleRequestResult<T>> ExecuteGeminiWithRetryAsync<T>(
        Func<CancellationToken, Task<T>> action,
        string operationName,
        int? chunkIndex,
        IProgress<AiSubtitleProgress>? progress,
        CancellationToken cancellationToken)
    {
        int retryCount = 0;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                T value = await action(cancellationToken);
                return new AiSubtitleRequestResult<T>(value, retryCount);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (GeminiRequestException ex) when (ex.Retryable && retryCount < MaxGeminiRetryCount)
            {
                retryCount++;
                TimeSpan delay = GetGeminiRetryDelay(retryCount);
                progress?.Report(new AiSubtitleProgress(
                    $"{operationName} 请求失败（{FormatGeminiErrorCategory(ex.Category)}），{delay.TotalSeconds:0} 秒后重试 {retryCount}/{MaxGeminiRetryCount}：{ex.Message}",
                    0,
                    0,
                    null));
                await Task.Delay(delay, cancellationToken);
            }
            catch (GeminiRequestException ex)
            {
                throw new AiSubtitleRequestFailedException(
                    operationName,
                    ex.Category,
                    retryCount,
                    chunkIndex,
                    ex.Message,
                    ex);
            }
        }
    }

    TimeSpan GetGeminiRetryDelay(int retryNumber)
    {
        int index = Math.Clamp(retryNumber - 1, 0, geminiRetryDelays.Count - 1);
        return geminiRetryDelays[index];
    }

    static void ValidateOptimizedChunkResult(
        IReadOnlyList<ParsedSubtitleCue> chunk,
        SubtitleChunkOptimizationResult result)
    {
        HashSet<int> expected = chunk.Select(cue => cue.Index).ToHashSet();
        var actual = new HashSet<int>();
        foreach (SubtitleOptimizedTextItem item in result.Items)
        {
            if (!expected.Contains(item.Index))
                throw GeminiModelClient.CreateContentException($"Gemini 返回了不属于当前分块的字幕 index：{item.Index}。");
            if (!actual.Add(item.Index))
                throw GeminiModelClient.CreateContentException($"Gemini 返回了重复字幕 index：{item.Index}。");
            if (string.IsNullOrWhiteSpace(item.Text))
                throw GeminiModelClient.CreateContentException($"Gemini 返回了空字幕 index：{item.Index}。");
        }

        if (actual.Count != expected.Count)
        {
            int[] missing = expected.Except(actual).OrderBy(index => index).Take(8).ToArray();
            throw GeminiModelClient.CreateContentException($"Gemini 返回字幕条数不完整，缺少 index：{string.Join(", ", missing)}。");
        }
    }

    static AiSubtitleFailureReport CreateFailureReport(AiSubtitleInputPair pair, Exception exception)
    {
        if (exception is AiSubtitleRequestFailedException requestFailure)
        {
            return new AiSubtitleFailureReport(
                MediaPath: pair.MediaPath,
                SubtitlePath: pair.SubtitlePath,
                DisplayName: pair.DisplayName,
                FailedAt: DateTimeOffset.Now,
                Stage: requestFailure.Stage,
                ErrorCategory: requestFailure.Category,
                ErrorMessage: requestFailure.Message,
                FailedChunkIndex: requestFailure.FailedChunkIndex,
                RetryCount: requestFailure.RetryCount);
        }

        if (exception is GeminiRequestException geminiException)
        {
            return new AiSubtitleFailureReport(
                MediaPath: pair.MediaPath,
                SubtitlePath: pair.SubtitlePath,
                DisplayName: pair.DisplayName,
                FailedAt: DateTimeOffset.Now,
                Stage: "Gemini 请求",
                ErrorCategory: geminiException.Category,
                ErrorMessage: geminiException.Message,
                FailedChunkIndex: null,
                RetryCount: 0);
        }

        return new AiSubtitleFailureReport(
            MediaPath: pair.MediaPath,
            SubtitlePath: pair.SubtitlePath,
            DisplayName: pair.DisplayName,
            FailedAt: DateTimeOffset.Now,
            Stage: "文件处理",
            ErrorCategory: exception is InvalidOperationException ? GeminiErrorCategory.Content : GeminiErrorCategory.Unknown,
            ErrorMessage: exception.Message,
            FailedChunkIndex: null,
            RetryCount: 0);
    }

    internal static AiSubtitleWriteResult WriteOptimizedSubtitle(
        string subtitlePath,
        string optimizedContent,
        int expectedCueCount,
        AiSubtitleOutputPolicy outputPolicy)
    {
        string directory = Path.GetDirectoryName(subtitlePath)!;
        string baseName = Path.GetFileNameWithoutExtension(subtitlePath);
        string stamp = DateTimeOffset.Now.ToString("yyyyMMdd-HHmmss-fff");
        string tempPath = Path.Combine(directory, $"{baseName}.ai-{stamp}-{Guid.NewGuid():N}.tmp");

        if (outputPolicy == AiSubtitleOutputPolicy.PreserveOriginal)
        {
            string optimizedPath = Path.Combine(directory, $"{baseName}.optimized.srt");
            try
            {
                WriteValidatedTempFile(tempPath, optimizedContent, expectedCueCount);
                ReplaceOrMove(tempPath, optimizedPath, backupPath: null);
                return new AiSubtitleWriteResult(optimizedPath, null);
            }
            finally
            {
                if (File.Exists(tempPath))
                    File.Delete(tempPath);
            }
        }

        string backupPath = Path.Combine(directory, $"{baseName}.original-{stamp}.srt");
        try
        {
            WriteValidatedTempFile(tempPath, optimizedContent, expectedCueCount);
            ReplaceOrMove(tempPath, subtitlePath, backupPath);
            return new AiSubtitleWriteResult(subtitlePath, backupPath);
        }
        finally
        {
            if (File.Exists(tempPath))
                File.Delete(tempPath);
        }
    }

    static void WriteValidatedTempFile(string tempPath, string content, int expectedCueCount)
    {
        File.WriteAllText(tempPath, content, new UTF8Encoding(true));
        ValidateRenderedSubRip(File.ReadAllText(tempPath), expectedCueCount, tempPath);
    }

    static void ReplaceOrMove(string sourcePath, string destinationPath, string? backupPath)
    {
        if (File.Exists(destinationPath))
        {
            File.Replace(sourcePath, destinationPath, backupPath, ignoreMetadataErrors: true);
            return;
        }

        File.Move(sourcePath, destinationPath);
    }

    static void ValidateRenderedSubRip(string content, int expectedCueCount, string path)
    {
        IReadOnlyList<ParsedSubtitleCue> cues = ParseSubRip(content);
        if (cues.Count != expectedCueCount)
            throw new InvalidOperationException($"优化字幕校验失败：{Path.GetFileName(path)} 条数 {cues.Count} 与原字幕 {expectedCueCount} 不一致。");

        for (int i = 0; i < cues.Count; i++)
        {
            ParsedSubtitleCue cue = cues[i];
            if (cue.Index != i + 1)
                throw new InvalidOperationException($"优化字幕校验失败：{Path.GetFileName(path)} 第 {i + 1} 条编号不连续。");
            if (cue.End <= cue.Begin)
                throw new InvalidOperationException($"优化字幕校验失败：{Path.GetFileName(path)} 第 {cue.Index} 条时间轴无效。");
        }
    }

    static string FormatOutputPolicy(AiSubtitleOutputPolicy policy) => policy switch
    {
        AiSubtitleOutputPolicy.OverwriteWithBackup => "覆盖原字幕并保留备份",
        _ => "保留原字幕，输出优化副本",
    };

    static string FormatGeminiErrorCategory(GeminiErrorCategory category) => category switch
    {
        GeminiErrorCategory.UserConfiguration => "配置错误",
        GeminiErrorCategory.RateLimited => "限流",
        GeminiErrorCategory.TemporaryService => "临时服务错误",
        GeminiErrorCategory.Network => "网络错误",
        GeminiErrorCategory.Timeout => "请求超时",
        GeminiErrorCategory.Content => "返回内容错误",
        _ => "未知错误",
    };

    static string TrimForReport(string value, int maxLength)
    {
        string normalized = value.Replace("\r", " ").Replace("\n", " ").Trim();
        return normalized.Length <= maxLength ? normalized : normalized[..Math.Max(0, maxLength - 1)] + "…";
    }

    static string EscapeMarkdownTable(string value) => value.Replace("|", "\\|");

    static string FormatElapsed(long milliseconds)
    {
        TimeSpan elapsed = TimeSpan.FromMilliseconds(Math.Max(0, milliseconds));
        return elapsed.TotalHours >= 1
            ? $"{(int)elapsed.TotalHours:00}:{elapsed.Minutes:00}:{elapsed.Seconds:00}"
            : $"{elapsed.Minutes:00}:{elapsed.Seconds:00}";
    }

    static string FormatRate(double value) => value <= 0 ? "0.0" : value.ToString("F1");

    static decimal EstimateCostUsd(IAiSubtitleModelClient modelClient, string model, GeminiUsage usage)
    {
        if (modelClient is OpenAiCompatibleModelClient)
            return 0m;

        return EstimateCostUsd(model, usage);
    }

    static decimal EstimateCostUsd(string model, GeminiUsage usage)
    {
        (decimal inputPrice, decimal outputPrice) = model switch
        {
            "gemini-3.5-flash" => (1.50m, 9.00m),
            "gemini-3.1-flash-lite" => (0.25m, 1.50m),
            "gemini-2.5-flash-lite" => (0.10m, 0.40m),
            _ => (0.25m, 1.50m),
        };
        int billableOutputTokens = Math.Max(usage.OutputTokens + usage.ThoughtsTokens, usage.TotalTokens - usage.PromptTokens);
        return (usage.PromptTokens * inputPrice + billableOutputTokens * outputPrice) / 1_000_000m;
    }
}

public enum AiSubtitleOutputPolicy
{
    PreserveOriginal,
    OverwriteWithBackup,
}

internal sealed record AiSubtitleWriteResult(string OutputPath, string? BackupPath);

internal sealed record AiSubtitleRequestResult<T>(T Value, int RetryCount);

internal sealed class AiSubtitleRequestFailedException : Exception
{
    public AiSubtitleRequestFailedException(
        string stage,
        GeminiErrorCategory category,
        int retryCount,
        int? failedChunkIndex,
        string message,
        Exception innerException)
        : base(message, innerException)
    {
        Stage = stage;
        Category = category;
        RetryCount = retryCount;
        FailedChunkIndex = failedChunkIndex;
    }

    public string Stage { get; }
    public GeminiErrorCategory Category { get; }
    public int RetryCount { get; }
    public int? FailedChunkIndex { get; }
}

internal sealed record AiSubtitleInputPair(string MediaPath, string SubtitlePath, string DisplayName);

internal sealed record ParsedSubtitleCue(int Index, TimeSpan Begin, TimeSpan End, string Text);

internal sealed record AiSubtitleProgress(string Message, int Completed, int Total, string? ReportPath);

internal sealed record AiSubtitleFailureReport(
    string MediaPath,
    string SubtitlePath,
    string DisplayName,
    DateTimeOffset FailedAt,
    string Stage,
    GeminiErrorCategory ErrorCategory,
    string ErrorMessage,
    int? FailedChunkIndex,
    int RetryCount);

internal sealed record AiSubtitleFileReport(
    string MediaPath,
    string OriginalSubtitlePath,
    string OptimizedSubtitlePath,
    AiSubtitleOutputPolicy OutputPolicy,
    string? BackupSubtitlePath,
    string ReportJsonPath,
    string ReportMarkdownPath,
    int CueCount,
    int ChangedCueCount,
    AiSubtitleFileStatistics Statistics,
    int OverallScoreBefore,
    int OverallScoreAfter,
    SubtitleQualityScores Scores,
    string Summary,
    IReadOnlyList<string> Improvements,
    IReadOnlyList<string> Risks,
    IReadOnlyList<SubtitleQualityExample> Examples,
    IReadOnlyList<string> ChunkSummaries,
    GeminiUsage Usage,
    decimal EstimatedCostUsd,
    decimal EstimatedCostCny);

internal sealed record AiSubtitleFileStatistics(
    int TextCharCount,
    int ChunkSize,
    int OptimizeRequestCount,
    int EvaluationRequestCount,
    int TotalRequestCount,
    long OptimizeElapsedMs,
    long EvaluationElapsedMs,
    long TotalElapsedMs,
    double CharsPerSecond,
    double CuesPerSecond,
    int RetryCount);

internal sealed record AiSubtitleBatchReport(
    DateTimeOffset GeneratedAt,
    string Model,
    string Language,
    int FileCount,
    GeminiUsage Usage,
    decimal EstimatedCostUsd,
    decimal EstimatedCostCny,
    IReadOnlyList<AiSubtitleFileReport> Reports,
    IReadOnlyList<AiSubtitleFailureReport> Failures,
    string ReportJsonPath,
    string ReportMarkdownPath)
{
    public int FailedCount => Failures.Count;
}
