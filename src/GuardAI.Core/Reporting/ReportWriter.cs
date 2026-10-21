using System.Text.Json;
using System.Text.Json.Serialization;
using GuardAI.Core.Engine;

namespace GuardAI.Core.Reporting;

/// <summary>
/// 检查报告输出器：同一份数据渲染为终端文本或 JSON（机器消费）。
/// </summary>
public static class ReportWriter
{
    /// <summary>
    /// 汇总扫描文件数（v0.3.1-④ 口径统一）：取各检查器扫描文件数的最大值。
    /// 同一文件会被多个检查器扫描（如 .cs 被 structure 与 duplicate 都看），
    /// 加总会双计；.cs 是 include_extensions 子集时最大值即去重文件数。
    /// </summary>
    private static int TotalFilesScanned(IReadOnlyList<CheckResult> results)
    {
        return results.Count == 0 ? 0 : results.Max(r => r.Scanned);
    }

    /// <summary>
    /// 渲染终端文本报告（含"给 AI 的修复指令"清单）。
    /// </summary>
    /// <param name="writer">输出目标。</param>
    /// <param name="repoPath">目标仓库路径。</param>
    /// <param name="mode">检查模式。</param>
    /// <param name="configFile">规则配置文件名。</param>
    /// <param name="results">各检查模块结果。</param>
    public static void WriteText(TextWriter writer, string repoPath, CheckMode mode, string configFile, IReadOnlyList<CheckResult> results)
    {
        writer.WriteLine("============================================================");
        writer.WriteLine("guardAI（单入口：一条命令跑完所有检查）");
        writer.WriteLine($"目标仓库: {repoPath}");
        writer.WriteLine($"检查模式: {(mode == CheckMode.Changed ? "changed（仅 git 新增/改动文件）" : "all（全部文件）")}");
        writer.WriteLine($"规则配置: {configFile}");
        writer.WriteLine("============================================================");
        writer.WriteLine();

        var total = 0;
        foreach (var result in results)
        {
            writer.WriteLine($"[{result.Name}] 扫描 {result.Scanned} 个文件，发现 {result.Violations.Count} 处违规");
            foreach (var v in result.Violations)
            {
                writer.WriteLine();
                writer.WriteLine($"  x {v.File}");
                writer.WriteLine($"    规则: {v.Rule}");
                writer.WriteLine($"    说明: {v.Message}");
                writer.WriteLine($"    建议: {v.Suggestion}");
            }

            total += result.Violations.Count;
            writer.WriteLine();
        }

        // 空扫描判定：all 模式下 0 个文件被扫描 = 配置未覆盖本仓库代码文件，
        // 此时"0 违规"不构成合规结论，绝不能报"通过"（假绿）。
        var emptyScan = mode == CheckMode.All && TotalFilesScanned(results) == 0;

        writer.WriteLine("------------------------------------------------------------");
        if (emptyScan)
        {
            writer.WriteLine("结果: 空扫描警告 - 扫描 0 个文件，本次结论不成立");
            writer.WriteLine();
            writer.WriteLine("【空扫描警告】all 模式下没有匹配到任何代码文件，“0 处违规”不构成合规结论。");
            writer.WriteLine("可能原因: guardai.yml 的 include_extensions 未覆盖本仓库的代码文件扩展名。");
            writer.WriteLine("处理: 运行 guardAI init --force 重新生成配置，或手工补齐 include_extensions 后重新检查。");
        }
        else
        {
            writer.WriteLine(total == 0
                ? "结果: 通过 - 0 处违规"
                : $"结果: 失败 - 共 {total} 处违规");
        }

        if (total > 0)
        {
            writer.WriteLine();
            writer.WriteLine("【给 AI 的修复指令】以下违规必须逐条修复后重新运行 guardAI check 复检：");
            var index = 1;
            foreach (var result in results)
            {
                foreach (var v in result.Violations)
                {
                    writer.WriteLine($"  {index}. [{v.Rule}] {v.File} -> {v.Suggestion}");
                    index++;
                }
            }
        }

        writer.WriteLine("------------------------------------------------------------");
    }

    /// <summary>
    /// 渲染 JSON 报告（供 hook / CI / 台账消费）。
    /// </summary>
    /// <param name="writer">输出目标。</param>
    /// <param name="repoPath">目标仓库路径。</param>
    /// <param name="mode">检查模式。</param>
    /// <param name="results">各检查模块结果。</param>
    public static void WriteJson(TextWriter writer, string repoPath, CheckMode mode, IReadOnlyList<CheckResult> results)
    {
        var payload = new JsonReport
        {
            Timestamp = DateTimeOffset.Now,
            Project = Path.GetFileName(Path.TrimEndingDirectorySeparator(repoPath)),
            Mode = mode == CheckMode.Changed ? "changed" : "all",
            Summary = new JsonSummary
            {
                FilesScanned = TotalFilesScanned(results),
                Violations = results.Sum(r => r.Violations.Count),
                // 空扫描标记：all 模式 0 文件 = 结论不成立（假绿防线），供 hook / CI 判定
                EmptyScan = mode == CheckMode.All && TotalFilesScanned(results) == 0,
            },
            Violations = results
                .SelectMany(r => r.Violations.Select(v => new JsonViolation
                {
                    Check = r.Name,
                    File = v.File,
                    Rule = v.Rule,
                    Message = v.Message,
                    Suggestion = v.Suggestion,
                }))
                .ToList(),
            AiFixInstructions = results
                .SelectMany(r => r.Violations)
                .Select((v, i) => $"{i + 1}. [{v.Rule}] {v.File} -> {v.Suggestion}")
                .ToList(),
        };

        writer.WriteLine(JsonSerializer.Serialize(payload, new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        }));
    }
}

/// <summary>JSON 报告根对象。</summary>
internal sealed class JsonReport
{
    /// <summary>报告时间。</summary>
    public DateTimeOffset Timestamp { get; set; }

    /// <summary>项目名。</summary>
    public string Project { get; set; } = string.Empty;

    /// <summary>检查模式。</summary>
    public string Mode { get; set; } = string.Empty;

    /// <summary>汇总。</summary>
    public JsonSummary Summary { get; set; } = new();

    /// <summary>违规明细。</summary>
    public List<JsonViolation> Violations { get; set; } = [];

    /// <summary>给 AI 的修复指令。</summary>
    public List<string> AiFixInstructions { get; set; } = [];
}

/// <summary>JSON 报告汇总。</summary>
internal sealed class JsonSummary
{
    /// <summary>扫描文件数（各检查器去重口径：取最大值，同一文件不重复计）。</summary>
    public int FilesScanned { get; set; }

    /// <summary>违规数。</summary>
    public int Violations { get; set; }

    /// <summary>是否空扫描（all 模式 0 文件 = 结论不成立）。</summary>
    public bool EmptyScan { get; set; }
}

/// <summary>JSON 报告违规条目。</summary>
internal sealed class JsonViolation
{
    /// <summary>所属检查模块。</summary>
    public string Check { get; set; } = string.Empty;

    /// <summary>违规文件。</summary>
    public string File { get; set; } = string.Empty;

    /// <summary>命中规则。</summary>
    public string Rule { get; set; } = string.Empty;

    /// <summary>说明。</summary>
    public string Message { get; set; } = string.Empty;

    /// <summary>建议。</summary>
    public string Suggestion { get; set; } = string.Empty;
}
