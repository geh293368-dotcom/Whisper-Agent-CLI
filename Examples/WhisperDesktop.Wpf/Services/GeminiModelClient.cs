using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace WhisperDesktop.Modern.Services;

internal interface IAiSubtitleModelClient
{
    string ProviderDisplayName { get; }

    Task<GeminiTestResult> TestConnectionAsync(string apiKey, string model, CancellationToken cancellationToken);

    Task<SubtitleOptimizationResult> OptimizeSubtitleTextAsync(
        string apiKey,
        string model,
        string sourceText,
        string languageName,
        CancellationToken cancellationToken);

    Task<SubtitleChunkOptimizationResult> OptimizeSubtitleChunkAsync(
        string apiKey,
        string model,
        IReadOnlyList<SubtitleTextItem> items,
        string languageName,
        string terminologyHint,
        CancellationToken cancellationToken);

    Task<SubtitleQualityEvaluation> EvaluateSubtitleQualityAsync(
        string apiKey,
        string model,
        string fileName,
        IReadOnlyList<SubtitleComparisonItem> items,
        string languageName,
        CancellationToken cancellationToken);
}

internal class GeminiModelClient : IAiSubtitleModelClient
{
    public const string DefaultModel = "gemini-3.1-flash-lite";

    static readonly HttpClient Http = new()
    {
        Timeout = TimeSpan.FromSeconds(45),
    };

    protected static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public virtual string ProviderDisplayName => "Gemini";

    public virtual async Task<GeminiTestResult> TestConnectionAsync(string apiKey, string model, CancellationToken cancellationToken)
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
        GeminiTestResult? result = DeserializeJson<GeminiTestResult>(response.Text, $"{ProviderDisplayName} 连接测试结果为空。");
        return result is null
            ? new GeminiTestResult(false, "连接成功，但返回为空")
            : result with { Message = string.IsNullOrWhiteSpace(result.Message) ? "连接成功" : result.Message, Usage = response.Usage };
    }

    public virtual async Task<SubtitleOptimizationResult> OptimizeSubtitleTextAsync(
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
        SubtitleOptimizationResult? result = DeserializeJson<SubtitleOptimizationResult>(response.Text, $"{ProviderDisplayName} 返回结果为空。");
        if (result is null || string.IsNullOrWhiteSpace(result.OptimizedText))
            throw CreateContentException($"{ProviderDisplayName} 返回结果为空。");

        return result with { Usage = response.Usage };
    }

    public virtual async Task<SubtitleChunkOptimizationResult> OptimizeSubtitleChunkAsync(
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
        SubtitleChunkOptimizationResult? result = DeserializeJson<SubtitleChunkOptimizationResult>(response.Text, $"{ProviderDisplayName} 返回的字幕优化结果为空。");
        if (result is null || result.Items.Count == 0)
            throw CreateContentException($"{ProviderDisplayName} 返回的字幕优化结果为空。");

        return result with { Usage = response.Usage };
    }

    public virtual async Task<SubtitleQualityEvaluation> EvaluateSubtitleQualityAsync(
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
        SubtitleQualityEvaluation? result = DeserializeJson<SubtitleQualityEvaluation>(response.Text, $"{ProviderDisplayName} 返回的评分结果为空。");
        if (result is null)
            throw CreateContentException($"{ProviderDisplayName} 返回的评分结果为空。");

        return result with { Usage = response.Usage };
    }

    protected virtual async Task<GeminiJsonResponse> GenerateJsonAsync(
        string apiKey,
        string model,
        string prompt,
        Dictionary<string, object?> schema,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new GeminiRequestException(GeminiErrorCategory.UserConfiguration, $"请先配置 {ProviderDisplayName} API Key。", retryable: false);

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

        HttpResponseMessage response;
        try
        {
            response = await Http.SendAsync(request, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (TaskCanceledException ex)
        {
            throw new GeminiRequestException(GeminiErrorCategory.Timeout, $"{ProviderDisplayName} 请求超时。", retryable: true, innerException: ex);
        }
        catch (HttpRequestException ex)
        {
            throw new GeminiRequestException(GeminiErrorCategory.Network, $"{ProviderDisplayName} 网络请求失败：{ex.Message}", retryable: true, innerException: ex);
        }

        using (response)
        {
        string responseText = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            GeminiErrorCategory category = ClassifyStatusCode(response.StatusCode);
            throw new GeminiRequestException(
                category,
                $"{ProviderDisplayName} 请求失败：{(int)response.StatusCode} {ExtractErrorMessage(responseText)}",
                IsRetryable(category),
                response.StatusCode);
        }

        try
        {
        using JsonDocument document = JsonDocument.Parse(responseText);
        if (!TryReadGeneratedText(document.RootElement, out string? generatedText) || string.IsNullOrWhiteSpace(generatedText))
            throw CreateContentException($"{ProviderDisplayName} 响应里没有可用文本。");

        return new GeminiJsonResponse(generatedText, ReadUsage(document.RootElement));
        }
        catch (JsonException ex)
        {
            throw CreateContentException($"{ProviderDisplayName} 响应不是有效 JSON。", ex);
        }
        }
    }

    static T? DeserializeJson<T>(string json, string emptyMessage)
    {
        try
        {
            return JsonSerializer.Deserialize<T>(NormalizeJsonText(json), JsonOptions);
        }
        catch (JsonException ex)
        {
            throw CreateContentException(emptyMessage, ex);
        }
    }

    internal static GeminiRequestException CreateContentException(string message, Exception? innerException = null) =>
        new(GeminiErrorCategory.Content, message, retryable: true, innerException: innerException);

    internal static GeminiErrorCategory ClassifyStatusCode(HttpStatusCode statusCode) => (int)statusCode switch
    {
        400 or 401 or 403 or 404 => GeminiErrorCategory.UserConfiguration,
        429 => GeminiErrorCategory.RateLimited,
        500 or 502 or 503 or 504 => GeminiErrorCategory.TemporaryService,
        >= 500 => GeminiErrorCategory.TemporaryService,
        _ => GeminiErrorCategory.Unknown,
    };

    internal static bool IsRetryable(GeminiErrorCategory category) => category switch
    {
        GeminiErrorCategory.RateLimited or
        GeminiErrorCategory.TemporaryService or
        GeminiErrorCategory.Network or
        GeminiErrorCategory.Timeout or
        GeminiErrorCategory.Content => true,
        _ => false,
    };

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

    static string NormalizeJsonText(string json)
    {
        string trimmed = json.Trim();
        if (!trimmed.StartsWith("```", StringComparison.Ordinal))
            return trimmed;

        int firstLineBreak = trimmed.IndexOf('\n');
        if (firstLineBreak < 0)
            return trimmed;

        int fenceEnd = trimmed.LastIndexOf("```", StringComparison.Ordinal);
        if (fenceEnd <= firstLineBreak)
            return trimmed;

        return trimmed[(firstLineBreak + 1)..fenceEnd].Trim();
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

internal sealed class OpenAiCompatibleModelClient : GeminiModelClient
{
    public const string DefaultBaseUrl = "http://localhost:11434/v1/";
    public const string DefaultApiKey = "ollama";
    public new const string DefaultModel = "qwen3:8b";

    static readonly HttpClient LocalHttp = new()
    {
        Timeout = TimeSpan.FromMinutes(10),
    };

    public string BaseUrl { get; set; } = DefaultBaseUrl;

    public override string ProviderDisplayName => "本地 AI";

    protected override async Task<GeminiJsonResponse> GenerateJsonAsync(
        string apiKey,
        string model,
        string prompt,
        Dictionary<string, object?> schema,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(BaseUrl))
            throw new GeminiRequestException(GeminiErrorCategory.UserConfiguration, "本地 AI Base URL 不能为空。", retryable: false);

        string safeModel = string.IsNullOrWhiteSpace(model) ? DefaultModel : model.Trim();
        string schemaJson = JsonSerializer.Serialize(schema, JsonOptions);
        var requestBody = new OpenAiChatCompletionRequest(
            Model: safeModel,
            Messages:
            [
                new OpenAiChatMessage("system", "你必须输出符合 JSON Schema 的紧凑 JSON。不要输出 Markdown，不要输出解释。"),
                new OpenAiChatMessage("user", $"{prompt}\n\nJSON Schema：{schemaJson}")
            ],
            Temperature: 0.1,
            Stream: false,
            ResponseFormat: new OpenAiResponseFormat("json_object"));

        using var request = new HttpRequestMessage(HttpMethod.Post, BuildChatCompletionsUri(BaseUrl));
        if (!string.IsNullOrWhiteSpace(apiKey))
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey.Trim());
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Content = new StringContent(JsonSerializer.Serialize(requestBody, JsonOptions), Encoding.UTF8, "application/json");

        HttpResponseMessage response;
        try
        {
            response = await LocalHttp.SendAsync(request, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (TaskCanceledException ex)
        {
            throw new GeminiRequestException(GeminiErrorCategory.Timeout, "本地 AI 请求超时。", retryable: true, innerException: ex);
        }
        catch (HttpRequestException ex)
        {
            throw new GeminiRequestException(
                GeminiErrorCategory.Network,
                $"本地 AI 网络请求失败：{ex.Message}。请确认 Ollama 已运行，Base URL 通常是 {DefaultBaseUrl}",
                retryable: true,
                innerException: ex);
        }

        using (response)
        {
            string responseText = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                GeminiErrorCategory category = ClassifyStatusCode(response.StatusCode);
                throw new GeminiRequestException(
                    category,
                    $"本地 AI 请求失败：{(int)response.StatusCode} {ExtractOpenAiErrorMessage(responseText)}",
                    IsRetryable(category),
                    response.StatusCode);
            }

            try
            {
                using JsonDocument document = JsonDocument.Parse(responseText);
                if (!TryReadOpenAiGeneratedText(document.RootElement, out string? generatedText) || string.IsNullOrWhiteSpace(generatedText))
                    throw CreateContentException("本地 AI 响应里没有可用文本。");

                return new GeminiJsonResponse(generatedText, ReadOpenAiUsage(document.RootElement));
            }
            catch (JsonException ex)
            {
                throw CreateContentException("本地 AI 响应不是有效 JSON。", ex);
            }
        }
    }

    static Uri BuildChatCompletionsUri(string baseUrl)
    {
        string value = baseUrl.Trim();
        if (!value.EndsWith("/", StringComparison.Ordinal))
            value += "/";

        var uri = new Uri(value, UriKind.Absolute);
        string path = uri.AbsolutePath.TrimEnd('/');
        if (path.EndsWith("/v1/chat/completions", StringComparison.OrdinalIgnoreCase))
            return uri;

        if (path.Equals("/api", StringComparison.OrdinalIgnoreCase) || path.EndsWith("/api", StringComparison.OrdinalIgnoreCase))
            return new Uri(new Uri(uri.GetLeftPart(UriPartial.Authority) + "/"), "v1/chat/completions");

        if (path.Equals("/v1", StringComparison.OrdinalIgnoreCase) || path.EndsWith("/v1", StringComparison.OrdinalIgnoreCase))
            return new Uri(uri, "chat/completions");

        return new Uri(uri, "v1/chat/completions");
    }

    static bool TryReadOpenAiGeneratedText(JsonElement root, out string? text)
    {
        text = null;
        if (!root.TryGetProperty("choices", out JsonElement choices) || choices.ValueKind != JsonValueKind.Array)
            return false;

        JsonElement firstChoice = choices.EnumerateArray().FirstOrDefault();
        if (firstChoice.ValueKind == JsonValueKind.Undefined ||
            !firstChoice.TryGetProperty("message", out JsonElement message) ||
            !message.TryGetProperty("content", out JsonElement content))
        {
            return false;
        }

        if (content.ValueKind == JsonValueKind.String)
        {
            text = content.GetString();
            return true;
        }

        if (content.ValueKind != JsonValueKind.Array)
            return false;

        var builder = new StringBuilder();
        foreach (JsonElement part in content.EnumerateArray())
        {
            if (part.TryGetProperty("text", out JsonElement textPart))
                builder.Append(textPart.GetString());
        }

        text = builder.ToString();
        return text.Length > 0;
    }

    static GeminiUsage ReadOpenAiUsage(JsonElement root)
    {
        if (!root.TryGetProperty("usage", out JsonElement usage))
            return GeminiUsage.Empty;

        int promptTokens = ReadInt(usage, "prompt_tokens");
        int outputTokens = ReadInt(usage, "completion_tokens");
        int totalTokens = ReadInt(usage, "total_tokens");
        return new GeminiUsage(promptTokens, outputTokens, 0, totalTokens);
    }

    static int ReadInt(JsonElement element, string name) =>
        element.TryGetProperty(name, out JsonElement value) && value.TryGetInt32(out int result) ? result : 0;

    static string ExtractOpenAiErrorMessage(string responseText)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(responseText);
            if (document.RootElement.TryGetProperty("error", out JsonElement error))
            {
                if (error.ValueKind == JsonValueKind.String)
                    return error.GetString() ?? "未知错误";
                if (error.TryGetProperty("message", out JsonElement message))
                    return message.GetString() ?? "未知错误";
            }
        }
        catch (JsonException)
        {
        }

        return string.IsNullOrWhiteSpace(responseText) ? "未知错误" : responseText;
    }

    sealed record OpenAiChatCompletionRequest(
        string Model,
        OpenAiChatMessage[] Messages,
        double Temperature,
        bool Stream,
        [property: JsonPropertyName("response_format")] OpenAiResponseFormat ResponseFormat);

    sealed record OpenAiChatMessage(string Role, string Content);

    sealed record OpenAiResponseFormat(string Type);
}

internal sealed record GeminiJsonResponse(string Text, GeminiUsage Usage);

internal enum GeminiErrorCategory
{
    UserConfiguration,
    RateLimited,
    TemporaryService,
    Network,
    Timeout,
    Content,
    Unknown,
}

internal sealed class GeminiRequestException : Exception
{
    public GeminiRequestException(
        GeminiErrorCategory category,
        string message,
        bool retryable,
        HttpStatusCode? statusCode = null,
        Exception? innerException = null)
        : base(message, innerException)
    {
        Category = category;
        Retryable = retryable;
        StatusCode = statusCode;
    }

    public GeminiErrorCategory Category { get; }
    public bool Retryable { get; }
    public HttpStatusCode? StatusCode { get; }
}

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
