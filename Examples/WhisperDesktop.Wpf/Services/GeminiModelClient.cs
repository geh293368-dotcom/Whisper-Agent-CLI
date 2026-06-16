using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace WhisperDesktop.Modern.Services;

internal sealed class GeminiModelClient
{
    public const string DefaultModel = "gemini-3.1-flash-lite";

    static readonly HttpClient Http = new()
    {
        Timeout = TimeSpan.FromSeconds(45),
    };

    static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public async Task<GeminiTestResult> TestConnectionAsync(string apiKey, string model, CancellationToken cancellationToken)
    {
        string prompt = "Return a JSON object confirming that the connection works. Keep the message under 20 Chinese characters.";
        var schema = new Dictionary<string, object?>
        {
            ["type"] = "object",
            ["properties"] = new Dictionary<string, object?>
            {
                ["ok"] = new Dictionary<string, object?> { ["type"] = "boolean" },
                ["message"] = new Dictionary<string, object?> { ["type"] = "string" },
            },
            ["required"] = new[] { "ok", "message" },
        };

        GeminiJsonResponse response = await GenerateJsonAsync(apiKey, model, prompt, schema, cancellationToken);
        GeminiTestResult? result = JsonSerializer.Deserialize<GeminiTestResult>(response.Text, JsonOptions);
        return result is null
            ? new GeminiTestResult(false, "连接成功，但返回为空")
            : result with { Message = string.IsNullOrWhiteSpace(result.Message) ? "连接成功" : result.Message, Usage = response.Usage };
    }

    public async Task<SubtitleOptimizationResult> OptimizeSubtitleTextAsync(
        string apiKey,
        string model,
        string sourceText,
        string languageName,
        CancellationToken cancellationToken)
    {
        string prompt = $$"""
        你是字幕润色助手。请只优化下面这一条字幕文本，保持原意，不新增事实，不解释画面，不改变专有名词。
        要求：
        1. 修正明显错别字、语气词堆叠、断句不顺。
        2. 保持口语自然，适合直接放入 SRT 字幕。
        3. 如果原文已经很好，可以原样返回。
        4. 输出 JSON，不要输出 Markdown。

        语言：{{languageName}}
        原字幕：{{sourceText}}
        """;

        var schema = new Dictionary<string, object?>
        {
            ["type"] = "object",
            ["properties"] = new Dictionary<string, object?>
            {
                ["optimized_text"] = new Dictionary<string, object?>
                {
                    ["type"] = "string",
                    ["description"] = "润色后的字幕文本",
                },
                ["notes"] = new Dictionary<string, object?>
                {
                    ["type"] = "string",
                    ["description"] = "非常简短说明修改点；没有修改时写未修改",
                },
            },
            ["required"] = new[] { "optimized_text", "notes" },
        };

        GeminiJsonResponse response = await GenerateJsonAsync(apiKey, model, prompt, schema, cancellationToken);
        SubtitleOptimizationResult? result = JsonSerializer.Deserialize<SubtitleOptimizationResult>(response.Text, JsonOptions);
        if (result is null || string.IsNullOrWhiteSpace(result.OptimizedText))
            throw new InvalidOperationException("Gemini 返回结果为空。");

        return result with { Usage = response.Usage };
    }

    public async Task<SubtitleChunkOptimizationResult> OptimizeSubtitleChunkAsync(
        string apiKey,
        string model,
        IReadOnlyList<SubtitleTextItem> items,
        string languageName,
        string terminologyHint,
        CancellationToken cancellationToken)
    {
        string cueJson = JsonSerializer.Serialize(items.Select(item => new { item.Index, item.Text }), JsonOptions);
        string prompt = $$"""
        你是字幕润色助手。请优化这一组字幕文本，保持每条字幕的 index 不变。
        只允许修改字幕正文，不要改时间码，不要新增/删除 index，不要改变课程术语含义。

        目标：
        1. 修正明显错别字、ASR 误识别、无意义口头禅堆叠。
        2. 改善中文断句和可读性，但保留讲课口吻。
        3. Unity、特效、材质、粒子、模型、贴图等术语要优先保持准确。
        4. 不确定时宁可少改，不要编造画面内容。

        语言：{{languageName}}
        术语提示：{{(string.IsNullOrWhiteSpace(terminologyHint) ? "无" : terminologyHint)}}
        字幕 JSON：{{cueJson}}
        """;

        var schema = new Dictionary<string, object?>
        {
            ["type"] = "object",
            ["properties"] = new Dictionary<string, object?>
            {
                ["items"] = new Dictionary<string, object?>
                {
                    ["type"] = "array",
                    ["items"] = new Dictionary<string, object?>
                    {
                        ["type"] = "object",
                        ["properties"] = new Dictionary<string, object?>
                        {
                            ["index"] = new Dictionary<string, object?> { ["type"] = "integer" },
                            ["text"] = new Dictionary<string, object?> { ["type"] = "string" },
                            ["notes"] = new Dictionary<string, object?> { ["type"] = "string" },
                        },
                        ["required"] = new[] { "index", "text" },
                    },
                },
                ["summary"] = new Dictionary<string, object?> { ["type"] = "string" },
            },
            ["required"] = new[] { "items", "summary" },
        };

        GeminiJsonResponse response = await GenerateJsonAsync(apiKey, model, prompt, schema, cancellationToken);
        SubtitleChunkOptimizationResult? result = JsonSerializer.Deserialize<SubtitleChunkOptimizationResult>(response.Text, JsonOptions);
        if (result is null || result.Items.Count == 0)
            throw new InvalidOperationException("Gemini 返回的字幕优化结果为空。");

        return result with { Usage = response.Usage };
    }

    public async Task<SubtitleQualityEvaluation> EvaluateSubtitleQualityAsync(
        string apiKey,
        string model,
        string fileName,
        IReadOnlyList<SubtitleComparisonItem> items,
        string languageName,
        CancellationToken cancellationToken)
    {
        string comparisonJson = JsonSerializer.Serialize(items.Select(item => new
        {
            item.Index,
            item.Begin,
            item.End,
            before = item.OriginalText,
            after = item.OptimizedText,
        }), JsonOptions);
        string prompt = $$"""
        你是字幕质量评估员。请对比同一课程字幕的优化前后版本，输出严格 JSON。
        评价重点：错别字、术语准确、口语流畅、字幕可读性、语义保真。
        分数范围 0-100。只有在优化确实更好时才提高分数；如果优化改变原意，要在 risks 中指出。

        文件：{{fileName}}
        语言：{{languageName}}
        字幕对比 JSON：{{comparisonJson}}
        """;

        var scoreSchema = new Dictionary<string, object?>
        {
            ["type"] = "object",
            ["properties"] = new Dictionary<string, object?>
            {
                ["typo"] = new Dictionary<string, object?> { ["type"] = "integer" },
                ["terminology"] = new Dictionary<string, object?> { ["type"] = "integer" },
                ["fluency"] = new Dictionary<string, object?> { ["type"] = "integer" },
                ["readability"] = new Dictionary<string, object?> { ["type"] = "integer" },
                ["meaningPreservation"] = new Dictionary<string, object?> { ["type"] = "integer" },
            },
            ["required"] = new[] { "typo", "terminology", "fluency", "readability", "meaningPreservation" },
        };
        var exampleSchema = new Dictionary<string, object?>
        {
            ["type"] = "object",
            ["properties"] = new Dictionary<string, object?>
            {
                ["index"] = new Dictionary<string, object?> { ["type"] = "integer" },
                ["before"] = new Dictionary<string, object?> { ["type"] = "string" },
                ["after"] = new Dictionary<string, object?> { ["type"] = "string" },
                ["reason"] = new Dictionary<string, object?> { ["type"] = "string" },
            },
            ["required"] = new[] { "index", "before", "after", "reason" },
        };
        var schema = new Dictionary<string, object?>
        {
            ["type"] = "object",
            ["properties"] = new Dictionary<string, object?>
            {
                ["overallScoreBefore"] = new Dictionary<string, object?> { ["type"] = "integer" },
                ["overallScoreAfter"] = new Dictionary<string, object?> { ["type"] = "integer" },
                ["scores"] = scoreSchema,
                ["summary"] = new Dictionary<string, object?> { ["type"] = "string" },
                ["improvements"] = new Dictionary<string, object?>
                {
                    ["type"] = "array",
                    ["items"] = new Dictionary<string, object?> { ["type"] = "string" },
                },
                ["risks"] = new Dictionary<string, object?>
                {
                    ["type"] = "array",
                    ["items"] = new Dictionary<string, object?> { ["type"] = "string" },
                },
                ["examples"] = new Dictionary<string, object?>
                {
                    ["type"] = "array",
                    ["items"] = exampleSchema,
                },
            },
            ["required"] = new[] { "overallScoreBefore", "overallScoreAfter", "scores", "summary", "improvements", "risks", "examples" },
        };

        GeminiJsonResponse response = await GenerateJsonAsync(apiKey, model, prompt, schema, cancellationToken);
        SubtitleQualityEvaluation? result = JsonSerializer.Deserialize<SubtitleQualityEvaluation>(response.Text, JsonOptions);
        if (result is null)
            throw new InvalidOperationException("Gemini 返回的评分结果为空。");

        return result with { Usage = response.Usage };
    }

    async Task<GeminiJsonResponse> GenerateJsonAsync(
        string apiKey,
        string model,
        string prompt,
        Dictionary<string, object?> schema,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new InvalidOperationException("请先配置 Gemini API Key。");

        string safeModel = string.IsNullOrWhiteSpace(model) ? DefaultModel : model.Trim();
        string url = $"https://generativelanguage.googleapis.com/v1beta/models/{Uri.EscapeDataString(safeModel)}:generateContent";
        var requestBody = new GeminiGenerateRequest(
            SystemInstruction: new GeminiContent([new GeminiPart("你必须输出符合 JSON Schema 的紧凑 JSON。")]),
            Contents: [new GeminiContent([new GeminiPart(prompt)])],
            GenerationConfig: new GeminiGenerationConfig(
                ResponseMimeType: "application/json",
                ResponseSchema: ConvertJsonSchemaToGemini(schema)));

        using var request = new HttpRequestMessage(HttpMethod.Post, url);
        request.Headers.Add("x-goog-api-key", apiKey.Trim());
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Content = new StringContent(JsonSerializer.Serialize(requestBody, JsonOptions), Encoding.UTF8, "application/json");

        using HttpResponseMessage response = await Http.SendAsync(request, cancellationToken);
        string responseText = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"Gemini 请求失败：{(int)response.StatusCode} {ExtractErrorMessage(responseText)}");

        using JsonDocument document = JsonDocument.Parse(responseText);
        if (!TryReadGeneratedText(document.RootElement, out string? generatedText) || string.IsNullOrWhiteSpace(generatedText))
            throw new InvalidOperationException("Gemini 响应里没有可用文本。");

        return new GeminiJsonResponse(generatedText, ReadUsage(document.RootElement));
    }

    static bool TryReadGeneratedText(JsonElement root, out string? text)
    {
        text = null;
        if (!root.TryGetProperty("candidates", out JsonElement candidates) || candidates.ValueKind != JsonValueKind.Array)
            return false;
        JsonElement firstCandidate = candidates.EnumerateArray().FirstOrDefault();
        if (firstCandidate.ValueKind == JsonValueKind.Undefined)
            return false;
        if (!firstCandidate.TryGetProperty("content", out JsonElement content) ||
            !content.TryGetProperty("parts", out JsonElement parts) ||
            parts.ValueKind != JsonValueKind.Array)
            return false;
        JsonElement firstPart = parts.EnumerateArray().FirstOrDefault();
        return firstPart.ValueKind != JsonValueKind.Undefined &&
            firstPart.TryGetProperty("text", out JsonElement value) &&
            (text = value.GetString()) is not null;
    }

    static GeminiUsage ReadUsage(JsonElement root)
    {
        if (!root.TryGetProperty("usageMetadata", out JsonElement usage))
            return GeminiUsage.Empty;

        return new GeminiUsage(
            PromptTokens: ReadInt(usage, "promptTokenCount"),
            OutputTokens: ReadInt(usage, "candidatesTokenCount"),
            ThoughtsTokens: ReadInt(usage, "thoughtsTokenCount"),
            TotalTokens: ReadInt(usage, "totalTokenCount"));
    }

    static int ReadInt(JsonElement element, string name) =>
        element.TryGetProperty(name, out JsonElement value) && value.TryGetInt32(out int result) ? result : 0;

    static string ExtractErrorMessage(string responseText)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(responseText);
            if (document.RootElement.TryGetProperty("error", out JsonElement error) &&
                error.TryGetProperty("message", out JsonElement message))
            {
                return message.GetString() ?? "未知错误";
            }
        }
        catch (JsonException)
        {
        }

        return string.IsNullOrWhiteSpace(responseText) ? "未知错误" : responseText;
    }

    static Dictionary<string, object?> ConvertJsonSchemaToGemini(Dictionary<string, object?> schema)
    {
        var result = new Dictionary<string, object?>();
        foreach ((string key, object? value) in schema)
        {
            if (key == "type" && value is string type)
            {
                result[key] = type.ToUpperInvariant();
                continue;
            }

            if (key is "properties" && value is Dictionary<string, object?> properties)
            {
                result[key] = properties.ToDictionary(
                    pair => pair.Key,
                    pair => pair.Value is Dictionary<string, object?> child
                        ? ConvertJsonSchemaToGemini(child)
                        : pair.Value);
                continue;
            }

            if (key is "items" && value is Dictionary<string, object?> items)
            {
                result[key] = ConvertJsonSchemaToGemini(items);
                continue;
            }

            if (value is Dictionary<string, object?> childSchema)
            {
                result[key] = ConvertJsonSchemaToGemini(childSchema);
                continue;
            }

            result[key] = value;
        }

        return result;
    }

    sealed record GeminiGenerateRequest(
        [property: JsonPropertyName("system_instruction")] GeminiContent SystemInstruction,
        GeminiContent[] Contents,
        GeminiGenerationConfig GenerationConfig);

    sealed record GeminiContent(GeminiPart[] Parts);

    sealed record GeminiPart(string Text);

    sealed record GeminiGenerationConfig(
        string ResponseMimeType,
        Dictionary<string, object?> ResponseSchema);
}

internal sealed record GeminiJsonResponse(string Text, GeminiUsage Usage);

internal sealed record GeminiUsage(int PromptTokens, int OutputTokens, int ThoughtsTokens, int TotalTokens)
{
    public static GeminiUsage Empty { get; } = new(0, 0, 0, 0);

    public static GeminiUsage operator +(GeminiUsage left, GeminiUsage right) => new(
        left.PromptTokens + right.PromptTokens,
        left.OutputTokens + right.OutputTokens,
        left.ThoughtsTokens + right.ThoughtsTokens,
        left.TotalTokens + right.TotalTokens);
}

internal sealed record GeminiTestResult(bool Ok, string Message, GeminiUsage? Usage = null);

internal sealed record SubtitleOptimizationResult(
    [property: JsonPropertyName("optimized_text")] string OptimizedText,
    string Notes,
    GeminiUsage? Usage = null);

internal sealed record SubtitleTextItem(int Index, string Text);

internal sealed record SubtitleChunkOptimizationResult(
    IReadOnlyList<SubtitleOptimizedTextItem> Items,
    string Summary,
    GeminiUsage? Usage = null);

internal sealed record SubtitleOptimizedTextItem(int Index, string Text, string? Notes);

internal sealed record SubtitleComparisonItem(int Index, string Begin, string End, string OriginalText, string OptimizedText);

internal sealed record SubtitleQualityScores(
    int Typo,
    int Terminology,
    int Fluency,
    int Readability,
    int MeaningPreservation);

internal sealed record SubtitleQualityExample(int Index, string Before, string After, string Reason);

internal sealed record SubtitleQualityEvaluation(
    int OverallScoreBefore,
    int OverallScoreAfter,
    SubtitleQualityScores Scores,
    string Summary,
    IReadOnlyList<string> Improvements,
    IReadOnlyList<string> Risks,
    IReadOnlyList<SubtitleQualityExample> Examples,
    GeminiUsage? Usage = null);
