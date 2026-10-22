namespace GuardAI.Cli;

/// <summary>
/// 命令行参数解析与校验（单一来源：参数白名单集中登记）。
/// 解析 "--key value / --flag" 形式；帮助记号（-h/--help）在解析前拦截；
/// 未知参数、缺少值、多余位置参数一律报错——绝不静默执行命令（防误触）。
/// </summary>
internal static class CliArgs
{
    /// <summary>帮助参数记号（子命令级，命中即打印用法而不是执行命令）。</summary>
    private static readonly string[] HelpTokens = { "-h", "--help" };

    /// <summary>
    /// 参数白名单：命令 → 参数名 → 是否带值（true=后面要跟值，false=开关）。
    /// 新增命令/参数必须在此登记，否则会被拦截报错。
    /// </summary>
    internal static readonly IReadOnlyDictionary<string, IReadOnlyDictionary<string, bool>> KnownOptions =
        new Dictionary<string, IReadOnlyDictionary<string, bool>>(StringComparer.Ordinal)
        {
            ["check"] = Table(
                ("repo", true), ("mode", true), ("config", true), ("json", false), ("report", true),
                ("html", true), ("strict", false), ("only", true), ("update-baseline", false),
                ("no-record", false), ("trigger", true)),
            ["review"] = Table(
                ("repo", true), ("mode", true), ("base", true), ("config", true), ("json", false),
                ("report", true), ("fail-on", true)),
            ["init"] = Table(
                ("repo", true), ("templates", true), ("yes", false), ("force", false), ("ai", false),
                ("ai-provider", true), ("ai-model", true), ("ai-base-url", true), ("ai-api-key-env", true)),
            ["learn"] = Table(
                ("repo", true), ("json", false), ("report", true),
                ("ai-provider", true), ("ai-model", true), ("ai-base-url", true), ("ai-api-key-env", true)),
            ["doctor"] = Table(("repo", true), ("templates", true)),
            ["ui"] = Table(("repo", true), ("port", true), ("no-open", false)),
            ["mcp"] = Table(("repo", true)),
            ["index"] = Table(("repo", true), ("output", true)),
            ["search"] = Table(("repo", true), ("top", true)),
        };

    /// <summary>
    /// 解析子命令参数。
    /// </summary>
    /// <param name="command">子命令名（check / review / init / learn / doctor / ui / mcp / index / search）。</param>
    /// <param name="args">子命令后的原始参数。</param>
    /// <returns>解析结果：帮助请求 / 选项字典 / 位置参数 / 错误信息（四者互斥语义见各字段）。</returns>
    internal static CliParseResult Parse(string command, string[] args)
    {
        if (!KnownOptions.TryGetValue(command, out var known))
        {
            return new CliParseResult(false, new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase), null,
                $"未知命令: {command}（可用：{string.Join(" / ", KnownOptions.Keys)}）");
        }

        // 帮助记号优先于一切：--help 的语义永远是"看用法"，绝不允许被当参数吞掉后执行命令
        if (args.Any(a => HelpTokens.Contains(a, StringComparer.Ordinal)))
        {
            return new CliParseResult(true, new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase), null, null);
        }

        var options = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        string? positional = null;
        for (var i = 0; i < args.Length; i++)
        {
            var token = args[i];
            if (!token.StartsWith('-'))
            {
                // 位置参数只有 search 的关键词合法；其余命令出现即报错（防把 --typo 之类吞成参数值）
                if (command != "search")
                {
                    return Err(options, $"无法识别的参数: {token}（guardAI {command} --help 看用法）");
                }

                if (positional is not null)
                {
                    return Err(options, $"search 只接受一个关键词（含空格请用引号包住）: 又遇到「{token}」");
                }

                positional = token;
                continue;
            }

            var key = token.TrimStart('-');
            if (!known.TryGetValue(key, out var takesValue))
            {
                return Err(options, $"未知参数: {token}（guardAI {command} --help 看用法）");
            }

            if (!takesValue)
            {
                options[key] = null;
                continue;
            }

            // 带值参数：下一个记号必须是值（不得以 - 开头，否则视为缺值——把拼写错误当场揪出来）
            if (i + 1 >= args.Length || args[i + 1].StartsWith('-'))
            {
                return Err(options, $"参数 {token} 缺少值（后面要跟参数值，如 {token} <值>）");
            }

            options[key] = args[i + 1];
            i++;
        }

        return new CliParseResult(false, options, positional, null);
    }

    /// <summary>
    /// 构造参数白名单表（带值/开关登记）。
    /// </summary>
    /// <param name="entries">（参数名，是否带值）列表。</param>
    /// <returns>参数名 → 是否带值的映射。</returns>
    private static IReadOnlyDictionary<string, bool> Table(params (string Name, bool TakesValue)[] entries)
    {
        return entries.ToDictionary(e => e.Name, e => e.TakesValue, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 构造解析失败结果（已解析出的选项不回传利用，统一清空防误用）。
    /// </summary>
    /// <param name="options">已解析选项（丢弃）。</param>
    /// <param name="error">错误信息。</param>
    /// <returns>失败的解析结果。</returns>
    private static CliParseResult Err(Dictionary<string, string?> options, string error)
    {
        return new CliParseResult(false, new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase), null, error);
    }
}

/// <summary>
/// 参数解析结果。
/// </summary>
/// <param name="HelpRequested">是否命中帮助记号（-h/--help）——为 true 时只打印用法，不得执行命令。</param>
/// <param name="Options">选项字典（键=参数名，值=参数值；开关型值为 null）。</param>
/// <param name="Positional">位置参数（仅 search 的关键词使用；其余命令恒为 null）。</param>
/// <param name="Error">错误信息（非 null 表示解析失败，应打印后以退出码 2 结束）。</param>
internal sealed record CliParseResult(
    bool HelpRequested,
    Dictionary<string, string?> Options,
    string? Positional,
    string? Error);
