using System.Text.Json;
using System.Text.Json.Serialization;
using GuardAI.Core.Config;
using GuardAI.Core.Engine;

namespace GuardAI.Checks.Assets;

/// <summary>
/// 能力清单条目：一个可复用的公共方法实现。
/// </summary>
/// <param name="TypeName">所属类型名（嵌套类型取最内层）。</param>
/// <param name="Name">方法名。</param>
/// <param name="Signature">归一化单行签名。</param>
/// <param name="File">仓库相对路径（正斜杠）。</param>
/// <param name="Line">声明起始行号（1 起）。</param>
/// <param name="BodyHash">方法体归一化哈希（重复检测线索）。</param>
/// <param name="Summary">XML 文档注释摘要（中文检索命中面；无注释为 null）。</param>
public sealed record CapabilityEntry(
    string TypeName,
    string Name,
    string Signature,
    string File,
    int Line,
    string BodyHash,
    string? Summary = null);

/// <summary>
/// 能力清单（capability-index）：仓库全部公共方法的确定性快照。
/// </summary>
/// <remarks>
/// 用途：写码前强制检索（P3-M3）与重复检测（P3-M2）的共同数据基础。
/// 生成原则：CI 自动生成不靠人手——随 guardAI index / check 确定性重建。
/// </remarks>
/// <param name="GeneratedAt">生成时间（ISO 8601）。</param>
/// <param name="RepoPath">目标仓库根目录。</param>
/// <param name="Entries">能力条目（按文件/行号排序）。</param>
public sealed record CapabilityIndex(
    string GeneratedAt,
    string RepoPath,
    IReadOnlyList<CapabilityEntry> Entries)
{
    /// <summary>
    /// 默认排除模式：测试代码不进能力清单（v0.3.1-③）。
    /// 写码前检索只应命中生产实现，测试方法（CacheServiceTests.*）混入会误导复用；
    /// C# 只用 *Tests.cs 复数后缀——*Test.cs 单数会误伤 Contest.cs 之类（只漏不误）；
    /// 其余语言按各自测试命名惯例补排除（P4 多语言）。
    /// </summary>
    public static readonly IReadOnlyList<string> DefaultExcludeGlobs =
        [
            "**/tests/**", "**/test/**", "**/__tests__/**", "**/*Tests/**", "**/*Tests.cs",
            "**/*Test.java", "**/*Tests.py", "**/test_*.py", "**/*_test.py", "**/*_test.go",
            "**/*.test.ts", "**/*.test.js", "**/*.spec.ts", "**/*.spec.js",
        ];

    /// <summary>
    /// 扫描仓库并生成能力清单（全量模式、gitignore 感知、测试代码默认排除）。
    /// </summary>
    /// <param name="repoPath">目标仓库根目录。</param>
    /// <param name="excludeGlobs">
    /// 排除路径模式（通配符）；null = 自动读 guardai.yml 的 capability.exclude，
    /// 配置段缺失回落 <see cref="DefaultExcludeGlobs"/>，显式空列表 = 不排除任何文件。
    /// </param>
    /// <returns>能力清单。</returns>
    /// <exception cref="ArgumentNullException">repoPath 为 null 时抛出。</exception>
    public static CapabilityIndex Build(string repoPath, IReadOnlyList<string>? excludeGlobs = null)
    {
        ArgumentNullException.ThrowIfNull(repoPath);

        var excludes = excludeGlobs ?? ResolveExcludeGlobs(repoPath);
        var files = TargetFileCollector.Collect(repoPath, CheckMode.All, MemberParserRegistry.Extensions)
            .Where(f => !excludes.Any(g => GlobMatcher.IsMatch(f, g)))
            .ToList();
        var entries = new List<CapabilityEntry>();
        foreach (var file in files)
        {
            var text = File.ReadAllText(Path.Combine(repoPath, file.Replace('/', Path.DirectorySeparatorChar)));
            foreach (var member in MemberParserRegistry.Parse(text, file))
            {
                entries.Add(new CapabilityEntry(
                    member.TypeName, member.Name, member.Signature, member.File, member.Line, member.BodyHash,
                    member.Summary.Length > 0 ? member.Summary : null));
            }
        }

        var sorted = entries
            .OrderBy(e => e.File, StringComparer.OrdinalIgnoreCase)
            .ThenBy(e => e.Line)
            .ToList();
        return new CapabilityIndex(DateTimeOffset.Now.ToString("o"), Path.GetFullPath(repoPath), sorted);
    }

    /// <summary>
    /// 解析生效的排除模式：guardai.yml 的 capability.exclude 优先，配置缺失/损坏回落默认测试模式。
    /// </summary>
    /// <remarks>清单生成不被坏配置卡死（自动生成不靠人手）；配置损坏由 check 命令给出正式报错。</remarks>
    /// <param name="repoPath">目标仓库根目录。</param>
    /// <returns>排除模式列表。</returns>
    private static IReadOnlyList<string> ResolveExcludeGlobs(string repoPath)
    {
        var configPath = Path.Combine(repoPath, "guardai.yml");
        if (!File.Exists(configPath))
        {
            return DefaultExcludeGlobs;
        }

        try
        {
            return StructureRuleConfigLoader.Load(configPath).CapabilityExclude ?? DefaultExcludeGlobs;
        }
        catch (FormatException)
        {
            return DefaultExcludeGlobs;
        }
    }

    /// <summary>
    /// 按关键词检索能力（大小写不敏感，命中方法名优先，其次类型名、注释摘要、签名/文件路径）。
    /// 注释摘要命中面让中文词（如「缓存」）能命中英文标识符实现（如 CacheService）。
    /// </summary>
    /// <param name="keyword">检索关键词。</param>
    /// <param name="limit">返回上限（0 或负数回落默认 20）。</param>
    /// <returns>匹配的能力条目。</returns>
    /// <exception cref="ArgumentNullException">keyword 为 null 时抛出。</exception>
    /// <exception cref="ArgumentException">keyword 为空白时抛出。</exception>
    public IReadOnlyList<CapabilityEntry> Search(string keyword, int limit = 20)
    {
        ArgumentNullException.ThrowIfNull(keyword);
        if (string.IsNullOrWhiteSpace(keyword))
        {
            throw new ArgumentException("检索关键词不能为空。", nameof(keyword));
        }

        return Entries
            .Select(e => (Entry: e, Rank: MatchRank(e, keyword)))
            .Where(x => x.Rank > 0)
            .OrderBy(x => x.Rank)
            .ThenBy(x => x.Entry.File, StringComparer.OrdinalIgnoreCase)
            .ThenBy(x => x.Entry.Line)
            .Take(limit > 0 ? limit : 20)
            .Select(x => x.Entry)
            .ToList();
    }

    /// <summary>
    /// 计算匹配档位：1=方法名命中，2=类型名命中，3=注释摘要命中，4=签名/文件命中，0=不匹配。
    /// </summary>
    private static int MatchRank(CapabilityEntry entry, string keyword)
    {
        if (entry.Name.Contains(keyword, StringComparison.OrdinalIgnoreCase))
        {
            return 1;
        }

        if (entry.TypeName.Contains(keyword, StringComparison.OrdinalIgnoreCase))
        {
            return 2;
        }

        if (!string.IsNullOrEmpty(entry.Summary)
            && entry.Summary.Contains(keyword, StringComparison.OrdinalIgnoreCase))
        {
            return 3;
        }

        return entry.Signature.Contains(keyword, StringComparison.OrdinalIgnoreCase)
            || entry.File.Contains(keyword, StringComparison.OrdinalIgnoreCase)
            ? 4
            : 0;
    }
}

/// <summary>
/// 能力清单存取（.guard/capability-index.json，记录不出本机）。
/// </summary>
public static class CapabilityIndexStore
{
    /// <summary>默认清单文件名。</summary>
    public const string FileName = "capability-index.json";

    /// <summary>
    /// 计算默认清单路径（.guard/capability-index.json）。
    /// </summary>
    /// <param name="repoPath">目标仓库根目录。</param>
    /// <returns>清单文件完整路径。</returns>
    /// <exception cref="ArgumentNullException">repoPath 为 null 时抛出。</exception>
    public static string DefaultPath(string repoPath)
    {
        ArgumentNullException.ThrowIfNull(repoPath);
        return Path.Combine(repoPath, ".guard", FileName);
    }

    /// <summary>
    /// 保存能力清单（JSON，camelCase，覆盖写）。
    /// </summary>
    /// <param name="filePath">目标文件路径。</param>
    /// <param name="index">能力清单。</param>
    /// <exception cref="ArgumentNullException">filePath 或 index 为 null 时抛出。</exception>
    public static void Save(string filePath, CapabilityIndex index)
    {
        ArgumentNullException.ThrowIfNull(filePath);
        ArgumentNullException.ThrowIfNull(index);

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(filePath))!);
        File.WriteAllText(filePath, JsonSerializer.Serialize(index, SerializerOptions));
    }

    /// <summary>
    /// 读取能力清单。
    /// </summary>
    /// <param name="filePath">清单文件路径。</param>
    /// <returns>能力清单。</returns>
    /// <exception cref="ArgumentNullException">filePath 为 null 时抛出。</exception>
    /// <exception cref="FileNotFoundException">清单不存在时抛出。</exception>
    /// <exception cref="FormatException">清单 JSON 损坏时抛出。</exception>
    public static CapabilityIndex Load(string filePath)
    {
        ArgumentNullException.ThrowIfNull(filePath);
        if (!File.Exists(filePath))
        {
            throw new FileNotFoundException($"能力清单不存在: {filePath}。请先运行 'guardAI index' 生成。", filePath);
        }

        try
        {
            return JsonSerializer.Deserialize<CapabilityIndex>(File.ReadAllText(filePath), SerializerOptions)
                ?? throw new FormatException($"能力清单内容为空: {filePath}");
        }
        catch (JsonException ex)
        {
            throw new FormatException($"能力清单 JSON 损坏: {filePath}（{ex.Message}）", ex);
        }
    }

    /// <summary>JSON 序列化选项：camelCase + 不转义中文。</summary>
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
}
