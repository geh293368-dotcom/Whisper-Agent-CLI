using System.IO;
using System.Text.Json;

namespace WhisperDesktop.Modern.Services;

internal sealed class AppSettingsStore
{
    static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    readonly string configPath;

    public AppSettingsStore()
    {
        string configDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "WhisperDesktop");
        configPath = Path.Combine(configDirectory, "config.json");
    }

    public AppConfig? Load(Action<string, Exception?>? warn = null)
    {
        try
        {
            if (!File.Exists(configPath))
                return null;

            string json = File.ReadAllText(configPath);
            return JsonSerializer.Deserialize<AppConfig>(json, JsonOptions);
        }
        catch (Exception ex)
        {
            warn?.Invoke("加载配置失败", ex);
            return null;
        }
    }

    public void Save(AppConfig config, Action<string, Exception?>? warn = null)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(configPath)!);
            string json = JsonSerializer.Serialize(config, JsonOptions);
            File.WriteAllText(configPath, json);
        }
        catch (Exception ex)
        {
            warn?.Invoke("保存配置失败", ex);
        }
    }

    public sealed class AppConfig
    {
        public string SelectedEngine { get; set; } = "cuda";
        public string SelectedLanguage { get; set; } = "zh";
        public string SelectedFormat { get; set; } = "srt";
        public string SelectedOutputLocation { get; set; } = "selected";
        public string OutputFolder { get; set; } = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        public bool Translate { get; set; }
        public string ModelPath { get; set; } = string.Empty;
        public bool AutoLoadModel { get; set; } = true;
        public string UiScale { get; set; } = "medium";
        public string AiModelProvider { get; set; } = "gemini";
        public string GeminiModel { get; set; } = GeminiModelClient.DefaultModel;
        public string LocalAiModel { get; set; } = OpenAiCompatibleModelClient.DefaultModel;
        public string LocalAiBaseUrl { get; set; } = OpenAiCompatibleModelClient.DefaultBaseUrl;
        public string AiSubtitleOutputPolicy { get; set; } = "overwriteBackup";
        public List<string> RecentModels { get; set; } = [];
        public string? SelectedCaptureEndpoint { get; set; }
        public bool TerminologyEnabled { get; set; }
        public bool DeveloperDiagnostics { get; set; }
        public List<string>? SelectedTerminologyPacks { get; set; }
    }
}
