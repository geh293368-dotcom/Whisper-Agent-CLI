using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace WhisperDesktop.Modern.Services;

internal sealed class AiSubtitleOptimizationService
{
    const int ChunkSize = 100;
    const decimal UsdToCny = 7.25m;
    readonly GeminiModelClient geminiClient;

    static readonly JsonSerializerOptions ReportJsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    public AiSubtitleOptimizationService(GeminiModelClient geminiClient)
    {
        this.geminiClient = geminiClient;
    }

    public async Task<AiSubtitleBatchReport> OptimizeAndEvaluateAsync(
        IReadOnlyList<string> inputPaths,
        string apiKey,
        string model,
        string languageName,
        string terminologyHint,
        IProgress<AiSubtitleProgress>? progress,
        CancellationToken cancellationToken)
    {
        List<AiSubtitleInputPair> pairs = DiscoverPairs(inputPaths);
        if (pairs.Count == 0)
            throw new InvalidOperationException("没有找到可优化的同名 SRT 字幕。请先把视频和字幕放在同一目录，文件名保持一致。");

        var reports = new List<AiSubtitleFileReport>();
        GeminiUsage totalUsage = GeminiUsage.Empty;
        var startedAt = DateTimeOffset.Now;
        for (int i = 0; i < pairs.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            AiSubtitleInputPair pair = pairs[i];
            progress?.Report(new AiSubtitleProgress($"正在优化 {pair.DisplayName} ({i + 1}/{pairs.Count})", i, pairs.Count, null));
            AiSubtitleFileReport report = await OptimizeAndEvaluateFileAsync(
                pair,
                apiKey,
                model,
                languageName,
                terminologyHint,
                progress,
                cancellationToken);
            reports.Add(report);
            totalUsage += report.Usage;
            progress?.Report(new AiSubtitleProgress($"完成 {pair.DisplayName}，评分 {report.OverallScoreBefore} -> {report.OverallScoreAfter}", i + 1, pairs.Count, report.ReportMarkdownPath));
        }

        string batchDirectory = GetCommonDirectory(pairs.Select(pair => pair.SubtitlePath));
        string stamp = DateTimeOffset.Now.ToString("yyyyMMdd-HHmmss");
        string batchJsonPath = Path.Combine(batchDirectory, $"ai-batch-report-{stamp}.json");
        string batchMarkdownPath = Path.Combine(batchDirectory, $"ai-batch-report-{stamp}.md");
        decimal costUsd = EstimateCostUsd(model, totalUsage);
        var batch = new AiSubtitleBatchReport(
            GeneratedAt: startedAt,
            Model: model,
            Language: languageName,
            FileCount: reports.Count,
            Usage: totalUsage,
            EstimatedCostUsd: costUsd,
            EstimatedCostCny: costUsd * UsdToCny,
            Reports: reports,
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
        AiSubtitleInputPair pair,
        string apiKey,
        string model,
        string languageName,
        string terminologyHint,
        IProgress<AiSubtitleProgress>? progress,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<ParsedSubtitleCue> originalCues = ParseSubRip(File.ReadAllText(pair.SubtitlePath));
        if (originalCues.Count == 0)
            throw new InvalidOperationException($"字幕文件为空或格式无法解析：{pair.SubtitlePath}");

        var optimizedTexts = originalCues.ToDictionary(cue => cue.Index, cue => cue.Text);
        var chunkSummaries = new List<string>();
        GeminiUsage usage = GeminiUsage.Empty;
        int chunkCount = (int)Math.Ceiling(originalCues.Count / (double)ChunkSize);
        for (int offset = 0; offset < originalCues.Count; offset += ChunkSize)
        {
            cancellationToken.ThrowIfCancellationRequested();
            IReadOnlyList<ParsedSubtitleCue> chunk = originalCues.Skip(offset).Take(ChunkSize).ToArray();
            progress?.Report(new AiSubtitleProgress($"正在优化 {pair.DisplayName}：第 {offset / ChunkSize + 1}/{chunkCount} 段", offset, originalCues.Count, null));
            SubtitleChunkOptimizationResult optimizedChunk = await geminiClient.OptimizeSubtitleChunkAsync(
                apiKey,
                model,
                chunk.Select(cue => new SubtitleTextItem(cue.Index, cue.Text)).ToArray(),
                languageName,
                terminologyHint,
                cancellationToken);
            usage += optimizedChunk.Usage ?? GeminiUsage.Empty;
            if (!string.IsNullOrWhiteSpace(optimizedChunk.Summary))
                chunkSummaries.Add(optimizedChunk.Summary);

            foreach (SubtitleOptimizedTextItem item in optimizedChunk.Items)
            {
                if (optimizedTexts.ContainsKey(item.Index) && !string.IsNullOrWhiteSpace(item.Text))
                    optimizedTexts[item.Index] = NormalizeSubtitleText(item.Text);
            }
        }

        IReadOnlyList<ParsedSubtitleCue> optimizedCues = originalCues
            .Select(cue => cue with { Text = optimizedTexts.TryGetValue(cue.Index, out string? text) ? text : cue.Text })
            .ToArray();
        string optimizedPath = Path.Combine(
            Path.GetDirectoryName(pair.SubtitlePath)!,
            $"{Path.GetFileNameWithoutExtension(pair.SubtitlePath)}.optimized.srt");
        File.WriteAllText(optimizedPath, RenderSubRip(optimizedCues), new UTF8Encoding(true));

        var comparisonItems = originalCues.Zip(optimizedCues, (before, after) => new SubtitleComparisonItem(
            before.Index,
            FormatTime(before.Begin, ','),
            FormatTime(before.End, ','),
            before.Text,
            after.Text)).ToArray();
        SubtitleQualityEvaluation evaluation = await geminiClient.EvaluateSubtitleQualityAsync(
            apiKey,
            model,
            pair.DisplayName,
            comparisonItems,
            languageName,
            cancellationToken);
        usage += evaluation.Usage ?? GeminiUsage.Empty;

        int changed = comparisonItems.Count(item => !string.Equals(
            NormalizeForComparison(item.OriginalText),
            NormalizeForComparison(item.OptimizedText),
            StringComparison.Ordinal));
        decimal costUsd = EstimateCostUsd(model, usage);
        string reportJsonPath = Path.Combine(
            Path.GetDirectoryName(pair.SubtitlePath)!,
            $"{Path.GetFileNameWithoutExtension(pair.SubtitlePath)}.ai-report.json");
        string reportMarkdownPath = Path.Combine(
            Path.GetDirectoryName(pair.SubtitlePath)!,
            $"{Path.GetFileNameWithoutExtension(pair.SubtitlePath)}.ai-report.md");
        var report = new AiSubtitleFileReport(
            MediaPath: pair.MediaPath,
            OriginalSubtitlePath: pair.SubtitlePath,
            OptimizedSubtitlePath: optimizedPath,
            ReportJsonPath: reportJsonPath,
            ReportMarkdownPath: reportMarkdownPath,
            CueCount: originalCues.Count,
            ChangedCueCount: changed,
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
        builder.AppendLine($"- 修改条数：{report.ChangedCueCount}");
        builder.AppendLine($"- 总分：{report.OverallScoreBefore} -> {report.OverallScoreAfter}");
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
        builder.AppendLine($"- Token：输入 {report.Usage.PromptTokens} / 输出 {report.Usage.OutputTokens} / 总计 {report.Usage.TotalTokens}");
        builder.AppendLine($"- 估算成本：${report.EstimatedCostUsd:F6} / ¥{report.EstimatedCostCny:F4}");
        builder.AppendLine();
        builder.AppendLine("| 文件 | 字幕条数 | 修改条数 | 分数 | 成本 |");
        builder.AppendLine("|---|---:|---:|---:|---:|");
        foreach (AiSubtitleFileReport item in report.Reports)
        {
            builder.AppendLine($"| {Path.GetFileName(item.OriginalSubtitlePath)} | {item.CueCount} | {item.ChangedCueCount} | {item.OverallScoreBefore} -> {item.OverallScoreAfter} | ¥{item.EstimatedCostCny:F4} |");
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

internal sealed record AiSubtitleInputPair(string MediaPath, string SubtitlePath, string DisplayName);

internal sealed record ParsedSubtitleCue(int Index, TimeSpan Begin, TimeSpan End, string Text);

internal sealed record AiSubtitleProgress(string Message, int Completed, int Total, string? ReportPath);

internal sealed record AiSubtitleFileReport(
    string MediaPath,
    string OriginalSubtitlePath,
    string OptimizedSubtitlePath,
    string ReportJsonPath,
    string ReportMarkdownPath,
    int CueCount,
    int ChangedCueCount,
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

internal sealed record AiSubtitleBatchReport(
    DateTimeOffset GeneratedAt,
    string Model,
    string Language,
    int FileCount,
    GeminiUsage Usage,
    decimal EstimatedCostUsd,
    decimal EstimatedCostCny,
    IReadOnlyList<AiSubtitleFileReport> Reports,
    string ReportJsonPath,
    string ReportMarkdownPath);
