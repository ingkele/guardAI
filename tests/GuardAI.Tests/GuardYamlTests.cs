using GuardAI.Core.Config;

namespace GuardAI.Tests;

/// <summary>极简 YAML 解析器测试（覆盖规则配置用到的全部语法子集）。</summary>
public class GuardYamlTests
{
    private const string Sample = """
        # 整行注释
        version: 1
        name: demo   # 行尾注释
        enabled: true
        count: 3
        quoted: "含空格的值"
        nested:
          key: value
          deeper:
            - alpha
            - beta
        items:
          - path: src/*/Domain/**
            purpose: 领域层
          - path: tests/**
            purpose: 测试
        scalars:
          - one
          - two
        """;

    [Fact]
    public void 解析标量嵌套列表与注释()
    {
        var root = GuardYaml.Parse(Sample);

        Assert.Equal(1, root["version"]);
        Assert.Equal("demo", root["name"]);
        Assert.Equal(true, root["enabled"]);
        Assert.Equal(3, root["count"]);
        Assert.Equal("含空格的值", root["quoted"]);

        var nested = Assert.IsType<Dictionary<string, object?>>(root["nested"]);
        Assert.Equal("value", nested["key"]);
        var deeper = Assert.IsType<List<object?>>(nested["deeper"]);
        Assert.Equal(new object?[] { "alpha", "beta" }, deeper);

        var items = Assert.IsType<List<object?>>(root["items"]);
        Assert.Equal(2, items.Count);
        var first = Assert.IsType<Dictionary<string, object?>>(items[0]);
        Assert.Equal("src/*/Domain/**", first["path"]);
        Assert.Equal("领域层", first["purpose"]);

        var scalars = Assert.IsType<List<object?>>(root["scalars"]);
        Assert.Equal(new object?[] { "one", "two" }, scalars);
    }

    [Fact]
    public void 解析流式集合空列表空映射与标量列表()
    {
        // v0.3.1-⑥：placement_rules: [] 这类流式空集合曾报"应为列表"
        var root = GuardYaml.Parse(
            """
            placement_rules: []
            overrides: {}
            flow_list: [a, b, "含, 逗号", 3, true]
            nested:
              empty: []
            """);

        Assert.Empty(Assert.IsType<List<object?>>(root["placement_rules"]));
        Assert.Empty(Assert.IsType<Dictionary<string, object?>>(root["overrides"]));
        Assert.Equal(new object?[] { "a", "b", "含, 逗号", 3, true }, Assert.IsType<List<object?>>(root["flow_list"]));

        var nested = Assert.IsType<Dictionary<string, object?>>(root["nested"]);
        Assert.Empty(Assert.IsType<List<object?>>(nested["empty"]));
    }

    [Fact]
    public void 非法语法抛出格式异常并指出行号()
    {
        var ex = Assert.Throws<FormatException>(() => GuardYaml.Parse("key: a\n这不是键值对"));
        Assert.Contains("第 2 行", ex.Message);
    }
}
