using GuardAI.AI;
using GuardAI.Core.Config;
using GuardAI.Core.Engine;

namespace GuardAI.Init;

/// <summary>
/// AI 装配建议的确定性校验器：用仓库现状验证建议本身，拦下会冤枉存量代码的规则。
/// </summary>
/// <remarks>
/// "引擎裁判，AI 装配"的延伸：AI 产出的配置建议也必须过确定性闸——
/// 落位规则只允许约束"未来新文件"，任何让既有文件违规的建议一律丢弃（首跑零误报铁律）。
/// 目录白名单与豁免只放宽权限、不产生违规，无需校验。
/// </remarks>
public static class InitAdviceValidator
{
    /// <summary>
    /// 用既有文件清单校验落位规则建议，剔除会误报存量的规则。
    /// </summary>
    /// <param name="advice">AI 装配建议。</param>
    /// <param name="relativeFiles">仓库既有文件的相对路径清单（与检查引擎同口径）。</param>
    /// <param name="projectType">生效的项目形态（app / library，落位规则 scope 过滤用）。</param>
    /// <returns>过滤后的建议与新增的丢弃说明。</returns>
    /// <exception cref="ArgumentNullException">入参为 null 时抛出。</exception>
    public static (InitAdvice Filtered, IReadOnlyList<string> DroppedNotes) FilterForFiles(
        InitAdvice advice, IReadOnlyList<string> relativeFiles, string projectType)
    {
        ArgumentNullException.ThrowIfNull(advice);
        ArgumentNullException.ThrowIfNull(relativeFiles);

        var dropped = new List<string>(advice.DroppedNotes);
        var kept = new List<InitAdvicePlacement>();

        foreach (var rule in advice.PlacementRules)
        {
            // scope 不适用 = 现状不会触发，原样保留（由形态裁决，与检查引擎同一套过滤）
            if (rule.Scope != "all" && !string.Equals(rule.Scope, projectType, StringComparison.OrdinalIgnoreCase))
            {
                kept.Add(rule);
                continue;
            }

            var offenders = relativeFiles
                .Where(f => WouldFlag(f, rule))
                .ToList();

            if (offenders.Count > 0)
            {
                dropped.Add($"落位规则「{rule.Name}」会让 {offenders.Count} 个既有文件违规" +
                    $"（如 {offenders[0]}），违背首跑零误报铁律，已丢弃");
            }
            else
            {
                kept.Add(rule);
            }
        }

        var filtered = advice with { PlacementRules = kept };
        return (filtered, dropped);
    }

    /// <summary>
    /// 判断某条落位规则是否会对既有文件报违规（与 StructureCheck 落位判定同口径）。
    /// </summary>
    /// <param name="relativePath">文件相对路径。</param>
    /// <param name="rule">落位规则建议。</param>
    /// <returns>会报违规返回 true。</returns>
    private static bool WouldFlag(string relativePath, InitAdvicePlacement rule)
    {
        var normalized = relativePath.Replace('\\', '/');
        var slash = normalized.LastIndexOf('/');
        var fileName = slash < 0 ? normalized : normalized[(slash + 1)..];
        var dirWithSlash = slash < 0 ? "/" : normalized[..(slash + 1)];

        return GlobMatcher.IsMatch(fileName, rule.FilePattern)
            && !GlobMatcher.IsMatch(dirWithSlash, rule.DirPattern);
    }

    /// <summary>
    /// 收集仓库既有代码文件的相对路径（与检查引擎同口径：gitignore 忽略的不算）。
    /// </summary>
    /// <param name="scan">项目扫描结果。</param>
    /// <returns>代码文件相对路径清单（/ 分隔）。</returns>
    /// <exception cref="ArgumentNullException">scan 为 null 时抛出。</exception>
    public static IReadOnlyList<string> CollectCodeFiles(ProjectScanResult scan)
    {
        ArgumentNullException.ThrowIfNull(scan);

        var ignore = GitIgnoreFilter.Load(scan.RepoPath);
        var extensions = scan.CodeFileExtensions
            .Select(e => e.StartsWith('.') ? e.ToLowerInvariant() : "." + e.ToLowerInvariant())
            .ToHashSet();

        var files = new List<string>();
        foreach (var file in Directory.EnumerateFiles(scan.RepoPath, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(scan.RepoPath, file).Replace('\\', '/');
            if (rel.StartsWith(".git/", StringComparison.OrdinalIgnoreCase)
                || !extensions.Contains(Path.GetExtension(rel).ToLowerInvariant())
                || ignore.IsIgnored(rel))
            {
                continue;
            }

            files.Add(rel);
        }

        return files;
    }
}
