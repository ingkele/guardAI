using System.Text.Json;
using GuardAI.Core.Config;
using GuardAI.Core.Engine;

namespace GuardAI.Cli;

/// <summary>
/// 前置门决策结果（路径级 allow/deny + 注入给模型的层契约上下文）。
/// </summary>
/// <param name="Deny">是否阻止本次写入。</param>
/// <param name="Reason">决策理由（回显给 hook 与开发者）。</param>
/// <param name="Context">注入模型的事实上下文。</param>
public sealed record GateDecision(bool Deny, string Reason, string Context);

/// <summary>
/// 写码前前置门：把 AI 客户端 hook 的 JSON 翻译成 hook 决策 JSON（第十命令 gate 的内核）。
/// </summary>
/// <remarks>
/// 能力边界（查证 Claude Code 官方 hooks 文档后确定）：PreToolUse 的 tool_input 明确带 file_path，
/// 所以能拦「往哪写」；content 字段不在明确协议内，所以「写了什么」留给 PostToolUse 的 layer 检查当轮拦。
/// 失败一律 fail-open（allow + 提示人工确认），避免工具不可用时卡死开发；写后门禁仍会兜底。
/// </remarks>
public static class GateCommand
{
    /// <summary>
    /// 根据 hook 输入与规则配置做前置决策（纯函数，便于测试）。
    /// </summary>
    /// <param name="stdinJson">hook  stdin 原始 JSON。</param>
    /// <param name="config">规则配置。</param>
    /// <param name="cwdFallback">stdin 未给 cwd 时的回退工作目录。</param>
    /// <returns>前置门决策。</returns>
    public static GateDecision Decide(string? stdinJson, StructureRuleConfig config, string? cwdFallback = null)
    {
        var (filePath, cwd, parsed) = ParseHookInput(stdinJson);
        if (!parsed)
        {
            return new GateDecision(false, "前置门未运行：无法解析 hook 输入",
                "【层契约】前置门未运行，请人工确认写入位置与依赖方向；写后 layer 检查会兜底。");
        }

        if (string.IsNullOrWhiteSpace(filePath))
        {
            return new GateDecision(false, "前置门未运行：hook 输入缺少 file_path",
                "【层契约】前置门未运行（未识别目标路径），请人工确认写入位置；写后 layer 检查会兜底。");
        }

        var repo = string.IsNullOrWhiteSpace(cwd) ? cwdFallback : cwd;
        var rel = NormalizeRelative(filePath, repo);
        if (rel.StartsWith("../", StringComparison.Ordinal))
        {
            return new GateDecision(false, "写入目标不在仓库目录内",
                "【层契约】目标文件在当前仓库之外，层契约不适用；确认是否写错位置。");
        }

        // 豁免优先：显式登记例外的文件不拦，也不注入误导性契约
        var exclusion = config.Exclusions.FirstOrDefault(e => GlobMatcher.IsMatch(rel, e.Path));
        if (exclusion is not null)
        {
            return new GateDecision(false, $"路径已豁免：{exclusion.Reason}",
                $"【层契约】该文件已登记豁免（{exclusion.Reason}）；写后检查同样跳过。");
        }

        var segments = rel.Split('/');
        var dirs = segments[..^1];
        var fileName = segments[^1];

        // 路径级硬拦：同义目录黑名单（与 StructureCheck 同一口径：目录段整段相等）
        foreach (var ban in config.BannedDirs)
        {
            if (dirs.Any(segment => segment.Equals(ban, StringComparison.OrdinalIgnoreCase)))
            {
                return new GateDecision(true, $"禁止写入禁用目录 '{ban}'（会破坏项目骨架）",
                    $"【层契约】路径含禁用目录 {ban}/；请把文件放进骨架既定目录，不要自建同义目录。");
            }
        }

        // 路径级硬拦：落位规则（只判 all 与当前 project_type 生效的规则；多项目 override 由写后检查兜底）
        var dirPart = string.Join('/', dirs);
        foreach (var rule in config.PlacementRules)
        {
            if (rule.Scope != "all" && rule.Scope != config.ProjectType)
            {
                continue;
            }

            if (!GlobMatcher.IsMatch(fileName, rule.FilePattern))
            {
                continue;
            }

            if (!GlobMatcher.IsMatch(dirPart + "/", rule.DirPattern))
            {
                return new GateDecision(true, $"落位规则：{rule.Name}",
                    $"【层契约】{rule.Suggestion}。写后检查会复核。");
            }
        }

        return new GateDecision(false, "允许写入", BuildContext(rel, config));
    }

    /// <summary>
    /// 把决策渲染成 Claude Code / Codex 的 hook 输出 JSON。
    /// </summary>
    /// <param name="decision">前置门决策。</param>
    /// <returns>hookSpecificOutput 形状的 JSON 文本。</returns>
    public static string Render(GateDecision decision)
    {
        return JsonSerializer.Serialize(new
        {
            hookSpecificOutput = new
            {
                hookEventName = "PreToolUse",
                permissionDecision = decision.Deny ? "deny" : "allow",
                permissionDecisionReason = decision.Reason,
                additionalContext = decision.Context,
            },
        });
    }

    /// <summary>
    /// 从 stdin 读取 hook 输入、从 cwd 加载配置、输出决策 JSON（CLI 入口编排，无状态）。
    /// </summary>
    /// <returns>恒为 0：deny 通过 JSON 协议表达，不是进程退出码。</returns>
    public static int Run()
    {
        var stdin = Console.In.ReadToEnd();
        var cwd = ReadCwd(stdin) ?? Directory.GetCurrentDirectory();
        StructureRuleConfig config;
        try
        {
            var configPath = Path.Combine(cwd, "guardai.yml");
            config = File.Exists(configPath)
                ? StructureRuleConfigLoader.Load(configPath)
                : new StructureRuleConfig();
        }
        catch (Exception ex) when (ex is FormatException or IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"警告：层契约配置加载失败，前置门仅注入提示（{ex.Message}）");
            config = new StructureRuleConfig();
        }

        var decision = Decide(stdin, config, cwd);
        Console.WriteLine(Render(decision));
        return 0;
    }

    /// <summary>
    /// 构造注入给模型的层契约上下文（纯事实：所属层 + 允许依赖 + 明确禁项）。
    /// </summary>
    private static string BuildContext(string rel, StructureRuleConfig config)
    {
        var layer = LayerResolver.Resolve(rel, config.Layers);
        if (layer is null)
        {
            return "【层契约】该文件未命中已声明的层；写后 layer 检查会校验骨架规则。";
        }

        var allowed = config.AllowedDependencies
            .Where(d => d.From.Equals(layer.Name, StringComparison.OrdinalIgnoreCase))
            .SelectMany(d => d.To)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var allowedText = allowed.Count == 0 ? "无（本层不依赖其他层）" : string.Join(" / ", allowed);
        var entityHint = config.ForbidDtoEntity && layer.Name is "Web" or "Application"
            ? "出参禁止直接暴露持久化实体；"
            : string.Empty;
        return $"【层契约】目标属于 {layer.Name} 层（{layer.Purpose}）。允许依赖：{allowedText}。{entityHint}写后 layer 检查会校验实际引用（内容级依赖无法在写入前判定）。";
    }

    /// <summary>
    /// 解析 hook stdin：取 tool_input.file_path / cwd，解析失败返回 parsed=false。
    /// </summary>
    private static (string? FilePath, string? Cwd, bool Parsed) ParseHookInput(string? stdinJson)
    {
        if (string.IsNullOrWhiteSpace(stdinJson))
        {
            return (null, null, false);
        }

        try
        {
            using var doc = JsonDocument.Parse(stdinJson);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return (null, null, false);
            }

            string? filePath = null;
            if (root.TryGetProperty("tool_input", out var input) && input.ValueKind == JsonValueKind.Object)
            {
                filePath = ReadString(input, "file_path");
            }

            return (filePath, ReadString(root, "cwd"), true);
        }
        catch (JsonException)
        {
            return (null, null, false);
        }
    }

    /// <summary>从 hook stdin 里读取 cwd（解析失败返回 null，由调用方回退当前目录）。</summary>
    private static string? ReadCwd(string? stdinJson)
    {
        if (string.IsNullOrWhiteSpace(stdinJson))
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(stdinJson);
            return doc.RootElement.ValueKind == JsonValueKind.Object
                ? ReadString(doc.RootElement, "cwd")
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>从 JSON 对象读字符串属性（非字符串返回 null）。</summary>
    private static string? ReadString(JsonElement obj, string name)
    {
        return obj.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
    }

    /// <summary>把 hook 给的路径换算成仓库相对路径（正斜杠）。</summary>
    private static string NormalizeRelative(string filePath, string? repo)
    {
        if (!Path.IsPathRooted(filePath))
        {
            return filePath.Replace('\\', '/').TrimStart('/');
        }

        if (string.IsNullOrWhiteSpace(repo))
        {
            return filePath.Replace('\\', '/').TrimStart('/');
        }

        try
        {
            return Path.GetRelativePath(Path.GetFullPath(repo), Path.GetFullPath(filePath)).Replace('\\', '/');
        }
        catch (ArgumentException)
        {
            return filePath.Replace('\\', '/').TrimStart('/');
        }
    }
}
