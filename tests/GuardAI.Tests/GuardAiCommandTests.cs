using GuardAI.Init;
using Xunit;

namespace GuardAI.Tests;

/// <summary>
/// 闸口调用命令解析测试——锁"钩子不依赖 PATH、占位符必被渲染"的防线（防裸调 guardAI 复发）。
/// </summary>
public class GuardAiCommandTests
{
    /// <summary>解析出的调用命令必须非空、不含占位符。</summary>
    [Fact]
    public void Resolve_非空且不含占位符()
    {
        var command = GuardAiCommand.Resolve();

        Assert.False(string.IsNullOrWhiteSpace(command));
        Assert.DoesNotContain("{{", command);
    }

    /// <summary>渲染把占位符换成实际命令，检查行原样保留。</summary>
    [Fact]
    public void Render_占位符被替换且检查行保留()
    {
        var rendered = GuardAiCommand.Render("{{GUARDAI_CMD}} check --repo . --mode changed --trigger hook");

        Assert.DoesNotContain(GuardAiCommand.Placeholder, rendered);
        Assert.Contains("check --repo . --mode changed --trigger hook", rendered);
    }

    /// <summary>安装出的 pre-commit 钩子不含占位符（可直接执行）。</summary>
    [Fact]
    public void ApplyHook_安装的钩子不含占位符()
    {
        // 装一个最小模板目录走真实安装链路
        var root = Path.Combine(Path.GetTempPath(), "guardai-hook-" + Guid.NewGuid().ToString("N"));
        var templates = Path.Combine(root, "templates");
        var repo = Path.Combine(root, "repo");
        Directory.CreateDirectory(Path.Combine(templates, "hooks"));
        Directory.CreateDirectory(Path.Combine(repo, ".git", "hooks"));
        File.WriteAllText(
            Path.Combine(templates, "hooks", "pre-commit"),
            "#!/bin/sh\n{{GUARDAI_CMD}} check --repo . --mode changed --trigger hook\nexit $?\n");

        try
        {
            var installer = new GateInstaller(templates);
            var results = installer.Apply(
                new[]
                {
                    new GateInstallAction(
                        ".git/hooks/pre-commit",
                        "git 提交闸口（pre-commit）",
                        "hooks/pre-commit",
                        InstallStrategy.InstallHook,
                        "guardAI"),
                },
                repo,
                force: false);

            Assert.True(results[0].Written);
            var hook = File.ReadAllText(Path.Combine(repo, ".git", "hooks", "pre-commit"));
            Assert.DoesNotContain(GuardAiCommand.Placeholder, hook);
            Assert.Contains("check --repo .", hook);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
