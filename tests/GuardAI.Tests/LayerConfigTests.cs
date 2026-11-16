using GuardAI.Core.Config;

namespace GuardAI.Tests;

/// <summary>
/// 层契约配置解析测试：合法解析、缺省兼容、配置错误 fail-closed。
/// </summary>
public class LayerConfigTests
{
    [Fact]
    public void 合法层契约配置解析()
    {
        var config = StructureRuleConfigLoader.Parse("""
            layers:
              - name: Domain
                path: "src/Demo.Domain/**"
                purpose: 领域层
              - name: Web
                path: "src/Demo.Web/**"
                purpose: 接口层
            allowed_dependencies:
              - from: Web
                to: [Domain]
            forbid_dto_entity: true
            """);

        Assert.Equal(2, config.Layers.Count);
        Assert.Equal("Domain", config.Layers[0].Name);
        Assert.Equal("src/Demo.Domain/**", config.Layers[0].Path);
        Assert.Equal("领域层", config.Layers[0].Purpose);
        Assert.Single(config.AllowedDependencies);
        Assert.Equal("Web", config.AllowedDependencies[0].From);
        Assert.Equal(new[] { "Domain" }, config.AllowedDependencies[0].To);
        Assert.True(config.ForbidDtoEntity);
    }

    [Fact]
    public void 缺省配置兼容旧仓库()
    {
        var config = StructureRuleConfigLoader.Parse("version: 1\n");

        Assert.Empty(config.Layers);
        Assert.Empty(config.AllowedDependencies);
        Assert.True(config.ForbidDtoEntity);
    }

    [Fact]
    public void 重复层名报配置错误()
    {
        Assert.Throws<FormatException>(() => StructureRuleConfigLoader.Parse("""
            layers:
              - name: Domain
                path: "src/A/**"
              - name: domain
                path: "src/B/**"
            """));
    }

    [Fact]
    public void 依赖来源引用未定义层报配置错误()
    {
        Assert.Throws<FormatException>(() => StructureRuleConfigLoader.Parse("""
            layers:
              - name: Domain
                path: "src/A/**"
            allowed_dependencies:
              - from: Web
                to: [Domain]
            """));
    }

    [Fact]
    public void 依赖目标引用未定义层报配置错误()
    {
        Assert.Throws<FormatException>(() => StructureRuleConfigLoader.Parse("""
            layers:
              - name: Web
                path: "src/W/**"
            allowed_dependencies:
              - from: Web
                to: [Infrastructure]
            """));
    }

    [Fact]
    public void 空依赖列表正确解析()
    {
        var config = StructureRuleConfigLoader.Parse("""
            layers:
              - name: Domain
                path: "src/D/**"
            allowed_dependencies:
              - from: Domain
                to: []
            """);

        Assert.Single(config.AllowedDependencies);
        Assert.Empty(config.AllowedDependencies[0].To);
    }

    [Fact]
    public void ForbidDtoEntity可显式关闭()
    {
        var config = StructureRuleConfigLoader.Parse("forbid_dto_entity: false\n");

        Assert.False(config.ForbidDtoEntity);
    }

    [Fact]
    public void 层配置结构非法报配置错误()
    {
        Assert.Throws<FormatException>(() => StructureRuleConfigLoader.Parse("layers: oops\n"));
    }
}
