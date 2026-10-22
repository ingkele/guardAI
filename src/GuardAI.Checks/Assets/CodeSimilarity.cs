namespace GuardAI.Checks.Assets;

/// <summary>
/// 方法体相似度：归一化 token 三元组 Jaccard——近似重复检测的指纹引擎。
/// </summary>
/// <remarks>
/// 归一化策略（吸收 AI 重写时的表面差异，保留实现骨架）：
/// 局部标识符 → id、数字 → num、字符串/字符字面量（清洗阶段已抹空）→ str/chr；
/// 保留关键字与成员访问名（.Foo 调用链）——API 调用序列才是"这个方法在干什么"的指纹。
/// 这样改名/改常量的换皮重写指纹不变，无关方法很难撞车。
/// </remarks>
public static class CodeSimilarity
{
    /// <summary>C# 关键字（保留原样，不归一化）。</summary>
    private static readonly HashSet<string> Keywords =
    [
        "abstract", "as", "base", "bool", "break", "byte", "case", "catch", "char", "checked",
        "class", "const", "continue", "decimal", "default", "delegate", "do", "double", "else",
        "enum", "event", "explicit", "extern", "false", "finally", "fixed", "float", "for",
        "foreach", "goto", "if", "implicit", "in", "int", "interface", "internal", "is", "lock",
        "long", "namespace", "new", "null", "object", "operator", "out", "override", "params",
        "private", "protected", "public", "readonly", "record", "ref", "return", "sbyte", "sealed",
        "short", "sizeof", "stackalloc", "static", "string", "struct", "switch", "this", "throw",
        "true", "try", "typeof", "uint", "ulong", "unchecked", "unsafe", "ushort", "using",
        "virtual", "void", "volatile", "while", "var", "async", "await", "yield", "get", "set",
        "init", "value", "when", "where", "with", "not", "and", "or",
    ];

    /// <summary>多字符运算符（整体成 token，避免拆散改变结构指纹）。</summary>
    private static readonly string[] MultiCharOperators =
        ["=>", "==", "!=", "<=", ">=", "&&", "||", "??=", "??", "++", "--", "+=", "-=", "*=", "/=", "%=", "&=", "|=", "^=", "<<", ">>", "->"];

    /// <summary>
    /// 把归一化方法体切成 token 流（标识符/数字按策略归一化）。
    /// </summary>
    /// <param name="normalizedBody">已去注释/字符串/多余空白的方法体文本。</param>
    /// <returns>token 列表。</returns>
    /// <exception cref="ArgumentNullException">normalizedBody 为 null 时抛出。</exception>
    public static IReadOnlyList<string> Tokenize(string normalizedBody)
    {
        ArgumentNullException.ThrowIfNull(normalizedBody);

        var tokens = new List<string>();
        var i = 0;
        while (i < normalizedBody.Length)
        {
            var c = normalizedBody[i];
            if (char.IsWhiteSpace(c))
            {
                i++;
                continue;
            }

            // 标识符 / 关键字
            if (char.IsLetter(c) || c is '_' or '@')
            {
                var start = i;
                i++;
                while (i < normalizedBody.Length && (char.IsLetterOrDigit(normalizedBody[i]) || normalizedBody[i] == '_'))
                {
                    i++;
                }

                var word = normalizedBody[start..i].TrimStart('@');
                if (tokens.Count > 0 && tokens[^1] == ".")
                {
                    tokens.Add(word); // 成员访问名（.Foo）保留——API 调用序列是方法职责的指纹
                }
                else if (Keywords.Contains(word))
                {
                    tokens.Add(word);
                }
                else
                {
                    tokens.Add("id");
                }

                continue;
            }

            // 数字字面量（含 0x/小数/后缀）→ num，吸收常量微调
            if (char.IsDigit(c))
            {
                while (i < normalizedBody.Length
                    && (char.IsLetterOrDigit(normalizedBody[i]) || normalizedBody[i] == '.'))
                {
                    i++;
                }

                tokens.Add("num");
                continue;
            }

            // 字符串占位（清洗产物 "" 或 "N" 长度标记）→ str；带长度标记时追加 len_N 与幂次桶 lenb_N。
            // 长度进 token：字符串内容归一后不同文本会撞成同一流，长度是最低成本的区分信号——
            // 精确 len_N 负责区分不同文本，幂次桶 lenb_N 给"±几字微调"留韧性（吸收字面量微调的原语义）
            if (c == '"')
            {
                i++;
                var contentStart = i;
                while (i < normalizedBody.Length && normalizedBody[i] != '"')
                {
                    i++;
                }

                var content = normalizedBody[contentStart..i];
                if (i < normalizedBody.Length)
                {
                    i++; // 收尾引号
                }

                tokens.Add("str");
                if (content.Length > 0 && long.TryParse(content, out var length) && length > 0)
                {
                    tokens.Add($"len_{length}");
                    var bucket = 1L;
                    while (bucket < length)
                    {
                        bucket <<= 1;
                    }

                    tokens.Add($"lenb_{bucket}");
                }

                continue;
            }

            if (c == '\'')
            {
                i++;
                if (i < normalizedBody.Length && normalizedBody[i] == '\'')
                {
                    i++;
                }

                tokens.Add("chr");
                continue;
            }

            // 多字符运算符优先
            var op = MatchOperator(normalizedBody, i);
            tokens.Add(op);
            i += op.Length;
        }

        return tokens;
    }

    /// <summary>
    /// 计算两段 token 流的三元组 Jaccard 相似度（0~1）。
    /// </summary>
    /// <param name="tokensA">token 流 A。</param>
    /// <param name="tokensB">token 流 B。</param>
    /// <returns>相似度（任一侧为空返回 0）。</returns>
    /// <exception cref="ArgumentNullException">tokensA 或 tokensB 为 null 时抛出。</exception>
    public static double Jaccard(IReadOnlyList<string> tokensA, IReadOnlyList<string> tokensB)
    {
        ArgumentNullException.ThrowIfNull(tokensA);
        ArgumentNullException.ThrowIfNull(tokensB);

        var shinglesA = Shingles(tokensA);
        var shinglesB = Shingles(tokensB);
        if (shinglesA.Count == 0 || shinglesB.Count == 0)
        {
            return 0;
        }

        var intersection = 0;
        foreach (var s in shinglesA)
        {
            if (shinglesB.Contains(s))
            {
                intersection++;
            }
        }

        var union = shinglesA.Count + shinglesB.Count - intersection;
        return union == 0 ? 0 : (double)intersection / union;
    }

    /// <summary>
    /// 计算两段 token 流的三元组包含度（|∩| / min(|A|,|B|)，0~1）。
    /// </summary>
    /// <remarks>
    /// 抓"子集型复制"：复制时删行/加行（如逐字段映射样板漏字段）时 Jaccard 被并集稀释会漏，
    /// 包含度以较小方为分母不受稀释——一小段被完整复读进大段也是重复。
    /// </remarks>
    /// <param name="tokensA">token 流 A。</param>
    /// <param name="tokensB">token 流 B。</param>
    /// <returns>包含度（任一侧为空返回 0）。</returns>
    /// <exception cref="ArgumentNullException">tokensA 或 tokensB 为 null 时抛出。</exception>
    public static double Containment(IReadOnlyList<string> tokensA, IReadOnlyList<string> tokensB)
    {
        ArgumentNullException.ThrowIfNull(tokensA);
        ArgumentNullException.ThrowIfNull(tokensB);

        var shinglesA = Shingles(tokensA);
        var shinglesB = Shingles(tokensB);
        if (shinglesA.Count == 0 || shinglesB.Count == 0)
        {
            return 0;
        }

        var intersection = 0;
        foreach (var s in shinglesA)
        {
            if (shinglesB.Contains(s))
            {
                intersection++;
            }
        }

        var smaller = Math.Min(shinglesA.Count, shinglesB.Count);
        return smaller == 0 ? 0 : (double)intersection / smaller;
    }

    /// <summary>
    /// 构造 token 三元组集合（不足 3 个 token 时整体成一枚指纹）。
    /// </summary>
    private static HashSet<string> Shingles(IReadOnlyList<string> tokens)
    {
        var set = new HashSet<string>();
        if (tokens.Count < 3)
        {
            if (tokens.Count > 0)
            {
                set.Add(string.Join(' ', tokens));
            }

            return set;
        }

        for (var i = 0; i + 2 < tokens.Count; i++)
        {
            set.Add($"{tokens[i]} {tokens[i + 1]} {tokens[i + 2]}");
        }

        return set;
    }

    /// <summary>
    /// 匹配位置 i 处的（多字符）运算符；无匹配返回单字符。
    /// </summary>
    private static string MatchOperator(string text, int i)
    {
        foreach (var op in MultiCharOperators)
        {
            if (i + op.Length <= text.Length && text.AsSpan(i, op.Length).SequenceEqual(op))
            {
                return op;
            }
        }

        return text[i].ToString();
    }
}
