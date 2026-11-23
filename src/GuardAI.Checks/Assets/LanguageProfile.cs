namespace GuardAI.Checks.Assets;

/// <summary>
/// C 系语言剖析参数：CLikeMemberParser 的语言差异单点（P4 多语言）。
/// </summary>
/// <remarks>
/// 单一来源原则：C#/Java/Kotlin/TS/JS 共用同一套括号引擎（CLikeMemberParser），
/// 语言间差异（类型声明、可见性要求、lambda 记号、文档注释前缀、关键字黑名单）全部收敛到本剖析参数，
/// 避免"同族新模块照搬骨架"（案例 C46 的教训）。
/// </remarks>
/// <param name="TypePattern">类型声明正则（命名组 name 取类型名）。</param>
/// <param name="VisibilityPattern">方法必备修饰正则；null 表示不限可见性（收全量成员）。</param>
/// <param name="LambdaToken">参数表中出现即判为"调用传 lambda"的记号（=&gt; / -&gt;）；null 不启用此防线。</param>
/// <param name="DocLinePrefix">文档注释行前缀（取上方紧邻注释作为摘要原料）。</param>
/// <param name="ExtraKeywords">语言关键字补充黑名单（防控制流/惯用调用被误判为方法）。</param>
/// <param name="SimilarityKeywords">相似度归一化时保留原样的关键字（跨语言区分的语义锚点，Issue #7）。</param>
public sealed record LanguageProfile(
    string TypePattern,
    string? VisibilityPattern,
    string? LambdaToken,
    string DocLinePrefix,
    IReadOnlySet<string> ExtraKeywords,
    IReadOnlySet<string>? SimilarityKeywords = null)
{
    /// <summary>
    /// 语言在重复检测里的身份键（Issue #7 跨语言隔离的判据）。
    /// </summary>
    /// <remarks>
    /// 同族语言共用一个键：.ts/.tsx 都是 TypeScript，.js/.jsx/.mjs/.cjs 都是 JavaScript。
    /// 不同键的成员物理上不可能互相复用，比对时直接跳过。
    /// </remarks>
    public string SimilarityLanguage { get; init; } = "unknown";

    /// <summary>
    /// 相似度归一化时要保留原样的关键字集合（缺省为通用 C 系控制流词）。
    /// </summary>
    /// <remarks>
    /// 与 <see cref="ExtraKeywords"/> 是两件事：那个防"控制流被误判成方法"，
    /// 这个防"语言特征词被归一成 id 导致跨语言撞车"（Issue #7 根因之一）。
    /// </remarks>
    public IReadOnlySet<string> Keywords => SimilarityKeywords ?? DefaultSimilarityKeywords;

    /// <summary>通用 C 系控制流关键字（C#/Java/Kotlin/TS/JS 共有，缺省值）。</summary>
    private static readonly HashSet<string> DefaultSimilarityKeywords = new(StringComparer.Ordinal)
    {
        "if", "else", "for", "foreach", "while", "do", "switch", "case", "default", "break", "continue",
        "return", "throw", "try", "catch", "finally", "new", "this", "null", "true", "false",
        "var", "await", "async", "yield", "in", "is", "as", "typeof", "instanceof", "void",
    };

    /// <summary>
    /// C# 既有语义（行为与历史版本完全一致）：public 方法、/// 文档注释、不启用 lambda 防线。
    /// </summary>
    public static readonly LanguageProfile CSharp = new(
        @"\b(?:(?:record)\s+(?:class|struct)|(?:class|interface|struct|enum|record))\s+(?<name>[A-Za-z_]\w*)",
        @"\bpublic\b",
        null,
        "///",
        new HashSet<string>(StringComparer.Ordinal),
        new HashSet<string>(StringComparer.Ordinal)
        {
            "abstract", "as", "base", "break", "case", "catch", "checked", "class", "const", "continue",
            "default", "delegate", "do", "else", "enum", "event", "explicit", "extern", "false", "finally",
            "fixed", "for", "foreach", "goto", "if", "implicit", "in", "interface", "internal", "is", "lock",
            "namespace", "new", "null", "operator", "out", "override", "params", "private", "protected",
            "public", "readonly", "record", "ref", "return", "sealed", "sizeof", "stackalloc", "static",
            "struct", "switch", "this", "throw", "true", "try", "typeof", "unchecked", "unsafe", "using",
            "virtual", "volatile", "while", "var", "async", "await", "yield", "get", "set", "init",
            "value", "when", "where", "with", "not", "and", "or",
        })
    {
        SimilarityLanguage = "csharp",
    };

    /// <summary>
    /// Java：类/接口/枚举/record，收全量成员（包级私有也入库），lambda 记号 -&gt;。
    /// </summary>
    public static readonly LanguageProfile Java = new(
        @"\b(?:class|interface|enum|record)\s+(?<name>[A-Za-z_]\w*)",
        null,
        "->",
        "//",
        new HashSet<string>(StringComparer.Ordinal)
        {
            "synchronized", "assert", "throw", "throws", "package", "import", "instanceof", "extends", "implements",
        },
        new HashSet<string>(StringComparer.Ordinal)
        {
            "abstract", "assert", "break", "case", "catch", "class", "continue", "default", "do", "else",
            "enum", "extends", "final", "finally", "for", "if", "implements", "import", "instanceof",
            "interface", "native", "new", "package", "private", "protected", "public", "return", "static",
            "strictfp", "super", "switch", "synchronized", "this", "throw", "throws", "transient", "try",
            "volatile", "while", "var", "true", "false", "null", "record", "yield",
        })
    {
        SimilarityLanguage = "java",
    };

    /// <summary>
    /// Kotlin：class/interface/object（enum class / data class 含 class 关键字自然命中），lambda 记号 -&gt;。
    /// </summary>
    public static readonly LanguageProfile Kotlin = new(
        @"\b(?:class|interface|object)\s+(?<name>[A-Za-z_]\w*)",
        null,
        "->",
        "//",
        new HashSet<string>(StringComparer.Ordinal)
        {
            "fun", "when", "companion", "suspend", "override", "init", "object", "package", "import", "is", "as",
        },
        new HashSet<string>(StringComparer.Ordinal)
        {
            "as", "break", "class", "companion", "continue", "data", "do", "else", "enum", "for",
            "fun", "if", "in", "interface", "is", "object", "package", "return", "sealed", "super",
            "suspend", "this", "throw", "try", "catch", "finally", "val", "var", "when", "while",
            "override", "private", "protected", "public", "internal", "open", "abstract", "null", "true", "false",
        })
    {
        SimilarityLanguage = "kotlin",
    };

    /// <summary>
    /// TypeScript：class/interface/enum；函数类型注解里的 =&gt; 会被 lambda 防线漏拦（宁漏不误）。
    /// </summary>
    public static readonly LanguageProfile TypeScript = new(
        @"\b(?:class|interface|enum)\s+(?<name>[A-Za-z_]\w*)",
        null,
        "=>",
        "//",
        new HashSet<string>(StringComparer.Ordinal)
        {
            "function", "describe", "it", "test", "expect", "beforeEach", "afterEach", "beforeAll", "afterAll",
            "typeof", "await", "yield", "constructor", "get", "set",
        },
        new HashSet<string>(StringComparer.Ordinal)
        {
            "as", "async", "await", "break", "case", "catch", "class", "const", "constructor", "continue",
            "debugger", "declare", "default", "delete", "do", "else", "enum", "export", "extends", "false",
            "finally", "for", "function", "if", "implements", "import", "in", "instanceof", "interface",
            "let", "new", "null", "readonly", "return", "static", "super", "switch", "this", "throw",
            "true", "try", "type", "typeof", "undefined", "var", "void", "while", "yield",
        })
    {
        SimilarityLanguage = "typescript",
    };

    /// <summary>
    /// JavaScript：仅 class（顶层 function 归无类型成员，与 C# 命名空间级方法同语义）。
    /// </summary>
    public static readonly LanguageProfile JavaScript = new(
        @"\bclass\s+(?<name>[A-Za-z_]\w*)",
        null,
        "=>",
        "//",
        TypeScript.ExtraKeywords,
        new HashSet<string>(StringComparer.Ordinal)
        {
            "async", "await", "break", "case", "catch", "class", "const", "continue", "debugger", "default",
            "delete", "do", "else", "export", "extends", "false", "finally", "for", "function", "if",
            "import", "in", "instanceof", "let", "new", "null", "return", "super", "switch", "this",
            "throw", "true", "try", "typeof", "undefined", "var", "void", "while", "yield",
        })
    {
        SimilarityLanguage = "javascript",
    };
}
