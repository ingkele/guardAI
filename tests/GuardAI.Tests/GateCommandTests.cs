using System.Text.Json;
using GuardAI.Cli;
using GuardAI.Core.Config;

namespace GuardAI.Tests;

/// <summary>
/// 写码前前置门测试：路径级 deny、始终注入上下文、失败不 deny、输出协议形状。
/// </summary>
public class GateCommandTests
{
    /// <summary>前置门测试配置：禁用目录 + 落位规则 + 层契约 + 豁免。</summary>
    private const string TestConfig = """
        banned_dirs:
          - utils2
        placement_rules:
          - name: 扩展类必须在 Extensions 目录
            file_pattern: "*Extensions.cs"
            dir_pattern: "**/Extensions/**"
            suggest: 移动到 Extensions/ 目录
            scope: all
        layers:
          - name: Web
            path: "src/Demo.Web/**"
            purpose: 接口层
          - name: Application
            path: "src/Demo.Application/**"
            purpose: 应用层
        allowed_dependencies:
          - from: Web
            to: [Application]
        exclusions:
          - path: "src/Demo.Web/Legacy.cs"
            reason: 历史兼容
        """;

    private static readonly StructureRuleConfig Config = StructureRuleConfigLoader.Parse(TestConfig);

    /// <summary>构造 hook stdin JSON（写入目标绝对路径 + cwd）。</summary>
    private static string Hook(string filePath, string cwd = "/repo") =>
        JsonSerializer.Serialize(new
        {
            tool_name = "Write",
            tool_input = new { file_path = filePath },
            cwd,
            hook_event_name = "PreToolUse",
        });

    [Fact]
    public void 合法路径允许写入并注入层契约()
    {
        var decision = GateCommand.Decide(Hook("/repo/src/Demo.Web/Controllers/OrderController.cs"), Config);

        Assert.False(decision.Deny);
        Assert.Contains("Web 层", decision.Context);
        Assert.Contains("Application", decision.Context);
        Assert.Contains("内容级", decision.Context);
    }

    [Fact]
    public void 禁用目录被Deny()
    {
        var decision = GateCommand.Decide(Hook("/repo/src/utils2/UploadHelper.cs"), Config);

        Assert.True(decision.Deny);
        Assert.Contains("utils2", decision.Reason);
    }

    [Fact]
    public void 落位规则不符被Deny()
    {
        var decision = GateCommand.Decide(Hook("/repo/src/Demo.Web/StringExtensions.cs"), Config);

        Assert.True(decision.Deny);
        Assert.Contains("Extensions", decision.Reason);
    }

    [Fact]
    public void 豁免路径不Deny()
    {
        var decision = GateCommand.Decide(Hook("/repo/src/Demo.Web/Legacy.cs"), Config);

        Assert.False(decision.Deny);
        Assert.Contains("历史兼容", decision.Context);
    }

    [Fact]
    public void 无效JSON不Deny且说明未运行()
    {
        var decision = GateCommand.Decide("not json", Config);

        Assert.False(decision.Deny);
        Assert.Contains("未运行", decision.Context);
    }

    [Fact]
    public void 缺少FilePath不Deny()
    {
        var decision = GateCommand.Decide("{\"tool_name\":\"Write\"}", Config);

        Assert.False(decision.Deny);
        Assert.Contains("未运行", decision.Context);
    }

    [Fact]
    public void 仓库外路径不Deny()
    {
        var decision = GateCommand.Decide(Hook("/other/project/Foo.cs", cwd: "/repo"), Config);

        Assert.False(decision.Deny);
        Assert.Contains("之外", decision.Context);
    }

    [Fact]
    public void 未命中层的路径允许但提示()
    {
        var decision = GateCommand.Decide(Hook("/repo/extras/FeatureFlag.cs"), Config);

        Assert.False(decision.Deny);
        Assert.Contains("未命中", decision.Context);
    }

    [Fact]
    public void Deny输出符合Hook协议形状()
    {
        var json = GateCommand.Render(new GateDecision(true, "原因", "上下文"));

        using var doc = JsonDocument.Parse(json);
        var output = doc.RootElement.GetProperty("hookSpecificOutput");
        Assert.Equal("PreToolUse", output.GetProperty("hookEventName").GetString());
        Assert.Equal("deny", output.GetProperty("permissionDecision").GetString());
        Assert.Equal("原因", output.GetProperty("permissionDecisionReason").GetString());
        Assert.Equal("上下文", output.GetProperty("additionalContext").GetString());
    }

    [Fact]
    public void Allow输出符合Hook协议形状()
    {
        var json = GateCommand.Render(new GateDecision(false, "允许", "ctx"));

        using var doc = JsonDocument.Parse(json);
        var output = doc.RootElement.GetProperty("hookSpecificOutput");
        Assert.Equal("allow", output.GetProperty("permissionDecision").GetString());
        Assert.Equal("ctx", output.GetProperty("additionalContext").GetString());
    }
}
