using GuardAI.Checks.Structure;
using GuardAI.Core.Config;
using GuardAI.Core.Engine;

namespace GuardAI.Tests;

/// <summary>
/// 层契约检查测试：方向判定、实体暴露、豁免边界、多语言与首跑零误报。
/// </summary>
public class LayerContractCheckTests
{
    /// <summary>完整层契约配置（四层 + 测试层，方向明确）。</summary>
    private const string LayerConfig = """
        layers:
          - name: Domain
            path: "src/Demo.Domain/**"
            purpose: 领域层
          - name: Application
            path: "src/Demo.Application/**"
            purpose: 应用层
          - name: Infrastructure
            path: "src/Demo.Infrastructure/**"
            purpose: 基础设施层
          - name: Web
            path: "src/Demo.Web/**"
            purpose: 接口层
          - name: Tests
            path: "tests/**"
            purpose: 测试层
        allowed_dependencies:
          - from: Application
            to: [Domain]
          - from: Infrastructure
            to: [Domain, Application]
          - from: Web
            to: [Application]
          - from: Domain
            to: []
        """;

    private static CheckResult Run(TempRepoFixture fixture, string? yaml = null)
    {
        var config = StructureRuleConfigLoader.Parse(yaml ?? LayerConfig);
        return new LayerContractCheck().Run(new CheckContext(fixture.Path, CheckMode.All, config, "guardai.yml"));
    }

    [Fact]
    public void 允许方向零违规()
    {
        using var fixture = new TempRepoFixture();
        fixture.AddFile("src/Demo.Web/Controllers/OrderController.cs", "using Demo.Application;\n");

        Assert.Empty(Run(fixture).Violations);
    }

    [Fact]
    public void Web直连Infrastructure被逮住()
    {
        using var fixture = new TempRepoFixture();
        fixture.AddFile("src/Demo.Web/Controllers/OrderController.cs",
            "using Demo.Application;\nusing Demo.Infrastructure.Caching;\n");

        var violation = Assert.Single(Run(fixture).Violations);

        Assert.Equal("层依赖方向", violation.Rule);
        Assert.Contains("Web", violation.Message);
        Assert.Contains("Infrastructure", violation.Message);
        Assert.Contains("Demo.Infrastructure.Caching", violation.Message);
        Assert.Contains("收口", violation.Suggestion);
    }

    [Fact]
    public void Application依赖Domain放行()
    {
        using var fixture = new TempRepoFixture();
        fixture.AddFile("src/Demo.Application/Services/OrderService.cs", "using Demo.Domain;\n");

        Assert.Empty(Run(fixture).Violations);
    }

    [Fact]
    public void 未命中层的文件跳过()
    {
        using var fixture = new TempRepoFixture();
        fixture.AddFile("extras/FeatureFlag.cs", "using Demo.Infrastructure;\n");

        Assert.Empty(Run(fixture).Violations);
    }

    [Fact]
    public void 测试代码豁免()
    {
        using var fixture = new TempRepoFixture();
        fixture.AddFile("tests/Demo.Tests/OrderTests.cs", "using Demo.Infrastructure;\n");

        Assert.Empty(Run(fixture).Violations);
    }

    [Fact]
    public void exclusions豁免()
    {
        var yaml = LayerConfig + "\n" + """
            exclusions:
              - path: "src/Demo.Web/Controllers/LegacyController.cs"
                reason: 历史兼容
            """;
        using var fixture = new TempRepoFixture();
        fixture.AddFile("src/Demo.Web/Controllers/LegacyController.cs", "using Demo.Infrastructure;\n");

        Assert.Empty(Run(fixture, yaml).Violations);
    }

    [Fact]
    public void Web层引用实体命名空间被逮住()
    {
        using var fixture = new TempRepoFixture();
        fixture.AddFile("src/Demo.Web/Controllers/OrderController.cs", "using Demo.Domain.Entities;\n");

        var violations = Run(fixture).Violations;

        Assert.Contains(violations, v => v.Rule == "出参禁止暴露实体" && v.Message.Contains("Entities"));
    }

    [Fact]
    public void 关闭实体检查后不再报实体违规()
    {
        var yaml = LayerConfig + "\nforbid_dto_entity: false\n";
        using var fixture = new TempRepoFixture();
        fixture.AddFile("src/Demo.Web/Controllers/OrderController.cs", "using Demo.Domain.Entities;\n");

        var violations = Run(fixture, yaml).Violations;

        Assert.DoesNotContain(violations, v => v.Rule == "出参禁止暴露实体");
    }

    [Fact]
    public void 未配置依赖表时方向检查不启用()
    {
        var yaml = """
            layers:
              - name: Web
                path: "src/Demo.Web/**"
                purpose: 接口层
            """;
        using var fixture = new TempRepoFixture();
        fixture.AddFile("src/Demo.Web/Controllers/X.cs", "using Demo.Infrastructure;\n");

        Assert.Empty(Run(fixture, yaml).Violations);
    }

    [Fact]
    public void 同层与外部包不报违规()
    {
        using var fixture = new TempRepoFixture();
        fixture.AddFile("src/Demo.Web/Controllers/OrderController.cs",
            "using Demo.Web.Filters;\nusing Newtonsoft.Json;\n");

        Assert.Empty(Run(fixture).Violations);
    }

    [Theory]
    [InlineData("src/Demo.Web/Api/Order.ts", "import { X } from '@app/Demo.Infrastructure';")]
    [InlineData("src/Demo.Web/Api/order.go", "package api\n\nimport \"github.com/demo/Demo.Infrastructure\"")]
    [InlineData("src/Demo.Web/Api/order.py", "from Demo.Infrastructure import X")]
    [InlineData("src/Demo.Web/Api/Order.java", "import Demo.Infrastructure.Persistence.X;")]
    [InlineData("src/Demo.Web/Api/OrderController.cs", "using Demo.Infrastructure;")]
    public void 五语言跨层引用都能逮住(string path, string code)
    {
        using var fixture = new TempRepoFixture();
        fixture.AddFile(path, code);

        Assert.Contains(Run(fixture).Violations, v => v.Rule == "层依赖方向");
    }

    [Fact]
    public void 扫描计数覆盖参与文件()
    {
        using var fixture = new TempRepoFixture();
        fixture.AddFile("src/Demo.Web/Api/OrderController.cs", "using Demo.Application;\n");
        fixture.AddFile("extras/notes.txt", "忽略的扩展名\n");

        Assert.Equal(1, Run(fixture).Scanned);
    }
}
