using GuardAI.Core.Config;
using GuardAI.Core.Engine;

namespace GuardAI.Checks.Structure;

/// <summary>
/// structure 检查模块：结构级规范检查——项目骨架落位。
/// </summary>
/// <remarks>
/// 来源：移植自本项目自研的 guard-tool（PowerShell 版 checks/structure.ps1），三类规则语义保持一致：
///   1. 同义目录黑名单：AI 惯用的野目录（utils2/helpers/common/...）一律禁止；
///   2. 落位规则：*Helper.cs / *Utils.cs 必须在 Utils 目录，*Extensions.cs 必须在 Extensions 目录；
///   3. 目录白名单：文件必须落在骨架既定目录内，白名单外 = 违规。
/// 另支持 exclusions 豁免（命中文件整体跳过）。
/// </remarks>
public sealed class StructureCheck : IGuardCheck
{
    /// <inheritdoc />
    public string Name => "structure";

    /// <inheritdoc />
    public CheckResult Run(CheckContext context)
    {
        var config = context.Config;
        var files = TargetFileCollector.Collect(context.RepoPath, context.Mode, config.IncludeExtensions);
        var violations = new List<Violation>();

        foreach (var rel in files)
        {
            if (IsExcluded(rel, config.Exclusions))
            {
                continue;
            }

            var segments = rel.Split('/');
            var fileName = segments[^1];
            var dirSegments = segments[..^1];
            var dirPart = string.Join('/', dirSegments);

            CheckBannedDirs(rel, dirSegments, config, violations);
            CheckPlacement(rel, fileName, dirPart, config, violations);
            CheckAllowlist(rel, config, violations);
        }

        return new CheckResult(Name, files.Count, violations);
    }

    /// <summary>
    /// 判断文件是否命中豁免规则。
    /// </summary>
    private static bool IsExcluded(string rel, IReadOnlyList<ExclusionRule> exclusions)
    {
        return exclusions.Any(e => GlobMatcher.IsMatch(rel, e.Path));
    }

    /// <summary>
    /// 解析文件生效的项目形态（v0.3.1-⑤）：project_type_overrides 命中即生效（多条命中靠后的优先），
    /// 未命中回落仓库级 project_type。
    /// </summary>
    private static string ResolveProjectType(string rel, StructureRuleConfig config)
    {
        for (var i = config.ProjectTypeOverrides.Count - 1; i >= 0; i--)
        {
            if (GlobMatcher.IsMatch(rel, config.ProjectTypeOverrides[i].Path))
            {
                return config.ProjectTypeOverrides[i].ProjectType;
            }
        }

        return config.ProjectType;
    }

    /// <summary>
    /// 规则 1：同义目录黑名单——路径任一层目录名命中即违规（不区分大小写）。
    /// </summary>
    private static void CheckBannedDirs(string rel, string[] dirSegments, StructureRuleConfig config, List<Violation> violations)
    {
        foreach (var ban in config.BannedDirs)
        {
            if (dirSegments.Any(seg => string.Equals(seg, ban, StringComparison.OrdinalIgnoreCase)))
            {
                violations.Add(new Violation(
                    rel,
                    "同义目录黑名单",
                    $"路径中包含禁用目录 '{ban}'——这是 AI 惯用的野目录，会破坏项目骨架",
                    $"不要自建 {ban}/ 目录，把文件放入骨架既定目录（见规则配置的 allowed_dirs）"));
            }
        }
    }

    /// <summary>
    /// 规则 2：落位规则——文件名命中时所在目录必须同时命中。
    /// 按 scope 过滤：app/library 只在对应形态生效（落位规则对类库失配率高，由项目形态裁决，逻辑做薄）。
    /// 多项目仓库按 project_type_overrides 对文件逐个裁决形态（v0.3.1-⑤）。
    /// </summary>
    private static void CheckPlacement(string rel, string fileName, string dirPart, StructureRuleConfig config, List<Violation> violations)
    {
        var projectType = ResolveProjectType(rel, config);
        foreach (var rule in config.PlacementRules)
        {
            if (rule.Scope != "all" && rule.Scope != projectType)
            {
                continue;
            }

            if (!GlobMatcher.IsMatch(fileName, rule.FilePattern))
            {
                continue;
            }

            var dirWithSlash = dirPart + "/";
            if (!GlobMatcher.IsMatch(dirWithSlash, rule.DirPattern))
            {
                violations.Add(new Violation(
                    rel,
                    $"落位规则：{rule.Name}",
                    $"文件名 '{fileName}' 命中 '{rule.FilePattern}'，但不在 {rule.DirPattern} 目录下",
                    rule.Suggestion));
            }
        }
    }

    /// <summary>
    /// 规则 3：目录白名单——文件路径必须命中至少一条白名单规则。
    /// </summary>
    private static void CheckAllowlist(string rel, StructureRuleConfig config, List<Violation> violations)
    {
        var allowed = config.AllowedDirs.Any(item => GlobMatcher.IsMatch(rel, item.Path));
        if (!allowed)
        {
            violations.Add(new Violation(
                rel,
                "目录白名单",
                "文件不在项目骨架白名单的任何目录内（疑似 AI 自由发挥新建了目录）",
                "先检索该文件应归属哪一层（Domain/Application/Infrastructure/Web/Tests），移动过去；若确需新目录，先与团队确认后更新规则配置"));
        }
    }
}
