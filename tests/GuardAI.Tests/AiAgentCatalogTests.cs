using GuardAI.Init;

namespace GuardAI.Tests;

/// <summary>
/// AI 智能体接入档案注册表测试：探测装配矩阵、通用兜底、模板完备性。
/// </summary>
public class AiAgentCatalogTests : IDisposable
{
    private readonly string _repo = Directory.CreateTempSubdirectory("guardai-agents-").FullName;

    public void Dispose()
    {
        Directory.Delete(_repo, recursive: true);
    }

    [Fact]
    public void 注册表档案标识唯一且探测信号非空()
    {
        Assert.Equal(
            AiAgentCatalog.Agents.Select(a => a.Id).Distinct().Count(),
            AiAgentCatalog.Agents.Count);
        Assert.All(AiAgentCatalog.Agents, a => Assert.NotEmpty(a.DetectPaths));
    }

    [Fact]
    public void 每个档案的模板都在已登记清单里()
    {
        // 新增智能体只加档案不改逻辑的前提：模板登记三处同步（磁盘/KnownTemplates/内嵌）
        foreach (var agent in AiAgentCatalog.Agents)
        {
            Assert.Contains(agent.TemplateRelativePath, TemplateProvider.KnownTemplates);
            Assert.NotNull(TemplateProvider.ReadEmbedded(agent.TemplateRelativePath));
        }

        Assert.Contains(AiAgentCatalog.DirectiveTemplate, TemplateProvider.KnownTemplates);
    }

    [Fact]
    public void 探测到智能体目录时装对应接入()
    {
        Directory.CreateDirectory(Path.Combine(_repo, ".workbuddy"));
        Directory.CreateDirectory(Path.Combine(_repo, ".qwen"));
        Directory.CreateDirectory(Path.Combine(_repo, ".trae"));

        var plan = Plan();

        Assert.Contains(plan, a => a.TargetRelativePath == ".workbuddy/skills/guardai-search/SKILL.md");
        Assert.Contains(plan, a => a.TargetRelativePath == "QWEN.md");
        Assert.Contains(plan, a => a.TargetRelativePath == ".trae/rules/project_rules.md");
    }

    [Fact]
    public void 探测信号是文件也触发()
    {
        // 千问只有 QWEN.md 没有 .qwen/ 目录时同样识别为在用
        File.WriteAllText(Path.Combine(_repo, "QWEN.md"), "# demo");

        var plan = Plan();

        Assert.Contains(plan, a => a.TargetRelativePath == "QWEN.md");
    }

    [Fact]
    public void 未探测不装且Force时全装()
    {
        var idle = Plan();
        Assert.DoesNotContain(idle, a => a.TargetRelativePath == ".workbuddy/skills/guardai-search/SKILL.md");
        Assert.DoesNotContain(idle, a => a.TargetRelativePath == "QWEN.md");
        Assert.DoesNotContain(idle, a => a.TargetRelativePath == ".claude/settings.json");
        Assert.DoesNotContain(idle, a => a.TargetRelativePath == ".codex/config.toml");

        var forced = Plan(force: true);
        foreach (var agent in AiAgentCatalog.Agents)
        {
            Assert.Contains(forced, a => a.TargetRelativePath == agent.TargetRelativePath);
        }
    }

    [Fact]
    public void 通用兜底与云端粘贴片段始终在计划()
    {
        var plan = Plan();

        Assert.Contains(plan, a => a.TargetRelativePath == "AGENTS.md");
        Assert.Contains(plan, a => a.TargetRelativePath == ".guardai-snippets/guardai-directive.md");
    }

    [Fact]
    public void WorkBuddy技能装配出合法SKILL文件()
    {
        Directory.CreateDirectory(Path.Combine(_repo, ".workbuddy"));
        var installer = new GateInstaller(TemplateProvider.Resolve(null));

        installer.Apply(installer.Plan(BuildScan(), force: false), _repo, force: false);

        var skillPath = Path.Combine(_repo, ".workbuddy", "skills", "guardai-search", "SKILL.md");
        Assert.True(File.Exists(skillPath));
        var content = File.ReadAllText(skillPath);
        Assert.Contains("name: guardai-search", content);
        Assert.Contains("search_capability", content); // 检索闭环指令随技能注入
    }

    [Fact]
    public void 指令注入与AGENTS兜底同源且可重复装配()
    {
        Directory.CreateDirectory(Path.Combine(_repo, ".qwen"));
        var installer = new GateInstaller(TemplateProvider.Resolve(null));

        installer.Apply(installer.Plan(BuildScan(), force: false), _repo, force: false);
        installer.Apply(installer.Plan(BuildScan(), force: false), _repo, force: false);

        var qwen = File.ReadAllText(Path.Combine(_repo, "QWEN.md"));
        var agents = File.ReadAllText(Path.Combine(_repo, "AGENTS.md"));
        Assert.Contains(AiAgentCatalog.DirectiveMarker, qwen);
        Assert.Contains(AiAgentCatalog.DirectiveMarker, agents);
        Assert.Contains("写码前强制检索", qwen);

        // 重复装配幂等：指令块只出现一次
        Assert.Equal(1, CountOccurrences(qwen, AiAgentCatalog.DirectiveMarker));
        Assert.Equal(1, CountOccurrences(agents, AiAgentCatalog.DirectiveMarker));
    }

    [Fact]
    public void 旧版指令块自动升级且保留块外用户内容()
    {
        // 存量项目实测踩到的坑：旧标记块被 init 跳过永不升级（无版本戳按 v0 计，应整块升级）
        var agentsPath = Path.Combine(_repo, "AGENTS.md");
        File.WriteAllText(agentsPath,
            "# 项目自有约定（用户内容）\n" +
            "<!-- guardAI:start -->\n" +
            "## 旧版三条指令（早期内容）\n" +
            "1. 禁止自建目录\n<!-- guardAI:end -->\n" +
            "## 尾部用户内容\n");
        var installer = new GateInstaller(TemplateProvider.Resolve(null));

        var result = installer.Apply([new GateInstallAction("AGENTS.md", "d", AiAgentCatalog.DirectiveTemplate, InstallStrategy.AppendIfMissing, AiAgentCatalog.DirectiveMarker)], _repo, force: false).Single();

        Assert.True(result.Written);
        Assert.Contains("已升级", result.Detail);
        var upgraded = File.ReadAllText(agentsPath);
        Assert.Contains("项目自有约定（用户内容）", upgraded); // 块前用户内容保留
        Assert.Contains("尾部用户内容", upgraded); // 块后用户内容保留
        Assert.Contains("写码前强制检索", upgraded); // 升级到检索闭环版
        Assert.DoesNotContain("旧版三条指令", upgraded); // 旧块整块替换
        Assert.Contains($"guardai-directive-version: {AiAgentCatalog.DirectiveVersion}", upgraded); // 盖上版本戳
        Assert.Equal(1, CountOccurrences(upgraded, AiAgentCatalog.DirectiveMarker));
    }

    [Fact]
    public void 同版本指令块跳过且重复装配幂等()
    {
        var agentsPath = Path.Combine(_repo, "AGENTS.md");
        var installer = new GateInstaller(TemplateProvider.Resolve(null));
        var action = new GateInstallAction("AGENTS.md", "d", AiAgentCatalog.DirectiveTemplate, InstallStrategy.AppendIfMissing, AiAgentCatalog.DirectiveMarker);

        installer.Apply([action], _repo, force: false);
        var first = File.ReadAllText(agentsPath);
        var second = installer.Apply([action], _repo, force: false).Single();

        Assert.False(second.Written);
        Assert.Contains("最新版", second.Detail);
        Assert.Equal(first, File.ReadAllText(agentsPath)); // 二次装配一字不改
    }

    [Fact]
    public void 无结束标记的旧块不自动升级防误删()
    {
        // 块尾缺失时块边界不可判定，宁可跳过也不动用户文件
        var agentsPath = Path.Combine(_repo, "AGENTS.md");
        File.WriteAllText(agentsPath, "<!-- guardAI:start -->\n旧内容\n## 也许是用户续写\n");
        var installer = new GateInstaller(TemplateProvider.Resolve(null));

        var result = installer.Apply([new GateInstallAction("AGENTS.md", "d", AiAgentCatalog.DirectiveTemplate, InstallStrategy.AppendIfMissing, AiAgentCatalog.DirectiveMarker)], _repo, force: false).Single();

        Assert.False(result.Written);
        Assert.Contains("不完整", result.Detail);
        Assert.Contains("也许是用户续写", File.ReadAllText(agentsPath)); // 文件原样未动
    }

    [Fact]
    public void 旧版粘贴片段与WorkBuddy技能自动升级()
    {
        // Create 型 guardAI 自有文件同享版本化升级：旧版（无戳）覆盖换新，二次装配跳过
        Directory.CreateDirectory(Path.Combine(_repo, ".workbuddy"));
        var snippetPath = Path.Combine(_repo, ".guardai-snippets", "guardai-directive.md");
        Directory.CreateDirectory(Path.GetDirectoryName(snippetPath)!);
        File.WriteAllText(snippetPath, "旧版粘贴片段（无版本戳）");
        var skillPath = Path.Combine(_repo, ".workbuddy", "skills", "guardai-search", "SKILL.md");
        Directory.CreateDirectory(Path.GetDirectoryName(skillPath)!);
        File.WriteAllText(skillPath, "# 旧版技能（无版本戳）");
        var installer = new GateInstaller(TemplateProvider.Resolve(null));

        installer.Apply(installer.Plan(BuildScan(), force: false), _repo, force: false);
        installer.Apply(installer.Plan(BuildScan(), force: false), _repo, force: false);

        var snippet = File.ReadAllText(snippetPath);
        var skill = File.ReadAllText(skillPath);
        Assert.DoesNotContain("旧版粘贴片段", snippet);
        Assert.DoesNotContain("旧版技能", skill);
        Assert.Contains($"guardai-directive-version: {AiAgentCatalog.DirectiveVersion}", snippet);
        Assert.Contains("name: guardai-search", skill);
    }

    [Fact]
    public void 指令模板版本戳与版本常量一致()
    {
        // 版本常量与模板戳必须同步（内容变更时 +1），否则升级判定失真
        var snippet = TemplateProvider.ReadEmbedded(AiAgentCatalog.DirectiveTemplate)!;
        Assert.Contains($"{AiAgentCatalog.DirectiveVersionPrefix} {AiAgentCatalog.DirectiveVersion}", snippet);
        Assert.Contains(AiAgentCatalog.DirectiveEndMarker, snippet);

        var skill = TemplateProvider.ReadEmbedded("agents/workbuddy-skill.md")!;
        Assert.Contains($"{AiAgentCatalog.DirectiveVersionPrefix} {AiAgentCatalog.DirectiveVersion}", skill);
    }

    /// <summary>统计子串出现次数（幂等断言用）。</summary>
    private static int CountOccurrences(string text, string value)
    {
        var count = 0;
        var index = 0;
        while ((index = text.IndexOf(value, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += value.Length;
        }

        return count;
    }

    /// <summary>构造安装计划（探测临时仓库信号）。</summary>
    private IReadOnlyList<GateInstallAction> Plan(bool force = false) =>
        new GateInstaller(TemplateProvider.Resolve(null)).Plan(BuildScan(), force);

    /// <summary>最小扫描结果夹具（Plan 只消费 RepoPath）。</summary>
    private ProjectScanResult BuildScan() => new(
        RepoPath: _repo, ProjectName: "Demo", Kind: ProjectKind.DotNet,
        ProjectType: ProjectTypeKind.App, Skeleton: SkeletonKind.Generic,
        SolutionFiles: [], ProjectFileCount: 1,
        CodeFileExtensions: [".cs"],
        TopDirectories: [], KnownDirectories: [], HasRootCodeFiles: false,
        HasGitHubWorkflow: false, HasGiteeWorkflow: false, HasPreCommitHook: false,
        HasClaudeSettings: false, HasCodexConfig: false, HasAgentsMd: false,
        HasGuardAiConfig: false, HasTestFixtures: false,
        ProjectTypeOverrides: []);
}
