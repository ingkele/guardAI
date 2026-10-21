using System.Text.RegularExpressions;

namespace GuardAI.Core.Config;

/// <summary>
/// 通配符路径匹配器。
/// </summary>
/// <remarks>
/// 来源：移植自本项目自研的 guard-tool（PowerShell 版 ConvertTo-GuardWildcardRegex / Test-GuardWildcard），语义保持一致。
/// 语法：** 匹配任意层（含跨目录），* 匹配单层内任意字符（不含 /），其余字符按字面匹配。
/// 统一用 / 比较（反斜杠自动归一），不区分大小写以适配 Windows 文件系统。
/// </remarks>
public static class GlobMatcher
{
    /// <summary>
    /// 判断文本是否命中通配符模式。
    /// </summary>
    /// <param name="text">待匹配文本（通常是文件相对路径）。</param>
    /// <param name="pattern">通配符模式。</param>
    /// <returns>命中返回 true，否则 false。</returns>
    public static bool IsMatch(string text, string pattern)
    {
        var normalized = text.Replace('\\', '/');
        var regex = ToRegex(pattern);
        return Regex.IsMatch(normalized, regex, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    /// <summary>
    /// 把通配符模式转换为正则表达式（锚定首尾）。
    /// </summary>
    /// <param name="pattern">通配符模式。</param>
    /// <returns>等价的正则表达式字符串。</returns>
    public static string ToRegex(string pattern)
    {
        // 注意：Regex.Escape 把每个 * 转义为 \*，"**" 的转义形态是 "\*""\*"（两个独立转义）。
        // 因此匹配模式必须写成 \\\*\\\* 才能命中，与 guard-tool 的 \\*\\* 语义一致。
        var rx = Regex.Escape(pattern);
        rx = Regex.Replace(rx, @"\\\*\\\*/", "(?:.*/)?");
        rx = Regex.Replace(rx, @"\\\*\\\*", ".*");
        rx = Regex.Replace(rx, @"\\\*", "[^/]*");
        return "^" + rx + "$";
    }
}
