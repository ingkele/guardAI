namespace GuardAI.AI.Review;

/// <summary>
/// 一条 AI 审查发现。
/// </summary>
/// <param name="Severity">严重级别：error / warn / info（已归一）。</param>
/// <param name="Category">类别：命名 / 注释 / 分层 / 复用 / 性能 / 安全 / 其他。</param>
/// <param name="File">问题所在文件（相对仓库根）。</param>
/// <param name="Line">问题行号（模型给不出时为 null）。</param>
/// <param name="Message">问题描述（中文）。</param>
/// <param name="Suggestion">修改建议（中文）。</param>
public sealed record ReviewFinding(
    string Severity,
    string Category,
    string File,
    int? Line,
    string Message,
    string Suggestion)
{
    /// <summary>严重级别：错误（必须修）。</summary>
    public const string SeverityError = "error";

    /// <summary>严重级别：警告（应当修）。</summary>
    public const string SeverityWarn = "warn";

    /// <summary>严重级别：提示（可选优化）。</summary>
    public const string SeverityInfo = "info";

    /// <summary>
    /// 归一化严重级别文本（warning→warn、严重→error 等常见别名）。
    /// </summary>
    /// <param name="raw">模型输出的原始级别。</param>
    /// <returns>归一后的 error / warn / info；无法识别时按 info 处理（只漏勿误：未知项不拦截）。</returns>
    public static string NormalizeSeverity(string? raw)
    {
        var value = (raw ?? string.Empty).Trim().ToLowerInvariant();
        return value switch
        {
            "error" or "严重" or "high" or "blocker" => SeverityError,
            "warn" or "warning" or "警告" or "medium" => SeverityWarn,
            "info" or "提示" or "low" or "note" or "suggestion" => SeverityInfo,
            _ => SeverityInfo,
        };
    }

    /// <summary>
    /// 严重级别排序权重（error=0 / warn=1 / info=2，用于拦截判定与报告分组）。
    /// </summary>
    /// <param name="severity">归一后的级别文本。</param>
    /// <returns>排序权重，越小越严重。</returns>
    public static int SeverityRank(string severity)
        => severity switch
        {
            SeverityError => 0,
            SeverityWarn => 1,
            _ => 2,
        };
}
