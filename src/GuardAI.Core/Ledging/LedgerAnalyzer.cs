namespace GuardAI.Core.Ledging;

/// <summary>
/// 单个违规指纹的闭环状态。
/// </summary>
/// <param name="Fingerprint">违规指纹。</param>
/// <param name="Rule">命中规则。</param>
/// <param name="File">违规文件相对路径。</param>
/// <param name="Message">违规说明。</param>
/// <param name="Status">状态：open（最新运行仍报）/ fixed（指纹消失 = 已修复）。</param>
/// <param name="Occurrences">出现次数（覆盖的运行数）。</param>
/// <param name="FirstSeen">首次发现时间。</param>
/// <param name="LastSeen">最近一次发现时间。</param>
public sealed record FingerprintStatus(
    string Fingerprint,
    string Rule,
    string File,
    string Message,
    string Status,
    int Occurrences,
    DateTimeOffset FirstSeen,
    DateTimeOffset LastSeen);

/// <summary>
/// 台账闭环分析汇总。
/// </summary>
/// <param name="TotalRuns">运行总数。</param>
/// <param name="FirstRun">首次运行时间（无记录时为 null）。</param>
/// <param name="LastRun">最近运行时间（无记录时为 null）。</param>
/// <param name="OpenCount">未闭环指纹数（最新运行仍报）。</param>
/// <param name="FixedCount">已闭环指纹数（指纹消失 = 已修复）。</param>
/// <param name="ClosureRate">闭环率（已闭环 / 全部历史指纹；无指纹时为 1）。</param>
/// <param name="Fingerprints">指纹状态明细（未闭环在前，同状态内按最近发现倒序）。</param>
public sealed record LedgerSummary(
    int TotalRuns,
    DateTimeOffset? FirstRun,
    DateTimeOffset? LastRun,
    int OpenCount,
    int FixedCount,
    double ClosureRate,
    IReadOnlyList<FingerprintStatus> Fingerprints);

/// <summary>
/// 台账闭环分析器：把运行历史折叠成"违规指纹 → 修复状态"的追踪视图。
/// </summary>
/// <remarks>
/// 闭环判定规则（产品语义）：违规指纹从最新运行中消失 = 已修复。
/// 若修复后违规复发（指纹重新出现），状态回到 open——闭环率如实回退，不做粉饰。
/// </remarks>
public static class LedgerAnalyzer
{
    /// <summary>指纹状态：未闭环（最新运行仍报）。</summary>
    public const string StatusOpen = "open";

    /// <summary>指纹状态：已闭环（指纹消失 = 已修复）。</summary>
    public const string StatusFixed = "fixed";

    /// <summary>
    /// 分析运行历史，输出闭环汇总。
    /// </summary>
    /// <param name="runs">运行记录列表（按时间先后）。</param>
    /// <returns>闭环汇总。</returns>
    /// <exception cref="ArgumentNullException">runs 为 null 时抛出。</exception>
    public static LedgerSummary Analyze(IReadOnlyList<LedgerRun> runs)
    {
        ArgumentNullException.ThrowIfNull(runs);

        var lastRun = runs.Count > 0 ? runs[^1] : null;
        var openInLast = lastRun is null
            ? new HashSet<string>(StringComparer.Ordinal)
            : new HashSet<string>(lastRun.Violations.Select(v => v.Fingerprint), StringComparer.Ordinal);

        // 按指纹聚合：每条指纹保留首见/末见/出现次数与代表性三要素
        var stats = new Dictionary<string, (LedgerViolation Sample, int Count, DateTimeOffset First, DateTimeOffset Last)>(StringComparer.Ordinal);
        foreach (var run in runs)
        {
            var seenThisRun = new HashSet<string>(StringComparer.Ordinal);
            foreach (var v in run.Violations)
            {
                if (!seenThisRun.Add(v.Fingerprint))
                {
                    // 同一运行内同一指纹出现多次只计一次（出现次数 = 覆盖的运行数）
                    continue;
                }

                if (stats.TryGetValue(v.Fingerprint, out var acc))
                {
                    stats[v.Fingerprint] = (acc.Sample, acc.Count + 1,
                        acc.First < run.Timestamp ? acc.First : run.Timestamp,
                        acc.Last > run.Timestamp ? acc.Last : run.Timestamp);
                }
                else
                {
                    stats[v.Fingerprint] = (v, 1, run.Timestamp, run.Timestamp);
                }
            }
        }

        var statuses = stats
            .Select(kv =>
            {
                var (sample, count, first, last) = kv.Value;
                return new FingerprintStatus(
                    kv.Key,
                    sample.Rule,
                    sample.File,
                    sample.Message,
                    openInLast.Contains(kv.Key) ? StatusOpen : StatusFixed,
                    count,
                    first,
                    last);
            })
            .OrderBy(s => s.Status == StatusOpen ? 0 : 1)
            .ThenByDescending(s => s.LastSeen)
            .ToList();

        var open = statuses.Count(s => s.Status == StatusOpen);
        var fixedCount = statuses.Count - open;
        var closureRate = statuses.Count == 0 ? 1.0 : (double)fixedCount / statuses.Count;

        return new LedgerSummary(
            TotalRuns: runs.Count,
            FirstRun: runs.Count > 0 ? runs[0].Timestamp : null,
            LastRun: lastRun?.Timestamp,
            OpenCount: open,
            FixedCount: fixedCount,
            ClosureRate: closureRate,
            Fingerprints: statuses);
    }
}
