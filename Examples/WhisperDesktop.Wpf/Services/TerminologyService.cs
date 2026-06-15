using System.IO;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace WhisperDesktop.Modern.Services;

public sealed class TerminologyService
{
    const int MaximumPromptTerms = 60;
    const int MaximumPromptCharacters = 320;
    static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = true,
    };

    readonly string directory;

    public TerminologyService()
    {
        directory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "WhisperDesktop",
            "terminology");
    }

    public string DirectoryPath => directory;

    public IReadOnlyList<TerminologyPack> LoadPacks(Action<string, Exception?>? warn = null)
    {
        Directory.CreateDirectory(directory);
        EnsureDefaultPacks();

        var packs = new List<TerminologyPack>();
        foreach (string path in Directory.EnumerateFiles(directory, "*.json").OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                string json = File.ReadAllText(path);
                TerminologyPack? pack = JsonSerializer.Deserialize<TerminologyPack>(json, JsonOptions);
                if (pack is null || string.IsNullOrWhiteSpace(pack.Id))
                    continue;
                pack.FilePath = path;
                packs.Add(pack);
            }
            catch (Exception ex)
            {
                warn?.Invoke($"词库文件解析失败：{path}", ex);
            }
        }

        return packs
            .GroupBy(pack => pack.Id, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.OrderByDescending(pack => pack.Priority).First())
            .OrderByDescending(pack => pack.Priority)
            .ThenBy(pack => pack.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public ActiveTerminology BuildActiveTerminology(
        IReadOnlyList<TerminologyPack> packs,
        IReadOnlyList<string> selectedPackIds)
    {
        HashSet<string> selected = selectedPackIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
        TerminologyPack[] activePacks = packs
            .Where(pack => pack.Enabled && selected.Contains(pack.Id))
            .OrderByDescending(pack => pack.Priority)
            .ToArray();

        var terms = activePacks
            .SelectMany(pack => pack.Terms.Select(term => (pack, term)))
            .Where(item => item.term.Enabled && !string.IsNullOrWhiteSpace(item.term.Text))
            .OrderByDescending(item => item.term.Priority + item.pack.Priority)
            .ToArray();

        string? prompt = BuildPrompt(terms.Select(item => item.term), out int promptTermCount);
        IReadOnlyList<TermCorrection> corrections = BuildCorrections(terms.Select(item => item.term));
        return new ActiveTerminology(activePacks, prompt, promptTermCount, corrections);
    }

    static string? BuildPrompt(IEnumerable<TerminologyTerm> source, out int promptTermCount)
    {
        promptTermCount = 0;
        var builder = new StringBuilder("本音频可能包含这些术语和专有名词，请优先使用标准写法：");
        int count = 0;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (TerminologyTerm term in source.Where(term => term.UseInPrompt))
        {
            string text = term.Text.Trim();
            if (text.Length == 0 || !seen.Add(text))
                continue;

            string next = count == 0 ? text : "、" + text;
            if (count >= MaximumPromptTerms || builder.Length + next.Length + 1 > MaximumPromptCharacters)
                break;
            builder.Append(next);
            count++;
        }

        if (count == 0)
            return null;
        promptTermCount = count;
        builder.Append('。');
        return builder.ToString();
    }

    static IReadOnlyList<TermCorrection> BuildCorrections(IEnumerable<TerminologyTerm> source)
    {
        var result = new List<TermCorrection>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (TerminologyTerm term in source)
        {
            string target = term.Text.Trim();
            foreach (string correction in term.Corrections ?? [])
            {
                string sourceText = correction.Trim();
                if (sourceText.Length == 0 || sourceText.Equals(target, StringComparison.OrdinalIgnoreCase))
                    continue;
                string key = sourceText + "\u001f" + target;
                if (seen.Add(key))
                    result.Add(new TermCorrection(sourceText, target));
            }
        }

        return result
            .OrderByDescending(item => item.Source.Length)
            .ThenBy(item => item.Source, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    void EnsureDefaultPacks()
    {
        if (Directory.EnumerateFiles(directory, "*.json").Any())
            return;

        WriteDefaultPack(new TerminologyPack
        {
            Id = "game-vfx-course",
            Name = "Unity 游戏特效课程",
            Description = "来自 docs/字幕质量评估 中的 Unity、MOBA 和游戏特效错词",
            Priority = 90,
            Terms =
            [
                new() { Text = "Unity", Category = "软件", Priority = 100, Corrections = ["优内体", "尤尼体"] },
                new() { Text = "Photoshop", Aliases = ["PS"], Category = "软件", Priority = 85, Corrections = ["佛头少", "破头少"] },
                new() { Text = "3ds Max", Category = "软件", Priority = 85, Corrections = ["三dmax", "3D max"] },
                new() { Text = "MOBA", Category = "游戏类型", Priority = 80, Corrections = ["摩巴"] },
                new() { Text = "光晕", Aliases = ["Glow"], Category = "特效", Priority = 100, Corrections = ["光运"] },
                new() { Text = "拖尾", Category = "特效", Priority = 100, Corrections = ["脱尾", "托尾", "油尾"] },
                new() { Text = "溶解", Category = "材质", Priority = 95, Corrections = ["融解"] },
                new() { Text = "法阵", Category = "特效", Priority = 95, Corrections = ["法针"] },
                new() { Text = "粒子", Category = "特效", Priority = 95, Corrections = ["微子"] },
                new() { Text = "地裂", Category = "特效", Priority = 90, Corrections = ["滴裂"] },
                new() { Text = "衬底", Category = "美术", Priority = 85, Corrections = ["称底"] },
                new() { Text = "UV", Category = "模型", Priority = 85, Corrections = ["优位"] },
                new() { Text = "软粒子", Category = "特效", Priority = 85, Corrections = ["蓝粒子"] },
                new() { Text = "Glow 材质", Category = "材质", Priority = 85, Corrections = ["光运材质"] },
                new() { Text = "贴图", Category = "美术", Priority = 80, Corrections = ["铁图"] },
                new() { Text = "渐强", Category = "节奏", Priority = 75, Corrections = ["健强"] },
            ],
        }, "game-vfx-course.json");

        WriteDefaultPack(new TerminologyPack
        {
            Id = "hand-drawn-animation",
            Name = "手绘动画教程",
            Description = "来自 docs/字幕质量评估 中的手绘、关键帧和动画错词",
            Priority = 80,
            Terms =
            [
                new() { Text = "手绘", Aliases = ["Hand-drawn"], Category = "动画", Priority = 100, Corrections = ["手会"] },
                new() { Text = "关键帧", Aliases = ["Keyframe"], Category = "动画", Priority = 100, Corrections = ["关键针", "关键真", "关键震", "关机针"] },
                new() { Text = "K帧", Aliases = ["Keyframing"], Category = "动画", Priority = 95, Corrections = ["K针"] },
                new() { Text = "空白关键帧", Aliases = ["Blank Keyframe"], Category = "动画", Priority = 95, Corrections = ["购买关键针", "普通针"] },
                new() { Text = "压感", Aliases = ["Pen Pressure"], Category = "绘制", Priority = 90, Corrections = ["压杆"] },
                new() { Text = "极值帧", Aliases = ["Extreme Frame", "极致帧"], Category = "动画", Priority = 90, Corrections = ["极致针"] },
                new() { Text = "流体力学", Category = "运动规律", Priority = 85, Corrections = ["流伦力学"] },
                new() { Text = "静止", Category = "运动规律", Priority = 75, Corrections = ["近者"] },
                new() { Text = "重力", Category = "运动规律", Priority = 75, Corrections = ["中立"] },
                new() { Text = "双击", Category = "操作", Priority = 70, Corrections = ["酸基"] },
                new() { Text = "新建", Category = "操作", Priority = 70, Corrections = ["行建"] },
                new() { Text = "光影", Category = "美术", Priority = 70, Corrections = ["观影"] },
                new() { Text = "三者", Category = "普通词", Priority = 60, Corrections = ["上则"] },
            ],
        }, "hand-drawn-animation.json");

        WriteDefaultPack(new TerminologyPack
        {
            Id = "xianxia-novel",
            Name = "修仙小说",
            Description = "玄幻、修仙、有声小说常见境界和设定词",
            Priority = 20,
            Terms =
            [
                new() { Text = "炼气", Category = "境界", Priority = 80, Corrections = ["练气"] },
                new() { Text = "筑基", Category = "境界", Priority = 90, Corrections = ["住基", "铸基"] },
                new() { Text = "金丹", Category = "境界", Priority = 90, Corrections = ["金蛋"] },
                new() { Text = "元婴", Category = "境界", Priority = 90, Corrections = ["原因", "原婴"] },
                new() { Text = "化神", Category = "境界", Priority = 80, Corrections = ["化身"] },
                new() { Text = "渡劫", Category = "境界", Priority = 80, Corrections = ["度劫"] },
                new() { Text = "灵石", Category = "物品", Priority = 70, Corrections = ["零食"] },
                new() { Text = "青云宗", Category = "宗门", Priority = 70, Corrections = ["青云中"] },
            ],
        }, "xianxia-novel.json");
    }

    void WriteDefaultPack(TerminologyPack pack, string fileName)
    {
        string path = Path.Combine(directory, fileName);
        if (File.Exists(path))
            return;
        string json = JsonSerializer.Serialize(pack, JsonOptions);
        File.WriteAllText(path, json, new UTF8Encoding(false));
    }
}

public sealed class TerminologyPack
{
    public int SchemaVersion { get; set; } = 1;
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public bool Enabled { get; set; } = true;
    public int Priority { get; set; }
    public List<TerminologyTerm> Terms { get; set; } = [];
    public string? FilePath { get; set; }
}

public sealed class TerminologyTerm
{
    public string Text { get; set; } = string.Empty;
    public List<string> Aliases { get; set; } = [];
    public string Category { get; set; } = string.Empty;
    public int Priority { get; set; } = 50;
    public bool Enabled { get; set; } = true;
    public bool UseInPrompt { get; set; } = true;
    public List<string> Corrections { get; set; } = [];
}

public sealed record ActiveTerminology(
    IReadOnlyList<TerminologyPack> Packs,
    string? InitialPrompt,
    int PromptTermCount,
    IReadOnlyList<TermCorrection> Corrections);
