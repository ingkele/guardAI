using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace GuardAI.Checks.Assets;

/// <summary>
/// 成员解析共用文本工具（P4 多语言）：归一化、哈希、括号配平、字面量清洗。
/// </summary>
/// <remarks>
/// 单一来源（案例 C46 教训）：各语言适配器（CLike/Go/Python）共用本类的文本处理，
/// 适配器只保留各自的语法识别差异，不再复制清洗/配平骨架。
/// 字符串清洗覆盖 C 系与 Go 的字面量形态（普通/逐字/原始/反引号），Python 的 # 注释由 PythonMemberParser 自行处理。
/// </remarks>
internal static class ParseText
{
    /// <summary>
    /// 空白归一化：连续空白压成单空格并去首尾。
    /// </summary>
    /// <param name="text">原文本。</param>
    /// <returns>归一化文本。</returns>
    public static string Normalize(string text)
    {
        return Regex.Replace(text, @"\s+", " ").Trim();
    }

    /// <summary>
    /// 计算方法体归一化文本的 SHA-256 前 16 位十六进制（精确重复判定用）。
    /// </summary>
    /// <param name="normalizedBody">归一化方法体。</param>
    /// <returns>十六进制哈希。</returns>
    public static string HashBody(string normalizedBody)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(normalizedBody));
        return Convert.ToHexString(bytes.AsSpan(0, 8)).ToLowerInvariant();
    }

    /// <summary>
    /// 计算文本的花括号配平差值（'{' 与 '}'）。
    /// </summary>
    /// <param name="line">单行或片段文本。</param>
    /// <returns>净增花括号数。</returns>
    public static int NetBraces(string line) => CountPair(line, '{', '}');

    /// <summary>
    /// 计算括号配平（'(' 与 ')'）。
    /// </summary>
    /// <param name="text">文本片段。</param>
    /// <returns>净增括号数。</returns>
    public static int ParenBalance(string text) => CountPair(text, '(', ')');

    /// <summary>
    /// 计算开/闭字符配平差值（NetBraces / ParenBalance 共用内核，单一来源）。
    /// </summary>
    /// <param name="text">文本片段。</param>
    /// <param name="open">开字符。</param>
    /// <param name="close">闭字符。</param>
    /// <returns>净增数。</returns>
    private static int CountPair(string text, char open, char close)
    {
        var balance = 0;
        foreach (var c in text)
        {
            if (c == open)
            {
                balance++;
            }
            else if (c == close)
            {
                balance--;
            }
        }

        return balance;
    }

    /// <summary>
    /// 找到与指定左括号配对的右括号下标。
    /// </summary>
    /// <param name="text">文本片段。</param>
    /// <param name="open">左括号下标。</param>
    /// <returns>配对右括号下标；未配平取文本末尾。</returns>
    public static int MatchingParen(string text, int open)
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
    /// 逐行累加统计花括号配平，发现闭合即停（用于块方法体收集）。
    /// </summary>
    /// <param name="text">新追加的文本片段。</param>
    /// <param name="balance">进入前的花括号净余额。</param>
    /// <param name="closed">是否已闭合（ref 输出）。</param>
    /// <returns>追加后的净余额。</returns>
    public static int CountUntilClose(string text, int balance, ref bool closed)
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
    /// 从块方法体文本（含最外层花括号）按花括号配对提取内部文本。
    /// </summary>
    /// <param name="full">含最外层花括号的方法体文本。</param>
    /// <returns>内部文本；无花括号返回空串。</returns>
    public static string ExtractInnerBody(string full)
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
    /// 追加字符串长度标记："N"（N 为原文内容长度）；空串保持 ""（与既有语义一致，不带长度 token）。
    /// </summary>
    /// <param name="sb">清洗输出缓冲。</param>
    /// <param name="length">被抹除的字面量内容长度。</param>
    public static void AppendLengthMarker(StringBuilder sb, int length)
    {
        if (length <= 0)
        {
            sb.Append("\"\"");
        }
        else
        {
            sb.Append('"').Append(length).Append('"');
        }
    }

    /// <summary>
    /// 清洗源码：注释与字符串字面量内容抹成空格（保留换行，行号不漂移）。
    /// </summary>
    /// <remarks>
    /// 覆盖：行注释、块注释、普通字符串（含转义）、逐字字符串 @"..."、原始字符串 """..."""、
    /// 反引号字符串（Go 原始串 / JS 模板串 / Kotlin 反引号标识符）、字符字面量。
    /// 插值字符串的插值段按字符串内容一并抹掉——对抽取签名/方法体只漏不误。
    /// 字符串内容抹除后以 "N" 长度标记占位（N=原文内容长度），供近似检测区分不同内容的字面量。
    /// </remarks>
    /// <param name="source">源码全文。</param>
    /// <returns>清洗后的行数组（与原文行号对齐）。</returns>
    public static string[] Clean(string source)
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

            // 原始字符串 """..."""（内容抹空格，长度留痕供近似检测区分不同文本）
            if (c == '"' && i + 2 < source.Length && source[i + 1] == '"' && source[i + 2] == '"')
            {
                i += 3;
                var length = 0;
                while (i < source.Length)
                {
                    if (source[i] == '"' && i + 2 < source.Length && source[i + 1] == '"' && source[i + 2] == '"')
                    {
                        i += 3;
                        break;
                    }

                    sb.Append(source[i] == '\n' ? '\n' : ' ');
                    i++;
                    length++;
                }

                AppendLengthMarker(sb, length);
                continue;
            }

            // 逐字字符串 @"..."
            if (c == '@' && i + 1 < source.Length && source[i + 1] == '"')
            {
                i += 2;
                var length = 0;
                while (i < source.Length)
                {
                    if (source[i] == '"')
                    {
                        if (i + 1 < source.Length && source[i + 1] == '"')
                        {
                            sb.Append("  ");
                            i += 2;
                            length += 2;
                            continue;
                        }

                        i++;
                        break;
                    }

                    sb.Append(source[i] == '\n' ? '\n' : ' ');
                    i++;
                    length++;
                }

                AppendLengthMarker(sb, length);
                continue;
            }

            // 反引号字符串 `...`（Go 原始串无转义 / JS 模板串插值段一并抹掉 / Kotlin 反引号标识符）
            if (c == '`')
            {
                i++;
                var length = 0;
                while (i < source.Length && source[i] != '`')
                {
                    sb.Append(source[i] == '\n' ? '\n' : ' ');
                    i++;
                    length++;
                }

                if (i < source.Length)
                {
                    i++;
                }

                AppendLengthMarker(sb, length);
                continue;
            }

            // 普通字符串（含 $"" 插值串，插值段一并抹掉）
            if (c == '"')
            {
                i++;
                var length = 0;
                while (i < source.Length && source[i] != '"' && source[i] != '\n')
                {
                    if (source[i] == '\\' && i + 1 < source.Length)
                    {
                        i++;
                        length++;
                    }

                    i++;
                    length++;
                }

                if (i < source.Length && source[i] == '"')
                {
                    i++;
                }

                AppendLengthMarker(sb, length);

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
    /// 抽取成员声明上方的注释摘要（能力清单中文检索的原料）。
    /// </summary>
    /// <remarks>
    /// 从声明行向上收集紧邻的文档注释行（特性/注解/装饰器行允许穿行），其余行即止——只漏不误。
    /// 取 summary 标签内容优先，无 summary 用整段注释文本；剥离 XML 标签并归一化空白。
    /// </remarks>
    /// <param name="rawLines">原始源码行（清洗前行号与清洗后对齐）。</param>
    /// <param name="start">成员声明起始行下标（0 起）。</param>
    /// <param name="docLinePrefix">文档注释行前缀（C# 为 ///，Java/Kotlin/TS/JS/Go 为 //）。</param>
    /// <returns>摘要文本；无文档注释返回空串。</returns>
    public static string ExtractCommentSummary(string[] rawLines, int start, string docLinePrefix)
    {
        var docs = new List<string>();
        var j = start - 1;
        while (j >= 0)
        {
            var t = rawLines[j].Trim();
            if (t.StartsWith(docLinePrefix, StringComparison.Ordinal))
            {
                docs.Insert(0, t[docLinePrefix.Length..]);
                j--;
            }
            else if (t.StartsWith('[') || t.StartsWith('@'))
            {
                j--; // 特性/注解/装饰器行穿行继续找注释
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
}
