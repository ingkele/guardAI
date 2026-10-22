using System.Text;
using System.Text.Json;

namespace GuardAI.AI.Review;

/// <summary>
/// AI 审查报告（一次 review 的完整结论）。
/// </summary>
/// <param name="GeneratedAt">生成时间。</param>
/// <param name="Provider">协议提供方。</param>
/// <param name="Model">模型名。</param>
/// <param name="DiffFiles">送审文件数。</param>
/// <param name="DiffChars">送审内容字符数（token 成本直观参考）。</param>
/// <param name="Findings">发现列表。</param>
/// <param name="Note">附注（如送审内容被截断的提示），可空。</param>
public sealed record ReviewReport(
    DateTime GeneratedAt,
    string Provider,
    string Model,
    int DiffFiles,
    int DiffChars,
    IReadOnlyList<ReviewFinding> Findings,
    string? Note)
{
    /// <summary>
    /// 输出中文控制台报告。
    /// </summary>
    /// <param name="writer">输出目标。</param>
    public void WriteText(TextWriter writer)
    {
        writer.WriteLine("============================================================");
        writer.WriteLine("guardAI 语义审查（AI 审查员：引擎管机械项，AI 管语义项）");
        writer.WriteLine($"模型: {Model}（{Provider}）");
        writer.WriteLine($"送审: {DiffFiles} 个文件 / {DiffChars} 字符");
        writer.WriteLine("============================================================");
        writer.WriteLine();

        if (!string.IsNullOrWhiteSpace(Note))
        {
            writer.WriteLine($"附注: {Note}");
            writer.WriteLine();
        }

        if (Findings.Count == 0)
        {
            writer.WriteLine("结果: 通过 - AI 审查未发现问题");
            return;
        }

        foreach (var group in Findings
            .GroupBy(f => f.Severity)
            .OrderBy(g => ReviewFinding.SeverityRank(g.Key)))
        {
            writer.WriteLine($"【{SeverityLabel(group.Key)}】{group.Count()} 处");
            foreach (var finding in group)
            {
                var location = finding.Line is { } line ? $"{finding.File}:{line}" : finding.File;
                writer.WriteLine($"  [{finding.Category}] {location}");
                writer.WriteLine($"    问题: {finding.Message}");
                writer.WriteLine($"    建议: {finding.Suggestion}");
            }

            writer.WriteLine();
        }

        writer.WriteLine($"结果: 共 {Findings.Count} 处发现（error {Findings.Count(f => f.Severity == ReviewFinding.SeverityError)} / "
            + $"warn {Findings.Count(f => f.Severity == ReviewFinding.SeverityWarn)} / "
            + $"info {Findings.Count(f => f.Severity == ReviewFinding.SeverityInfo)}）");
    }

    /// <summary>
    /// 序列化为 JSON 并写入文本输出（供落盘/机器消费）。
    /// </summary>
    /// <param name="writer">输出目标。</param>
    public void WriteJson(TextWriter writer)
    {
        var payload = new
        {
            generatedAt = GeneratedAt.ToString("o"),
            provider = Provider,
            model = Model,
            diffFiles = DiffFiles,
            diffChars = DiffChars,
            note = Note,
            findings = Findings.Select(f => new
            {
                severity = f.Severity,
                category = f.Category,
                file = f.File,
                line = f.Line,
                message = f.Message,
                suggestion = f.Suggestion,
            }),
        };
        writer.WriteLine(JsonSerializer.Serialize(payload, Llm.LlmJson.IndentedOptions));
    }

    /// <summary>级别中文标签。</summary>
    private static string SeverityLabel(string severity)
        => severity switch
        {
            ReviewFinding.SeverityError => "错误·必须修",
            ReviewFinding.SeverityWarn => "警告·应当修",
            _ => "提示·可选优化",
        };
}
