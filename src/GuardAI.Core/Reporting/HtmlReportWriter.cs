using System.Net;
using System.Text;
using GuardAI.Core.Engine;
using GuardAI.Core.Ledging;

namespace GuardAI.Core.Reporting;

/// <summary>
/// HTML 报告输出器：把检查结果渲染为单页体检报告（内联 CSS，零外部依赖，浏览器直接打开）。
/// </summary>
/// <remarks>
/// 与文本/JSON 报告同源同数据，额外提供"与上次运行对比"（新增/已修复/遗留）——
/// 对比数据来自台账（.guard/records/runs.jsonl）的最近一次运行，按违规指纹比对。
/// 报告须在本次台账追加之前生成，对比才不含本次运行。
/// </remarks>
public static class HtmlReportWriter
{
    /// <summary>
    /// 渲染单页 HTML 报告。
    /// </summary>
    /// <param name="writer">输出目标。</param>
    /// <param name="repoPath">目标仓库路径。</param>
    /// <param name="mode">检查模式。</param>
    /// <param name="configFile">规则配置文件名。</param>
    /// <param name="results">各检查模块结果。</param>
    /// <param name="previousRuns">台账既有运行记录（不含本次）；省略或为空列表时无对比数据。</param>
    /// <exception cref="ArgumentNullException">results 为 null 时抛出。</exception>
    public static void Write(
        TextWriter writer,
        string repoPath,
        CheckMode mode,
        string configFile,
        IReadOnlyList<CheckResult> results,
        IReadOnlyList<LedgerRun>? previousRuns = null)
    {
        ArgumentNullException.ThrowIfNull(results);
        previousRuns ??= [];

        var project = Path.GetFileName(Path.TrimEndingDirectorySeparator(repoPath));
        var totalViolations = results.Sum(r => r.Violations.Count);
        var totalScanned = results.Count == 0 ? 0 : results.Max(r => r.Scanned); // 口径统一（v0.3.1-④）：取最大值不加总，同一文件被多检查器扫描不双计
        var emptyScan = mode == CheckMode.All && totalScanned == 0;
        var outcome = emptyScan
            ? "空扫描警告"
            : totalViolations == 0 ? "通过" : "失败";

        // 与上次运行对比：按违规指纹（规则+文件+说明）比对，AI 移动代码不破坏追踪
        var comparison = BuildComparison(results, previousRuns);

        var sb = new StringBuilder();
        AppendHead(sb, project);
        AppendSummary(sb, project, repoPath, mode, configFile, totalScanned, totalViolations, outcome, emptyScan);
        AppendComparison(sb, comparison, previousRuns.Count > 0);
        AppendViolations(sb, results);
        AppendAiFixInstructions(sb, results);
        AppendTail(sb);

        writer.Write(sb.ToString());
    }

    /// <summary>本次与上次运行的指纹对比结果。</summary>
    /// <param name="New">新增违规（本次报、上次未报）。</param>
    /// <param name="Fixed">已修复（上次报、本次消失）。</param>
    /// <param name="Remaining">遗留（两次都报）。</param>
    private sealed record RunComparison(
        IReadOnlyList<(string Rule, string File, string Message)> New,
        IReadOnlyList<(string Rule, string File, string Message)> Fixed,
        int Remaining);

    /// <summary>
    /// 按违规指纹构建对比结果。
    /// </summary>
    private static RunComparison BuildComparison(IReadOnlyList<CheckResult> results, IReadOnlyList<LedgerRun> previousRuns)
    {
        var current = results
            .SelectMany(r => r.Violations)
            .Select(v => (Fingerprint: Fingerprint.Compute(v), v.Rule, v.File, v.Message))
            .GroupBy(x => x.Fingerprint) // 同指纹只计一次
            .Select(g => g.First())
            .ToList();

        if (previousRuns.Count == 0)
        {
            return new RunComparison(
                current.Select(x => (x.Rule, x.File, x.Message)).ToList(), [], 0);
        }

        var lastRun = previousRuns[^1];
        var prevSet = lastRun.Violations
            .Select(v => v.Fingerprint)
            .ToHashSet(StringComparer.Ordinal);

        var currentSet = current.Select(x => x.Fingerprint).ToHashSet(StringComparer.Ordinal);
        var newItems = current
            .Where(x => !prevSet.Contains(x.Fingerprint))
            .Select(x => (x.Rule, x.File, x.Message))
            .ToList();
        var fixedItems = lastRun.Violations
            .Where(v => !currentSet.Contains(v.Fingerprint))
            .GroupBy(v => v.Fingerprint)
            .Select(g => (g.First().Rule, g.First().File, g.First().Message))
            .ToList();
        var remaining = current.Count(x => prevSet.Contains(x.Fingerprint));

        return new RunComparison(newItems, fixedItems, remaining);
    }

    /// <summary>
    /// 输出文档头与样式（内联 CSS，零外部依赖）。
    /// </summary>
    private static void AppendHead(StringBuilder sb, string project)
    {
        sb.AppendLine("<!DOCTYPE html>");
        sb.AppendLine("<html lang=\"zh-CN\">");
        sb.AppendLine("<head>");
        sb.AppendLine("<meta charset=\"utf-8\">");
        sb.AppendLine("<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">");
        sb.AppendLine($"<title>guardAI 体检报告 - {Encode(project)}</title>");
        sb.AppendLine("""
            <style>
              :root { --bg: #f5f6f8; --card: #ffffff; --text: #1f2329; --muted: #6b7280;
                      --accent: #2563eb; --ok: #16a34a; --bad: #dc2626; --warn: #d97706; --line: #e5e7eb; }
              * { box-sizing: border-box; }
              body { margin: 0; padding: 24px; background: var(--bg); color: var(--text);
                     font: 14px/1.7 "Microsoft YaHei", "PingFang SC", system-ui, sans-serif; }
              .wrap { max-width: 960px; margin: 0 auto; }
              h1 { font-size: 22px; margin: 0 0 4px; }
              h2 { font-size: 16px; margin: 28px 0 12px; padding-left: 10px; border-left: 4px solid var(--accent); }
              .meta { color: var(--muted); font-size: 13px; margin-bottom: 20px; }
              .meta code { background: var(--card); padding: 1px 6px; border-radius: 4px; }
              .cards { display: flex; gap: 12px; flex-wrap: wrap; }
              .card { flex: 1 1 160px; background: var(--card); border: 1px solid var(--line);
                      border-radius: 10px; padding: 14px 16px; }
              .card .num { font-size: 26px; font-weight: 700; }
              .card .label { color: var(--muted); font-size: 12px; }
              .ok { color: var(--ok); } .bad { color: var(--bad); } .warn { color: var(--warn); }
              .banner { border-radius: 10px; padding: 12px 16px; margin: 16px 0; font-size: 13px; }
              .banner-warn { background: #fef3c7; border: 1px solid #fcd34d; color: #92400e; }
              table { width: 100%; border-collapse: collapse; background: var(--card);
                      border: 1px solid var(--line); border-radius: 10px; overflow: hidden; }
              th, td { text-align: left; padding: 8px 12px; border-bottom: 1px solid var(--line);
                       font-size: 13px; vertical-align: top; }
              th { background: #f9fafb; color: var(--muted); font-weight: 600; }
              tr:last-child td { border-bottom: none; }
              .violation { background: var(--card); border: 1px solid var(--line); border-radius: 10px;
                           padding: 12px 16px; margin-bottom: 10px; }
              .violation .file { font-family: Consolas, monospace; font-weight: 600; word-break: break-all; }
              .violation .rule { display: inline-block; background: #fee2e2; color: var(--bad);
                                 border-radius: 4px; padding: 0 8px; font-size: 12px; margin: 4px 0; }
              .violation .suggest { background: #f0f9ff; border-left: 3px solid var(--accent);
                                    padding: 6px 10px; margin-top: 8px; font-size: 13px; }
              ol.fix { background: var(--card); border: 1px solid var(--line); border-radius: 10px;
                       padding: 16px 16px 16px 40px; margin: 0; }
              ol.fix li { margin-bottom: 6px; font-size: 13px; word-break: break-all; }
              .empty { color: var(--muted); background: var(--card); border: 1px dashed var(--line);
                       border-radius: 10px; padding: 16px; text-align: center; }
              footer { color: var(--muted); font-size: 12px; margin-top: 32px; text-align: center; }
            </style>
            """);
        sb.AppendLine("</head>");
        sb.AppendLine("<body><div class=\"wrap\">");
    }

    /// <summary>
    /// 输出报告标题与汇总卡片。
    /// </summary>
    private static void AppendSummary(
        StringBuilder sb, string project, string repoPath, CheckMode mode, string configFile,
        int totalScanned, int totalViolations, string outcome, bool emptyScan)
    {
        sb.AppendLine($"<h1>guardAI 项目骨架体检报告 —— {Encode(project)}</h1>");
        sb.AppendLine($"<div class=\"meta\">生成时间：{DateTime.Now:yyyy-MM-dd HH:mm:ss}　|　检查模式：{Encode(mode == CheckMode.Changed ? "changed（仅 git 新增/改动文件）" : "all（全部文件）")}</div>");
        sb.AppendLine($"<div class=\"meta\">目标仓库：<code>{Encode(repoPath)}</code>　|　规则配置：<code>{Encode(configFile)}</code></div>");

        var outcomeClass = emptyScan ? "warn" : totalViolations == 0 ? "ok" : "bad";
        sb.AppendLine("<div class=\"cards\">");
        sb.AppendLine($"<div class=\"card\"><div class=\"num\">{totalScanned}</div><div class=\"label\">扫描文件数</div></div>");
        sb.AppendLine($"<div class=\"card\"><div class=\"num {(totalViolations > 0 ? "bad" : "ok")}\">{totalViolations}</div><div class=\"label\">违规数</div></div>");
        sb.AppendLine($"<div class=\"card\"><div class=\"num {outcomeClass}\">{Encode(outcome)}</div><div class=\"label\">检查结论</div></div>");
        sb.AppendLine("</div>");

        if (emptyScan)
        {
            sb.AppendLine("<div class=\"banner banner-warn\"><b>空扫描警告：</b>all 模式下没有匹配到任何代码文件，"
                + "“0 处违规”不构成合规结论。请运行 guardAI init --force 重新生成配置，"
                + "或手工补齐 include_extensions 后重新检查。</div>");
        }
    }

    /// <summary>
    /// 输出与上次运行的对比区块。
    /// </summary>
    private static void AppendComparison(StringBuilder sb, RunComparison comparison, bool hasPrevious)
    {
        sb.AppendLine("<h2>与上次运行对比</h2>");

        if (!hasPrevious)
        {
            sb.AppendLine("<div class=\"card\"><div class=\"num\">"
                + comparison.New.Count
                + "</div><div class=\"label\">本次违规（首次运行，无对比数据；累计追踪在 guardAI ui 台账网页）</div></div>");
            return;
        }

        sb.AppendLine("<div class=\"cards\">");
        sb.AppendLine($"<div class=\"card\"><div class=\"num bad\">+{comparison.New.Count}</div><div class=\"label\">新增违规</div></div>");
        sb.AppendLine($"<div class=\"card\"><div class=\"num ok\">-{comparison.Fixed.Count}</div><div class=\"label\">已修复</div></div>");
        sb.AppendLine($"<div class=\"card\"><div class=\"num\">{comparison.Remaining}</div><div class=\"label\">遗留未修</div></div>");
        sb.AppendLine("</div>");

        if (comparison.New.Count > 0)
        {
            sb.AppendLine("<table><tr><th>新增违规</th><th>文件</th><th>说明</th></tr>");
            foreach (var (rule, file, message) in comparison.New)
            {
                sb.AppendLine($"<tr><td>{Encode(rule)}</td><td><code>{Encode(file)}</code></td><td>{Encode(message)}</td></tr>");
            }

            sb.AppendLine("</table>");
        }

        if (comparison.Fixed.Count > 0)
        {
            sb.AppendLine("<table style=\"margin-top:10px\"><tr><th>已修复</th><th>文件</th><th>说明</th></tr>");
            foreach (var (rule, file, message) in comparison.Fixed)
            {
                sb.AppendLine($"<tr><td class=\"ok\">{Encode(rule)}</td><td><code>{Encode(file)}</code></td><td>{Encode(message)}</td></tr>");
            }

            sb.AppendLine("</table>");
        }
    }

    /// <summary>
    /// 输出违规明细（规则归属、说明、修复建议）。
    /// </summary>
    private static void AppendViolations(StringBuilder sb, IReadOnlyList<CheckResult> results)
    {
        sb.AppendLine("<h2>违规明细</h2>");

        if (results.Sum(r => r.Violations.Count) == 0)
        {
            sb.AppendLine("<div class=\"empty\">未发现违规——项目骨架符合规则配置。</div>");
            return;
        }

        foreach (var result in results)
        {
            foreach (var v in result.Violations)
            {
                sb.AppendLine("<div class=\"violation\">");
                sb.AppendLine($"<div class=\"file\">{Encode(v.File)}</div>");
                sb.AppendLine($"<div class=\"rule\">[{Encode(result.Name)}] {Encode(v.Rule)}</div>");
                sb.AppendLine($"<div>{Encode(v.Message)}</div>");
                sb.AppendLine($"<div class=\"suggest\"><b>建议：</b>{Encode(v.Suggestion)}</div>");
                sb.AppendLine("</div>");
            }
        }
    }

    /// <summary>
    /// 输出给 AI 的修复指令清单。
    /// </summary>
    private static void AppendAiFixInstructions(StringBuilder sb, IReadOnlyList<CheckResult> results)
    {
        if (results.Sum(r => r.Violations.Count) == 0)
        {
            return;
        }

        sb.AppendLine("<h2>给 AI 的修复指令</h2>");
        sb.AppendLine("<div class=\"meta\">以下违规必须逐条修复后重新运行 guardAI check 复检：</div>");
        sb.AppendLine("<ol class=\"fix\">");
        foreach (var v in results.SelectMany(r => r.Violations))
        {
            sb.AppendLine($"<li>[{Encode(v.Rule)}] <code>{Encode(v.File)}</code> → {Encode(v.Suggestion)}</li>");
        }

        sb.AppendLine("</ol>");
    }

    /// <summary>
    /// 输出文档尾。
    /// </summary>
    private static void AppendTail(StringBuilder sb)
    {
        sb.AppendLine("<footer>本报告由 guardAI 生成（引擎裁判，AI 装配）——记录与报告均不出本机。</footer>");
        sb.AppendLine("</div></body></html>");
    }

    /// <summary>
    /// HTML 实体编码（防违规内容里的尖括号破坏页面结构）。
    /// </summary>
    private static string Encode(string value) => WebUtility.HtmlEncode(value);
}
