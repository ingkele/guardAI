using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace GuardAI.Checks.Assets;

/// <summary>
/// 重复对基线记录：一对被接受的存量重复实现（只记身份，不记代码——记录不出本机）。
/// </summary>
/// <param name="Key">对指纹（两个成员键排序后的稳定哈希）。</param>
/// <param name="A">成员键 A（TypeName.Name@File）。</param>
/// <param name="B">成员键 B（TypeName.Name@File）。</param>
public sealed record DuplicatePairRecord(string Key, string A, string B);

/// <summary>
/// 重复检测基线（.guard/duplicates-baseline.json）：存量重复对快照，只报新增。
/// </summary>
/// <remarks>
/// 零误报铁律的落地：首跑把存量重复全部收进基线（不吵不闹），之后只有新产生的重复对会被检出。
/// 成员键不含行号——上文增删代码导致行号漂移不会让存量重复"复活"。
/// </remarks>
/// <param name="GeneratedAt">生成时间（ISO 8601）。</param>
/// <param name="Pairs">已接受的重复对。</param>
/// <param name="DetectorVersion">收编时的检测能力版本（旧基线文件缺省 1）；检测器升级后首跑自动把新发现的存量对并入基线，不轰炸存量债。</param>
public sealed record DuplicateBaseline(
    string GeneratedAt,
    IReadOnlyList<DuplicatePairRecord> Pairs,
    int DetectorVersion = 1)
{
    /// <summary>基线文件名。</summary>
    public const string FileName = "duplicates-baseline.json";

    /// <summary>
    /// 计算基线文件路径（.guard/duplicates-baseline.json）。
    /// </summary>
    /// <param name="repoPath">目标仓库根目录。</param>
    /// <returns>基线文件完整路径。</returns>
    /// <exception cref="ArgumentNullException">repoPath 为 null 时抛出。</exception>
    public static string FilePath(string repoPath)
    {
        ArgumentNullException.ThrowIfNull(repoPath);
        return Path.Combine(repoPath, ".guard", FileName);
    }

    /// <summary>
    /// 计算重复对指纹（两个成员键按序拼接后取稳定哈希前 16 位）。
    /// </summary>
    /// <param name="memberKeyA">成员键 A。</param>
    /// <param name="memberKeyB">成员键 B。</param>
    /// <returns>16 位十六进制对指纹。</returns>
    /// <exception cref="ArgumentNullException">任一键为 null 时抛出。</exception>
    public static string PairKey(string memberKeyA, string memberKeyB)
    {
        ArgumentNullException.ThrowIfNull(memberKeyA);
        ArgumentNullException.ThrowIfNull(memberKeyB);

        var ordered = new[] { memberKeyA, memberKeyB }
            .OrderBy(k => k, StringComparer.Ordinal)
            .ToArray();
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(ordered[0] + "|" + ordered[1]));
        return Convert.ToHexString(hash)[..16].ToLowerInvariant();
    }

    /// <summary>
    /// 读取基线；文件不存在返回 null（调用方据此走首跑基线初始化）。
    /// </summary>
    /// <param name="repoPath">目标仓库根目录。</param>
    /// <returns>基线；不存在时为 null。</returns>
    /// <exception cref="ArgumentNullException">repoPath 为 null 时抛出。</exception>
    /// <exception cref="FormatException">基线 JSON 损坏时抛出。</exception>
    public static DuplicateBaseline? Load(string repoPath)
    {
        ArgumentNullException.ThrowIfNull(repoPath);
        var path = FilePath(repoPath);
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<DuplicateBaseline>(File.ReadAllText(path), SerializerOptions)
                ?? throw new FormatException($"重复基线内容为空: {path}");
        }
        catch (JsonException ex)
        {
            throw new FormatException($"重复基线 JSON 损坏: {path}（{ex.Message}）", ex);
        }
    }

    /// <summary>
    /// 保存基线（覆盖写）。
    /// </summary>
    /// <param name="repoPath">目标仓库根目录。</param>
    /// <param name="baseline">基线内容。</param>
    /// <exception cref="ArgumentNullException">repoPath 或 baseline 为 null 时抛出。</exception>
    public static void Save(string repoPath, DuplicateBaseline baseline)
    {
        ArgumentNullException.ThrowIfNull(repoPath);
        ArgumentNullException.ThrowIfNull(baseline);

        var path = FilePath(repoPath);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        File.WriteAllText(path, JsonSerializer.Serialize(baseline, SerializerOptions));
    }

    /// <summary>
    /// 把新重复对合并进基线（按对指纹去重，保留历史标签）。
    /// </summary>
    /// <param name="existing">既有基线（可为 null，视作空）。</param>
    /// <param name="newPairs">新接受的重复对。</param>
    /// <returns>合并后的基线。</returns>
    /// <exception cref="ArgumentNullException">newPairs 为 null 时抛出。</exception>
    public static DuplicateBaseline Merge(DuplicateBaseline? existing, IReadOnlyList<DuplicatePairRecord> newPairs)
    {
        ArgumentNullException.ThrowIfNull(newPairs);

        var merged = new Dictionary<string, DuplicatePairRecord>(StringComparer.Ordinal);
        foreach (var pair in (existing?.Pairs ?? []).Concat(newPairs))
        {
            merged.TryAdd(pair.Key, pair);
        }

        // 保留既有收编版本（检测器升级收编场景由调用方用 with 覆写为新版）
        return new DuplicateBaseline(DateTimeOffset.Now.ToString("o"), merged.Values.ToList(), existing?.DetectorVersion ?? 1);
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
