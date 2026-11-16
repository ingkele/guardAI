using System.Text.Json.Nodes;
using GuardAI.Init;

namespace GuardAI.Tests;

/// <summary>
/// 闸口安装测试：AI hook 的 PreToolUse + PostToolUse 双段合并、幂等、命令占位符渲染。
/// </summary>
public class GateInstallerTests : IDisposable
{
    private readonly string _repo = Directory.CreateTempSubdirectory("guardai-installer-").FullName;
    private readonly GateInstaller _installer = new(TemplateProvider.Resolve(null));

    public void Dispose()
    {
        Directory.Delete(_repo, recursive: true);
    }

    private GateInstallAction ClaudeAction => new(
        ".claude/settings.json", "d", "hooks/claude-code.json", InstallStrategy.MergeJson, "guardAI");

    [Fact]
    public void 首次安装写入前置门与后置门双段()
    {
        var result = Apply();

        Assert.True(result.Written);
        var hooks = ReadHooks();
        Assert.NotNull(hooks["PreToolUse"]);
        Assert.NotNull(hooks["PostToolUse"]);
        Assert.Contains("gate --stdin", hooks["PreToolUse"]!.ToJsonString());
        Assert.Contains("check --repo", hooks["PostToolUse"]!.ToJsonString());
    }

    [Fact]
    public void 重复安装幂等不重复追加()
    {
        Apply();
        var second = Apply();

        Assert.False(second.Written);
        var hooks = ReadHooks();
        Assert.Single(hooks["PreToolUse"]!.AsArray());
        Assert.Single(hooks["PostToolUse"]!.AsArray());
    }

    [Fact]
    public void 合并时保留用户原有Hooks()
    {
        var target = Path.Combine(_repo, ".claude", "settings.json");
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        File.WriteAllText(target, """
            {
              "hooks": {
                "PostToolUse": [
                  { "matcher": "Bash", "hooks": [ { "type": "command", "command": "echo my-own-hook" } ] }
                ],
                "Notification": [ { "hooks": [ { "type": "command", "command": "notify-me" } ] } ]
              },
              "permissions": { "allow": ["Read"] }
            }
            """);

        Apply();

        var root = JsonNode.Parse(File.ReadAllText(target))!;
        Assert.Equal("Read", root["permissions"]!["allow"]![0]!.GetValue<string>());
        Assert.Contains("my-own-hook", root["hooks"]!["PostToolUse"]!.ToJsonString());
        Assert.Contains("check --repo", root["hooks"]!["PostToolUse"]!.ToJsonString());
        Assert.NotNull(root["hooks"]!["Notification"]);
        Assert.Contains("gate --stdin", root["hooks"]!["PreToolUse"]!.ToJsonString());
    }

    [Fact]
    public void 旧版只有后置门的配置升级时补装前置门()
    {
        var target = Path.Combine(_repo, ".claude", "settings.json");
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        File.WriteAllText(target, """
            {
              "hooks": {
                "PostToolUse": [
                  { "matcher": "Write|Edit", "hooks": [ { "type": "command", "command": "guardAI check --repo . --mode changed --trigger hook" } ] }
                ]
              }
            }
            """);

        var result = Apply();

        Assert.True(result.Written);
        var hooks = ReadHooks();
        Assert.Single(hooks["PostToolUse"]!.AsArray()); // 后置门不重复追加
        Assert.Contains("gate --stdin", hooks["PreToolUse"]!.ToJsonString()); // 补装前置门
    }

    [Fact]
    public void 命令占位符渲染为实际调用命令不依赖PATH()
    {
        Apply();

        var text = File.ReadAllText(Path.Combine(_repo, ".claude", "settings.json"));
        Assert.DoesNotContain(GuardAiCommand.Placeholder, text);
        Assert.Contains("gate --stdin", text);
    }

    [Fact]
    public void Codex模板含前置门与后置门两块()
    {
        var content = TemplateProvider.ReadEmbedded("hooks/codex.toml")!;

        Assert.Contains("event = \"PreToolUse\"", content);
        Assert.Contains("gate --stdin", content);
        Assert.Contains("event = \"PostToolUse\"", content);
        Assert.Contains("on_failure = \"block\"", content);
    }

    /// <summary>执行一次 Claude 配置合并安装。</summary>
    private GateInstallResult Apply() => _installer.Apply([ClaudeAction], _repo, force: false).Single();

    /// <summary>读取已安装的 hooks 节点。</summary>
    private JsonObject ReadHooks() => JsonNode.Parse(
        File.ReadAllText(Path.Combine(_repo, ".claude", "settings.json")))!["hooks"]!.AsObject();
}
