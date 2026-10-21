namespace GuardAI.Init;

/// <summary>
/// guardAI init 的执行结果。
/// </summary>
/// <param name="Scan">项目扫描结果。</param>
/// <param name="ConfigPath">生成/确认的规则配置路径。</param>
/// <param name="ConfigWritten">规则配置是否实际写入。</param>
/// <param name="GateResults">闸口安装结果。</param>
/// <param name="Notes">补充说明（如未检测到的工具跳过了什么）。</param>
public sealed record InitResult(
    ProjectScanResult Scan,
    string ConfigPath,
    bool ConfigWritten,
    IReadOnlyList<GateInstallResult> GateResults,
    IReadOnlyList<string> Notes);

/// <summary>
/// guardAI init 装配编排器：扫描 → 生成规则配置 → 补齐闸口。
/// </summary>
/// <remarks>
/// 装配原则（产品铁律"引擎裁判，AI 装配"）：本流程只产出配置文件，全部可人工核对；
/// 合规判定永远由检查引擎完成，装配层不参与。
/// </remarks>
public static class GuardAiInitializer
{
    /// <summary>
    /// 执行装配。
    /// </summary>
    /// <param name="repoPath">目标仓库根目录。</param>
    /// <param name="templatesDir">模板目录（可空，自动查找）。</param>
    /// <param name="force">是否覆盖已有配置 / 全量安装 AI 钩子。</param>
    /// <param name="log">进度日志输出。</param>
    /// <returns>装配结果。</returns>
    /// <exception cref="DirectoryNotFoundException">目标目录或模板目录不存在时抛出。</exception>
    public static InitResult Run(string repoPath, string? templatesDir, bool force, Action<string> log)
    {
        var scan = ProjectScanner.Scan(repoPath);
        var notes = new List<string>();

        log($"扫描完成：生态={scan.Kind}，骨架={scan.Skeleton}，" +
            $"解决方案 {scan.SolutionFiles.Count} 个，项目文件 {scan.ProjectFileCount} 个");

        // 1. 生成规则配置（guardai.yml）
        var configPath = Path.Combine(scan.RepoPath, "guardai.yml");
        var configWritten = false;
        if (File.Exists(configPath) && !force)
        {
            log("guardai.yml 已存在，跳过生成（--force 可重新生成）");
        }
        else
        {
            File.WriteAllText(configPath, ConfigGenerator.Generate(scan));
            configWritten = true;
            log($"已生成规则配置: {configPath}");
        }

        // 2. 补齐闸口（先计划后落盘）；模板磁盘优先、内嵌资源兜底（M2-3：单拷 exe 也可装配）
        var templates = TemplateProvider.Resolve(templatesDir);
        var installer = new GateInstaller(templates);
        var plan = installer.Plan(scan, force);

        log("闸口安装计划：");
        foreach (var action in plan)
        {
            log($"  - {action.Description} -> {action.TargetRelativePath}");
        }

        // 未接入的智能体逐条提示（未进计划即未探测到；--force 全装时无提示）
        if (!force)
        {
            foreach (var agent in AiAgentCatalog.Agents)
            {
                if (plan.All(a => a.TargetRelativePath != agent.TargetRelativePath))
                {
                    notes.Add($"未检测到 {agent.DisplayName}（{string.Join(" / ", agent.DetectPaths)}），已跳过其接入；需要时可加 --force 全量安装");
                }
            }
        }

        var gateResults = installer.Apply(plan, scan.RepoPath, force);
        foreach (var result in gateResults)
        {
            log($"  [{(result.Written ? "写入" : "跳过")}] {result.TargetRelativePath}：{result.Detail}");
        }

        // 3. 台账目录不进 git（隐私铁律：记录不出本机）
        if (EnsureLedgerIgnored(scan.RepoPath))
        {
            log("已在 .gitignore 中忽略台账目录 .guard/records/（记录不出本机）");
        }

        return new InitResult(scan, configPath, configWritten, gateResults, notes);
    }

    /// <summary>
    /// 确保台账目录 .guard/records/ 被 .gitignore 忽略（不存在则创建条目）。
    /// </summary>
    /// <param name="repoPath">目标仓库根目录。</param>
    /// <returns>是否实际改动了 .gitignore。</returns>
    /// <exception cref="ArgumentNullException">repoPath 为 null 时抛出。</exception>
    public static bool EnsureLedgerIgnored(string repoPath)
    {
        ArgumentNullException.ThrowIfNull(repoPath);

        var gitignorePath = Path.Combine(repoPath, ".gitignore");
        var entry = ".guard/records/";

        var lines = File.Exists(gitignorePath)
            ? File.ReadAllLines(gitignorePath).ToList()
            : new List<string>();

        // 已有等价条目（带不带尾斜杠都算）则不动
        if (lines.Any(l => l.Trim() is ".guard/records/" or ".guard/records" or ".guard/"))
        {
            return false;
        }

        if (lines.Count > 0 && !string.IsNullOrWhiteSpace(lines[^1]))
        {
            lines.Add(string.Empty);
        }

        lines.Add("# guardAI 台账（本地记录，不出本机）");
        lines.Add(entry);
        File.WriteAllLines(gitignorePath, lines);
        return true;
    }
}
