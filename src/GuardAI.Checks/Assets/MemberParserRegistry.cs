namespace GuardAI.Checks.Assets;

/// <summary>
/// 成员解析器注册表：按文件扩展名分发到对应语言适配器（P4 多语言）。
/// </summary>
/// <remarks>
/// 单一来源：支持的代码文件扩展名只在本类定义，重复检测与能力清单共用 <see cref="Extensions"/>，
/// 避免两处各持一份扩展名清单后漂移。未知扩展名返回空列表——只漏不误。
/// </remarks>
public static class MemberParserRegistry
{
    /// <summary>扩展名 → 解析器映射（C 系共用括号引擎，Go/Python 专用适配器）。</summary>
    private static readonly Dictionary<string, Func<string, string, IReadOnlyList<CodeMember>>> Parsers =
        new(StringComparer.OrdinalIgnoreCase)
        {
            [".cs"] = (text, path) => CLikeMemberParser.Parse(text, path, LanguageProfile.CSharp),
            [".java"] = (text, path) => CLikeMemberParser.Parse(text, path, LanguageProfile.Java),
            [".kt"] = (text, path) => CLikeMemberParser.Parse(text, path, LanguageProfile.Kotlin),
            [".kts"] = (text, path) => CLikeMemberParser.Parse(text, path, LanguageProfile.Kotlin),
            [".ts"] = (text, path) => CLikeMemberParser.Parse(text, path, LanguageProfile.TypeScript),
            [".tsx"] = (text, path) => CLikeMemberParser.Parse(text, path, LanguageProfile.TypeScript),
            [".js"] = (text, path) => CLikeMemberParser.Parse(text, path, LanguageProfile.JavaScript),
            [".jsx"] = (text, path) => CLikeMemberParser.Parse(text, path, LanguageProfile.JavaScript),
            [".mjs"] = (text, path) => CLikeMemberParser.Parse(text, path, LanguageProfile.JavaScript),
            [".cjs"] = (text, path) => CLikeMemberParser.Parse(text, path, LanguageProfile.JavaScript),
            [".go"] = (text, path) => GoMemberParser.Parse(text, path),
            [".py"] = (text, path) => PythonMemberParser.Parse(text, path),
        };

    /// <summary>
    /// 支持解析的代码文件扩展名清单（含点号，如 .cs）。
    /// </summary>
    public static IReadOnlyList<string> Extensions { get; } = Parsers.Keys.ToArray();

    /// <summary>
    /// 扩展名 → 语言身份键（Issue #7 跨语言隔离的唯一判据）。
    /// </summary>
    /// <remarks>
    /// 与解析器映射同置于本类，保证"能解析的文件必有语言身份"——漏一个扩展名就会让它退化成
    /// 跨语言可比，正是 Issue #7 的成因。同族扩展名共用一个键（.ts/.tsx → typescript）。
    /// Go/Python 不走 <see cref="LanguageProfile"/>，键在此显式给出。
    /// </remarks>
    private static readonly Dictionary<string, string> Languages =
        new(StringComparer.OrdinalIgnoreCase)
        {
            [".cs"] = LanguageProfile.CSharp.SimilarityLanguage,
            [".java"] = LanguageProfile.Java.SimilarityLanguage,
            [".kt"] = LanguageProfile.Kotlin.SimilarityLanguage,
            [".kts"] = LanguageProfile.Kotlin.SimilarityLanguage,
            [".ts"] = LanguageProfile.TypeScript.SimilarityLanguage,
            [".tsx"] = LanguageProfile.TypeScript.SimilarityLanguage,
            [".js"] = LanguageProfile.JavaScript.SimilarityLanguage,
            [".jsx"] = LanguageProfile.JavaScript.SimilarityLanguage,
            [".mjs"] = LanguageProfile.JavaScript.SimilarityLanguage,
            [".cjs"] = LanguageProfile.JavaScript.SimilarityLanguage,
            [".go"] = "go",
            [".py"] = "python",
        };

    /// <summary>
    /// 取文件的语言身份键（跨语言比对隔离用）。
    /// </summary>
    /// <param name="relativePath">仓库相对路径（正斜杠）。</param>
    /// <returns>语言键（如 csharp / typescript）；未知扩展名返回 null（不参与跨语言隔离判定）。</returns>
    public static string? LanguageOf(string relativePath)
    {
        ArgumentNullException.ThrowIfNull(relativePath);
        return Languages.TryGetValue(ExtensionOf(relativePath), out var language) ? language : null;
    }

    /// <summary>
    /// 取扩展名对应的语言剖析参数（C 系语言；Go/Python 无剖析参数返回 null）。
    /// </summary>
    /// <param name="relativePath">仓库相对路径（正斜杠）。</param>
    /// <returns>语言剖析参数；非 C 系或未知扩展名返回 null。</returns>
    public static LanguageProfile? ProfileOf(string relativePath)
    {
        ArgumentNullException.ThrowIfNull(relativePath);
        return ExtensionOf(relativePath).ToLowerInvariant() switch
        {
            ".cs" => LanguageProfile.CSharp,
            ".java" => LanguageProfile.Java,
            ".kt" or ".kts" => LanguageProfile.Kotlin,
            ".ts" or ".tsx" => LanguageProfile.TypeScript,
            ".js" or ".jsx" or ".mjs" or ".cjs" => LanguageProfile.JavaScript,
            _ => null,
        };
    }

    /// <summary>
    /// 判断扩展名是否支持解析。
    /// </summary>
    /// <param name="relativePath">仓库相对路径或文件名。</param>
    /// <returns>是否有对应语言适配器。</returns>
    public static bool Supports(string relativePath)
    {
        return Parsers.ContainsKey(ExtensionOf(relativePath));
    }

    /// <summary>
    /// 解析单个源码文本，抽取全部公共成员快照（按扩展名分发到语言适配器）。
    /// </summary>
    /// <param name="sourceText">源码全文。</param>
    /// <param name="relativePath">仓库相对路径（正斜杠），写入成员快照并决定语言。</param>
    /// <returns>公共成员快照列表；未知语言返回空列表。</returns>
    /// <exception cref="ArgumentNullException">sourceText 或 relativePath 为 null 时抛出。</exception>
    public static IReadOnlyList<CodeMember> Parse(string sourceText, string relativePath)
    {
        ArgumentNullException.ThrowIfNull(sourceText);
        ArgumentNullException.ThrowIfNull(relativePath);
        return Parsers.TryGetValue(ExtensionOf(relativePath), out var parser)
            ? parser(sourceText, relativePath)
            : [];
    }

    /// <summary>
    /// 取路径的扩展名（含点号）；无扩展名返回空串。
    /// </summary>
    /// <param name="relativePath">文件路径。</param>
    /// <returns>扩展名（如 .cs）。</returns>
    private static string ExtensionOf(string relativePath)
    {
        return Path.GetExtension(relativePath.Replace('\\', '/'));
    }
}
