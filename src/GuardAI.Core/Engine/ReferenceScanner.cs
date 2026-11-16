using System.Text.RegularExpressions;

namespace GuardAI.Core.Engine;

/// <summary>
/// 跨语言引用扫描：从源码里提取 using / import / require 目标（轻量词法，不引 AST 库）。
/// </summary>
/// <remarks>
/// 支持 .cs / .java / .ts / .tsx / .go / .py；先过 <see cref="SourceText.StripComments"/>，
/// 且指令不在字符串里的语言（.cs / .java）再过 <see cref="SourceText.BlankStringLiterals"/>，
/// 避免注释或字符串里独占一行的 "using Xxx;" 被误判。只认明确的指令形态，识别不了的一律放行（只漏不误）。
/// </remarks>
public static class ReferenceScanner
{
    private const RegexOptions Options = RegexOptions.Multiline | RegexOptions.CultureInvariant;

    private static readonly Regex CsUsing = new(@"^\s*using\s+([A-Za-z0-9_.]+)\s*;", Options);
    private static readonly Regex JavaImport = new(@"^\s*import\s+(static\s+)?([A-Za-z0-9_.]+)\s*;", Options);
    private static readonly Regex TsFrom = new(@"^\s*import\s+.*?from\s+['""]([^'""]+)['""]", Options);
    private static readonly Regex TsRequire = new(@"\brequire\s*\(\s*['""]([^'""]+)['""]\s*\)", RegexOptions.CultureInvariant);
    private static readonly Regex GoSingle = new(@"^\s*import\s+""([^""]+)""", Options);
    private static readonly Regex GoBlock = new(@"^\s*import\s*\(([\s\S]*?)\)", Options);
    private static readonly Regex Quoted = new(@"""([^""]+)""", RegexOptions.CultureInvariant);
    private static readonly Regex PyImport = new(@"^\s*(?:from\s+([A-Za-z0-9_.]+)\s+import|import\s+([A-Za-z0-9_.]+))", Options);

    /// <summary>
    /// 扫描源码中的引用目标。
    /// </summary>
    /// <param name="text">源码全文。</param>
    /// <param name="extension">文件扩展名（含点，大小写不敏感）。</param>
    /// <returns>去重后的引用目标列表（保持出现顺序）；未知语言返回空。</returns>
    public static IReadOnlyList<string> Scan(string text, string extension)
    {
        var clean = SourceText.StripComments(text);
        var ext = extension.ToLowerInvariant();
        if (ext is ".cs" or ".java")
        {
            clean = SourceText.BlankStringLiterals(clean);
        }

        var result = new List<string>();
        void Add(string? value)
        {
            if (!string.IsNullOrEmpty(value) && !result.Contains(value, StringComparer.OrdinalIgnoreCase))
            {
                result.Add(value);
            }
        }

        switch (ext)
        {
            case ".cs":
                foreach (Match m in CsUsing.Matches(clean))
                {
                    Add(m.Groups[1].Value);
                }

                break;
            case ".java":
                foreach (Match m in JavaImport.Matches(clean))
                {
                    var target = m.Groups[2].Value;
                    // 静态 import 末段是成员名，归属判定按包/类，去掉末段（同类多成员也只算一次）
                    if (m.Groups[1].Success)
                    {
                        var dot = target.LastIndexOf('.');
                        if (dot > 0)
                        {
                            target = target[..dot];
                        }
                    }

                    Add(target);
                }

                break;
            case ".ts":
            case ".tsx":
                foreach (Match m in TsFrom.Matches(clean))
                {
                    Add(m.Groups[1].Value);
                }

                foreach (Match m in TsRequire.Matches(clean))
                {
                    Add(m.Groups[1].Value);
                }

                break;
            case ".go":
                foreach (Match m in GoSingle.Matches(clean))
                {
                    Add(m.Groups[1].Value);
                }

                foreach (Match m in GoBlock.Matches(clean))
                {
                    foreach (Match q in Quoted.Matches(m.Groups[1].Value))
                    {
                        Add(q.Groups[1].Value);
                    }
                }

                break;
            case ".py":
                foreach (Match m in PyImport.Matches(clean))
                {
                    Add(m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value);
                }

                break;
        }

        return result;
    }
}
