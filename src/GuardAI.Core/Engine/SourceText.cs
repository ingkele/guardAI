using System.Text;

namespace GuardAI.Core.Engine;

/// <summary>
/// 源码文本公共处理工具：注释剥离与字符串字面量屏蔽，供代码级检查与层契约检查共用。
/// </summary>
/// <remarks>
/// 上提自 <c>CodeScanCheckBase.StripComments</c>（2026-11-16）：行为保持字节级一致，
/// 由 DocCheckTests 的既有用例锁定；两个用途口径不同——SQL/文档特征要看见字符串内容，
/// 而 import 判定不能把字符串里的文本当指令，故两个方法分开而非合并。
/// </remarks>
public static class SourceText
{
    /// <summary>
    /// 剥离行注释与块注释、保留字符串字面量（SQL/数据访问特征要看见字符串里的内容）。
    /// </summary>
    /// <param name="text">源码全文。</param>
    /// <returns>剥离注释后的文本（行结构保持）。</returns>
    public static string StripComments(string text)
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

    /// <summary>
    /// 把字符串字面量的内容替换成等长空格（保留换行与行结构），用于 import/using 词法扫描。
    /// </summary>
    /// <remarks>
    /// 与 <see cref="StripComments"/> 串联使用：先剥注释，再屏蔽字符串，避免 C# verbatim 字符串
    /// 里独占一行的 "using Xxx;" 被误判成 using 指令。支持普通串的 \ 转义与 verbatim 串的 "" 转义。
    /// 只用于「指令不在字符串里」的语言（.cs / .java）；TS/Go/Python 的 import 目标本身写在字符串里，不调用本方法。
    /// </remarks>
    /// <param name="text">已剥注释的源码文本。</param>
    /// <returns>字符串内容被空格替换后的文本。</returns>
    public static string BlankStringLiterals(string text)
    {
        var sb = new StringBuilder(text.Length);
        var i = 0;
        while (i < text.Length)
        {
            var c = text[i];
            if (c != '"')
            {
                sb.Append(c);
                i++;
                continue;
            }

            // 回看 $@ / @$ / @ / $ 前缀：verbatim 用 "" 转义，普通串用 \ 转义
            var verbatim = false;
            var prefixStart = i - 1;
            while (prefixStart >= 0 && (text[prefixStart] == '@' || text[prefixStart] == '$'))
            {
                if (text[prefixStart] == '@')
                {
                    verbatim = true;
                }

                prefixStart--;
            }

            for (var p = prefixStart + 1; p < i; p++)
            {
                sb.Append(' '); // 抹掉前缀字符，保持列宽
            }

            sb.Append(' '); // 开头引号
            i++;
            while (i < text.Length)
            {
                var sc = text[i];
                if (sc == '\n')
                {
                    sb.Append('\n');
                    i++;
                    continue;
                }

                if (sc == '\\' && !verbatim && i + 1 < text.Length)
                {
                    sb.Append(' ');
                    sb.Append(text[i + 1] == '\n' ? '\n' : ' ');
                    i += 2;
                    continue;
                }

                if (sc == '"')
                {
                    if (verbatim && i + 1 < text.Length && text[i + 1] == '"')
                    {
                        sb.Append(' ');
                        sb.Append(' ');
                        i += 2;
                        continue;
                    }

                    sb.Append(' ');
                    i++;
                    break;
                }

                sb.Append(' ');
                i++;
            }
        }

        return sb.ToString();
    }
}
