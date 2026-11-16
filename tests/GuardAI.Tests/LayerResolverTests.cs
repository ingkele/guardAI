using GuardAI.Core.Config;
using GuardAI.Core.Engine;

namespace GuardAI.Tests;

/// <summary>
/// 层归属判定测试：顺序优先、未命中放行、glob 与大小写口径。
/// </summary>
public class LayerResolverTests
{
    /// <summary>标准四层配置（顺序即优先级）。</summary>
    private static LayerDef[] StandardLayers() =>
    [
        new("Domain", "src/Demo.Domain/**", "领域层"),
        new("Application", "src/Demo.Application/**", "应用层"),
        new("Infrastructure", "src/Demo.Infrastructure/**", "基础设施层"),
        new("Web", "src/Demo.Web/**", "接口层"),
        new("Tests", "tests/**", "测试"),
    ];

    [Fact]
    public void 顺序命中优先()
    {
        LayerDef[] layers =
        [
            new("First", "src/**", "宽泛规则在前"),
            new("Second", "src/Demo.Web/**", "精确规则在后"),
        ];

        var layer = LayerResolver.Resolve("src/Demo.Web/Controllers/OrderController.cs", layers);

        Assert.Equal("First", layer?.Name);
    }

    [Fact]
    public void 未命中任何层返回空()
    {
        var layer = LayerResolver.Resolve("extras/FeatureFlag.cs", StandardLayers());

        Assert.Null(layer);
    }

    [Fact]
    public void 双星号跨目录命中()
    {
        var layer = LayerResolver.Resolve("src/Demo.Web/Controllers/Api/OrderController.cs", StandardLayers());

        Assert.Equal("Web", layer?.Name);
    }

    [Fact]
    public void 路径大小写不敏感()
    {
        var layer = LayerResolver.Resolve("SRC/DEMO.WEB/Controllers/OrderController.cs", StandardLayers());

        Assert.Equal("Web", layer?.Name);
    }

    [Fact]
    public void 多项目路径各自命中()
    {
        var layers = StandardLayers();

        Assert.Equal("Application", LayerResolver.Resolve("src/Demo.Application/Services/OrderService.cs", layers)?.Name);
        Assert.Equal("Infrastructure", LayerResolver.Resolve("src/Demo.Infrastructure/Persistence/OrderRepository.cs", layers)?.Name);
        Assert.Equal("Tests", LayerResolver.Resolve("tests/Demo.Tests/OrderTests.cs", layers)?.Name);
    }

    [Fact]
    public void 空层配置返回空()
    {
        Assert.Null(LayerResolver.Resolve("src/Demo.Web/X.cs", []));
    }
}
