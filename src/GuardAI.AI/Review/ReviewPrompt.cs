using System.Text;

namespace GuardAI.AI.Review;

/// <summary>
/// AI 审查提示词构造器：系统提示（角色与输出约束）+ 用户提示（规则上下文与差异）。
/// </summary>
public static class ReviewPrompt
{
    /// <summary>送审内容的最大字符数（超出截断，保护 token 成本与模型上下文）。</summary>
    public const int MaxDiffChars = 60000;

    /// <summary>
    /// 构造系统提示词（.NET 代码规范审查员角色 + 严格 JSON 输出约束）。
    /// </summary>
    /// <returns>系统提示词文本。</returns>
    public static string BuildSystem()
    {
        return """
            你是资深 .NET 代码规范审查员。你的职责是对代码改动做语义审查：
            命名是否恰当（PascalCase/camelCase 约定、名字是否达意）、注释是否达标（公共方法有中文 XML 注释且与实现一致）、
            分层与职责是否合理（依赖方向、单一职责、不该出现的跨层调用）、是否重复造轮子（已有实现应复用）、
            以及明显的性能与安全问题。

            审查纪律：
            1. 宁漏勿误——只报告你有把握、且能给出具体修改建议的问题；拿不准的不报。
            2. 只审本次改动涉及的代码，不评论未改动的存量代码。
            3. 机械性问题（文件位置、重复检测、格式）由确定性引擎负责，你不需要重复报告。

            输出要求：只输出一个 JSON 对象，不要输出任何其他文字（包括解释和代码块标记）。
            格式：
            {"findings":[{"severity":"error|warn|info","category":"命名|注释|分层|复用|性能|安全|其他","file":"相对路径","line":行号,"message":"问题描述（中文）","suggestion":"修改建议（中文，可执行）"}]}
            没有发现问题时输出：{"findings":[]}
            """;
    }

    /// <summary>
    /// 构造用户提示词（规则上下文 + 送审差异）。
    /// </summary>
    /// <param name="ruleSummary">项目规则摘要（来自 guardai.yml 的关键约定），可空。</param>
    /// <param name="diffText">送审差异文本（git diff 或新增文件内容）。</param>
    /// <returns>用户提示词文本。</returns>
    /// <exception cref="ArgumentException">差异文本为空。</exception>
    public static string BuildUser(string? ruleSummary, string diffText)
    {
        if (string.IsNullOrWhiteSpace(diffText))
        {
            throw new ArgumentException("送审差异为空，无法构造审查提示词。", nameof(diffText));
        }

        var truncated = false;
        if (diffText.Length > MaxDiffChars)
        {
            diffText = diffText[..MaxDiffChars];
            truncated = true;
        }

        var sb = new StringBuilder();
        sb.AppendLine("## 项目规则约定（摘自 guardai.yml）");
        sb.AppendLine(string.IsNullOrWhiteSpace(ruleSummary) ? "（无自定义规则，按 .NET 社区通行规范审查）" : ruleSummary);
        sb.AppendLine();
        sb.AppendLine("## 本次代码改动");
        sb.AppendLine(diffText);
        if (truncated)
        {
            sb.AppendLine();
            sb.AppendLine("（改动内容过长已截断，请按可见部分审查）");
        }

        sb.AppendLine();
        sb.AppendLine("请按系统提示的 JSON 格式输出审查结论。");
        return sb.ToString();
    }

    /// <summary>
    /// 从 guardai.yml 解析结果提取规则摘要（送给模型的项目约定上下文）。
    /// </summary>
    /// <param name="root">guardai.yml 解析出的根字典。</param>
    /// <returns>规则摘要文本；无可提炼内容时返回 null。</returns>
    public static string? BuildRuleSummary(IReadOnlyDictionary<string, object?>? root)
    {
        if (root is null)
        {
            return null;
        }

        var sb = new StringBuilder();
        foreach (var key in new[] { "project_type", "allowed_dirs", "exclusions", "placement_rules" })
        {
            if (root.TryGetValue(key, out var value) && value is not null)
            {
                sb.Append("- ").Append(key).Append(": ").AppendLine(FormatValue(value));
            }
        }

        return sb.Length == 0 ? null : sb.ToString().TrimEnd();
    }

    /// <summary>递归把配置值格式化为一行文本（列表元素递归、字典 key=value、避免类型名污染）。</summary>
    private static string FormatValue(object? value)
        => value switch
        {
            null => "null",
            IReadOnlyList<object?> list => string.Join(", ", list.Select(FormatValue)),
            IReadOnlyDictionary<string, object?> map => string.Join(", ", map.Select(kv => $"{kv.Key}={FormatValue(kv.Value)}")),
            _ => value.ToString() ?? string.Empty,
        };
}
