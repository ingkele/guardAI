using System.Text;
using GuardAI.AI;
using GuardAI.AI.Config;
using GuardAI.Core.Config;

namespace GuardAI.Init;

/// <summary>
/// 规则配置生成器：根据扫描结果产出 guardai.yml（规则资产）。
/// </summary>
/// <remarks>
/// 生成的配置可人工核对修改——AI/模板装配只出配置，判定永远归引擎。
/// AI 装配建议（init --ai）以"增量"方式合并：模板基线保留，AI 条目去重后追加并标注来源。
/// </remarks>
public static class ConfigGenerator
{
    /// <summary>AI 惯用的野目录黑名单（与 guard-tool 预设一致）。</summary>
    private static readonly string[] BannedDirs =
    [
        "utils2", "util", "uitli", "uitls", "helper", "helpers",
        "common", "shared", "misc", "temp", "tmp", "lib", "libs",
    ];

    /// <summary>
    /// 生成 guardai.yml 文本。
    /// </summary>
    /// <param name="scan">项目扫描结果。</param>
    /// <param name="advice">AI 装配建议（可空；非空时以增量方式合并进白名单/落位/豁免）。</param>
    /// <param name="aiConfig">AI 接入配置（可空；非空时把 ai: 段写入配置，review 开箱即用）。</param>
    /// <returns>规则配置 YAML 文本。</returns>
    public static string Generate(ProjectScanResult scan, InitAdvice? advice = null, AIConfig? aiConfig = null)
    {
        advice ??= InitAdvice.Empty;
        var sb = new StringBuilder();
        sb.AppendLine("# ============================================================");
        sb.AppendLine("# guardAI 项目骨架规则配置（由 guardAI init 生成，可人工修改）");
        sb.AppendLine($"# 项目: {scan.ProjectName} | 生态: {scan.Kind} | 骨架: {scan.Skeleton}");
        sb.AppendLine("# 本文件是\"规则资产\"——引擎只是执行宿主，升级宿主时配置原样沿用。");
        sb.AppendLine("# 语法：支持 key: value、嵌套、- 列表、# 注释。字符串含空格请加引号。");
        sb.AppendLine("# ============================================================");
        sb.AppendLine();
        sb.AppendLine("version: 1");
        sb.AppendLine();
        sb.AppendLine("# 项目形态：app（应用）/ library（类库）。落位规则按 scope 匹配此值才生效");
        sb.AppendLine("# init 自动判定，可人工修改（类库项目的扩展类放模块根是惯例，落位规则 scope: app 不适用）");
        sb.AppendLine(advice.ProjectType is not null
            ? $"project_type: {advice.ProjectType}   # AI 装配建议（init --ai），可人工改"
            : $"project_type: {(scan.ProjectType == ProjectTypeKind.Library ? "library" : "app")}");
        if (scan.ProjectTypeOverrides.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("# 项目形态按路径覆盖（多项目仓库混 app/library，init 按各项目 csproj 自动建议，可人工改）");
            sb.AppendLine("# 命中即生效，多条命中靠后的优先；未命中回落上面的 project_type");
            sb.AppendLine("project_type_overrides:");
            foreach (var ov in scan.ProjectTypeOverrides)
            {
                sb.AppendLine($"  - path: {ov.Path}");
                sb.AppendLine($"    type: {ov.ProjectType}");
            }
        }
        sb.AppendLine();
        sb.AppendLine("# 参与检查的文件扩展名（小写，带点）");
        sb.AppendLine("# init 按仓库实测到的代码文件扩展名生成，改用其他扩展名后请同步更新");
        sb.AppendLine("include_extensions:");
        foreach (var ext in IncludeExtensions(scan))
        {
            sb.AppendLine($"  - {ext}");
        }

        sb.AppendLine();
        sb.AppendLine("# 同义目录黑名单：路径中出现任一目录名即违规（不区分大小写）");
        sb.AppendLine("# 注意：规范目录名（如 Utils/Extensions）不在黑名单——它们是合法归宿");
        sb.AppendLine("banned_dirs:");
        foreach (var dir in BannedDirs)
        {
            sb.AppendLine($"  - {dir}");
        }

        sb.AppendLine();
        sb.AppendLine("# 目录白名单：文件相对路径必须命中至少一条（** 任意层，* 单层）");
        sb.AppendLine("# 前段是 init 实测的目录基线快照（每层既有目录一条 '目录/*'，精确放行存量骨架）；");
        sb.AppendLine("# 后段是通用兜底；新增目录先与团队确认后补进来");
        sb.AppendLine("allowed_dirs:");
        var allowedSeen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (path, purpose) in AllowedDirs(scan))
        {
            allowedSeen.Add(path);
            sb.AppendLine($"  - path: {path}");
            sb.AppendLine($"    purpose: {purpose}");
        }

        // AI 装配建议（init --ai）：增量追加，与模板基线重复的丢弃
        var extraAllowed = advice.AllowedDirs.Where(i => allowedSeen.Add(i.Path)).ToList();
        if (extraAllowed.Count > 0)
        {
            sb.AppendLine("  # —— 以下为 AI 装配建议补充（init --ai），可人工增删 ——");
            foreach (var item in extraAllowed)
            {
                sb.AppendLine($"  - path: {item.Path}");
                sb.AppendLine($"    purpose: {item.Purpose}");
            }
        }

        sb.AppendLine();
        sb.AppendLine("# 落位规则：文件名命中 file_pattern 时，所在目录必须命中 dir_pattern");
        sb.AppendLine("# scope: 适用项目形态（app / library / all），只在 project_type 匹配时生效");
        sb.AppendLine("placement_rules:");
        var placementSeen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, filePattern, dirPattern, suggest, scope) in PlacementRules())
        {
            placementSeen.Add(name);
            sb.AppendLine($"  - name: {name}");
            sb.AppendLine($"    file_pattern: \"{filePattern}\"");
            sb.AppendLine($"    dir_pattern: \"{dirPattern}\"");
            sb.AppendLine($"    suggest: \"{suggest}\"");
            sb.AppendLine($"    scope: {scope}");
        }

        var extraPlacements = advice.PlacementRules
            .Where(p => placementSeen.Add(p.Name)).ToList();
        if (extraPlacements.Count > 0)
        {
            sb.AppendLine("  # —— 以下为 AI 装配建议补充（init --ai），可人工增删 ——");
            foreach (var rule in extraPlacements)
            {
                sb.AppendLine($"  - name: {rule.Name}");
                sb.AppendLine($"    file_pattern: \"{rule.FilePattern}\"");
                sb.AppendLine($"    dir_pattern: \"{rule.DirPattern}\"");
                sb.AppendLine($"    suggest: \"{rule.Suggest}\"");
                sb.AppendLine($"    scope: {rule.Scope}");
            }
        }

        // 豁免：测试固定现场 + AI 装配建议（生成代码/第三方等有意放行）
        var exclusions = new List<(string Path, string Reason)>();
        if (scan.HasTestFixtures)
        {
            exclusions.Add(("tests/**/Fixtures/**", "回归测试固定现场，允许包含故意违规文件"));
        }

        var exclusionSeen = new HashSet<string>(
            exclusions.Select(e => e.Path), StringComparer.OrdinalIgnoreCase);
        exclusions.AddRange(advice.Exclusions
            .Where(i => exclusionSeen.Add(i.Path))
            .Select(i => (i.Path, i.Purpose)));

        if (exclusions.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("# 豁免：有意放行的路径（含故意违规文件/生成代码/第三方等）");
            sb.AppendLine("exclusions:");
            foreach (var (path, reason) in exclusions)
            {
                sb.AppendLine($"  - path: {path}");
                sb.AppendLine($"    reason: {reason}");
            }
        }

        // 重复检测（P3）：目前语法适配器只做 C#，非 .NET 生态不出这段（出了也不生效）
        if (scan.Kind == ProjectKind.DotNet)
        {
            sb.AppendLine();
            sb.AppendLine("# 重复检测：AI 重写已有实现时当场拦下（存量重复自动进基线，只报新增）");
            sb.AppendLine("# threshold 近似阈值（0~1）；min_tokens 过短方法不参与近似检测；");
            sb.AppendLine("# registry 高频方法名（通配符，两侧同名命中同一模式才报）");
            sb.AppendLine("duplicates:");
            sb.AppendLine("  enabled: true");
            sb.AppendLine("  threshold: 0.85");
            sb.AppendLine("  min_tokens: 12");
            sb.AppendLine("  registry:");
            foreach (var pattern in DuplicateRuleConfig.DefaultRegistry)
            {
                sb.AppendLine($"    - {pattern}");
            }
        }

        // AI 接入段：init --ai 时把实际使用的接入配置写入，review 开箱即用
        //（只写环境变量名不写密钥——密钥永不落文件）
        if (aiConfig is not null)
        {
            sb.AppendLine();
            sb.AppendLine("# AI 语义审查（guardAI review）与 AI 装配接入：大模型配置");
            sb.AppendLine("# 密钥放环境变量不落配置：api_key_env 指定环境变量名");
            sb.AppendLine("ai:");
            sb.AppendLine($"  provider: {aiConfig.Provider}");
            sb.AppendLine($"  base_url: \"{aiConfig.BaseUrl}\"");
            sb.AppendLine($"  model: \"{aiConfig.Model}\"");
            sb.AppendLine($"  api_key_env: {aiConfig.ApiKeyEnv}");
            sb.AppendLine($"  max_tokens: {aiConfig.MaxTokens}");
            sb.AppendLine($"  timeout_seconds: {aiConfig.TimeoutSeconds}");
            sb.AppendLine($"  temperature: {aiConfig.Temperature.ToString(System.Globalization.CultureInfo.InvariantCulture)}");
        }

        return sb.ToString();
    }

    /// <summary>
    /// 参与检查的扩展名：优先用仓库实测到的代码文件扩展名（F3 生态感知），
    /// 无代码文件时回落生态默认（避免空仓库生成空配置）。
    /// </summary>
    private static string[] IncludeExtensions(ProjectScanResult scan)
    {
        if (scan.CodeFileExtensions.Count > 0)
        {
            return [.. scan.CodeFileExtensions];
        }

        return scan.Kind == ProjectKind.Node
            ? [".ts", ".tsx", ".js", ".jsx"]
            : [".cs", ".csproj"];
    }

    /// <summary>
    /// 目录白名单 = init 实测的目录基线快照（精确到每层）+ 通用兜底。
    /// </summary>
    /// <remarks>
    /// "首跑零误报、之后野目录必拦"的关键：白名单是 init 时刻仓库真实目录的快照——
    /// 既有目录全部合法（不冤枉存量代码），init 之后 AI 新建的目录才拦。
    /// 快照条目用"目录/*"（单层）而不是"目录/**"（递归）：递归通配会把之后新建的
    /// 嵌套野目录一并放行——这是案例库实测抓到的漏报盲区（M2-2 修复）。
    /// </remarks>
    /// <param name="scan">项目扫描结果。</param>
    /// <returns>白名单条目（路径模式，用途说明）。</returns>
    private static IEnumerable<(string Path, string Purpose)> AllowedDirs(ProjectScanResult scan)
    {
        // 1. 实测基线快照：init 时刻既有目录逐层列出（含各层子目录）
        foreach (var dir in scan.KnownDirectories)
        {
            var isTop = !dir.Contains('/');
            yield return ($"{dir}/*", isTop
                ? $"{scan.TopDirectories.FirstOrDefault(t => t.Name == dir)?.Purpose ?? "代码目录"}（init 实测既有目录）"
                : "init 实测既有子目录");
        }

        if (scan.HasRootCodeFiles)
        {
            yield return ("*", "仓库根目录代码文件（init 实测既有）");
        }

        // 2. 通用兜底：覆盖未来新建的常规目录（不与实测基线首段重复的）
        var baseline = scan.KnownDirectories.Select(d => d.Split('/')[0])
            .Concat(scan.HasRootCodeFiles ? ["*"] : [])
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var (path, purpose) in FallbackDirs(scan.Skeleton))
        {
            var topName = path.Split('/')[0];
            if (!baseline.Contains(topName))
            {
                yield return (path, purpose);
            }
        }
    }

    /// <summary>
    /// 通用兜底白名单（DDD 双写法都内置，避免"项目即分层/嵌套分层"误报）。
    /// </summary>
    private static IEnumerable<(string Path, string Purpose)> FallbackDirs(SkeletonKind skeleton)
    {
        if (skeleton == SkeletonKind.Generic)
        {
            yield return ("src/**", "源代码");
            yield return ("tests/**", "测试");
            yield return ("config/**", "配置");
            yield return ("templates/**", "模板资产");
            yield return ("scripts/**", "脚本");
            yield return ("docs/**", "文档");
            yield break;
        }

        yield return ("src/*Domain/**", "领域层-项目即分层（实体、值对象、领域服务、仓储接口）");
        yield return ("src/*/Domain/**", "领域层-嵌套分层");
        yield return ("src/*Application/**", "应用层-项目即分层（应用服务、DTO、用例编排）");
        yield return ("src/*/Application/**", "应用层-嵌套分层");
        yield return ("src/*Infrastructure/**", "基础设施层-项目即分层（持久化、外部服务、工具类 Utils/Extensions）");
        yield return ("src/*/Infrastructure/**", "基础设施层-嵌套分层");
        yield return ("src/*Web/**", "表现层-项目即分层（控制器、中间件、启动配置）");
        yield return ("src/*/Web/**", "表现层-嵌套分层");
        yield return ("tests/**", "测试项目");
        yield return ("docs/**", "文档");
    }

    /// <summary>
    /// 落位规则（与 guard-tool 预设一致）。
    /// scope: app——工具类/扩展类的 Utils/Extensions 归位是应用项目惯例；
    /// 类库/框架项目的入口扩展类天然放模块根（WitchC 试用实测 5 处失配），故不适用。
    /// </summary>
    private static IEnumerable<(string Name, string FilePattern, string DirPattern, string Suggest, string Scope)> PlacementRules()
    {
        yield return ("工具类必须放 Utils 目录", "*Helper.cs", "**/Utils/**", "移动到所在项目的 Utils/ 目录下，例如 src/MyApp.Infrastructure/Utils/", "app");
        yield return ("工具类必须放 Utils 目录", "*Utils.cs", "**/Utils/**", "移动到所在项目的 Utils/ 目录下；优先复用已有 Utils/ 内的同类方法", "app");
        yield return ("扩展类必须放 Extensions 目录", "*Extensions.cs", "**/Extensions/**", "移动到所在项目的 Extensions/ 目录下；先检索是否已有同名扩展方法", "app");
    }
}
