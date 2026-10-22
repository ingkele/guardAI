using System.Text;

namespace GuardAI.Init;

/// <summary>
/// 仓库结构摘要构建器：把扫描结果压缩成给 AI 装配顾问看的结构化摘要。
/// </summary>
/// <remarks>
/// 只发"结构事实"（目录名、用途、项目清单、闸口现状），不发代码内容——
/// 与 review 的送审安全边界同一条铁律：给外部模型的信息最小化。
/// </remarks>
public static class InitDigest
{
    /// <summary>摘要中目录基线最多列出的条数（防止大仓库摘要爆量）。</summary>
    private const int MaxKnownDirectories = 60;

    /// <summary>
    /// 构建仓库结构摘要文本。
    /// </summary>
    /// <param name="scan">项目扫描结果。</param>
    /// <returns>中文结构摘要（送大模型的用户提示组成部分）。</returns>
    /// <exception cref="ArgumentNullException">scan 为 null 时抛出。</exception>
    public static string Build(ProjectScanResult scan)
    {
        ArgumentNullException.ThrowIfNull(scan);

        var sb = new StringBuilder();
        sb.AppendLine($"仓库: {scan.ProjectName}");
        sb.AppendLine($"生态: {scan.Kind} | 骨架: {scan.Skeleton} | " +
            $"形态: {(scan.ProjectType == ProjectTypeKind.Library ? "library" : "app")}");
        sb.AppendLine($"解决方案 {scan.SolutionFiles.Count} 个（{string.Join(", ", scan.SolutionFiles)}），" +
            $"项目文件 {scan.ProjectFileCount} 个");
        sb.AppendLine($"代码扩展名: {string.Join(", ", scan.CodeFileExtensions)}");

        sb.AppendLine("顶层目录（含推断用途）：");
        foreach (var dir in scan.TopDirectories)
        {
            sb.AppendLine($"  - {dir.Name} — {dir.Purpose}");
        }

        if (scan.HasRootCodeFiles)
        {
            sb.AppendLine("  - （仓库根目录有代码文件）");
        }

        sb.AppendLine($"目录基线 {scan.KnownDirectories.Count} 个既有目录" +
            $"（白名单快照会逐层放行，最多列出 {MaxKnownDirectories} 个）：");
        foreach (var dir in scan.KnownDirectories.Take(MaxKnownDirectories))
        {
            sb.AppendLine($"  - {dir}");
        }

        if (scan.ProjectTypeOverrides.Count > 0)
        {
            sb.AppendLine("项目形态按路径覆盖：" + string.Join(
                ", ", scan.ProjectTypeOverrides.Select(o => $"{o.Path}={o.ProjectType}")));
        }

        sb.AppendLine("闸口现状：" + string.Join("，", new[]
        {
            scan.HasGitHubWorkflow ? "GitHub Actions 有" : "GitHub Actions 无",
            scan.HasGiteeWorkflow ? "Gitee Go 有" : "Gitee Go 无",
            scan.HasPreCommitHook ? "pre-commit 有" : "pre-commit 无",
            scan.HasAgentsMd ? "AGENTS.md 有" : "AGENTS.md 无",
            scan.HasGuardAiConfig ? "guardai.yml 有" : "guardai.yml 无",
        }));
        sb.AppendLine($"测试固定现场（Fixtures）：{(scan.HasTestFixtures ? "有" : "无")}");

        return sb.ToString();
    }
}
