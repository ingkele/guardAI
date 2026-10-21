using System.Text;
using System.Text.Json.Nodes;

namespace GuardAI.Init;

/// <summary>
/// 闸口安装策略。
/// </summary>
public enum InstallStrategy
{
    /// <summary>目标不存在则创建；已存在则跳过（--force 覆盖）。</summary>
    Create,

    /// <summary>目标已含标记块则跳过，否则追加内容。</summary>
    AppendIfMissing,

    /// <summary>合并 JSON（保留用户已有配置，只增不改）。</summary>
    MergeJson,

    /// <summary>安装 git 钩子（缺失创建、已有追加）。</summary>
    InstallHook,
}

/// <summary>
/// 一项闸口安装动作。
/// </summary>
/// <param name="TargetRelativePath">目标文件（仓库相对路径）。</param>
/// <param name="Description">动作说明（展示给用户核对）。</param>
/// <param name="TemplateRelativePath">模板相对路径（templates 目录下）。</param>
/// <param name="Strategy">安装策略。</param>
/// <param name="Marker">去重标记（AppendIfMissing 使用）。</param>
public sealed record GateInstallAction(
    string TargetRelativePath,
    string Description,
    string TemplateRelativePath,
    InstallStrategy Strategy,
    string Marker);

/// <summary>
/// 一项闸口安装结果。
/// </summary>
/// <param name="TargetRelativePath">目标文件。</param>
/// <param name="Written">是否实际写入。</param>
/// <param name="Detail">结果说明。</param>
public sealed record GateInstallResult(string TargetRelativePath, bool Written, string Detail);

/// <summary>
/// 闸口模板安装器：把 CI / pre-commit / AI hook / AGENTS 片段装配进目标仓库。
/// </summary>
/// <remarks>每一步都可人工核对（先计划、后落盘）——"AI 装配"只出配置，绝不碰检查判定。</remarks>
public sealed class GateInstaller
{
    private readonly string _templatesDir;

    /// <summary>构造安装器。</summary>
    /// <param name="templatesDir">模板目录（含 ci/ hooks/ agents/ 子目录）。</param>
    /// <exception cref="DirectoryNotFoundException">模板目录不存在时抛出。</exception>
    public GateInstaller(string templatesDir)
    {
        if (!Directory.Exists(templatesDir))
        {
            throw new DirectoryNotFoundException($"找不到模板目录: {templatesDir}");
        }

        _templatesDir = templatesDir;
    }

    /// <summary>
    /// 定位模板目录：显式指定 → 程序输出目录 → 逐级向上查找（仅磁盘；内嵌回退见 <see cref="TemplateProvider.Resolve"/>）。
    /// </summary>
    /// <param name="explicitDir">显式指定的模板目录（可空）。</param>
    /// <returns>模板目录绝对路径；找不到返回 null。</returns>
    public static string? FindTemplatesDir(string? explicitDir)
    {
        return TemplateProvider.FindOnDisk(explicitDir);
    }

    /// <summary>
    /// 生成安装计划（只读，不落盘），供用户核对。
    /// </summary>
    /// <remarks>
    /// AI 接入部分走 <see cref="AiAgentCatalog.Agents"/> 注册表：探测到哪家装哪家（--force 全装），
    /// 再补 AGENTS.md 通用兜底与 .guardai-snippets/ 云端粘贴片段——新增智能体只加档案，不改本方法。
    /// </remarks>
    /// <param name="scan">项目扫描结果。</param>
    /// <param name="force">是否强制覆盖已存在文件 / 全量安装 AI 钩子。</param>
    /// <returns>动作列表。</returns>
    public IReadOnlyList<GateInstallAction> Plan(ProjectScanResult scan, bool force)
    {
        var actions = new List<GateInstallAction>
        {
            new(".github/workflows/guardai.yml", "CI 门禁（GitHub Actions）", "ci/github-actions.yml", InstallStrategy.Create, "guardAI"),
            new(".workflow/guardai-gate-step.yml", "CI 门禁片段（Gitee Go，合并进现有构建 stage）", "ci/gitee-go-step.yml", InstallStrategy.Create, "guardAI"),
        };

        if (Directory.Exists(Path.Combine(scan.RepoPath, ".git")))
        {
            actions.Add(new GateInstallAction(
                ".git/hooks/pre-commit",
                "git 提交闸口（pre-commit）",
                "hooks/pre-commit",
                InstallStrategy.InstallHook,
                "guardAI"));
        }

        // AI 智能体接入：按注册表探测装配（探测到在用才装，--force 时全装）
        foreach (var agent in AiAgentCatalog.Agents)
        {
            if (force || agent.DetectPaths.Any(p => SignalExists(scan.RepoPath, p)))
            {
                actions.Add(agent.ToAction());
            }
        }

        // 通用兜底：一切读项目指令文件的智能体（mimo claw、OpenClaw、Cursor 等）通吃同一份指令
        actions.Add(new GateInstallAction(
            "AGENTS.md",
            "AI 规范注入片段（通用兜底：mimo claw、OpenClaw、Cursor 等一切读项目指令文件的智能体）",
            AiAgentCatalog.DirectiveTemplate,
            InstallStrategy.AppendIfMissing,
            AiAgentCatalog.DirectiveMarker));

        // 云端粘贴片段：无仓库级配置的智能体（字节办公/飞书智能伙伴等）手动粘到其系统提示
        actions.Add(new GateInstallAction(
            ".guardai-snippets/guardai-directive.md",
            "云端粘贴片段（字节办公、飞书智能伙伴等无仓库级配置的智能体，粘到其系统提示即可生效）",
            AiAgentCatalog.DirectiveTemplate,
            InstallStrategy.Create,
            "guardAI"));

        return actions;
    }

    /// <summary>
    /// 判断探测信号是否存在（目录或文件任一命中即视为该智能体在用）。
    /// </summary>
    /// <param name="repoPath">目标仓库根目录。</param>
    /// <param name="relativePath">仓库相对路径。</param>
    /// <returns>存在返回 true。</returns>
    private static bool SignalExists(string repoPath, string relativePath)
    {
        var full = Path.Combine(repoPath, relativePath.Replace('/', Path.DirectorySeparatorChar));
        return Directory.Exists(full) || File.Exists(full);
    }

    /// <summary>
    /// 执行安装计划，返回每项结果。
    /// </summary>
    /// <param name="actions">安装动作。</param>
    /// <param name="repoPath">目标仓库根目录。</param>
    /// <param name="force">是否覆盖已存在文件。</param>
    /// <returns>安装结果列表。</returns>
    public IReadOnlyList<GateInstallResult> Apply(IEnumerable<GateInstallAction> actions, string repoPath, bool force)
    {
        var results = new List<GateInstallResult>();
        foreach (var action in actions)
        {
            var templatePath = Path.Combine(_templatesDir, action.TemplateRelativePath.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(templatePath))
            {
                results.Add(new GateInstallResult(action.TargetRelativePath, false, $"模板缺失: {action.TemplateRelativePath}"));
                continue;
            }

            var target = Path.Combine(repoPath, action.TargetRelativePath.Replace('/', Path.DirectorySeparatorChar));
            results.Add(action.Strategy switch
            {
                InstallStrategy.Create => ApplyCreate(target, action, templatePath, force),
                InstallStrategy.AppendIfMissing => ApplyAppend(target, action, templatePath),
                InstallStrategy.MergeJson => ApplyMergeJson(target, action, templatePath, force),
                InstallStrategy.InstallHook => ApplyHook(target, action, templatePath),
                _ => new GateInstallResult(action.TargetRelativePath, false, "未知安装策略"),
            });
        }

        return results;
    }

    /// <summary>
    /// 创建文件（已存在且未强制时跳过；带版本戳的模板在目标版本落后时自动覆盖升级）。
    /// </summary>
    private static GateInstallResult ApplyCreate(string target, GateInstallAction action, string templatePath, bool force)
    {
        var content = File.ReadAllText(templatePath);
        if (File.Exists(target) && !force)
        {
            // 带版本戳的 guardAI 自有文件（技能/粘贴片段）：版本落后整文件升级，同版跳过
            var templateVersion = AiAgentCatalog.TryParseDirectiveVersion(content);
            if (templateVersion is null)
            {
                return new GateInstallResult(action.TargetRelativePath, false, "已存在，跳过（--force 可覆盖）");
            }

            var currentVersion = AiAgentCatalog.TryParseDirectiveVersion(File.ReadAllText(target)) ?? 0;
            if (currentVersion >= templateVersion.Value)
            {
                return new GateInstallResult(action.TargetRelativePath, false, $"已存在最新版（v{templateVersion.Value}），跳过");
            }

            File.Copy(templatePath, target, overwrite: true);
            return new GateInstallResult(action.TargetRelativePath, true, $"已升级（v{currentVersion} → v{templateVersion.Value}）");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        File.Copy(templatePath, target, overwrite: true);
        return new GateInstallResult(action.TargetRelativePath, true, "已写入");
    }

    /// <summary>
    /// 追加内容（无标记块则追加；带版本戳的指令块按版本三态处理：旧版整块升级、同版跳过）。
    /// </summary>
    private static GateInstallResult ApplyAppend(string target, GateInstallAction action, string templatePath)
    {
        var content = File.ReadAllText(templatePath);
        if (!File.Exists(target))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.WriteAllText(target, content);
            return new GateInstallResult(action.TargetRelativePath, true, "已写入");
        }

        var existing = File.ReadAllText(target);
        if (!existing.Contains(action.Marker, StringComparison.Ordinal))
        {
            File.AppendAllText(target, Environment.NewLine + content);
            return new GateInstallResult(action.TargetRelativePath, true, "已追加（保留原有内容）");
        }

        // 已含标记块：仅带版本戳的模板参与升级判定（无戳模板如 Codex hook 保持"有即跳过"）
        var templateVersion = AiAgentCatalog.TryParseDirectiveVersion(content);
        if (templateVersion is null)
        {
            return new GateInstallResult(action.TargetRelativePath, false, "已包含 guardAI 配置，跳过");
        }

        var start = existing.IndexOf(action.Marker, StringComparison.Ordinal);
        var end = existing.IndexOf(AiAgentCatalog.DirectiveEndMarker, start, StringComparison.Ordinal);
        if (end < 0)
        {
            // 标记块不完整（缺结束标记）：不动用户文件，提示人工处理，避免误删块外内容
            return new GateInstallResult(action.TargetRelativePath, false, "guardAI 标记块不完整（缺结束标记），跳过自动升级，请人工核对");
        }

        var blockEnd = end + AiAgentCatalog.DirectiveEndMarker.Length;
        var currentVersion = AiAgentCatalog.TryParseDirectiveVersion(existing[start..blockEnd]) ?? 0;
        if (currentVersion >= templateVersion.Value)
        {
            return new GateInstallResult(action.TargetRelativePath, false, $"已包含最新版 guardAI 指令（v{templateVersion.Value}），跳过");
        }

        // 旧版整块替换升级：块外的用户内容原样保留
        var upgraded = existing[..start] + content.TrimEnd('\r', '\n') + existing[blockEnd..];
        File.WriteAllText(target, upgraded);
        return new GateInstallResult(action.TargetRelativePath, true, $"已升级 guardAI 指令（v{currentVersion} → v{templateVersion.Value}，保留原有内容）");
    }

    /// <summary>
    /// 合并 JSON hook 配置（只增不改；解析失败时输出片段供人工合并）。
    /// </summary>
    private static GateInstallResult ApplyMergeJson(string target, GateInstallAction action, string templatePath, bool force)
    {
        var templateText = File.ReadAllText(templatePath);
        try
        {
            JsonNode? targetNode = File.Exists(target) && !force
                ? JsonNode.Parse(File.ReadAllText(target))
                : new JsonObject();
            if (targetNode is not JsonObject root)
            {
                root = new JsonObject();
            }

            var snippet = JsonNode.Parse(templateText) as JsonObject;
            var entries = snippet?["hooks"]?["PostToolUse"] as JsonArray;
            if (entries is null)
            {
                return new GateInstallResult(action.TargetRelativePath, false, "模板结构不符，跳过");
            }

            var hooks = root["hooks"] as JsonObject ?? new JsonObject();
            root["hooks"] = hooks;
            var post = hooks["PostToolUse"] as JsonArray ?? new JsonArray();
            hooks["PostToolUse"] = post;

            var changed = false;
            foreach (var entry in entries)
            {
                var text = entry?.ToJsonString() ?? string.Empty;
                if (post.Any(x => (x?.ToJsonString() ?? string.Empty).Contains("guardAI", StringComparison.Ordinal)))
                {
                    continue;
                }

                post.Add(entry?.DeepClone());
                changed = true;
            }

            if (!changed)
            {
                return new GateInstallResult(action.TargetRelativePath, false, "已包含 guardAI hook，跳过");
            }

            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.WriteAllText(target, root.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
            return new GateInstallResult(action.TargetRelativePath, true, File.Exists(target) ? "已合并（保留原有 hooks）" : "已写入");
        }
        catch (System.Text.Json.JsonException)
        {
            var snippetPath = target + ".guardai-snippet.json";
            File.WriteAllText(snippetPath, templateText);
            return new GateInstallResult(action.TargetRelativePath, false, $"原文件不是合法 JSON，片段已输出到 {Path.GetFileName(snippetPath)} 供人工合并");
        }
    }

    /// <summary>
    /// 安装 git 钩子（缺失创建、已有追加、Unix 下补执行权限）。
    /// </summary>
    private static GateInstallResult ApplyHook(string target, GateInstallAction action, string templatePath)
    {
        var content = File.ReadAllText(templatePath);
        if (File.Exists(target))
        {
            var existing = File.ReadAllText(target);
            if (existing.Contains("guardAI", StringComparison.Ordinal))
            {
                return new GateInstallResult(action.TargetRelativePath, false, "已包含 guardAI 钩子，跳过");
            }

            File.AppendAllText(target, Environment.NewLine + content);
            GrantExecute(target);
            return new GateInstallResult(action.TargetRelativePath, true, "已追加（保留原有钩子）");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        File.WriteAllText(target, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        GrantExecute(target);
        return new GateInstallResult(action.TargetRelativePath, true, "已写入");
    }

    /// <summary>
    /// 在 Unix 系统上为钩子文件补执行权限。
    /// </summary>
    private static void GrantExecute(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        File.SetUnixFileMode(path, File.GetUnixFileMode(path)
            | UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute);
    }
}
