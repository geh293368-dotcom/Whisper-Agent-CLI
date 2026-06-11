using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace WhisperDesktop.Modern.Models;

public sealed class SourceFolderItem : INotifyPropertyChanged
{
    bool isSelected = true;
    int fileCount;

    public required string Path { get; init; }
    public string Name => System.IO.Path.GetFileName(Path.TrimEnd(System.IO.Path.DirectorySeparatorChar)) is { Length: > 0 } name
        ? name
        : Path;
    public int FileCount
    {
        get => fileCount;
        set { fileCount = value; OnPropertyChanged(); }
    }

    public bool IsSelected
    {
        get => isSelected;
        set { isSelected = value; OnPropertyChanged(); }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public sealed record SubtitlePreviewItem(
    int Index,
    string FileName,
    TimeSpan Begin,
    TimeSpan End,
    string Text)
{
    public string BeginText => Format(Begin);
    public string EndText => Format(End);
    public string RangeText => $"{BeginText} -> {EndText}";

    static string Format(TimeSpan value) => $"{(int)value.TotalHours:00}:{value.Minutes:00}:{value.Seconds:00}.{value.Milliseconds:000}";
}

public sealed record CaptureDeviceOption(string Name, string Endpoint);
