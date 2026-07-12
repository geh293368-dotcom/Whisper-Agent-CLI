using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;

namespace WhisperDesktop.Modern.Models;

public enum JobState
{
    Pending,
    Running,
    Completed,
    Failed,
    Canceled,
    Skipped,
    Interrupted,
}

public sealed class TranscriptionJob : INotifyPropertyChanged
{
    JobState state;
    double progress;
    string statusText = "等待中";
    string? error;
    bool isSelected = true;

    public required string JobId { get; init; }
    public required string InputPath { get; init; }
    public string? SourceRoot { get; init; }
    public string? ClientRequestId { get; set; }
    public DateTime CreatedAtUtc { get; init; } = DateTime.UtcNow;
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
    public string FileName => Path.GetFileName(InputPath);
    public string FolderName => Path.GetDirectoryName(InputPath) ?? string.Empty;
    public string RelativePath => string.IsNullOrWhiteSpace(SourceRoot)
        ? FileName
        : Path.GetRelativePath(SourceRoot, InputPath);
    public string? OutputPath { get; set; }
    public TimeSpan? Duration { get; set; }
    public TimeSpan? Elapsed { get; set; }
    public string EngineId { get; set; } = string.Empty;
    public string LanguageId { get; set; } = string.Empty;
    public string FormatId { get; set; } = string.Empty;
    public string OutputLocationId { get; set; } = string.Empty;
    public string ConfiguredOutputFolder { get; set; } = string.Empty;
    public bool Translate { get; set; }

    public bool IsSelected
    {
        get => isSelected;
        set { isSelected = value; OnPropertyChanged(); }
    }

    public JobState State
    {
        get => state;
        set
        {
            state = value;
            UpdatedAtUtc = DateTime.UtcNow;
            OnPropertyChanged();
        }
    }

    public double Progress
    {
        get => progress;
        set { progress = value; OnPropertyChanged(); }
    }

    public string StatusText
    {
        get => statusText;
        set { statusText = value; OnPropertyChanged(); }
    }

    public string? Error
    {
        get => error;
        set { error = value; OnPropertyChanged(); }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
