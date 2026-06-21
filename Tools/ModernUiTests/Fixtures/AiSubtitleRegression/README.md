# AI Subtitle Regression Fixtures

这些样本来自 `SampleClips/ProblemCases` 的三段 Unity 游戏特效课程字幕，用于回归检查字幕优化和报告输出。

保留内容：

- 原始 `.srt`
- AI 优化后的 `.optimized.srt`
- 单文件 `.ai-report.md` / `.ai-report.json`
- 批量 `ai-batch-report-20260616-134614.md` / `.json`

不保留内容：

- `.mp4` 等大媒体文件。原视频只应留在本机 `SampleClips/ProblemCases`，不要提交到 Git。

样本规模：

| 文件 | 原始字幕 | 优化字幕 | 报告 |
| --- | --- | --- | --- |
| `03 地面效果3` | 686 条 | 686 条 | 75 -> 88 |
| `05 空中效果1` | 740 条 | 740 条 | 65 -> 92 |
| `07 爆点制作` | 901 条 | 901 条 | 75 -> 92 |
