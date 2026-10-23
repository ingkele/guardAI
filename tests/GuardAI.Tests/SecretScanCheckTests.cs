using GuardAI.AI.Review;
using GuardAI.Checks.Security;
using GuardAI.Core.Config;
using GuardAI.Core.Engine;

namespace GuardAI.Tests;

/// <summary>
/// secret 检查测试（密钥泄露确定性检查）。重点：
/// ①与送审侧 DiffCollector 三道闸特征逐字锁定防漂移；
/// ②违规消息只报类别与行号、绝不回显密钥值（报告可能外传）；
/// ③豁免逃生口与零误报。
/// </summary>
public class SecretScanCheckTests
{
    /// <summary>假密钥样本（组合构造，避免本文件自身命中内容特征）。</summary>
    private static string FakeToken => "sk-" + "1234567890abcdef1234567890abcdef";

    [Fact]
    public void 特征与送审侧三道闸_逐字一致()
    {
        // 防漂移锁定：送审侧拦什么，检查侧就抓什么；改一侧必须同步改另一侧
        Assert.Equal(DiffCollector.SensitiveNamePatternText, SecretScanCheck.SensitiveNamePatternText);
        Assert.Equal(DiffCollector.SensitiveDirPatternText, SecretScanCheck.SensitiveDirPatternText);
        Assert.Equal(DiffCollector.SecretContentPatternText, SecretScanCheck.SecretContentPatternText);
    }

    [Fact]
    public void 硬编码密钥_报违规且不回显密钥值()
    {
        using var fixture = new TempRepoFixture();
        fixture.AddFile("src/App/KeyHolder.cs", $"public class KeyHolder {{ string k = \"{FakeToken}\"; }}");

        var result = Run(fixture.Path);

        var violation = Assert.Single(result.Violations);
        Assert.Equal("硬编码密钥", violation.Rule);
        Assert.Contains("第 1 行", violation.Message);
        Assert.DoesNotContain("1234567890abcdef", violation.Message); // 密钥值不进报告
        Assert.DoesNotContain("1234567890abcdef", violation.Suggestion);
    }

    [Fact]
    public void 敏感文件入库_按文件名特征报违规()
    {
        using var fixture = new TempRepoFixture();
        fixture.AddFile(".env", "OPENAI_API_KEY=" + FakeToken);
        fixture.AddFile("certs/server.pem", "-----");

        var result = Run(fixture.Path);

        Assert.Equal(2, result.Violations.Count);
        Assert.All(result.Violations, v => Assert.Equal("敏感文件", v.Rule));
        Assert.Contains(result.Violations, v => v.File == ".env");
        Assert.Contains(result.Violations, v => v.File == "certs/server.pem");
    }

    [Fact]
    public void 敏感目录下的文件_整体报违规()
    {
        using var fixture = new TempRepoFixture();
        fixture.AddFile(".ssh/config", "Host demo");

        var result = Run(fixture.Path);

        var violation = Assert.Single(result.Violations);
        Assert.Equal("敏感文件", violation.Rule);
        Assert.Equal(".ssh/config", violation.File);
    }

    [Fact]
    public void 命中文件名特征的文件_不再做内容扫描_同一密钥不双报()
    {
        using var fixture = new TempRepoFixture();
        fixture.AddFile(".env", "OPENAI_API_KEY=" + FakeToken);

        var result = Run(fixture.Path);

        Assert.Single(result.Violations);
    }

    [Fact]
    public void 豁免规则命中_整体跳过()
    {
        using var fixture = new TempRepoFixture();
        fixture.AddFile("tests/FakeKeys.cs", $"string k = \"{FakeToken}\";");

        var config = StructureRuleConfigLoader.Parse("""
            version: 1
            exclusions:
              - path: tests/**
                reason: 测试夹具假密钥
            """);
        var result = Run(fixture.Path, config);

        Assert.Empty(result.Violations);
    }

    [Fact]
    public void 普通代码_零误报()
    {
        using var fixture = new TempRepoFixture();
        fixture.AddFile("src/App/UserCredentialsService.cs", "public class UserCredentialsService { string sk = \"short\"; }");
        fixture.AddFile("docs/readme.md", "环境变量请参考 api_key_env 模式配置。");

        var result = Run(fixture.Path);

        Assert.Empty(result.Violations);
    }

    [Fact]
    public void 二进制文件_跳过内容扫描()
    {
        using var fixture = new TempRepoFixture();
        File.WriteAllBytes(
            Path.Combine(fixture.Path, "logo.bin"),
            [0x89, 0x50, 0x4E, 0x47, 0x00, 0x01, 0x02, 0x03]);

        var result = Run(fixture.Path);

        Assert.Empty(result.Violations);
    }

    [Fact]
    public void 不限扩展名收集_无扩展名凭据文件也进扫描()
    {
        using var fixture = new TempRepoFixture();
        fixture.AddFile("id_rsa", "PRIVATE");

        var result = Run(fixture.Path);

        var violation = Assert.Single(result.Violations);
        Assert.Equal("id_rsa", violation.File);
    }

    /// <summary>执行 secret 检查。</summary>
    private static CheckResult Run(string repoPath, StructureRuleConfig? config = null)
    {
        var context = new CheckContext(repoPath, CheckMode.All, config ?? StructureRuleConfigLoader.Parse("version: 1"), "guardai.yml");
        return new SecretScanCheck().Run(context);
    }
}
