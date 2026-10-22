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
public sealed record LanguageProfile(
    string TypePattern,
    string? VisibilityPattern,
    string? LambdaToken,
    string DocLinePrefix,
    IReadOnlySet<string> ExtraKeywords)
{
    /// <summary>
    /// C# 既有语义（行为与历史版本完全一致）：public 方法、/// 文档注释、不启用 lambda 防线。
    /// </summary>
    public static readonly LanguageProfile CSharp = new(
        @"\b(?:(?:record)\s+(?:class|struct)|(?:class|interface|struct|enum|record))\s+(?<name>[A-Za-z_]\w*)",
        @"\bpublic\b",
        null,
        "///",
        new HashSet<string>(StringComparer.Ordinal));

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
        });

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
        });

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
        });

    /// <summary>
    /// JavaScript：仅 class（顶层 function 归无类型成员，与 C# 命名空间级方法同语义）。
    /// </summary>
    public static readonly LanguageProfile JavaScript = new(
        @"\bclass\s+(?<name>[A-Za-z_]\w*)",
        null,
        "=>",
        "//",
        TypeScript.ExtraKeywords);
}
