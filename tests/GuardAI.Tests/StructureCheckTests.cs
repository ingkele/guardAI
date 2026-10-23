using GuardAI.Checks.Structure;
using GuardAI.Core.Config;
using GuardAI.Core.Engine;
using GuardAI.Init;

namespace GuardAI.Tests;

/// <summary>
/// structure 检查测试。回归基准与 guard-tool（P0 参考实现）保持一致：
/// 合规骨架 0 违规；违规现场 5 处全抓（extras/FeatureFlag.cs 1 处 + utils2 与 common 各 2 处）。
/// </summary>
public class StructureCheckTests
{
    /// <summary>合规骨架文件（DDD 项目即分层）。</summary>
    private static readonly string[] CleanFiles =
    [
        "src/DemoShop.Domain/Entities/Order.cs",
        "src/DemoShop.Application/Services/OrderService.cs",
        "src/DemoShop.Infrastructure/Persistence/OrderRepository.cs",
        "src/DemoShop.Infrastructure/Utils/FileHelper.cs",
        "src/DemoShop.Web/Controllers/OrdersController.cs",
        "tests/DemoShop.Tests/OrderTests.cs",
    ];

    /// <summary>违规现场文件（模拟 AI 自由发挥）。</summary>
    private static readonly string[] ViolationFiles =
    [
        "src/DemoShop.Infrastructure/utils2/UploadHelper.cs",
        "src/DemoShop.Web/common/StringExtensions.cs",
        "extras/FeatureFlag.cs",
    ];

    [Fact]
    public void 形态覆盖按项目裁决落位规则Scope()
    {
        // v0.3.1-⑤：ServiceDefaults 是类库（门面扩展类放模块根是模板惯例）不适用 app 落位规则；
        // 同仓库的 app 项目下扩展类照样必须进 Extensions/
        using var fixture = new TempRepoFixture([
            "AspireApp.ServiceDefaults/Extensions.cs",
            "AspireApp.Web/StringExtensions.cs",
        ]);
        var config = StructureRuleConfigLoader.Parse("""
            version: 1
            project_type: app
            include_extensions:
              - .cs
            allowed_dirs:
              - path: "**"
                purpose: 测试放行
            placement_rules:
              - name: 扩展类必须放 Extensions 目录
                file_pattern: "*Extensions.cs"
                dir_pattern: "**/Extensions/**"
                suggest: 移动到 Extensions/ 目录
                scope: app
            project_type_overrides:
              - path: AspireApp.ServiceDefaults/**
                type: library
            """);

        var result = Run(fixture.Path, config);

        var violation = Assert.Single(result.Violations);
        Assert.Equal("AspireApp.Web/StringExtensions.cs", violation.File); // 类库项目下 0 违规，app 项目下照抓
    }

    [Fact]
    public void 合规骨架零违规()
    {
        using var fixture = new TempRepoFixture(CleanFiles);
        var result = Run(fixture.Path, BuildDotNetConfig());

        Assert.Equal(6, result.Scanned);
        Assert.Empty(result.Violations);
    }

    [Fact]
    public void 违规现场五处全抓且规则归属正确()
    {
        using var fixture = new TempRepoFixture(CleanFiles.Concat(ViolationFiles).ToArray());
        var result = Run(fixture.Path, BuildDotNetConfig());

        Assert.Equal(9, result.Scanned);
        Assert.Equal(5, result.Violations.Count);

        AssertRules("extras/FeatureFlag.cs", ["目录白名单"]);
        AssertRules("src/DemoShop.Infrastructure/utils2/UploadHelper.cs",
            ["同义目录黑名单", "落位规则：工具类必须放 Utils 目录"]);
        AssertRules("src/DemoShop.Web/common/StringExtensions.cs",
            ["同义目录黑名单", "落位规则：扩展类必须放 Extensions 目录"]);
        return;

        void AssertRules(string file, string[] expected)
        {
            var actual = result.Violations.Where(v => v.File == file).Select(v => v.Rule).ToArray();
            Assert.Equal(expected, actual);
        }
    }

    [Fact]
    public void 豁免规则跳过指定路径()
    {
        var config = StructureRuleConfigLoader.Parse("""
            version: 1
            include_extensions:
              - .cs
            banned_dirs:
              - utils2
            allowed_dirs:
              - path: src/**
                purpose: 源码
            exclusions:
              - path: src/**/utils2/**
                reason: 测试固定现场
            """);

        using var fixture = new TempRepoFixture(["src/App/utils2/UploadHelper.cs"]);
        var result = Run(fixture.Path, config);

        Assert.Empty(result.Violations);
    }

    [Fact]
    public void library项目不吃scope_app的落位规则()
    {
        // F4：WitchC 试用实测——类库门面类放模块根是惯例，scope: app 落位规则不得误报
        var config = StructureRuleConfigLoader.Parse("""
            version: 1
            project_type: library
            include_extensions:
              - .cs
            allowed_dirs:
              - path: framework/**
                purpose: 框架
            placement_rules:
              - name: 扩展类必须放 Extensions 目录
                file_pattern: "*Extensions.cs"
                dir_pattern: "**/Extensions/**"
                suggest: 移动到 Extensions 目录
                scope: app
            """);

        using var fixture = new TempRepoFixture(["framework/WitchC/WitchCExtensions.cs"]);
        var result = Run(fixture.Path, config);

        Assert.Empty(result.Violations);
    }

    [Fact]
    public void app项目照常吃落位规则且scope_all永远生效()
    {
        var config = StructureRuleConfigLoader.Parse("""
            version: 1
            project_type: app
            include_extensions:
              - .cs
            allowed_dirs:
              - path: src/**
                purpose: 源码
            placement_rules:
              - name: app规则
                file_pattern: "*Extensions.cs"
                dir_pattern: "**/Extensions/**"
                suggest: 移动到 Extensions 目录
                scope: app
              - name: all规则
                file_pattern: "*Helper.cs"
                dir_pattern: "**/Utils/**"
                suggest: 移动到 Utils 目录
                scope: all
            """);

        using var fixture = new TempRepoFixture(["src/App/StringExtensions.cs", "src/App/FileHelper.cs"]);
        var result = Run(fixture.Path, config);

        Assert.Equal(2, result.Violations.Count);
        Assert.Contains(result.Violations, v => v.Rule == "落位规则：app规则");
        Assert.Contains(result.Violations, v => v.Rule == "落位规则：all规则");
    }

    /// <summary>用 .NET 预设生成器产出规则配置（同时覆盖配置生成器）。</summary>
    private static StructureRuleConfig BuildDotNetConfig()
    {
        var scan = new ProjectScanResult(
            RepoPath: "x", ProjectName: "DemoShop", Kind: ProjectKind.DotNet,
            ProjectType: ProjectTypeKind.App, Skeleton: SkeletonKind.ProjectAsLayer,
            SolutionFiles: [], ProjectFileCount: 1,
            CodeFileExtensions: [".cs", ".csproj"],
            TopDirectories: [], KnownDirectories: [], HasRootCodeFiles: false,
            HasGitHubWorkflow: false, HasGiteeWorkflow: false, HasPreCommitHook: false,
            HasClaudeSettings: false, HasCodexConfig: false, HasAgentsMd: false,
            HasGuardAiConfig: false, HasTestFixtures: false,
            ProjectTypeOverrides: []);
        return StructureRuleConfigLoader.Parse(ConfigGenerator.Generate(scan), "guardai.yml");
    }

    /// <summary>在临时目录执行 structure 检查（all 模式，不依赖 git）。</summary>
    private static CheckResult Run(string repoPath, StructureRuleConfig config)
    {
        var context = new CheckContext(repoPath, CheckMode.All, config, "guardai.yml");
        return new StructureCheck().Run(context);
    }
}
