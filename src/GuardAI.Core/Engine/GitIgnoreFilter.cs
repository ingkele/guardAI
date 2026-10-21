using System.Text;
using System.Text.RegularExpressions;

namespace GuardAI.Core.Engine;

/// <summary>
/// .gitignore 过滤器：判断文件/目录是否被 git 忽略。
/// </summary>
/// <remarks>
/// 语义对齐 gitignore 规则子集：
///   # 注释、空行、! 反取、转义（\! \#）、目录限定（尾 /）、锚定（模式含 / 时锚定根，否则任意层命中）、
///   * 单层通配、** 跨层通配、? 单字符、[...] 字符类；
///   按文件顺序生效，最后命中的模式定夺；父目录被忽略则其下文件一律忽略（git 不允许跨目录反向包含）。
/// v1 只读仓库根的 .gitignore——嵌套 .gitignore 暂不支持（漏报方向为"多查不漏查"，可接受）。
/// </remarks>
public sealed class GitIgnoreFilter
{
    /// <summary>已解析的模式（按文件顺序）。</summary>
    private readonly List<IgnorePattern> _patterns = [];

    /// <summary>
    /// 从仓库根的 .gitignore 加载过滤器（文件不存在时返回空过滤器）。
    /// </summary>
    /// <param name="repoPath">仓库根目录。</param>
    /// <returns>过滤器实例。</returns>
    public static GitIgnoreFilter Load(string repoPath)
    {
        var filter = new GitIgnoreFilter();
        var path = Path.Combine(repoPath, ".gitignore");
        if (File.Exists(path))
        {
            filter.Parse(File.ReadAllLines(path));
        }

        return filter;
    }

    /// <summary>
    /// 判断文件或目录是否被忽略。
    /// </summary>
    /// <param name="relativePath">仓库相对路径（正斜杠或反斜杠均可）。</param>
    /// <param name="isDirectory">路径本身是否为目录（目录限定模式"尾 /"只对目录生效）。</param>
    /// <returns>被忽略返回 true。</returns>
    public bool IsIgnored(string relativePath, bool isDirectory = false)
    {
        var normalized = relativePath.Replace('\\', '/').Trim('/');
        if (normalized.Length == 0)
        {
            return false;
        }

        var segments = normalized.Split('/');

        // git 语义：父目录被忽略，其下文件一律忽略（反向重新包含无效）
        for (var i = 1; i < segments.Length; i++)
        {
            if (Evaluate(string.Join('/', segments[..i]), isDir: true))
            {
                return true;
            }
        }

        return Evaluate(normalized, isDir: isDirectory);
    }

    /// <summary>
    /// 解析 gitignore 行（测试可直接调用）。
    /// </summary>
    /// <param name="lines">gitignore 文本行。</param>
    public void Parse(IEnumerable<string> lines)
    {
        foreach (var raw in lines)
        {
            var line = raw;
            if (string.IsNullOrWhiteSpace(line) || line.StartsWith('#'))
            {
                continue;
            }

            line = TrimTrailingSpaces(line);
            if (line.Length == 0)
            {
                continue;
            }

            var negated = false;
            if (line.StartsWith(@"\!") || line.StartsWith(@"\#"))
            {
                line = line[1..];
            }
            else if (line.StartsWith('!'))
            {
                negated = true;
                line = line[1..];
            }

            var dirOnly = line.EndsWith('/');
            if (dirOnly)
            {
                line = line[..^1];
            }

            if (line.Length == 0)
            {
                continue;
            }

            // 模式含 /（除尾部）即锚定到 .gitignore 所在目录；开头的单个 / 只是锚定记号
            var anchored = line.Contains('/');
            line = line.TrimStart('/');

            _patterns.Add(new IgnorePattern(BuildRegex(line, anchored), negated, dirOnly));
        }
    }

    /// <summary>
    /// 按"最后命中的模式定夺"评估路径。
    /// </summary>
    private bool Evaluate(string path, bool isDir)
    {
        var ignored = false;
        foreach (var pattern in _patterns)
        {
            if (pattern.DirOnly && !isDir)
            {
                continue;
            }

            if (pattern.Regex.IsMatch(path))
            {
                ignored = !pattern.Negated;
            }
        }

        return ignored;
    }

    /// <summary>
    /// 把 gitignore 模式编译为正则（锚定首尾，忽略大小写以适配 Windows 文件系统）。
    /// </summary>
    private static Regex BuildRegex(string body, bool anchored)
    {
        var sb = new StringBuilder("^");
        if (!anchored)
        {
            sb.Append("(?:[^/]+/)*");
        }

        for (var i = 0; i < body.Length; i++)
        {
            var c = body[i];
            if (c == '*')
            {
                if (i + 1 < body.Length && body[i + 1] == '*')
                {
                    i++;
                    if (i + 1 < body.Length && body[i + 1] == '/')
                    {
                        i++;
                        sb.Append("(?:.*/)?"); // **/ 跨层目录前缀
                    }
                    else
                    {
                        sb.Append(".*"); // ** 跨层任意
                    }
                }
                else
                {
                    sb.Append("[^/]*"); // * 单层任意
                }
            }
            else if (c == '?')
            {
                sb.Append("[^/]");
            }
            else if (c == '[')
            {
                i = AppendCharClass(body, i, sb);
            }
            else
            {
                sb.Append(Regex.Escape(c.ToString()));
            }
        }

        sb.Append('$');
        return new Regex(sb.ToString(), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
    }

    /// <summary>
    /// 追加字符类 [...]（起始位置为 '['），返回 ']' 的位置。
    /// </summary>
    private static int AppendCharClass(string body, int start, StringBuilder sb)
    {
        var end = body.IndexOf(']', start + 1);
        if (end < 0)
        {
            sb.Append(Regex.Escape("[")); // 未闭合按字面处理
            return start;
        }

        var content = body[(start + 1)..end];
        if (content.StartsWith('!'))
        {
            content = "^" + content[1..];
        }

        sb.Append('[').Append(content).Append(']');
        return end;
    }

    /// <summary>
    /// 去掉行尾空白（gitignore 对行尾空格的处理；转义空格场景从简）。
    /// </summary>
    private static string TrimTrailingSpaces(string line)
    {
        return line.TrimEnd(' ', '\t');
    }

    /// <summary>单条 gitignore 模式。</summary>
    /// <param name="Regex">编译后的正则。</param>
    /// <param name="Negated">是否反取（! 前缀）。</param>
    /// <param name="DirOnly">是否仅目录（尾 /）。</param>
    private sealed record IgnorePattern(Regex Regex, bool Negated, bool DirOnly);
}
