using GuardAI.Core.Config;

namespace GuardAI.Core.Engine;

/// <summary>
/// 检查文件的收集模式。
/// </summary>
public enum CheckMode
{
    /// <summary>仅检查 git 新增/改动文件（默认，快且贴合"AI 刚写完"场景）。</summary>
    Changed,

    /// <summary>检查仓库内全部文件（CI 或非 git 环境用）。</summary>
    All,
}

/// <summary>
/// 单条违规记录。
/// </summary>
/// <param name="File">违规文件的仓库相对路径（正斜杠）。</param>
/// <param name="Rule">命中的规则名称。</param>
/// <param name="Message">违规说明。</param>
/// <param name="Suggestion">修复建议（可直接喂给 AI）。</param>
public sealed record Violation(string File, string Rule, string Message, string Suggestion);

/// <summary>
/// 单个检查模块的执行结果。
/// </summary>
/// <param name="Name">检查模块名称。</param>
/// <param name="Scanned">扫描文件数。</param>
/// <param name="Violations">违规列表。</param>
public sealed record CheckResult(string Name, int Scanned, IReadOnlyList<Violation> Violations);

/// <summary>
/// 检查上下文：一次检查运行所需的全部输入。
/// </summary>
public sealed class CheckContext
{
    /// <summary>构造检查上下文。</summary>
    /// <param name="repoPath">目标仓库根目录（绝对路径）。</param>
    /// <param name="mode">文件收集模式。</param>
    /// <param name="config">structure 规则配置。</param>
    /// <param name="configFileName">规则配置文件名（用于报告回显）。</param>
    /// <param name="strict">严格审计模式：存量重复（基线内）也报违规且不写基线，案例验收/审计用。</param>
    public CheckContext(string repoPath, CheckMode mode, StructureRuleConfig config, string configFileName, bool strict = false)
    {
        RepoPath = repoPath;
        Mode = mode;
        Config = config;
        ConfigFileName = configFileName;
        Strict = strict;
    }

    /// <summary>目标仓库根目录（绝对路径）。</summary>
    public string RepoPath { get; }

    /// <summary>文件收集模式。</summary>
    public CheckMode Mode { get; }

    /// <summary>structure 规则配置。</summary>
    public StructureRuleConfig Config { get; }

    /// <summary>规则配置文件名（用于报告回显）。</summary>
    public string ConfigFileName { get; }

    /// <summary>严格审计模式（check --strict）：存量重复也报违规、不读写基线豁免。</summary>
    public bool Strict { get; }
}

/// <summary>
/// 检查模块统一契约。每个检查模块实现本接口，由引擎按配置调度。
/// </summary>
/// <remarks>
/// 实现必须是确定性的：同输入同输出。这是产品可信度的基础（引擎裁判原则）。
/// </remarks>
public interface IGuardCheck
{
    /// <summary>检查模块唯一名称（如 structure、duplicate），用于 -Only 与报告分组。</summary>
    string Name { get; }

    /// <summary>
    /// 执行检查并返回结果。
    /// </summary>
    /// <param name="context">检查上下文（目标仓库、模式、规则配置）。</param>
    /// <returns>检查结果；无违规时违规列表为空。</returns>
    CheckResult Run(CheckContext context);
}
