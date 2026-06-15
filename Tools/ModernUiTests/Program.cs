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

Console.WriteLine("Modern UI subtitle tests passed");
