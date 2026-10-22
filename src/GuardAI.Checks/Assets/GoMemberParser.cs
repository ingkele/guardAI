using System.Text;
using System.Text.RegularExpressions;

namespace GuardAI.Checks.Assets;

/// <summary>
/// Go 轻量语法适配器：从源码文本抽取导出方法/函数（能力清单 / 重复检测共用）。
/// </summary>
/// <remarks>
/// 与 CLikeMemberParser 同设计取舍：轻量行级扫描 + 文本归一化，只漏不误。
/// Go 差异：函数用 func 声明、接收者括号在函数名之前、可见性 = 首字母大写（导出）。
/// 文本处理（清洗/归一化/哈希/括号配平）全部走 <see cref="ParseText"/>，不复制骨架。
/// </remarks>
public static class GoMemberParser
{
    /// <summary>函数声明匹配：func [接收者] 泛型? 函数名(。</summary>
    private static readonly Regex FuncRegex = new(
        @"^func\s+(?:\([^)]*\)\s*)?(?<name>[A-Za-z_]\w*)\s*(?:\[[^\]]*\])?\s*\(",
        RegexOptions.Compiled);

    /// <summary>接收者类型提取：func (recv *pkg.Type) … 中的类型名（接收者括号内最后一个标识符）。</summary>
    private static readonly Regex ReceiverTypeRegex = new(
        @"^func\s+\(\s*[\w\[\]\*\.\s]*?(?<type>[A-Za-z_]\w*)\s*\)",
        RegexOptions.Compiled);

    /// <summary>
    /// 解析单个 Go 源码文本，抽取全部导出方法/函数快照。
    /// </summary>
    /// <param name="sourceText">源码全文。</param>
    /// <param name="relativePath">仓库相对路径（正斜杠），写入成员快照。</param>
    /// <returns>导出方法/函数快照列表（按出现顺序）。</returns>
    /// <exception cref="ArgumentNullException">sourceText 或 relativePath 为 null 时抛出。</exception>
    public static IReadOnlyList<CodeMember> Parse(string sourceText, string relativePath)
    {
        ArgumentNullException.ThrowIfNull(sourceText);
        ArgumentNullException.ThrowIfNull(relativePath);

        var lines = ParseText.Clean(sourceText);
        var rawLines = sourceText.Replace("\r\n", "\n").Split('\n');
        var members = new List<CodeMember>();
        var i = 0;

        while (i < lines.Length)
        {
            var trimmed = lines[i].Trim();
            var match = FuncRegex.Match(trimmed);
            if (!match.Success || !IsExported(match.Groups["name"].Value))
            {
                i++;
                continue;
            }

            // 收齐签名：直到括号配平（参数表可能跨行）
            var sig = new StringBuilder(trimmed);
            var end = i + 1;
            while (ParseText.ParenBalance(sig.ToString()) > 0 && end < lines.Length)
            {
                sig.Append(' ').Append(lines[end].Trim());
                end++;
            }

            if (ParseText.ParenBalance(sig.ToString()) != 0)
            {
                i = end;
                continue;
            }

            var sigText = sig.ToString();

            // 参数表左括号 = 函数名之后第一个 '('（接收者括号在名字之前，天然跳过）
            var open = sigText.IndexOf('(', match.Index + match.Length - 1);
            if (open < 0)
            {
                i = end;
                continue;
            }

            var close = ParseText.MatchingParen(sigText, open);

            // 收方法体：Go 语法要求 '{' 与签名同行，尾段没有 '{' 即外部函数声明（汇编桩）——
            // 直接放弃，不越行找（越行会把下一个函数吞进尾段造幻影成员）
            var tail = sigText[(close + 1)..];
            var braceAt = tail.IndexOf('{');
            if (braceAt < 0)
            {
                i = end;
                continue;
            }

            var body = new StringBuilder();
            var balance = 0;
            var closed = false;
            var pending = tail[braceAt..];
            while (!closed)
            {
                body.Append(pending);
                balance = ParseText.CountUntilClose(pending, balance, ref closed);
                if (closed || end >= lines.Length)
                {
                    break;
                }

                pending = "\n" + lines[end];
                end++;
            }

            if (!closed)
            {
                i = end;
                continue;
            }

            var bodyText = ParseText.ExtractInnerBody(body.ToString());
            var normalizedBody = ParseText.Normalize(bodyText);
            var receiverType = ReceiverTypeRegex.Match(sigText);
            members.Add(new CodeMember(
                receiverType.Success ? receiverType.Groups["type"].Value : string.Empty,
                match.Groups["name"].Value,
                ParseText.Normalize(sigText[..(close + 1)]),
                relativePath,
                i + 1,
                normalizedBody,
                ParseText.HashBody(normalizedBody),
                ParseText.ExtractCommentSummary(rawLines, i, "//")));

            i = end;
        }

        return members;
    }

    /// <summary>
    /// 判断 Go 标识符是否导出（首字母大写）。
    /// </summary>
    /// <param name="name">标识符。</param>
    /// <returns>是否导出。</returns>
    private static bool IsExported(string name)
    {
        return name.Length > 0 && char.IsUpper(name[0]);
    }
}
