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
}

public sealed class TranscriptionJob : INotifyPropertyChanged
{
    JobState state;
    double progress;
    string statusText = "等待中";
    string? error;

    public required string InputPath { get; init; }
    public string FileName => Path.GetFileName(InputPath);
    public string? OutputPath { get; set; }

    public JobState State
    {
        get => state;
        set { state = value; OnPropertyChanged(); }
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
