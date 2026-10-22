using System.Text;
using System.Text.RegularExpressions;

namespace GuardAI.Checks.Assets;

/// <summary>
/// Python 轻量语法适配器：从源码文本抽取公共函数/方法（能力清单 / 重复检测共用）。
/// </summary>
/// <remarks>
/// 与 CLikeMemberParser 同设计取舍：轻量行级扫描 + 文本归一化，只漏不误。
/// Python 差异：def / async def 声明、缩进即块（无花括号）、可见性 = 不以 _ 开头、
/// 注释用 #、docstring 作为摘要原料。# 注释与字符串清洗由本类自管（C 系清洗器不认 #），
/// 其余文本处理（归一化/哈希/括号配平/注释摘要）走 <see cref="ParseText"/>，不复制骨架。
/// </remarks>
public static class PythonMemberParser
{
    /// <summary>函数声明匹配：[async] def 函数名(。</summary>
    private static readonly Regex DefRegex = new(
        @"^(?:async\s+)?def\s+(?<name>[A-Za-z_]\w*)\s*\(",
        RegexOptions.Compiled);

    /// <summary>类声明匹配：class 类名[（基类列表）]。</summary>
    private static readonly Regex ClassRegex = new(
        @"^class\s+(?<name>[A-Za-z_]\w*)",
        RegexOptions.Compiled);

    /// <summary>
    /// 解析单个 Python 源码文本，抽取全部公共函数/方法快照。
    /// </summary>
    /// <param name="sourceText">源码全文。</param>
    /// <param name="relativePath">仓库相对路径（正斜杠），写入成员快照。</param>
    /// <returns>公共函数/方法快照列表（按出现顺序）。</returns>
    /// <exception cref="ArgumentNullException">sourceText 或 relativePath 为 null 时抛出。</exception>
    public static IReadOnlyList<CodeMember> Parse(string sourceText, string relativePath)
    {
        ArgumentNullException.ThrowIfNull(sourceText);
        ArgumentNullException.ThrowIfNull(relativePath);

        var lines = Clean(sourceText);
        var rawLines = sourceText.Replace("\r\n", "\n").Split('\n');
        var members = new List<CodeMember>();
        var typeStack = new List<(string Name, int Indent)>();
        var i = 0;

        while (i < lines.Length)
        {
            var raw = lines[i];
            var trimmed = raw.Trim();
            if (trimmed.Length == 0)
            {
                i++;
                continue;
            }

            var indent = IndentOf(raw);

            // 维护类作用域栈：缩进退出即弹栈
            while (typeStack.Count > 0 && indent <= typeStack[^1].Indent)
            {
                typeStack.RemoveAt(typeStack.Count - 1);
            }

            // 类声明：压栈（方法归属用）
            var classMatch = ClassRegex.Match(trimmed);
            if (classMatch.Success)
            {
                typeStack.Add((classMatch.Groups["name"].Value, indent));
                i++;
                continue;
            }

            // 函数/方法声明：整段签名 + 缩进体一次消费（嵌套函数不外漏）
            var defMatch = DefRegex.Match(trimmed);
            if (!defMatch.Success)
            {
                i++;
                continue;
            }

            var name = defMatch.Groups["name"].Value;

            // 收齐签名：参数表可能跨行（def 后的括号配平）
            var sig = new StringBuilder(trimmed);
            var end = i + 1;
            while (ParseText.ParenBalance(sig.ToString()) > 0 && end < lines.Length)
            {
                sig.Append(' ').Append(lines[end].Trim());
                end++;
            }

            // 函数体 = 后续缩进更深的行（空行随行），到缩进退回为止
            var bodyEnd = end;
            while (bodyEnd < lines.Length
                && (lines[bodyEnd].Trim().Length == 0 || IndentOf(lines[bodyEnd]) > indent))
            {
                bodyEnd++;
            }

            // 只收公共（不以 _ 开头）且非构造方法（方法名 == 类名）；体照常消费防嵌套函数外漏
            var isPublic = name.Length > 0 && name[0] != '_';
            var isConstructor = typeStack.Count > 0 && string.Equals(name, typeStack[^1].Name, StringComparison.Ordinal);
            if (isPublic && !isConstructor)
            {
                var bodyText = string.Join("\n", lines[end..bodyEnd]);
                var normalizedBody = ParseText.Normalize(bodyText);
                members.Add(new CodeMember(
                    typeStack.Count > 0 ? typeStack[^1].Name : string.Empty,
                    name,
                    ParseText.Normalize(sig.ToString().TrimEnd(':')),
                    relativePath,
                    i + 1,
                    normalizedBody,
                    ParseText.HashBody(normalizedBody),
                    ExtractSummary(rawLines, end, bodyEnd, i)));
            }

            i = bodyEnd;
        }

        return members;
    }

    /// <summary>
    /// 抽取函数摘要：docstring（函数体首个字符串语句）优先，其次声明行上方的 # 注释。
    /// </summary>
    /// <param name="rawLines">原始源码行。</param>
    /// <param name="bodyStart">函数体起始行下标（0 起）。</param>
    /// <param name="bodyEnd">函数体结束行下标（不含）。</param>
    /// <param name="defLine">函数声明行下标。</param>
    /// <returns>摘要文本；无 docstring 且无注释返回空串。</returns>
    private static string ExtractSummary(string[] rawLines, int bodyStart, int bodyEnd, int defLine)
    {
        for (var k = bodyStart; k < bodyEnd; k++)
        {
            var t = rawLines[k].Trim();
            if (t.Length == 0)
            {
                continue;
            }

            if (t.StartsWith("\"\"\"", StringComparison.Ordinal) || t.StartsWith("'''", StringComparison.Ordinal))
            {
                var quote = t[..3];
                var content = t[3..];
                var close = content.IndexOf(quote, StringComparison.Ordinal);
                var text = close >= 0 ? content[..close] : content;
                var firstLine = text.Replace('\n', ' ').Trim();
                return ParseText.Normalize(firstLine.Length > 0 ? firstLine : text);
            }

            break; // 首个非空行不是 docstring 即止（只漏不误）
        }

        return ParseText.ExtractCommentSummary(rawLines, defLine, "#");
    }

    /// <summary>
    /// 取行首缩进宽度（空格计 1，制表符计 4，粗略对齐即可——只用于相对比较）。
    /// </summary>
    /// <param name="line">源码行。</param>
    /// <returns>缩进宽度。</returns>
    private static int IndentOf(string line)
    {
        var n = 0;
        foreach (var c in line)
        {
            if (c == ' ')
            {
                n++;
            }
            else if (c == '\t')
            {
                n += 4;
            }
            else
            {
                break;
            }
        }

        return n;
    }

    /// <summary>
    /// 清洗 Python 源码：# 注释与字符串字面量内容抹成空格（保留换行，行号不漂移）。
    /// </summary>
    /// <remarks>
    /// 覆盖：# 行注释、'''/""" 三引号字符串（docstring）、普通字符串（含转义）。
    /// 字符串内容抹除后以 "N" 长度标记占位（N=原文内容长度，与 C 系清洗同语义）。
    /// </remarks>
    /// <param name="source">源码全文。</param>
    /// <returns>清洗后的行数组（与原文行号对齐）。</returns>
    private static string[] Clean(string source)
    {
        var sb = new StringBuilder(source.Length);
        var i = 0;
        while (i < source.Length)
        {
            var c = source[i];

            // # 行注释
            if (c == '#')
            {
                while (i < source.Length && source[i] != '\n')
                {
                    sb.Append(' ');
                    i++;
                }

                continue;
            }

            // 三引号字符串 '''...''' / """..."""（docstring 也在此抹掉，摘要走原始行）
            if ((c == '"' || c == '\'') && i + 2 < source.Length && source[i + 1] == c && source[i + 2] == c)
            {
                var quote = c;
                i += 3;
                var length = 0;
                while (i < source.Length)
                {
                    if (source[i] == quote && i + 2 < source.Length
                        && source[i + 1] == quote && source[i + 2] == quote)
                    {
                        i += 3;
                        break;
                    }

                    sb.Append(source[i] == '\n' ? '\n' : ' ');
                    i++;
                    length++;
                }

                ParseText.AppendLengthMarker(sb, length);
                continue;
            }

            // 普通字符串（含转义）
            if (c == '"' || c == '\'')
            {
                i++;
                var length = 0;
                while (i < source.Length && source[i] != c && source[i] != '\n')
                {
                    if (source[i] == '\\' && i + 1 < source.Length)
                    {
                        i++;
                        length++;
                    }

                    i++;
                    length++;
                }

                if (i < source.Length && source[i] == c)
                {
                    i++;
                }

                ParseText.AppendLengthMarker(sb, length);
                continue;
            }

            sb.Append(c);
            i++;
        }

        return sb.ToString().Split('\n');
    }
}
