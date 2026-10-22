using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace GuardAI.Checks.Assets;

/// <summary>
/// C 系轻量语法适配器：从源码文本抽取公共方法（能力清单 / 重复检测共用）。
/// </summary>
/// <remarks>
/// 设计取舍（P3-D1）：轻量行级扫描 + 文本归一化，不引 Roslyn——守住零外部依赖铁律。
/// 复杂语法（局部函数、复杂泛型、花式条件编译）遵循"只漏不误"原则：
/// 漏 = 漏报一个能力，绝不产生误报条目——误报比漏报更伤体验。
/// 方法体统一归一化（去注释/字符串字面量/空白），供能力哈希与 M2 近似检测复用。
/// P4 多语言：C#/Java/Kotlin/TS/JS 共用本引擎，语言差异收敛到 <see cref="LanguageProfile"/>；
/// 无可见性约束的语言（Java/Kotlin/TS/JS）额外收紧判定（调用形态过滤），宁漏不误。
/// </remarks>
public static class CLikeMemberParser
{
    /// <summary>不作为方法名识别的关键字（控制流/上下文关键字等，全语言公共部分）。</summary>
    private static readonly HashSet<string> MethodNameBlacklist =
    [
        "if", "for", "foreach", "while", "switch", "catch", "using", "lock", "return", "do", "else",
        "try", "fixed", "when", "new", "in", "is", "as", "get", "set", "init", "where", "select",
        "from", "join", "let", "into", "orderby", "group", "by", "on", "equals", "global", "add",
        "remove", "value", "await", "yield", "case", "default", "throw", "checked", "unchecked",
        "sizeof", "typeof", "nameof", "delegate", "operator", "implicit", "explicit", "not", "and", "or",
        "base", "this", "super",
    ];

    /// <summary>类型声明正则缓存（键为剖析参数的 TypePattern，剖析参数是静态单例，命中率恒定）。</summary>
    private static readonly Dictionary<string, Regex> TypeRegexCache = new();

    /// <summary>关键字黑名单缓存（全语言公共黑名单 + 语言补充关键字的并集）。</summary>
    private static readonly Dictionary<string, HashSet<string>> BlacklistCache = new();

    /// <summary>
    /// 取语言对应类型声明正则（按 TypePattern 缓存，剖析参数是静态单例）。
    /// </summary>
    /// <param name="profile">语言剖析参数。</param>
    /// <returns>类型声明正则。</returns>
    private static Regex TypeRegexFor(LanguageProfile profile)
    {
        lock (TypeRegexCache)
        {
            if (!TypeRegexCache.TryGetValue(profile.TypePattern, out var regex))
            {
                TypeRegexCache[profile.TypePattern] = regex = new Regex(
                    profile.TypePattern, RegexOptions.Compiled);
            }

            return regex;
        }
    }

    /// <summary>
    /// 取语言对应方法名黑名单（公共关键字 ∪ 语言补充关键字）。
    /// </summary>
    /// <param name="profile">语言剖析参数。</param>
    /// <returns>合并后的关键字黑名单。</returns>
    private static HashSet<string> BlacklistFor(LanguageProfile profile)
    {
        var key = profile.TypePattern + "|" + profile.DocLinePrefix;
        lock (BlacklistCache)
        {
            if (!BlacklistCache.TryGetValue(key, out var merged))
            {
                merged = new HashSet<string>(MethodNameBlacklist, StringComparer.Ordinal);
                foreach (var word in profile.ExtraKeywords)
                {
                    merged.Add(word);
                }

                BlacklistCache[key] = merged;
            }

            return merged;
        }
    }

    /// <summary>方法名匹配：方法名（可带泛型参数）紧跟左括号，且不被成员访问符修饰。</summary>
    private static readonly Regex MethodNameRegex = new(
        @"(?<![.\w])(?<name>[A-Za-z_]\w*)\s*(?:<[^<>]*>)?\s*\(",
        RegexOptions.Compiled);

    /// <summary>签名头部结尾的方法名（可带泛型参数）。</summary>
    private static readonly Regex TrailingNameRegex = new(
        @"(?<![.\w])(?<name>[A-Za-z_]\w*)\s*(?:<[^<>]*>)?\s*$",
        RegexOptions.Compiled);

    /// <summary>
    /// 解析单个 C 系源码文本，抽取全部公共方法快照（缺省按 C# 语义，行为与历史版本一致）。
    /// </summary>
    /// <param name="sourceText">源码全文。</param>
    /// <param name="relativePath">仓库相对路径（正斜杠），写入成员快照。</param>
    /// <param name="profile">语言剖析参数；null 表示 C#。</param>
    /// <returns>公共方法快照列表（按出现顺序）。</returns>
    /// <exception cref="ArgumentNullException">sourceText 或 relativePath 为 null 时抛出。</exception>
    public static IReadOnlyList<CodeMember> Parse(string sourceText, string relativePath, LanguageProfile? profile = null)
    {
        ArgumentNullException.ThrowIfNull(sourceText);
        ArgumentNullException.ThrowIfNull(relativePath);
        profile ??= LanguageProfile.CSharp;

        var lines = ParseText.Clean(sourceText);
        var rawLines = sourceText.Replace("\r\n", "\n").Split('\n');
        var members = new List<CodeMember>();
        var typeStack = new Stack<TypeScope>();
        var typeRegex = TypeRegexFor(profile);
        var blacklist = BlacklistFor(profile);
        var depth = 0;
        var i = 0;

        while (i < lines.Length)
        {
            var trimmed = lines[i].Trim();

            // 空行 / 预处理指令 / 特性行：只走花括号深度
            if (trimmed.Length == 0 || trimmed.StartsWith('#') || trimmed.StartsWith('['))
            {
                var net = ParseText.NetBraces(lines[i]);
                depth += net;
                if (net < 0)
                {
                    PopTypes(typeStack, depth);
                }

                i++;
                continue;
            }

            // 类型声明：压栈（方法归属用）
            if (TryMatchTypeName(trimmed, typeRegex, out var typeName))
            {
                typeStack.Push(new TypeScope(typeName, depth));
                var net = ParseText.NetBraces(lines[i]);
                depth += net;
                if (net < 0)
                {
                    PopTypes(typeStack, depth);
                }

                i++;
                continue;
            }

            // 方法候选：整段签名（可跨行）+ 方法体（可跨行）一次消费
            if (IsMethodCandidate(trimmed, blacklist))
            {
                var before = depth;
                var end = ConsumeMethod(lines, rawLines, i, typeStack, relativePath, members, profile, blacklist);
                for (var k = i; k < end; k++)
                {
                    depth += ParseText.NetBraces(lines[k]);
                }

                // 紧凑写法里类型可能随方法同收尾，深度下降才弹栈（多行主构造函数等深度未降的行绝不弹）
                if (depth < before)
                {
                    PopTypes(typeStack, depth);
                }

                i = end;
                continue;
            }

            var plainNet = ParseText.NetBraces(lines[i]);
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
    /// 消费一个方法候选（签名 + 方法体），成功则写入成员列表。
    /// </summary>
    /// <returns>消费结束后的下一行行号（未识别出公共方法时同样前进，保证不死循环）。</returns>
    private static int ConsumeMethod(
        string[] lines,
        string[] rawLines,
        int start,
        Stack<TypeScope> typeStack,
        string relativePath,
        List<CodeMember> members,
        LanguageProfile profile,
        HashSet<string> blacklist)
    {
        var buffer = new StringBuilder(lines[start].Trim());
        var end = start + 1;

        // 1) 收齐签名：直到右括号闭合
        while (ParseText.ParenBalance(buffer.ToString()) > 0 && end < lines.Length)
        {
            buffer.Append(' ').Append(lines[end].Trim());
            end++;
        }

        if (ParseText.ParenBalance(buffer.ToString()) != 0)
        {
            return end;
        }

        var sigText = buffer.ToString();

        // 参数表左括号 = "配对右括号后跟 => / { / ; / where / 结尾"的括号组——
        // 返回值可能是元组 (string A, string B)，首个 '(' 不一定是参数表
        // （此前取首个 '('，元组返回方法的方法名被解析成 "static"）
        var open = FindParamsOpenParen(sigText);
        if (open < 0)
        {
            return end;
        }

        var head = sigText[..open];

        // 2) 声明形态闸：注释/特性剥离后按语言要求判定（构造函数（方法名 == 类型名）跳过）
        if (!IsDeclarationHead(head, profile) || !TryGetMethodName(head, blacklist, out var name))
        {
            return end;
        }

        if (typeStack.Count > 0 && string.Equals(name, typeStack.Peek().Name, StringComparison.Ordinal))
        {
            return end;
        }

        var close = ParseText.MatchingParen(sigText, open);

        // 2.5) lambda 防线：参数表中出现 lambda 记号说明是"调用传 lambda"而非声明——宁漏不误
        // （方法声明的参数类型里带 lambda 记号会连带漏掉，按只漏不误接受）
        if (profile.LambdaToken is { } lambdaToken
            && sigText[open..(close + 1)].Contains(lambdaToken, StringComparison.Ordinal))
        {
            return end;
        }

        // 3) 收终结符：';'（抽象/接口，跳过）/ '=>'（表达式体）/ '{'（块方法体）
        var tail = new StringBuilder(sigText[(close + 1)..]);
        var terminator = FindTerminator(tail.ToString(), out var at);
        while (terminator is null && end < lines.Length)
        {
            // 越行收集防线：下一行已是新的成员/类型声明则放弃（只漏不误，防把后续方法吞进尾段造幻影成员）
            if (IsBoundaryLine(lines[end], TypeRegexFor(profile), blacklist))
            {
                return end;
            }

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
            // 表达式体：收集到语句结束分号（越行收集同防线：遇新声明边界即止）
            var expr = new StringBuilder(tail.ToString()[(at + 2)..].TrimStart());
            while (!TryCutStatementEnd(expr.ToString(), out _) && end < lines.Length)
            {
                if (IsBoundaryLine(lines[end], TypeRegexFor(profile), blacklist))
                {
                    break;
                }

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
                balance = ParseText.CountUntilClose(pending, balance, ref closed);
                if (closed || end >= lines.Length)
                {
                    break;
                }

                pending = "\n" + lines[end];
                end++;
            }

            // 文件尾仍未闭合：放弃，按"只漏不误"处理
            bodyText = closed ? ParseText.ExtractInnerBody(body.ToString()) : string.Empty;
        }

        var normalizedBody = ParseText.Normalize(bodyText);
        var signature = ParseText.Normalize(sigText[..(close + 1)]);
        members.Add(new CodeMember(
            typeStack.Count > 0 ? typeStack.Peek().Name : string.Empty,
            name,
            signature,
            relativePath,
            start + 1,
            normalizedBody,
            ParseText.HashBody(normalizedBody),
            ParseText.ExtractCommentSummary(rawLines, start, profile.DocLinePrefix)));
        return end;
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
    /// <param name="trimmed">清洗后去首尾空白的行。</param>
    /// <param name="blacklist">语言合并关键字黑名单。</param>
    /// <returns>是否方法候选。</returns>
    private static bool IsMethodCandidate(string trimmed, HashSet<string> blacklist)
    {
        if (!trimmed.Contains('(') || trimmed.StartsWith("return ", StringComparison.Ordinal))
        {
            return false;
        }

        foreach (Match match in MethodNameRegex.Matches(trimmed))
        {
            if (!blacklist.Contains(match.Groups["name"].Value))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// 匹配类型声明并取出类型名。
    /// </summary>
    /// <param name="trimmed">清洗后去首尾空白的行。</param>
    /// <param name="typeRegex">语言类型声明正则。</param>
    /// <param name="typeName">类型名（未命中为空串）。</param>
    /// <returns>是否类型声明行。</returns>
    private static bool TryMatchTypeName(string trimmed, Regex typeRegex, out string typeName)
    {
        var match = typeRegex.Match(trimmed);
        typeName = match.Success ? match.Groups["name"].Value : string.Empty;
        return match.Success;
    }

    /// <summary>
    /// 从签名头部（左括号之前）提取方法名。
    /// </summary>
    /// <param name="head">签名头部文本。</param>
    /// <param name="blacklist">语言合并关键字黑名单。</param>
    /// <param name="name">方法名（未命中为空串）。</param>
    /// <returns>是否提取成功。</returns>
    private static bool TryGetMethodName(string head, HashSet<string> blacklist, out string name)
    {
        var match = TrailingNameRegex.Match(head);
        name = match.Success ? match.Groups["name"].Value : string.Empty;
        return match.Success && !blacklist.Contains(name);
    }

    /// <summary>
    /// 声明形态闸：剥注解/装饰器后按语言要求判定头部是否为方法声明。
    /// </summary>
    /// <remarks>
    /// 有可见性要求（C# public）按要求匹配；无可见性要求的语言要求剥注解后头部不含 '='——
    /// 字段初始化 / 属性赋值 / 匿名类创建都不是声明（宁漏不误）。
    /// 纯注解/装饰器行（剥完为空）不构成声明（防 @Component({...}) 之类被当成方法头）。
    /// </remarks>
    /// <param name="head">签名头部文本。</param>
    /// <param name="profile">语言剖析参数。</param>
    /// <returns>是否方法声明形态。</returns>
    private static bool IsDeclarationHead(string head, LanguageProfile profile)
    {
        var stripped = Regex.Replace(head, @"@\w+(?:\s*\((?:[^()]|\([^()]*\))*\))?", " ");
        if (string.IsNullOrWhiteSpace(stripped))
        {
            return false;
        }

        if (profile.VisibilityPattern is { } visibility)
        {
            return Regex.IsMatch(stripped, visibility, RegexOptions.CultureInvariant);
        }

        return !stripped.Contains('=');
    }

    /// <summary>
    /// 越行收集边界判定：下一行是否已是新的类型/成员声明（是则停止越行收集，只漏不误）。
    /// </summary>
    /// <remarks>
    /// 括号净欠的行是多行实参续行，不当边界；行尾是 '{' / ';' / '=>' 的声明形态行才算。
    /// 防"fun f() = 42"这类无终结符尾段把后续方法吞进来造幻影成员。
    /// </remarks>
    /// <param name="cleanLine">清洗后的行。</param>
    /// <param name="typeRegex">语言类型声明正则。</param>
    /// <param name="blacklist">语言合并关键字黑名单。</param>
    /// <returns>是否声明边界行。</returns>
    private static bool IsBoundaryLine(string cleanLine, Regex typeRegex, HashSet<string> blacklist)
    {
        var trimmed = cleanLine.Trim();
        if (trimmed.Length == 0 || ParseText.ParenBalance(trimmed) < 0)
        {
            return false;
        }

        if (TryMatchTypeName(trimmed, typeRegex, out _))
        {
            return true;
        }

        return IsMethodCandidate(trimmed, blacklist)
            && (trimmed.EndsWith('{') || trimmed.EndsWith(';')
                || trimmed.EndsWith("=>", StringComparison.Ordinal));
    }

    /// <summary>
    /// 找参数表左括号：逐组跳过前置括号组（如返回值元组、注解实参），
    /// 取"配对右括号到下一个方法体起始记号（=> / { / ;）之间不再含括号"的那一组——
    /// 参数表之后只有修饰尾巴（返回类型 / throws / where / 冒号继承列表），不会再有括号。
    /// （旧实现按紧跟记号白名单判定，Java 的 throws、Kotlin 的返回类型尾巴全被漏掉。）
    /// </summary>
    /// <param name="signature">括号已配平的签名文本。</param>
    /// <returns>参数表左括号下标；找不到返回 -1。</returns>
    private static int FindParamsOpenParen(string signature)
    {
        for (var i = 0; i < signature.Length; i++)
        {
            if (signature[i] != '(')
            {
                continue;
            }

            var close = ParseText.MatchingParen(signature, i);
            if (close < 0)
            {
                return -1;
            }

            var k = close + 1;
            var hasParen = false;
            while (k < signature.Length)
            {
                var c = signature[k];
                if (c is '{' or ';' || (c == '=' && k + 1 < signature.Length && signature[k + 1] == '>'))
                {
                    break;
                }

                if (c is '(' or ')')
                {
                    hasParen = true;
                    break;
                }

                k++;
            }

            if (!hasParen)
            {
                return i;
            }

            i = close; // 非参数表括号组：整组跳过（内部括号不再单看）
        }

        return -1;
    }

    /// <summary>类型作用域（方法归属用）。</summary>
    /// <param name="Name">类型名。</param>
    /// <param name="Depth">声明时的花括号深度。</param>
    private sealed record TypeScope(string Name, int Depth);
}
