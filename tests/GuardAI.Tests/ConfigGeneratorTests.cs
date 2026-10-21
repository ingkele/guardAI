using GuardAI.Core.Config;
using GuardAI.Init;

namespace GuardAI.Tests;

/// <summary>规则配置生成器测试（骨架识别与预设内容）。</summary>
public class ConfigGeneratorTests
{
    [Fact]
    public void DDD骨架生成双写法白名单()
    {
        var config = Load(ProjectKind.DotNet, SkeletonKind.ProjectAsLayer, hasFixtures: false);

        Assert.Contains(config.AllowedDirs, r => r.Path == "src/*Domain/**");
        Assert.Contains(config.AllowedDirs, r => r.Path == "src/*/Domain/**");
        Assert.Contains(config.AllowedDirs, r => r.Path == "tests/**");
        Assert.Equal([".cs", ".csproj"], config.IncludeExtensions);
        Assert.Contains("utils2", config.BannedDirs);
        Assert.Equal(3, config.PlacementRules.Count);
        Assert.Empty(config.Exclusions);
    }

    [Fact]
    public void 通用骨架生成工具型仓库白名单()
    {
        var config = Load(ProjectKind.Unknown, SkeletonKind.Generic, hasFixtures: false);

        Assert.Contains(config.AllowedDirs, r => r.Path == "src/**");
        Assert.Contains(config.AllowedDirs, r => r.Path == "templates/**");
    }

    [Fact]
    public void 含测试固定现场时自动加豁免()
    {
        var config = Load(ProjectKind.DotNet, SkeletonKind.ProjectAsLayer, hasFixtures: true);

        Assert.Contains(config.Exclusions, e => e.Path == "tests/**/Fixtures/**");
    }

    [Fact]
    public void 实测顶层目录进白名单并标注用途()
    {
        var scan = BuildScan(topDirs:
        [
            new TopDirectoryInfo("framework", "源码模块"),
            new TopDirectoryInfo("samples", "源码模块"),
        ]);
        var config = StructureRuleConfigLoader.Parse(ConfigGenerator.Generate(scan), "guardai.yml");

        Assert.Contains(config.AllowedDirs, r => r.Path == "framework/*" && r.Purpose.Contains("init 实测"));
        Assert.Contains(config.AllowedDirs, r => r.Path == "samples/*");
    }

    [Fact]
    public void 实测基线与兜底不重复()
    {
        // 仓库既有 src/，兜底的 src/** 不应重复出现
        var scan = BuildScan(topDirs: [new TopDirectoryInfo("src", "源码模块")]);
        var config = StructureRuleConfigLoader.Parse(ConfigGenerator.Generate(scan), "guardai.yml");

        Assert.Single(config.AllowedDirs, r => r.Path == "src/*");
        Assert.DoesNotContain(config.AllowedDirs, r => r.Path == "src/**");
    }

    [Fact]
    public void 根目录有代码文件时允许根级文件()
    {
        var scan = BuildScan(topDirs: [], hasRootCodeFiles: true);
        var config = StructureRuleConfigLoader.Parse(ConfigGenerator.Generate(scan), "guardai.yml");

        Assert.Contains(config.AllowedDirs, r => r.Path == "*");
    }

    [Fact]
    public void 空仓库保持通用兜底白名单()
    {
        var scan = BuildScan(topDirs: []);
        var config = StructureRuleConfigLoader.Parse(ConfigGenerator.Generate(scan), "guardai.yml");

        Assert.Contains(config.AllowedDirs, r => r.Path == "src/**");
        Assert.DoesNotContain(config.AllowedDirs, r => r.Path == "*");
    }

    [Fact]
    public void 子目录逐层进基线快照()
    {
        var scan = BuildScan(
            topDirs: [new TopDirectoryInfo("src", "源码模块")],
            knownDirs: ["src", "src/Api", "src/Api/Controllers"]);
        var config = StructureRuleConfigLoader.Parse(ConfigGenerator.Generate(scan), "guardai.yml");

        Assert.Contains(config.AllowedDirs, r => r.Path == "src/*");
        Assert.Contains(config.AllowedDirs, r => r.Path == "src/Api/*");
        Assert.Contains(config.AllowedDirs, r => r.Path == "src/Api/Controllers/*");
    }

    [Fact]
    public void 嵌套野目录不被放行而存量子目录放行()
    {
        // M2-2 核心：快照条目是单层 '目录/*'，init 后新建的嵌套目录不命中任何条目
        var scan = BuildScan(
            topDirs: [new TopDirectoryInfo("src", "代码")],
            knownDirs: ["src", "src/Api", "src/Api/Controllers"]);
        var config = StructureRuleConfigLoader.Parse(ConfigGenerator.Generate(scan), "guardai.yml");

        bool Allowed(string rel) => config.AllowedDirs.Any(r => GlobMatcher.IsMatch(rel, r.Path));

        Assert.True(Allowed("src/Api/Controllers/HomeController.cs")); // 存量子目录放行
        Assert.True(Allowed("src/Api/OrderService.cs"));               // 存量子目录放行
        Assert.False(Allowed("src/Api/Wild/Foo.cs"));                  // init 后嵌套野目录拦截
        Assert.False(Allowed("src/MyNewHelpers/ImageHelper.cs"));      // init 后嵌套野目录拦截
    }

    [Fact]
    public void 实测扩展名生成include_extensions()
    {
        // F3：按仓库实际代码文件扩展名生成，不再写死生态默认
        var scan = BuildScan(topDirs: [], codeExtensions: [".vue", ".mjs", ".js"]);
        var config = StructureRuleConfigLoader.Parse(ConfigGenerator.Generate(scan), "guardai.yml");

        Assert.Equal([".vue", ".mjs", ".js"], config.IncludeExtensions);
    }

    [Fact]
    public void 无实测扩展名时回落生态默认()
    {
        var config = Load(ProjectKind.DotNet, SkeletonKind.ProjectAsLayer, hasFixtures: false);

        Assert.Equal([".cs", ".csproj"], config.IncludeExtensions);
    }

    [Fact]
    public void 生成projectType与落位规则scope()
    {
        // F4：project_type 写入配置，落位规则带 scope: app（类库项目由引擎过滤）
        var scan = BuildScan(topDirs: []);
        var yaml = ConfigGenerator.Generate(scan);

        Assert.Contains("project_type: library", yaml);
        Assert.Contains("scope: app", yaml);

        var config = StructureRuleConfigLoader.Parse(yaml, "guardai.yml");
        Assert.Equal("library", config.ProjectType);
        Assert.All(config.PlacementRules, r => Assert.Equal("app", r.Scope));
    }

    [Fact]
    public void 应用项目生成project_type_app()
    {
        var scan = BuildScan(topDirs: [], projectType: ProjectTypeKind.App);
        var yaml = ConfigGenerator.Generate(scan);

        Assert.Contains("project_type: app", yaml);
    }

    [Fact]
    public void DotNet项目生成duplicates段并带默认值()
    {
        var scan = BuildScan(topDirs: []);
        var yaml = ConfigGenerator.Generate(scan);

        Assert.Contains("duplicates:", yaml);

        var config = StructureRuleConfigLoader.Parse(yaml, "guardai.yml");
        Assert.True(config.Duplicates.Enabled);
        Assert.Equal(0.85, config.Duplicates.Threshold);
        Assert.Equal(12, config.Duplicates.MinTokens);
        Assert.Contains("Upload*", config.Duplicates.Registry);
    }

    [Fact]
    public void Node项目不生成duplicates段()
    {
        // 重复检测目前只针对 .NET 方法体，Node 项目不输出该段（解析回落默认值也不会误报）
        var scan = BuildScan(topDirs: [], kind: ProjectKind.Node);
        var yaml = ConfigGenerator.Generate(scan);

        Assert.DoesNotContain("duplicates:", yaml);
    }

    /// <summary>构造带实测基线的扫描结果（knownDirs 缺省时取顶层目录名）。</summary>
    private static ProjectScanResult BuildScan(
        IReadOnlyList<TopDirectoryInfo> topDirs,
        IReadOnlyList<string>? knownDirs = null,
        bool hasRootCodeFiles = false,
        ProjectTypeKind projectType = ProjectTypeKind.Library,
        IReadOnlyList<string>? codeExtensions = null,
        ProjectKind kind = ProjectKind.DotNet)
    {
        return new ProjectScanResult(
            RepoPath: "x", ProjectName: "WitchC", Kind: kind,
            ProjectType: projectType, Skeleton: SkeletonKind.Generic,
            SolutionFiles: [], ProjectFileCount: 3,
            CodeFileExtensions: codeExtensions ?? [".cs", ".csproj"],
            TopDirectories: topDirs,
            KnownDirectories: knownDirs ?? topDirs.Select(d => d.Name).ToList(),
            HasRootCodeFiles: hasRootCodeFiles,
            HasGitHubWorkflow: false, HasGiteeWorkflow: false, HasPreCommitHook: false,
            HasClaudeSettings: false, HasCodexConfig: false, HasAgentsMd: false,
            HasGuardAiConfig: false, HasTestFixtures: false,
            ProjectTypeOverrides: []);
    }

    [Fact]
    public void 混形态仓库生成形态覆盖段并可解析回读()
    {
        // v0.3.1-⑤：多项目混 app/library 时生成 project_type_overrides，规则资产随配置下发
        var scan = new ProjectScanResult(
            RepoPath: "x", ProjectName: "AspireApp", Kind: ProjectKind.DotNet,
            ProjectType: ProjectTypeKind.App, Skeleton: SkeletonKind.Generic,
            SolutionFiles: [], ProjectFileCount: 2,
            CodeFileExtensions: [".cs", ".csproj"],
            TopDirectories: [], KnownDirectories: [], HasRootCodeFiles: false,
            HasGitHubWorkflow: false, HasGiteeWorkflow: false, HasPreCommitHook: false,
            HasClaudeSettings: false, HasCodexConfig: false, HasAgentsMd: false,
            HasGuardAiConfig: false, HasTestFixtures: false,
            ProjectTypeOverrides: [new ProjectTypeOverride("AspireApp.ServiceDefaults/**", "library")]);

        var config = StructureRuleConfigLoader.Parse(ConfigGenerator.Generate(scan), "guardai.yml");

        var overrideRule = Assert.Single(config.ProjectTypeOverrides);
        Assert.Equal("AspireApp.ServiceDefaults/**", overrideRule.Path);
        Assert.Equal("library", overrideRule.ProjectType);
    }

    /// <summary>按指定生态/骨架生成并解析配置。</summary>
    private static StructureRuleConfig Load(ProjectKind kind, SkeletonKind skeleton, bool hasFixtures)
    {
        var scan = new ProjectScanResult(
            RepoPath: "x", ProjectName: "Demo", Kind: kind,
            ProjectType: ProjectTypeKind.App, Skeleton: skeleton,
            SolutionFiles: [], ProjectFileCount: 1,
            CodeFileExtensions: kind == ProjectKind.Node ? [".ts", ".js"] : [".cs", ".csproj"],
            TopDirectories: [], KnownDirectories: [], HasRootCodeFiles: false,
            HasGitHubWorkflow: false, HasGiteeWorkflow: false, HasPreCommitHook: false,
            HasClaudeSettings: false, HasCodexConfig: false, HasAgentsMd: false,
            HasGuardAiConfig: false, HasTestFixtures: hasFixtures,
            ProjectTypeOverrides: []);
        return StructureRuleConfigLoader.Parse(ConfigGenerator.Generate(scan), "guardai.yml");
    }
}
