using GuardAI.Checks.Code;
using GuardAI.Core.Config;
using GuardAI.Core.Engine;

namespace GuardAI.Tests;

/// <summary>
/// doc 检查测试（公共 API 文档注释确定性检查）。重点：
/// ①公共类型/公共方法缺 /// 摘要报违规、有则不报；
/// ②/// &lt;inheritdoc /&gt; 算有、仅 // 行注释不算；
/// ③非公共成员、partial 拆分件、接口分号成员不查（只漏不误）；
/// ④测试代码豁免与豁免逃生口零误报。
/// </summary>
public class DocCheckTests
{
    [Fact]
    public void 公共类型缺文档注释_报违规含类型名与行号()
    {
        using var fixture = new TempRepoFixture();
        fixture.AddFile("src/App/Order.cs", "namespace Demo;\n\npublic class Order\n{\n}\n");

        var result = Run(fixture.Path);

        var violation = Assert.Single(result.Violations);
        Assert.Equal("缺少文档注释", violation.Rule);
        Assert.Contains("Order", violation.Message);
        Assert.Contains("第 3 行", violation.Message);
    }

    [Fact]
    public void 公共类型有摘要_不报()
    {
        using var fixture = new TempRepoFixture();
        fixture.AddFile("src/App/Order.cs", """
            namespace Demo;

            /// <summary>
            /// 订单聚合根。
            /// </summary>
            public class Order
            {
            }
            """);

        var result = Run(fixture.Path);

        Assert.Empty(result.Violations);
    }

    [Fact]
    public void 纯inheritdoc注释块_算有文档不报()
    {
        using var fixture = new TempRepoFixture();
        fixture.AddFile("src/App/Order.cs", """
            namespace Demo;

            /// <inheritdoc />
            public class Order : OrderBase
            {
            }
            """);

        var result = Run(fixture.Path);

        Assert.Empty(result.Violations);
    }

    [Fact]
    public void 公共方法缺文档注释_报违规含方法全名()
    {
        using var fixture = new TempRepoFixture();
        fixture.AddFile("src/App/OrderService.cs", """
            namespace Demo;

            /// <summary>
            /// 订单服务。
            /// </summary>
            public class OrderService
            {
                public void Place(int id)
                {
                }
            }
            """);

        var result = Run(fixture.Path);

        var violation = Assert.Single(result.Violations);
        Assert.Equal("缺少文档注释", violation.Rule);
        Assert.Contains("OrderService.Place", violation.Message);
    }

    [Fact]
    public void 方法仅行注释_不算文档报违规()
    {
        using var fixture = new TempRepoFixture();
        fixture.AddFile("src/App/OrderService.cs", """
            namespace Demo;

            /// <summary>
            /// 订单服务。
            /// </summary>
            public class OrderService
            {
                // 下单
                public void Place(int id)
                {
                }
            }
            """);

        var result = Run(fixture.Path);

        var violation = Assert.Single(result.Violations);
        Assert.Contains("仅 // 行注释不算", violation.Message);
    }

    [Fact]
    public void 方法有摘要或inheritdoc_不报()
    {
        using var fixture = new TempRepoFixture();
        fixture.AddFile("src/App/OrderService.cs", """
            namespace Demo;

            /// <summary>
            /// 订单服务。
            /// </summary>
            public class OrderService
            {
                /// <summary>
                /// 下单。
                /// </summary>
                public void Place(int id)
                {
                }

                /// <inheritdoc />
                public override string ToString() => "x";
            }
            """);

        var result = Run(fixture.Path);

        Assert.Empty(result.Violations);
    }

    [Fact]
    public void 非公共类型与方法_零误报()
    {
        using var fixture = new TempRepoFixture();
        fixture.AddFile("src/App/Helper.cs", """
            namespace Demo;

            internal class Helper
            {
                private void Run()
                {
                }
            }
            """);

        var result = Run(fixture.Path);

        Assert.Empty(result.Violations);
    }

    [Fact]
    public void partial拆分件_整体跳过()
    {
        using var fixture = new TempRepoFixture();
        fixture.AddFile("src/App/Foo.cs", "namespace Demo;\n\npublic partial class Foo\n{\n}\n");

        var result = Run(fixture.Path);

        Assert.Empty(result.Violations);
    }

    [Fact]
    public void 接口分号成员_不查()
    {
        using var fixture = new TempRepoFixture();
        fixture.AddFile("src/App/IFacade.cs", """
            namespace Demo;

            /// <summary>
            /// 门面接口。
            /// </summary>
            public interface IFacade
            {
                void Run();
            }
            """);

        var result = Run(fixture.Path);

        Assert.Empty(result.Violations);
    }

    [Fact]
    public void 测试代码_豁免()
    {
        using var fixture = new TempRepoFixture();
        fixture.AddFile("tests/App/Foo.cs", "namespace Demo;\n\npublic class Foo\n{\n}\n");
        fixture.AddFile("src/App/BarTests.cs", "namespace Demo;\n\npublic class Bar\n{\n}\n");

        var result = Run(fixture.Path);

        Assert.Empty(result.Violations);
    }

    [Fact]
    public void 豁免规则命中_整体跳过()
    {
        using var fixture = new TempRepoFixture();
        fixture.AddFile("src/App/Legacy.cs", "namespace Demo;\n\npublic class Legacy\n{\n}\n");

        var config = StructureRuleConfigLoader.Parse("""
            version: 1
            exclusions:
              - path: src/App/Legacy.cs
                reason: 遗留代码待补注释
            """);
        var result = Run(fixture.Path, config);

        Assert.Empty(result.Violations);
    }

    [Fact]
    public void 合规代码_零误报()
    {
        using var fixture = new TempRepoFixture();
        fixture.AddFile("src/App/Order.cs", """
            namespace Demo;

            /// <summary>
            /// 订单聚合根。
            /// </summary>
            /// <param name="id">订单号。</param>
            public class Order(int id)
            {
                /// <summary>
                /// 订单号。
                /// </summary>
                public int Id { get; } = id;
            }
            """);

        var result = Run(fixture.Path);

        Assert.Empty(result.Violations);
    }

    /// <summary>执行 doc 检查。</summary>
    private static CheckResult Run(string repoPath, StructureRuleConfig? config = null)
    {
        var context = new CheckContext(repoPath, CheckMode.All, config ?? StructureRuleConfigLoader.Parse("version: 1"), "guardai.yml");
        return new DocCheck().Run(context);
    }
}
