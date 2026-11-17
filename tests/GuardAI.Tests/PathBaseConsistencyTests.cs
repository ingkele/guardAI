using GuardAI.Checks.Structure;
using GuardAI.Core.Config;
using GuardAI.Core.Engine;
using Xunit;

namespace GuardAI.Tests;

/// <summary>
/// 路径基准一致性测试（Issue #5）：monorepo 子目录 + --mode changed 时，
/// 白名单/豁免/落位必须与 --mode all 同结论。
/// </summary>
/// <remarks>
/// 根因：git status 在子目录下输出相对**仓库根**的路径（web/src/x.ts），
/// 而规则配置相对 --repo（src/x.ts）。差一层前缀 → 全部误判「野目录」、exclusions 全失效，
/// pre-commit 被硬拦只能 --no-verify。这些测试用真 git 仓库锁住行为，防回归。
/// </remarks>
public class PathBaseConsistencyTests
{
    /// <summary>白名单场景：子目录 changed 与 all 必须同结论（Issue #5 主症状）。</summary>
    [Fact]
    public void 子目录changed_白名单与all同结论()
    {
        using var fixture = new TempRepoFixture();
        fixture.AddFile("web/guardai.yml", """
            version: 1
            allowed_dirs:
              - path: src/lib/*
                purpose: 基础工具与单例模块
            include_extensions:
              - .ts
            """);
        fixture.AddFile("web/src/lib/http.ts", "export const a = 1;\n");
        fixture.AddFile("README.md", "root\n");
        fixture.InitGitRepository();
        fixture.CommitAll();

        // 新增一个白名单内的文件（父目录 src/lib 已在白名单）
        fixture.AddFile("web/src/lib/feature-flags.ts", "export const f = 2;\n");

        var all = RunStructure(Path.Combine(fixture.Path, "web"), CheckMode.All);
        var changed = RunStructure(Path.Combine(fixture.Path, "web"), CheckMode.Changed);

        Assert.Empty(all.Violations);
        Assert.Empty(changed.Violations);
    }

    /// <summary>真野目录仍要报：路径归一不能把白名单放宽。</summary>
    [Fact]
    public void 子目录changed_真野目录仍报违规()
    {
        using var fixture = new TempRepoFixture();
        fixture.AddFile("web/guardai.yml", """
            version: 1
            allowed_dirs:
              - path: src/lib/*
                purpose: 基础工具与单例模块
            include_extensions:
              - .ts
            """);
        fixture.AddFile("web/src/lib/http.ts", "export const a = 1;\n");
        fixture.InitGitRepository();
        fixture.CommitAll();

        // 白名单外的全新目录
        fixture.AddFile("web/src/random/rogue.ts", "export const r = 3;\n");

        var changed = RunStructure(Path.Combine(fixture.Path, "web"), CheckMode.Changed);

        var violation = Assert.Single(changed.Violations);
        Assert.Equal("目录白名单", violation.Rule);
        // 报出路径必须是相对 --repo 的口径（不带 web/ 前缀），用户才能直接定位
        Assert.Equal("src/random/rogue.ts", violation.File);
    }

    /// <summary>exclusions 豁免在 changed 模式必须生效（Issue #5 评论补充的第二处同源症状）。</summary>
    [Fact]
    public void 子目录changed_exclusions豁免生效()
    {
        using var fixture = new TempRepoFixture();
        fixture.AddFile("web/guardai.yml", """
            version: 1
            allowed_dirs:
              - path: src/*
                purpose: 源码
            include_extensions:
              - .ts
            exclusions:
              - path: src/lib/legacy.ts
                reason: 遗留代码待重构
            """);
        fixture.AddFile("web/src/lib/http.ts", "export const a = 1;\n");
        fixture.InitGitRepository();
        fixture.CommitAll();

        fixture.AddFile("web/src/lib/legacy.ts", "export const l = 9;\n");

        var changed = RunStructure(Path.Combine(fixture.Path, "web"), CheckMode.Changed);

        Assert.Empty(changed.Violations);
    }

    /// <summary>仓库根（repo == git 根）行为不变：路径归一不得引入回归。</summary>
    [Fact]
    public void 仓库根changed_行为不变()
    {
        using var fixture = new TempRepoFixture();
        fixture.AddFile("guardai.yml", """
            version: 1
            allowed_dirs:
              - path: src/*
                purpose: 源码
            include_extensions:
              - .ts
            """);
        fixture.AddFile("src/http.ts", "export const a = 1;\n");
        fixture.InitGitRepository();
        fixture.CommitAll();

        fixture.AddFile("src/feature.ts", "export const f = 2;\n");

        var changed = RunStructure(fixture.Path, CheckMode.Changed);

        Assert.Empty(changed.Violations);
    }

    /// <summary>收集到的路径本身必须相对 --repo（Issue #5 的直接断言）。</summary>
    [Fact]
    public void Collect_changed_返回相对repo的路径()
    {
        using var fixture = new TempRepoFixture();
        fixture.AddFile("web/src/lib/http.ts", "export const a = 1;\n");
        fixture.InitGitRepository();
        fixture.CommitAll();

        fixture.AddFile("web/src/lib/feature-flags.ts", "export const f = 2;\n");

        var files = TargetFileCollector.Collect(
            Path.Combine(fixture.Path, "web"), CheckMode.Changed, [".ts"]);

        Assert.Contains("src/lib/feature-flags.ts", files);
        Assert.DoesNotContain("web/src/lib/feature-flags.ts", files);
    }

    /// <summary>跑 structure 检查（配置从 fixture 目录读取）。</summary>
    /// <param name="repoPath">检查目录。</param>
    /// <param name="mode">收集模式。</param>
    /// <returns>检查结果。</returns>
    private static CheckResult RunStructure(string repoPath, CheckMode mode)
    {
        var yaml = File.ReadAllText(Path.Combine(repoPath, "guardai.yml"));
        var config = StructureRuleConfigLoader.Parse(yaml);
        var context = new CheckContext(repoPath, mode, config, "guardai.yml");
        return new StructureCheck().Run(context);
    }
}