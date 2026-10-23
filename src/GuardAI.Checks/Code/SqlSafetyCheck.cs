using System.Text.RegularExpressions;
using GuardAI.Core.Engine;

namespace GuardAI.Checks.Code;

/// <summary>
/// sql 检查模块：SQL 拼接确定性检查——用户可控值拼进 SQL 文本当场报（注入防线前置）。
/// </summary>
/// <remarks>
/// 来源：learn 回放 91 发现（2026-10-23 人工筛 P1）——SQL 注入是安全类别唯一 error 爆发点
/// （参数拼接 / LIKE 注入 / ORDER 大小写类排序注入 三个位置），模式经典确定。
/// 口径（只漏不误 + 零误报）：
/// ①只查 .cs；注释剥离、字符串字面量保留（SQL 就住在字符串里）；
/// ②SQL 语句形状识别用大写关键字组合（SELECT…FROM / INSERT INTO / UPDATE…SET / DELETE FROM /
///   WHERE 谓词 / ORDER BY / GROUP BY），单个单词不算——避免日志文案误伤；
/// ③三种构造报违规：$"" 插值字符串含洞、字符串与标识符/调用拼接（纯字面量拼接不算用户输入）、string.Format；
/// ④FromSqlInterpolated 行豁免（EF 参数化插值，洞走参数不拼文本）；测试代码豁免见基类。
/// </remarks>
public sealed class SqlSafetyCheck : CodeScanCheckBase
{
    /// <summary>SQL 语句形状（大写关键字组合，单个单词不算）。</summary>
    private static readonly Regex SqlShapePattern = new(
        @"\bSELECT\b.+\bFROM\b"
        + @"|\bINSERT\s+INTO\b"
        + @"|\bUPDATE\s+[\w.]+\s+SET\b"
        + @"|\bDELETE\s+FROM\b"
        + @"|\bWHERE\s+\S+\s*(?:=|<>|!=|LIKE\b|IN\b|IS\b)"
        + @"|\bORDER\s+BY\b"
        + @"|\bGROUP\s+BY\b",
        RegexOptions.Compiled);

    /// <summary>字符串块识别（可带 $ 插值前缀；转义字符不截串）。</summary>
    private static readonly Regex ChunkPattern = new(
        "\\$?\"(?:\\\\.|[^\"\\\\])*\"",
        RegexOptions.Compiled);

    /// <summary>EF 参数化插值豁免（洞走参数，不拼 SQL 文本）。</summary>
    private const string ParameterizedInterpolation = "FromSqlInterpolated";

    /// <summary>字符串格式化 API（占位符把值拼进文本，同属拼接）。</summary>
    private const string FormatApi = "string.Format";

    /// <inheritdoc />
    public override string Name => "sql";

    /// <inheritdoc />
    protected override void ScanFile(string rel, string text, List<Violation> violations)
    {
        var lines = StripComments(text).Replace("\r\n", "\n").Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            if (TryFindViolationLine(lines[i]))
            {
                violations.Add(new Violation(
                    rel,
                    "SQL 拼接",
                    $"疑似把用户可控值拼进 SQL 文本（第 {i + 1} 行；插值/拼接/格式化构造，值已隐去）",
                    "改用参数化查询（DbParameter / ORM 参数化 API）：值走参数不进语句文本；动态排序字段走白名单映射；模糊查询先转义通配符再拼"));
            }
        }
    }

    /// <summary>
    /// 判断单行是否存在 SQL 拼接构造（三种形态任一命中即真）。
    /// </summary>
    /// <param name="line">已剥离注释的单行文本。</param>
    /// <returns>是否存在违规构造。</returns>
    private static bool TryFindViolationLine(string line)
    {
        var skipInterpolation = line.Contains(ParameterizedInterpolation, StringComparison.Ordinal);
        var hasFormat = line.Contains(FormatApi, StringComparison.Ordinal);

        foreach (Match chunk in ChunkPattern.Matches(line))
        {
            if (!SqlShapePattern.IsMatch(chunk.Value))
            {
                continue;
            }

            // 形态一：$"" 插值字符串带洞（EF 参数化插值豁免）
            if (chunk.Value.StartsWith("$\"", StringComparison.Ordinal) && chunk.Value.Contains('{'))
            {
                if (!skipInterpolation)
                {
                    return true;
                }

                continue;
            }

            // 形态二：字符串与标识符/调用拼接（块前或块后紧邻 +，另一侧是代码不是字面量）
            if (IsConcatenated(line, chunk.Index, chunk.Index + chunk.Length))
            {
                return true;
            }

            // 形态三：string.Format 把值拼进语句文本
            if (hasFormat)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// 判断字符串块是否与标识符/调用拼接（纯字面量 + 字面量不算用户输入，不判）。
    /// </summary>
    /// <param name="line">所在行。</param>
    /// <param name="start">块起始位置（含）。</param>
    /// <param name="end">块结束位置（不含）。</param>
    /// <returns>是否与代码侧拼接。</returns>
    private static bool IsConcatenated(string line, int start, int end)
    {
        // 块前： 代码 + "块"
        var j = start - 1;
        while (j >= 0 && char.IsWhiteSpace(line[j]))
        {
            j--;
        }

        if (j >= 0 && line[j] == '+')
        {
            var k = j - 1;
            while (k >= 0 && char.IsWhiteSpace(line[k]))
            {
                k--;
            }

            if (k >= 0 && (char.IsLetterOrDigit(line[k]) || line[k] is '_' or ')' or ']'))
            {
                return true;
            }
        }

        // 块后： "块" + 代码
        j = end;
        while (j < line.Length && char.IsWhiteSpace(line[j]))
        {
            j++;
        }

        if (j < line.Length && line[j] == '+')
        {
            var k = j + 1;
            while (k < line.Length && char.IsWhiteSpace(line[k]))
            {
                k++;
            }

            if (k < line.Length && (char.IsLetterOrDigit(line[k]) || line[k] is '_' or '('))
            {
                return true;
            }
        }

        return false;
    }
}
