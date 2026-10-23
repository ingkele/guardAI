using System.Text;
using GuardAI.Core.Config;
using GuardAI.Core.Engine;

namespace GuardAI.Checks.Code;

/// <summary>
/// 代码级检查模块共用骨架（doc / sql / perf）：.cs 文件收集 → 豁免与测试豁免过滤 → 逐文件扫描 → 汇总。
/// </summary>
/// <remarks>
/// 抽骨架合一（2026-10-23）：三个代码级检查共用本模板，子类只写逐文件扫描逻辑
/// （<see cref="ScanFile"/>），避免同族新模块照搬骨架后被自家重复检测逮住（C46 教训）。
/// 口径（只漏不误，宁漏不误）：
/// ①只扫 .cs（多语言文档/SQL 规则后续扩，.ts/.tsx 第二步）；
/// ②测试代码按惯例豁免——测试方法名即文档（微软惯例不强制 XML 注释），
/// 且测试夹具常内嵌 SQL/密钥等反例文本，不参与代码级判定；
/// ③豁免逃生口照旧：guardai.yml exclusions 命中的文件整体跳过。
/// </remarks>
public abstract class CodeScanCheckBase : IGuardCheck
{
    /// <summary>参与扫描的扩展名（v1 仅 C#，多语言后续）。</summary>
    private static readonly IReadOnlyList<string> CsExtensions = [".cs"];

    /// <inheritdoc />
    public abstract string Name { get; }

    /// <inheritdoc />
    public CheckResult Run(CheckContext context)
    {
        var files = TargetFileCollector.Collect(context.RepoPath, context.Mode, CsExtensions);
        var violations = new List<Violation>();
        foreach (var rel in files)
        {
            // 内联豁免判定（不抽公共方法：与 Security 模块的同名小方法错开形态，防重复误判）
            if (context.Config.Exclusions.Any(e => GlobMatcher.IsMatch(rel, e.Path)) || IsTestCode(rel))
            {
                continue;
            }

            var full = Path.Combine(context.RepoPath, rel.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(full))
            {
                continue;
            }

            // UTF-8 宽容解码（带替换字符）：源码混入个别非法字节时不抛异常，仍能完成扫描
            var text = File.ReadAllText(full, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: false));
            ScanFile(rel, text, violations);
        }

        return new CheckResult(Name, files.Count, violations);
    }

    /// <summary>
    /// 逐文件扫描：子类实现各自的规则逻辑，命中即向 violations 追加。
    /// </summary>
    /// <param name="rel">仓库相对路径（正斜杠）。</param>
    /// <param name="text">文件全文（UTF-8 宽容解码）。</param>
    /// <param name="violations">违规收集列表。</param>
    protected abstract void ScanFile(string rel, string text, List<Violation> violations);

    /// <summary>
    /// 判断是否测试代码（按惯例豁免：tests/test 目录段、*.Tests/*.Test 项目段、*Tests.cs/*Test.cs 文件名）。
    /// </summary>
    /// <param name="rel">仓库相对路径（正斜杠）。</param>
    /// <returns>是否测试代码（误判为测试=漏报方向，可接受）。</returns>
    protected static bool IsTestCode(string rel)
    {
        foreach (var segment in rel.Split('/'))
        {
            if (segment.Equals("tests", StringComparison.OrdinalIgnoreCase)
                || segment.Equals("test", StringComparison.OrdinalIgnoreCase)
                || segment.EndsWith(".Tests", StringComparison.OrdinalIgnoreCase)
                || segment.EndsWith(".Test", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return rel.EndsWith("Tests.cs", StringComparison.OrdinalIgnoreCase)
            || rel.EndsWith("Test.cs", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 剥离行注释与块注释、保留字符串字面量（SQL/数据访问特征要看见字符串里的内容）。
    /// </summary>
    /// <param name="text">源码全文。</param>
    /// <returns>剥离注释后的文本（行结构保持）。</returns>
    protected static string StripComments(string text)
    {
        var sb = new StringBuilder(text.Length);
        var i = 0;
        var inString = false;
        while (i < text.Length)
        {
            var c = text[i];
            if (inString)
            {
                sb.Append(c);
                if (c == '\\' && i + 1 < text.Length)
                {
                    sb.Append(text[i + 1]); // 字符串内转义字符（\" 等）原样保留，不误判串尾
                    i += 2;
                    continue;
                }

                if (c == '"')
                {
                    inString = false;
                }

                i++;
                continue;
            }

            if (c == '"')
            {
                inString = true;
                sb.Append(c);
                i++;
                continue;
            }

            if (c == '/' && i + 1 < text.Length && text[i + 1] == '/')
            {
                while (i < text.Length && text[i] != '\n')
                {
                    i++; // 行注释吃掉（换行保留行结构）
                }

                continue;
            }

            if (c == '/' && i + 1 < text.Length && text[i + 1] == '*')
            {
                i += 2;
                while (i + 1 < text.Length && !(text[i] == '*' && text[i + 1] == '/'))
                {
                    if (text[i] == '\n')
                    {
                        sb.Append('\n'); // 块注释内的换行保留行号对齐
                    }

                    i++;
                }

                i = Math.Min(i + 2, text.Length);
                continue;
            }

            sb.Append(c);
            i++;
        }

        return sb.ToString();
    }
}
