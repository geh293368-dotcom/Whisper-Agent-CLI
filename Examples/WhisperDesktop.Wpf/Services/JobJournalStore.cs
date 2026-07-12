using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using WhisperDesktop.Modern.Models;

namespace WhisperDesktop.Modern.Services;

internal sealed class JobJournalStore
{
    const int CurrentVersion = 1;
    const int MaxPersistedJobs = 500;
    static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();

    readonly string journalPath;

    public string JournalPath => journalPath;

    public JobJournalStore()
    {
        string? overrideDirectory = Environment.GetEnvironmentVariable("WHISPERDESKTOP_DATA_DIR");
        string dataDirectory = string.IsNullOrWhiteSpace(overrideDirectory)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WhisperDesktop")
            : Path.GetFullPath(overrideDirectory);
        journalPath = Path.Combine(dataDirectory, "jobs.json");
    }

    static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            WriteIndented = true,
        };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        return options;
    }

    public IReadOnlyList<JobJournalEntry> Load(Action<string, Exception?>? warn = null)
    {
        try
        {
            if (!File.Exists(journalPath))
                return [];

            string json = File.ReadAllText(journalPath);
            JobJournalDocument? document = JsonSerializer.Deserialize<JobJournalDocument>(json, JsonOptions);
            return document?.Version == CurrentVersion ? document.Jobs : [];
        }
        catch (Exception ex)
        {
            warn?.Invoke("加载任务日志失败", ex);
            return [];
        }
    }

    public void Save(IEnumerable<TranscriptionJob> jobs, Action<string, Exception?>? warn = null)
    {
        try
        {
            string directory = Path.GetDirectoryName(journalPath)!;
            Directory.CreateDirectory(directory);
            var document = new JobJournalDocument
            {
                Version = CurrentVersion,
                SavedAtUtc = DateTime.UtcNow,
                Jobs = jobs
                    .OrderByDescending(job => job.UpdatedAtUtc)
                    .Take(MaxPersistedJobs)
                    .Select(JobJournalEntry.FromJob)
                    .ToList(),
            };

            string temporaryPath = journalPath + ".tmp";
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(document, JsonOptions));
            File.Move(temporaryPath, journalPath, overwrite: true);
        }
        catch (Exception ex)
        {
            warn?.Invoke("保存任务日志失败", ex);
        }
    }

    internal sealed class JobJournalDocument
    {
        public int Version { get; set; }
        public DateTime SavedAtUtc { get; set; }
        public List<JobJournalEntry> Jobs { get; set; } = [];
    }

    internal sealed class JobJournalEntry
    {
        public string JobId { get; set; } = string.Empty;
        public string InputPath { get; set; } = string.Empty;
        public string? SourceRoot { get; set; }
        public string? ClientRequestId { get; set; }
        public JobState State { get; set; }
        public double Progress { get; set; }
        public string StatusText { get; set; } = string.Empty;
        public string? Error { get; set; }
        public string? OutputPath { get; set; }
        public double? DurationSeconds { get; set; }
        public double? ElapsedSeconds { get; set; }
        public bool IsSelected { get; set; }
        public DateTime CreatedAtUtc { get; set; }
        public DateTime UpdatedAtUtc { get; set; }
        public string EngineId { get; set; } = string.Empty;
        public string LanguageId { get; set; } = string.Empty;
        public string FormatId { get; set; } = string.Empty;
        public string OutputLocationId { get; set; } = string.Empty;
        public string ConfiguredOutputFolder { get; set; } = string.Empty;
        public bool Translate { get; set; }

        public static JobJournalEntry FromJob(TranscriptionJob job) => new()
        {
            JobId = job.JobId,
            InputPath = job.InputPath,
            SourceRoot = job.SourceRoot,
            ClientRequestId = job.ClientRequestId,
            State = job.State,
            Progress = job.Progress,
            StatusText = job.StatusText,
            Error = job.Error,
            OutputPath = job.OutputPath,
            DurationSeconds = job.Duration?.TotalSeconds,
            ElapsedSeconds = job.Elapsed?.TotalSeconds,
            IsSelected = job.IsSelected,
            CreatedAtUtc = job.CreatedAtUtc,
            UpdatedAtUtc = job.UpdatedAtUtc,
            EngineId = job.EngineId,
            LanguageId = job.LanguageId,
            FormatId = job.FormatId,
            OutputLocationId = job.OutputLocationId,
            ConfiguredOutputFolder = job.ConfiguredOutputFolder,
            Translate = job.Translate,
        };
    }
}
