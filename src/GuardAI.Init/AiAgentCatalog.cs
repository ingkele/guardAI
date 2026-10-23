namespace GuardAI.Init;

/// <summary>
/// AI 智能体接入档案：一家智能体一条数据（探测信号 + 安装动作）。
/// </summary>
/// <param name="Id">档案标识（小写短横线，如 claude-code）。</param>
/// <param name="DisplayName">展示名（给用户核对安装计划）。</param>
/// <param name="DetectPaths">探测信号（仓库相对路径，目录或文件任一存在即判定在用）。</param>
/// <param name="TargetRelativePath">安装目标（仓库相对路径）。</param>
/// <param name="TemplateRelativePath">模板相对路径（templates 目录下）。</param>
/// <param name="Strategy">安装策略。</param>
/// <param name="Marker">去重标记（AppendIfMissing 使用）。</param>
/// <param name="Description">动作说明（展示给用户核对）。</param>
public sealed record AiAgentProfile(
    string Id,
    string DisplayName,
    IReadOnlyList<string> DetectPaths,
    string TargetRelativePath,
    string TemplateRelativePath,
    InstallStrategy Strategy,
    string Marker,
    string Description)
{
    /// <summary>
    /// 转成闸口安装动作（接入档案与安装器解耦，Plan 直接消费）。
    /// </summary>
    /// <returns>安装动作。</returns>
    public GateInstallAction ToAction() =>
        new(TargetRelativePath, Description, TemplateRelativePath, Strategy, Marker);
}

/// <summary>
/// 内置 AI 智能体接入档案注册表（数据驱动：新增智能体只加一条档案，不改安装逻辑）。
/// </summary>
/// <remarks>
/// 接入形态分三类：
/// ① 事件 hook（Claude Code / Codex）——写文件后当轮拦截复检，不修完不放行；
/// ② 项目级 skill（WorkBuddy）——技能声明式接入，智能体写码前自动加载检索指令；
/// ③ 指令文件注入（千问 Qwen Code / 字节 Trae）——把同一份 guardAI 指令追加进对方的项目指令文件。
/// 没有仓库级配置的云端型智能体（字节办公 / 飞书智能伙伴、mimo claw 等）走两条通用通道：
/// .guardai-snippets/ 粘贴片段（手动粘到对方系统提示）+ AGENTS.md 兜底（一切读项目指令文件的智能体通吃）。
/// </remarks>
public static class AiAgentCatalog
{
    /// <summary>通用指令片段模板（多智能体共用的同一份 guardAI 指令文本）。</summary>
    public const string DirectiveTemplate = "agents/AGENTS-snippet.md";

    /// <summary>通用指令块去重标记（块首）。</summary>
    public const string DirectiveMarker = "<!-- guardAI:start -->";

    /// <summary>通用指令块结束标记（块尾）。</summary>
    public const string DirectiveEndMarker = "<!-- guardAI:end -->";

    /// <summary>指令块版本戳前缀（块内注释行 guardai-directive-version: N）。</summary>
    public const string DirectiveVersionPrefix = "guardai-directive-version:";

    /// <summary>
    /// 当前指令块版本号（模板内容变更时 +1）。
    /// 无版本戳的旧块按 v0 计，init 装配时自动整块升级，无需人工手动换新。
    /// v3（2026-10-23）：补命名纪律/分层穿透/DTO 边界/魔法串四条 prompt 规则（learn 回放 91 发现人工筛落地）。
    /// </summary>
    public const int DirectiveVersion = 3;

    /// <summary>
    /// 解析文本中的指令块版本戳。
    /// </summary>
    /// <param name="text">待解析文本（整文件或标记块片段）。</param>
    /// <returns>版本号；无版本戳返回 null（视为非版本化内容）。</returns>
    public static int? TryParseDirectiveVersion(string text)
    {
        var match = System.Text.RegularExpressions.Regex.Match(
            text, DirectiveVersionPrefix + @"\s*(\d+)");
        return match.Success ? int.Parse(match.Groups[1].Value) : null;
    }

    /// <summary>内置智能体接入档案（顺序即安装计划的展示顺序）。</summary>
    public static readonly IReadOnlyList<AiAgentProfile> Agents =
    [
        new(
            Id: "claude-code",
            DisplayName: "Claude Code",
            DetectPaths: [".claude"],
            TargetRelativePath: ".claude/settings.json",
            TemplateRelativePath: "hooks/claude-code.json",
            Strategy: InstallStrategy.MergeJson,
            Marker: "guardAI",
            Description: "AI 工具 hook（Claude Code，写文件后当轮拦截）"),
        new(
            Id: "codex",
            DisplayName: "Codex",
            DetectPaths: [".codex"],
            TargetRelativePath: ".codex/config.toml",
            TemplateRelativePath: "hooks/codex.toml",
            Strategy: InstallStrategy.AppendIfMissing,
            Marker: "guardAI",
            Description: "AI 工具 hook（Codex，on_failure=block 不修完不放行）"),
        new(
            Id: "workbuddy",
            DisplayName: "WorkBuddy",
            DetectPaths: [".workbuddy"],
            TargetRelativePath: ".workbuddy/skills/guardai-search/SKILL.md",
            TemplateRelativePath: "agents/workbuddy-skill.md",
            Strategy: InstallStrategy.Create,
            Marker: "guardAI",
            Description: "AI 工具 skill（WorkBuddy 项目级技能，写码前强制检索）"),
        new(
            Id: "qwen",
            DisplayName: "千问 Qwen Code",
            DetectPaths: [".qwen", "QWEN.md"],
            TargetRelativePath: "QWEN.md",
            TemplateRelativePath: DirectiveTemplate,
            Strategy: InstallStrategy.AppendIfMissing,
            Marker: DirectiveMarker,
            Description: "AI 指令注入（千问 Qwen Code，QWEN.md 项目指令文件）"),
        new(
            Id: "trae",
            DisplayName: "字节 Trae / 豆包 MarsCode",
            DetectPaths: [".trae", ".marscode"],
            TargetRelativePath: ".trae/rules/project_rules.md",
            TemplateRelativePath: DirectiveTemplate,
            Strategy: InstallStrategy.AppendIfMissing,
            Marker: DirectiveMarker,
            Description: "AI 指令注入（字节 Trae/豆包，工程规则文件）"),
    ];
}
