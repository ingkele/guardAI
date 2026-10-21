using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace GuardAI.Checks.Assets;

/// <summary>
/// C# 轻量语法适配器：从源码文本抽取公共方法（能力清单 / 重复检测共用）。
/// </summary>
/// <remarks>
/// 设计取舍（P3-D1）：轻量行级扫描 + 文本归一化，不引 Roslyn——守住零外部依赖铁律。
/// 复杂语法（局部函数、复杂泛型、花式条件编译）遵循"只漏不误"原则：
/// 漏 = 漏报一个能力，绝不产生误报条目——误报比漏报更伤体验。
/// 方法体统一归一化（去注释/字符串字面量/空白），供能力哈希与 M2 近似检测复用。
/// </remarks>
public static class CSharpMemberParser
{
    /// <summary>不作为方法名识别的关键字（控制流/上下文关键字等）。</summary>
    private static readonly HashSet<string> MethodNameBlacklist =
    [
        "if", "for", "foreach", "while", "switch", "catch", "using", "lock", "return", "do", "else",
        "try", "fixed", "when", "new", "in", "is", "as", "get", "set", "init", "where", "select",
        "from", "join", "let", "into", "orderby", "group", "by", "on", "equals", "global", "add",
        "remove", "value", "await", "yield", "case", "default", "throw", "checked", "unchecked",
        "sizeof", "typeof", "nameof", "delegate", "operator", "implicit", "explicit", "not", "and", "or",
    ];

    /// <summary>类型声明匹配（class/record/interface/struct/enum + 类型名）。</summary>
    private static readonly Regex TypeRegex = new(
        @"\b(?:(?:record)\s+(?:class|struct)|(?:class|interface|struct|enum|record))\s+(?<name>[A-Za-z_]\w*)",
        RegexOptions.Compiled);

    /// <summary>方法名匹配：方法名（可带泛型参数）紧跟左括号，且不被成员访问符修饰。</summary>
    private static readonly Regex MethodNameRegex = new(
        @"(?<![.\w])(?<name>[A-Za-z_]\w*)\s*(?:<[^<>]*>)?\s*\(",
        RegexOptions.Compiled);

    /// <summary>签名头部结尾的方法名（可带泛型参数）。</summary>
    private static readonly Regex TrailingNameRegex = new(
        @"(?<![.\w])(?<name>[A-Za-z_]\w*)\s*(?:<[^<>]*>)?\s*$",
        RegexOptions.Compiled);

    /// <summary>
    /// 解析单个 C# 源码文本，抽取全部公共方法快照。
    /// </summary>
    /// <param name="sourceText">源码全文。</param>
    /// <param name="relativePath">仓库相对路径（正斜杠），写入成员快照。</param>
    /// <returns>公共方法快照列表（按出现顺序）。</returns>
    /// <exception cref="ArgumentNullException">sourceText 或 relativePath 为 null 时抛出。</exception>
    public static IReadOnlyList<CodeMember> Parse(string sourceText, string relativePath)
    {
        ArgumentNullException.ThrowIfNull(sourceText);
        ArgumentNullException.ThrowIfNull(relativePath);

        var lines = Clean(sourceText);
        var rawLines = sourceText.Replace("\r\n", "\n").Split('\n');
        var members = new List<CodeMember>();
        var typeStack = new Stack<TypeScope>();
        var depth = 0;
        var i = 0;

        while (i < lines.Length)
        {
            var trimmed = lines[i].Trim();

            // 空行 / 预处理指令 / 特性行：只走花括号深度
            if (trimmed.Length == 0 || trimmed.StartsWith('#') || trimmed.StartsWith('['))
            {
                var net = NetBraces(lines[i]);
                depth += net;
                if (net < 0)
                {
                    PopTypes(typeStack, depth);
                }

                i++;
                continue;
            }

            // 类型声明：压栈（方法归属用）
            if (TryMatchTypeName(trimmed, out var typeName))
            {
                typeStack.Push(new TypeScope(typeName, depth));
                var net = NetBraces(lines[i]);
                depth += net;
                if (net < 0)
                {
                    PopTypes(typeStack, depth);
                }

                i++;
                continue;
            }

            // 方法候选：整段签名（可跨行）+ 方法体（可跨行）一次消费
            if (IsMethodCandidate(trimmed))
            {
                var before = depth;
                var end = ConsumeMethod(lines, rawLines, i, typeStack, relativePath, members);
                for (var k = i; k < end; k++)
                {
                    depth += NetBraces(lines[k]);
                }

                // 紧凑写法里类型可能随方法同收尾，深度下降才弹栈（多行主构造函数等深度未降的行绝不弹）
                if (depth < before)
                {
                    PopTypes(typeStack, depth);
                }

                i = end;
                continue;
            }

            var plainNet = NetBraces(lines[i]);
            depth += plainNet;
            if (plainNet < 0)
            {
                PopTypes(typeStack, depth);
            }

            i++;
        }

        return members;
    }

    /// <summary>
    /// 深度下降后弹出已结束的类型作用域。
    /// </summary>
    private static void PopTypes(Stack<TypeScope> typeStack, int depth)
    {
        while (typeStack.Count > 0 && typeStack.Peek().Depth >= depth)
        {
            typeStack.Pop();
        }
    }

    /// <summary>
    /// 计算归一化方法体的稳定哈希（SHA-256 前 16 位十六进制）。
    /// </summary>
    /// <param name="normalizedBody">已归一化的方法体文本。</param>
    /// <returns>16 位十六进制哈希。</returns>
    public static string HashBody(string normalizedBody)
    {
        ArgumentNullException.ThrowIfNull(normalizedBody);
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(normalizedBody));
        return Convert.ToHexString(bytes)[..16].ToLowerInvariant();
    }

    /// <summary>
    /// 消费一个方法候选（签名 + 方法体），成功则写入成员列表。
    /// </summary>
    /// <returns>消费结束后的下一行行号（未识别出公共方法时同样前进，保证不死循环）。</returns>
    private static int ConsumeMethod(
        string[] lines,
        string[] rawLines,
        int start,
        Stack<TypeScope> typeStack,
        string relativePath,
        List<CodeMember> members)
    {
        var buffer = new StringBuilder(lines[start].Trim());
        var end = start + 1;

        // 1) 收齐签名：直到右括号闭合
        while (ParenBalance(buffer) > 0 && end < lines.Length)
        {
            buffer.Append(' ').Append(lines[end].Trim());
            end++;
        }

        if (ParenBalance(buffer) != 0)
        {
            return end;
        }

        var sigText = buffer.ToString();
        var open = sigText.IndexOf('(');
        var head = sigText[..open];

        // 2) 只保留公共方法；构造函数（方法名 == 类型名）跳过
        if (!Regex.IsMatch(head, @"\bpublic\b") || !TryGetMethodName(head, out var name))
        {
            return end;
        }

        if (typeStack.Count > 0 && string.Equals(name, typeStack.Peek().Name, StringComparison.Ordinal))
        {
            return end;
        }

        // 3) 收终结符：';'（抽象/接口，跳过）/ '=>'（表达式体）/ '{'（块方法体）
        var close = MatchingParen(sigText, open);
        var tail = new StringBuilder(sigText[(close + 1)..]);
        var terminator = FindTerminator(tail.ToString(), out var at);
        while (terminator is null && end < lines.Length)
        {
            tail.Append(' ').Append(lines[end].Trim());
            end++;
            terminator = FindTerminator(tail.ToString(), out at);
        }

        if (terminator is not ("=>" or "{"))
        {
            return end;
        }

        string bodyText;
        if (terminator == "=>")
        {
            // 表达式体：收集到语句结束分号
            var expr = new StringBuilder(tail.ToString()[(at + 2)..].TrimStart());
            while (!TryCutStatementEnd(expr.ToString(), out _) && end < lines.Length)
            {
                expr.Append(' ').Append(lines[end].Trim());
                end++;
            }

            bodyText = TryCutStatementEnd(expr.ToString(), out var cut) ? expr.ToString()[..cut] : expr.ToString();
        }
        else
        {
            // 块方法体：从 '{' 起花括号配平收集（只扫新增片段，不重复累计）
            var body = new StringBuilder();
            var balance = 0;
            var closed = false;
            var pending = tail.ToString()[at..];
            while (!closed)
            {
                body.Append(pending);
                balance = CountUntilClose(pending, balance, ref closed);
                if (closed || end >= lines.Length)
                {
                    break;
                }

                pending = "\n" + lines[end];
                end++;
            }

            // 文件尾仍未闭合：放弃，按"只漏不误"处理
            bodyText = closed ? ExtractInnerBody(body.ToString()) : string.Empty;
        }

        var normalizedBody = Normalize(bodyText);
        var signature = Normalize(sigText[..(close + 1)]);
        members.Add(new CodeMember(
            typeStack.Count > 0 ? typeStack.Peek().Name : string.Empty,
            name,
            signature,
            relativePath,
            start + 1,
            normalizedBody,
            HashBody(normalizedBody),
            ExtractDocSummary(rawLines, start)));
        return end;
    }

    /// <summary>
    /// 抽取方法声明上方的 XML 文档注释摘要（v0.3.1-② 中文检索的原料）。
    /// </summary>
    /// <remarks>
    /// 从声明行向上收集紧邻的 /// 注释行（特性行 [..] 允许穿行），其余行即止——只漏不误。
    /// 取 summary 标签内容优先，无 summary 用整段文档文本；剥离 XML 标签并归一化空白。
    /// </remarks>
    /// <param name="rawLines">原始源码行（清洗前行号与清洗后对齐）。</param>
    /// <param name="start">方法声明起始行下标（0 起）。</param>
    /// <returns>摘要文本；无文档注释返回空串。</returns>
    private static string ExtractDocSummary(string[] rawLines, int start)
    {
        var docs = new List<string>();
        var j = start - 1;
        while (j >= 0)
        {
            var t = rawLines[j].Trim();
            if (t.StartsWith("///", StringComparison.Ordinal))
            {
                docs.Insert(0, t[3..]);
                j--;
            }
            else if (t.StartsWith('['))
            {
                j--; // 特性行穿行继续找文档注释
            }
            else
            {
                break;
            }
        }

        if (docs.Count == 0)
        {
            return string.Empty;
        }

        var text = string.Join(" ", docs);
        var summary = Regex.Match(text, "<summary>(?<s>.*?)</summary>", RegexOptions.Singleline);
        var content = summary.Success ? summary.Groups["s"].Value : text;
        return Normalize(Regex.Replace(content, "<[^>]+>", " "));
    }

    /// <summary>
    /// 从块方法体文本（含最外层花括号）按花括号配对提取内部文本。
    /// </summary>
    private static string ExtractInnerBody(string full)
    {
        var open = full.IndexOf('{');
        if (open < 0)
        {
            return string.Empty;
        }

        var balance = 0;
        for (var i = open; i < full.Length; i++)
        {
            if (full[i] == '{')
            {
                balance++;
            }
            else if (full[i] == '}')
            {
                balance--;
                if (balance == 0)
                {
                    return full[(open + 1)..i];
                }
            }
        }

        return string.Empty;
    }

    /// <summary>
    /// 逐行累加统计花括号配平，发现闭合即停（用于块方法体收集）。
    /// </summary>
    private static int CountUntilClose(string text, int balance, ref bool closed)
    {
        foreach (var c in text)
        {
            if (c == '{')
            {
                balance++;
            }
            else if (c == '}')
            {
                balance--;
                if (balance == 0)
                {
                    closed = true;
                    return balance;
                }
            }
        }

        return balance;
    }

    /// <summary>
    /// 判断表达式体是否已到语句结束分号（括号/花括号配平位置上的分号）。
    /// </summary>
    /// <param name="text">表达式体候选文本。</param>
    /// <param name="cutIndex">结束分号下标（未找到为 -1）。</param>
    /// <returns>是否已结束。</returns>
    private static bool TryCutStatementEnd(string text, out int cutIndex)
    {
        cutIndex = -1;
        var paren = 0;
        var brace = 0;
        for (var i = 0; i < text.Length; i++)
        {
            switch (text[i])
            {
                case '(':
                    paren++;
                    break;
                case ')':
                    paren--;
                    break;
                case '{':
                    brace++;
                    break;
                case '}':
                    brace--;
                    break;
                case ';' when paren <= 0 && brace <= 0:
                    cutIndex = i;
                    return true;
            }
        }

        return false;
    }

    /// <summary>
    /// 在签名后的尾段中找第一个终结符（';' / '=>' / '{'）。
    /// </summary>
    /// <param name="tail">右括号之后的文本。</param>
    /// <param name="at">终结符下标（未找到为 -1）。</param>
    /// <returns>终结符（';' / '=>' / '{'），未找到返回 null。</returns>
    private static string? FindTerminator(string tail, out int at)
    {
        at = -1;
        var best = int.MaxValue;
        string? found = null;

        var semi = tail.IndexOf(';');
        if (semi >= 0 && semi < best)
        {
            best = semi;
            found = ";";
        }

        var arrow = tail.IndexOf("=>", StringComparison.Ordinal);
        if (arrow >= 0 && arrow < best)
        {
            best = arrow;
            found = "=>";
        }

        var brace = tail.IndexOf('{');
        if (brace >= 0 && brace < best)
        {
            best = brace;
            found = "{";
        }

        at = found is null ? -1 : best;
        return found;
    }

    /// <summary>
    /// 判断一行是否是方法候选（含"方法名(" 形态且方法名非关键字）。
    /// </summary>
    private static bool IsMethodCandidate(string trimmed)
    {
        if (!trimmed.Contains('(') || trimmed.StartsWith("return ", StringComparison.Ordinal))
        {
            return false;
        }

        foreach (Match match in MethodNameRegex.Matches(trimmed))
        {
            if (!MethodNameBlacklist.Contains(match.Groups["name"].Value))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// 匹配类型声明并取出类型名。
    /// </summary>
    private static bool TryMatchTypeName(string trimmed, out string typeName)
    {
        var match = TypeRegex.Match(trimmed);
        typeName = match.Success ? match.Groups["name"].Value : string.Empty;
        return match.Success;
    }

    /// <summary>
    /// 从签名头部（左括号之前）提取方法名。
    /// </summary>
    private static bool TryGetMethodName(string head, out string name)
    {
        var match = TrailingNameRegex.Match(head);
        name = match.Success ? match.Groups["name"].Value : string.Empty;
        return match.Success && !MethodNameBlacklist.Contains(name);
    }

    /// <summary>
    /// 计算文本的括号配平差值（'{' 与 '}'）。
    /// </summary>
    private static int NetBraces(string line)
    {
        var net = 0;
        foreach (var c in line)
        {
            if (c == '{')
            {
                net++;
            }
            else if (c == '}')
            {
                net--;
            }
        }

        return net;
    }

    /// <summary>
    /// 计算括号配平（'(' 与 ')'）。
    /// </summary>
    private static int ParenBalance(StringBuilder text)
    {
        var balance = 0;
        foreach (var c in text.ToString())
        {
            if (c == '(')
            {
                balance++;
            }
            else if (c == ')')
            {
                balance--;
            }
        }

        return balance;
    }

    /// <summary>
    /// 找到与指定左括号配对的右括号下标。
    /// </summary>
    private static int MatchingParen(string text, int open)
    {
        var balance = 0;
        for (var i = open; i < text.Length; i++)
        {
            if (text[i] == '(')
            {
                balance++;
            }
            else if (text[i] == ')')
            {
                balance--;
                if (balance == 0)
                {
                    return i;
                }
            }
        }

        return text.Length - 1;
    }

    /// <summary>
    /// 空白归一化：连续空白压成单空格并去首尾。
    /// </summary>
    private static string Normalize(string text)
    {
        return Regex.Replace(text, @"\s+", " ").Trim();
    }

    /// <summary>
    /// 清洗源码：注释与字符串字面量内容抹成空格（保留换行，行号不漂移）。
    /// </summary>
    /// <remarks>
    /// 覆盖：行注释、块注释、普通字符串（含转义）、逐字字符串 @"..."、原始字符串 """..."""、字符字面量。
    /// 插值字符串的插值段按字符串内容一并抹掉——对抽取签名/方法体只漏不误。
    /// </remarks>
    private static string[] Clean(string source)
    {
        var sb = new StringBuilder(source.Length);
        var i = 0;
        while (i < source.Length)
        {
            var c = source[i];

            // 行注释
            if (c == '/' && i + 1 < source.Length && source[i + 1] == '/')
            {
                while (i < source.Length && source[i] != '\n')
                {
                    sb.Append(' ');
                    i++;
                }

                continue;
            }

            // 块注释
            if (c == '/' && i + 1 < source.Length && source[i + 1] == '*')
            {
                sb.Append("  ");
                i += 2;
                while (i < source.Length && !(source[i] == '*' && i + 1 < source.Length && source[i + 1] == '/'))
                {
                    sb.Append(source[i] == '\n' ? '\n' : ' ');
                    i++;
                }

                if (i < source.Length)
                {
                    sb.Append("  ");
                    i += 2;
                }

                continue;
            }

            // 原始字符串 """..."""
            if (c == '"' && i + 2 < source.Length && source[i + 1] == '"' && source[i + 2] == '"')
            {
                sb.Append("\"\"");
                i += 3;
                while (i < source.Length)
                {
                    if (source[i] == '"' && i + 2 < source.Length && source[i + 1] == '"' && source[i + 2] == '"')
                    {
                        i += 3;
                        break;
                    }

                    sb.Append(source[i] == '\n' ? '\n' : ' ');
                    i++;
                }

                continue;
            }

            // 逐字字符串 @"..."
            if (c == '@' && i + 1 < source.Length && source[i + 1] == '"')
            {
                sb.Append("\"\"");
                i += 2;
                while (i < source.Length)
                {
                    if (source[i] == '"')
                    {
                        if (i + 1 < source.Length && source[i + 1] == '"')
                        {
                            sb.Append("  ");
                            i += 2;
                            continue;
                        }

                        i++;
                        break;
                    }

                    sb.Append(source[i] == '\n' ? '\n' : ' ');
                    i++;
                }

                continue;
            }

            // 普通字符串（含 $"" 插值串，插值段一并抹掉）
            if (c == '"')
            {
                sb.Append("\"\"");
                i++;
                while (i < source.Length && source[i] != '"' && source[i] != '\n')
                {
                    if (source[i] == '\\' && i + 1 < source.Length)
                    {
                        i++;
                    }

                    i++;
                }

                if (i < source.Length && source[i] == '"')
                {
                    i++;
                }

                continue;
            }

            // 字符字面量（防止 '{' 之类的字符干扰花括号配平）
            if (c == '\'')
            {
                sb.Append("''");
                i++;
                while (i < source.Length && source[i] != '\'' && source[i] != '\n')
                {
                    if (source[i] == '\\' && i + 1 < source.Length)
                    {
                        i++;
                    }

                    i++;
                }

                if (i < source.Length && source[i] == '\'')
                {
                    i++;
                }

                continue;
            }

            sb.Append(c);
            i++;
        }

        return sb.ToString().Split('\n');
    }

    /// <summary>
    /// 类型作用域（类型名 + 进入时的花括号深度）。
    /// </summary>
    private sealed record TypeScope(string Name, int Depth);
}
