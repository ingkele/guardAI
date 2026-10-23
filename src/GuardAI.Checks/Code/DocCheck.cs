using System.Text.RegularExpressions;
using GuardAI.Checks.Assets;
using GuardAI.Core.Engine;

namespace GuardAI.Checks.Code;

/// <summary>
/// doc 检查模块：公共 API 文档注释确定性检查——public 类型/方法缺 /// 摘要当场报。
/// </summary>
/// <remarks>
/// 来源：learn 回放 91 发现（2026-10-23 人工筛 P0）——注释缺失 15 条全库最高频，
/// "公共成员 + /// 摘要存在性"完全确定性，最适合引擎执法。
/// 口径（只漏不误，宁漏不误）：
/// ①公共类型（class/interface/struct/enum/record）与公共方法（成员解析器口径），缺 /// 即违规，
///   仅 // 行注释不算（生成不了 API 文档）；/// &lt;inheritdoc /&gt; 算有；
/// ②partial 类型拆分件、泛型约束行、属性/字段/抽象与接口成员（分号声明）暂不查——漏报优于误报；
/// ③测试代码豁免与豁免逃生口见 <see cref="CodeScanCheckBase"/>。
/// </remarks>
public sealed class DocCheck : CodeScanCheckBase
{
    /// <summary>类型声明识别（C# 语义，命名组 name 取类型名）。</summary>
    private static readonly Regex TypePattern = new(
        LanguageProfile.CSharp.TypePattern,
        RegexOptions.Compiled);

    /// <summary>公共可见性关键字（类型声明行必备）。</summary>
    private static readonly Regex PublicKeywordPattern = new(
        @"\bpublic\b",
        RegexOptions.Compiled);

    /// <summary>partial 拆分件识别（多拆分件只有一件带注释时会误报，整体跳过）。</summary>
    private static readonly Regex PartialPattern = new(
        @"\bpartial\b",
        RegexOptions.Compiled);

    /// <summary>类型名黑名单：泛型约束（where T : class）同形假名，不是类型声明。</summary>
    private static readonly HashSet<string> FakeTypeNames = ["where", "new"];

    /// <inheritdoc />
    public override string Name => "doc";

    /// <inheritdoc />
    protected override void ScanFile(string rel, string text, List<Violation> violations)
    {
        var rawLines = text.Replace("\r\n", "\n").Split('\n');
        var cleanLines = ParseText.Clean(text);

        // ① 公共类型：clean 行（注释/字符串已抹掉）识别声明，原始行上查 /// 摘要
        for (var i = 0; i < cleanLines.Length; i++)
        {
            var line = cleanLines[i];
            var match = TypePattern.Match(line);
            if (!match.Success || FakeTypeNames.Contains(match.Groups["name"].Value))
            {
                continue;
            }

            var head = line[..match.Index];
            if (!PublicKeywordPattern.IsMatch(head)
                || head.Contains('(')
                || PartialPattern.IsMatch(line))
            {
                continue;
            }

            if (HasApiDoc(rawLines, i))
            {
                continue;
            }

            violations.Add(new Violation(
                rel,
                "缺少文档注释",
                $"公共类型「{match.Groups["name"].Value}」缺少 XML 文档注释（第 {i + 1} 行；/// <summary> 缺失）",
                "补中文 XML 文档注释：/// <summary> 说明职责（公共 API 建议 Summary/Parameters/Returns 齐全）；确有特殊情形走 guardai.yml exclusions 豁免"));
        }

        // ② 公共方法：成员解析器抽公共方法（多语言语义），无 /// 摘要即违规
        foreach (var member in MemberParserRegistry.Parse(text, rel))
        {
            if (HasApiDoc(rawLines, member.Line - 1))
            {
                continue;
            }

            violations.Add(new Violation(
                rel,
                "缺少文档注释",
                $"公共方法「{member.TypeName}.{member.Name}」缺少 XML 文档注释（第 {member.Line} 行；/// <summary> 缺失，仅 // 行注释不算）",
                "补中文 XML 文档注释：/// <summary> 说明职责，方法再补 <param>/<returns>（参考微软 .NET 文档标准）；确有特殊情形走 guardai.yml exclusions 豁免"));
        }
    }

    /// <summary>
    /// 判断声明行是否有可生成 API 文档的注释：/// 摘要非空，或注释块含 &lt;inheritdoc /&gt;（继承基类文档，算有）。
    /// </summary>
    /// <param name="rawLines">原始源码行（与清洗后行对齐）。</param>
    /// <param name="lineIndex">声明行下标（0 起）。</param>
    /// <returns>是否有文档注释。</returns>
    private static bool HasApiDoc(string[] rawLines, int lineIndex)
    {
        if (ParseText.ExtractCommentSummary(rawLines, lineIndex, "///").Length > 0)
        {
            return true;
        }

        // 纯 /// <inheritdoc /> 注释块经摘要归一化后为空，但继承基类文档——按有文档论（不报）
        var docBlock = rawLines.Take(lineIndex).Reverse()
            .TakeWhile(line =>
            {
                var t = line.TrimStart();
                return t.StartsWith("///", StringComparison.Ordinal) || t.StartsWith('[');
            });
        return docBlock.Any(line => line.Contains("<inheritdoc", StringComparison.OrdinalIgnoreCase));
    }
}
