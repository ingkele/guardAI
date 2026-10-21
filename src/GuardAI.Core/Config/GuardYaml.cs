using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace GuardAI.Core.Config;

/// <summary>
/// 极简 YAML 解析器（零外部依赖）。
/// </summary>
/// <remarks>
/// 来源：移植自本项目自研的 guard-tool（PowerShell 版 lib/yaml.ps1），语义保持一致。
/// 用途：解析规则配置文件，把"规则资产"与"执行宿主"分离。
/// 支持语法子集：整行注释（# 开头）与行尾注释（" #" 起，引号内的 # 不支持）、
/// key: value 标量（自动识别 true/false/整数/小数，其余为字符串，支持成对引号）、
/// 嵌套 map（缩进）、"- item" 列表（标量项或内嵌 map 项）、
/// 流式集合（[] / {} 空集合，[a, b, c] 标量列表）。
/// 不支持锚点、多行字符串等高级语法。
/// </remarks>
public static class GuardYaml
{
    /// <summary>
    /// 解析 YAML 文本，返回嵌套字典/列表结构。
    /// </summary>
    /// <param name="yamlText">YAML 文本内容。</param>
    /// <returns>根级键值字典；值可能是字符串、布尔、整数、小数、字典或列表。</returns>
    /// <exception cref="FormatException">语法超出支持子集时抛出，并指出行号。</exception>
    public static IReadOnlyDictionary<string, object?> Parse(string yamlText)
    {
        return new Parser(yamlText).Parse();
    }

    /// <summary>
    /// 单次解析的内部实现：持有缩进栈，逐行构建容器树。
    /// </summary>
    private sealed class Parser
    {
        private readonly string[] _lines;
        private readonly Dictionary<string, object?> _root = new(StringComparer.Ordinal);
        private readonly Stack<(int Indent, object Container)> _stack = new();

        /// <summary>构造解析器并按行切分文本。</summary>
        /// <param name="yamlText">YAML 文本。</param>
        public Parser(string yamlText)
        {
            _lines = yamlText.Replace("\r\n", "\n").Split('\n');
            _stack.Push((-1, _root));
        }

        /// <summary>执行解析并返回根字典。</summary>
        public IReadOnlyDictionary<string, object?> Parse()
        {
            for (var i = 0; i < _lines.Length; i++)
            {
                var line = Preprocess(_lines[i]);
                if (line is null)
                {
                    continue;
                }

                var indent = line.Length - line.TrimStart(' ').Length;
                var trimmed = line.Trim();

                // 弹出缩进不小于当前行的栈帧（与 guard-tool 语义一致）
                while (_stack.Count > 1 && _stack.Peek().Indent >= indent)
                {
                    _stack.Pop();
                }

                var top = _stack.Peek();
                if (trimmed == "-" || trimmed.StartsWith("- "))
                {
                    ParseListItem(trimmed, indent, top.Container, i);
                }
                else
                {
                    ParsePair(trimmed, indent, top.Container, i);
                }
            }

            return _root;
        }

        /// <summary>
        /// 预处理单行：去空行/注释/行尾注释，返回可解析行；不可解析返回 null。
        /// </summary>
        private static string? Preprocess(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                return null;
            }

            if (raw.TrimStart().StartsWith('#'))
            {
                return null;
            }

            // 行尾注释剥离（保守策略：该行不含引号时才剥离 " #" 之后的内容）
            var line = raw;
            if (!line.Contains('\'') && !line.Contains('"'))
            {
                line = Regex.Replace(line, @"\s+#.*$", string.Empty);
            }

            return string.IsNullOrWhiteSpace(line) ? null : line;
        }

        /// <summary>
        /// 解析一个列表项（标量或内嵌 map 的首对键值）；map 项压栈以接收后续键值。
        /// </summary>
        private void ParseListItem(string trimmed, int indent, object container, int lineNo)
        {
            if (container is not List<object?> list)
            {
                throw new FormatException($"配置解析失败（第 {lineNo + 1} 行）：列表项出现在非列表容器中");
            }

            var itemText = trimmed == "-" ? string.Empty : trimmed[2..].Trim();
            if (itemText.Length == 0)
            {
                // 空列表项：后续行填充为 map
                var emptyItem = new Dictionary<string, object?>(StringComparer.Ordinal);
                list.Add(emptyItem);
                _stack.Push((indent, emptyItem));
                return;
            }

            var kv = TryMatchKey(itemText);
            if (kv is not null)
            {
                var mapItem = new Dictionary<string, object?>(StringComparer.Ordinal);
                list.Add(mapItem);
                mapItem[kv.Value.Key] = ParseScalar(kv.Value.Value);
                _stack.Push((indent, mapItem));
            }
            else
            {
                list.Add(ParseValue(itemText));
            }
        }

        /// <summary>
        /// 解析值文本：流式集合（[] / {} 空集合、[a, b] 标量列表）优先，其余走标量。
        /// </summary>
        private static object ParseValue(string value)
        {
            var v = value.Trim();
            if (v == "[]")
            {
                return new List<object?>();
            }

            if (v == "{}")
            {
                return new Dictionary<string, object?>(StringComparer.Ordinal);
            }

            if (v.Length >= 2 && v.StartsWith('[') && v.EndsWith(']'))
            {
                var list = new List<object?>();
                foreach (var item in SplitFlowItems(v[1..^1]))
                {
                    list.Add(ParseScalar(item));
                }

                return list;
            }

            return ParseScalar(v);
        }

        /// <summary>
        /// 按逗号拆分流式列表项（引号内的逗号不拆）。
        /// </summary>
        private static IEnumerable<string> SplitFlowItems(string inner)
        {
            var items = new List<string>();
            var current = new StringBuilder();
            var quote = '\0';
            foreach (var c in inner)
            {
                if (quote != '\0')
                {
                    current.Append(c);
                    if (c == quote)
                    {
                        quote = '\0';
                    }

                    continue;
                }

                if (c is '"' or '\'')
                {
                    quote = c;
                    current.Append(c);
                }
                else if (c == ',')
                {
                    items.Add(current.ToString().Trim());
                    current.Clear();
                }
                else
                {
                    current.Append(c);
                }
            }

            if (current.Length > 0)
            {
                items.Add(current.ToString().Trim());
            }

            return items.Where(i => i.Length > 0);
        }

        /// <summary>
        /// 解析一对 key: value；值为空时按后续行判定子容器类型（列表或 map）。
        /// </summary>
        private void ParsePair(string trimmed, int indent, object container, int lineNo)
        {
            if (container is not Dictionary<string, object?> map)
            {
                throw new FormatException($"配置解析失败（第 {lineNo + 1} 行）：键值对出现在非字典容器中");
            }

            var kv = TryMatchKey(trimmed)
                ?? throw new FormatException($"配置解析失败（第 {lineNo + 1} 行）：无法识别的语法 -> {trimmed}");

            if (kv.Value.Length > 0)
            {
                map[kv.Key] = ParseValue(kv.Value);
                return;
            }

            // 查看下一个有效行决定子容器类型："- " 开头为列表，否则为 map
            var childIsList = false;
            for (var j = lineNo + 1; j < _lines.Length; j++)
            {
                var peek = Preprocess(_lines[j]);
                if (peek is null)
                {
                    continue;
                }

                var peekTrimmed = peek.Trim();
                childIsList = peekTrimmed == "-" || peekTrimmed.StartsWith("- ");
                break;
            }

            object child = childIsList
                ? new List<object?>()
                : new Dictionary<string, object?>(StringComparer.Ordinal);
            map[kv.Key] = child;
            _stack.Push((indent, child));
        }

        /// <summary>
        /// 尝试把 "key: value" 文本拆分为键与值。
        /// </summary>
        /// <returns>命中返回键值对；不是键值形式返回 null。</returns>
        private static (string Key, string Value)? TryMatchKey(string text)
        {
            var m = Regex.Match(text, @"^([^:]+):\s*(.*)$");
            return m.Success ? (m.Groups[1].Value.Trim(), m.Groups[2].Value.Trim()) : null;
        }

        /// <summary>
        /// 把 YAML 标量文本转换为 .NET 值（字符串/布尔/整数/小数）。
        /// </summary>
        private static object ParseScalar(string value)
        {
            var v = value.Trim();
            if (v.Length >= 2 && v.StartsWith('"') && v.EndsWith('"'))
            {
                return v[1..^1];
            }

            if (v.Length >= 2 && v.StartsWith('\'') && v.EndsWith('\''))
            {
                return v[1..^1];
            }

            if (v == "true")
            {
                return true;
            }

            if (v == "false")
            {
                return false;
            }

            if (int.TryParse(v, out var num))
            {
                return num;
            }

            if (double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var d))
            {
                return d;
            }

            return v;
        }
    }
}
