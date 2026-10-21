using System.Text.Json;
using System.Text.Json.Serialization;
using GuardAI.Core.Engine;

namespace GuardAI.Core.Ledging;

/// <summary>
/// 台账中的单条违规（含稳定指纹，供闭环追踪）。
/// </summary>
/// <param name="Check">所属检查模块。</param>
/// <param name="File">违规文件相对路径。</param>
/// <param name="Rule">命中规则。</param>
/// <param name="Message">违规说明。</param>
/// <param name="Suggestion">修复建议。</param>
/// <param name="Fingerprint">违规指纹（跨运行稳定）。</param>
public sealed record LedgerViolation(
    string Check,
    string File,
    string Rule,
    string Message,
    string Suggestion,
    string Fingerprint);

/// <summary>
/// 台账中的单次检查运行记录。
/// </summary>
/// <param name="Id">运行标识（时间戳短码，便于人眼定位）。</param>
/// <param name="Timestamp">运行时间（ISO 8601）。</param>
/// <param name="Trigger">触发来源：cli / hook / ci 等。</param>
/// <param name="Mode">检查模式：changed / all。</param>
/// <param name="FilesScanned">扫描文件数。</param>
/// <param name="ViolationCount">违规数。</param>
/// <param name="Outcome">运行结论：pass / fail / empty（空扫描，结论不成立）。</param>
/// <param name="Violations">违规明细。</param>
public sealed record LedgerRun(
    string Id,
    DateTimeOffset Timestamp,
    string Trigger,
    string Mode,
    int FilesScanned,
    int ViolationCount,
    string Outcome,
    IReadOnlyList<LedgerViolation> Violations);

/// <summary>
/// 本地台账：检查运行留痕的写入与读取。
/// </summary>
/// <remarks>
/// 产品铁律：台账只落在目标仓库的 .guard/records/ 目录，记录不出本机。
/// 存储为 JSONL（runs.jsonl），每次运行追加一行——追加式写入天然抗中断，
/// 崩溃时最多损失半行，读取端会跳过损坏行。
/// </remarks>
public static class Ledger
{
    /// <summary>台账目录（相对目标仓库根）。</summary>
    public const string RecordsDir = ".guard/records";

    /// <summary>台账文件名（JSONL，每行一次运行）。</summary>
    public const string RecordsFile = "runs.jsonl";

    /// <summary>JSON 序列化选项：驼峰命名 + 宽松转义（中文可读）。</summary>
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>
    /// 计算台账文件的绝对路径。
    /// </summary>
    /// <param name="repoPath">目标仓库根目录。</param>
    /// <returns>runs.jsonl 的绝对路径。</returns>
    public static string RecordsPath(string repoPath)
    {
        return Path.GetFullPath(Path.Combine(repoPath, RecordsDir, RecordsFile));
    }

    /// <summary>
    /// 由一次检查的模块结果构建运行记录。
    /// </summary>
    /// <param name="repoPath">目标仓库根目录（用于生成运行标识与项目名）。</param>
    /// <param name="mode">检查模式。</param>
    /// <param name="trigger">触发来源（cli / hook / ci 等）。</param>
    /// <param name="results">各检查模块结果。</param>
    /// <returns>运行记录（含指纹）。</returns>
    /// <exception cref="ArgumentNullException">results 为 null 时抛出。</exception>
    public static LedgerRun CreateRun(string repoPath, CheckMode mode, string trigger, IReadOnlyList<CheckResult> results)
    {
        ArgumentNullException.ThrowIfNull(results);
        var now = DateTimeOffset.Now;
        var violations = results
            .SelectMany(r => r.Violations.Select(v => new LedgerViolation(
                r.Name,
                v.File,
                v.Rule,
                v.Message,
                v.Suggestion,
                Fingerprint.Compute(v))))
            .ToList();

        var filesScanned = results.Sum(r => r.Scanned);

        // 空扫描不记 "pass"（假绿防线同样适用于台账）：all 模式 0 文件 = 结论不成立
        var outcome = violations.Count > 0
            ? "fail"
            : mode == CheckMode.All && filesScanned == 0 ? "empty" : "pass";

        return new LedgerRun(
            Id: now.ToString("yyyyMMdd-HHmmss"),
            Timestamp: now,
            Trigger: string.IsNullOrWhiteSpace(trigger) ? "cli" : trigger,
            Mode: mode == CheckMode.Changed ? "changed" : "all",
            FilesScanned: filesScanned,
            ViolationCount: violations.Count,
            Outcome: outcome,
            Violations: violations);
    }

    /// <summary>
    /// 追加一次运行记录到台账（目录不存在时自动创建）。
    /// </summary>
    /// <param name="repoPath">目标仓库根目录。</param>
    /// <param name="run">运行记录。</param>
    /// <returns>台账文件的绝对路径。</returns>
    /// <exception cref="ArgumentNullException">repoPath 或 run 为 null 时抛出。</exception>
    public static string Append(string repoPath, LedgerRun run)
    {
        ArgumentNullException.ThrowIfNull(repoPath);
        ArgumentNullException.ThrowIfNull(run);

        var path = RecordsPath(repoPath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.AppendAllText(path, JsonSerializer.Serialize(run, JsonOptions) + Environment.NewLine);
        return path;
    }

    /// <summary>
    /// 读取全部运行记录（按写入顺序，即时间先后）。
    /// </summary>
    /// <param name="repoPath">目标仓库根目录。</param>
    /// <returns>运行记录列表；台账不存在时为空列表，损坏行自动跳过。</returns>
    /// <exception cref="ArgumentNullException">repoPath 为 null 时抛出。</exception>
    public static IReadOnlyList<LedgerRun> Read(string repoPath)
    {
        ArgumentNullException.ThrowIfNull(repoPath);

        var path = RecordsPath(repoPath);
        if (!File.Exists(path))
        {
            return [];
        }

        var runs = new List<LedgerRun>();
        foreach (var line in File.ReadLines(path))
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            try
            {
                var run = JsonSerializer.Deserialize<LedgerRun>(line, JsonOptions);
                if (run is not null)
                {
                    runs.Add(run);
                }
            }
            catch (JsonException)
            {
                // 追加式台账的容忍策略：崩溃残留的半行不应让整个台账不可用
            }
        }

        return runs;
    }
}
