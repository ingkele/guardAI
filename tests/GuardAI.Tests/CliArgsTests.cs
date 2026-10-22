using GuardAI.Cli;
using Xunit;

namespace GuardAI.Tests;

/// <summary>
/// CLI 参数解析与帮助拦截测试——锁"未知参数绝不静默执行命令"的防线（防 --help 误触真跑命令复发）。
/// </summary>
public class CliArgsTests
{
    /// <summary>子命令 -h/--help 一律标记帮助请求，不产出任何选项。</summary>
    [Theory]
    [InlineData("--help")]
    [InlineData("-h")]
    public void 帮助记号_标记帮助请求且不产选项(string flag)
    {
        var result = CliArgs.Parse("review", new[] { flag, "--repo", "D:\\x" });

        Assert.True(result.HelpRequested);
        Assert.Null(result.Error);
        Assert.Empty(result.Options);
    }

    /// <summary>未知参数直接报错，绝不静默吞掉后执行命令。</summary>
    [Fact]
    public void 未知参数_报错不静默执行()
    {
        var result = CliArgs.Parse("check", new[] { "--hlep", "x" });

        Assert.NotNull(result.Error);
        Assert.Contains("未知参数", result.Error);
        Assert.False(result.HelpRequested);
    }

    /// <summary>带值参数后面没跟值时报错（拼写错误当场揪出）。</summary>
    [Fact]
    public void 带值参数缺值_报错()
    {
        var result = CliArgs.Parse("check", new[] { "--repo" });

        Assert.NotNull(result.Error);
        Assert.Contains("缺少值", result.Error);
    }

    /// <summary>带值参数的下一个记号是另一个参数时视为缺值。</summary>
    [Fact]
    public void 带值参数后紧跟参数_视为缺值()
    {
        var result = CliArgs.Parse("check", new[] { "--repo", "--json" });

        Assert.NotNull(result.Error);
        Assert.Contains("缺少值", result.Error);
    }

    /// <summary>正常参数解析：带值与开关各归其位。</summary>
    [Fact]
    public void 合法参数_解析成功()
    {
        var result = CliArgs.Parse("check", new[] { "--repo", "D:\\x", "--mode", "all", "--json", "--strict" });

        Assert.Null(result.Error);
        Assert.False(result.HelpRequested);
        Assert.Equal("D:\\x", result.Options["repo"]);
        Assert.Equal("all", result.Options["mode"]);
        Assert.True(result.Options.ContainsKey("json"));
        Assert.True(result.Options.ContainsKey("strict"));
    }

    /// <summary>开关参数不吞掉后面的记号（旧解析器会把 --repo 的值吞成开关值）。</summary>
    [Fact]
    public void 开关参数_不吞后续值()
    {
        var result = CliArgs.Parse("check", new[] { "--json", "--repo", "D:\\x" });

        Assert.Null(result.Error);
        Assert.True(result.Options.ContainsKey("json"));
        Assert.Equal("D:\\x", result.Options["repo"]);
    }

    /// <summary>search 的位置参数即关键词，走显式传参。</summary>
    [Fact]
    public void Search_位置参数作关键词()
    {
        var result = CliArgs.Parse("search", new[] { "上传", "--repo", "D:\\x", "--top", "5" });

        Assert.Null(result.Error);
        Assert.Equal("上传", result.Positional);
        Assert.Equal("5", result.Options["top"]);
    }

    /// <summary>search 只接受一个关键词，多给直接报错。</summary>
    [Fact]
    public void Search_多个位置参数_报错()
    {
        var result = CliArgs.Parse("search", new[] { "上传", "下载" });

        Assert.NotNull(result.Error);
        Assert.Contains("只接受一个关键词", result.Error);
    }

    /// <summary>非 search 命令出现位置参数即报错（防参数拼错被吞）。</summary>
    [Fact]
    public void 非Search命令_位置参数_报错()
    {
        var result = CliArgs.Parse("check", new[] { "多余" });

        Assert.NotNull(result.Error);
        Assert.Contains("无法识别的参数", result.Error);
    }

    /// <summary>未知命令报错并列出可用命令。</summary>
    [Fact]
    public void 未知命令_报错()
    {
        var result = CliArgs.Parse("frobnicate", Array.Empty<string>());

        Assert.NotNull(result.Error);
        Assert.Contains("未知命令", result.Error);
    }

    /// <summary>子命令帮助渲染包含该命令用法与示例（文案单一来源）。</summary>
    [Fact]
    public void 子命令帮助_含用法与示例()
    {
        var help = CliHelp.Build("review");

        Assert.Contains("guardAI review", help);
        Assert.Contains("--fail-on", help);
        Assert.Contains("示例", help);
    }

    /// <summary>全局帮助包含全部九命令（文案单一来源不漏命令）。</summary>
    [Fact]
    public void 全局帮助_含全部命令()
    {
        var help = CliHelp.Build(null);

        foreach (var command in new[] { "check", "review", "init", "learn", "doctor", "ui", "mcp", "index", "search" })
        {
            Assert.Contains(command, help);
        }

        Assert.Contains("台账", help);
    }

    /// <summary>入口级联动：review --help 只打帮助返回 0，绝不触发真审查。</summary>
    /// <returns>异步任务。</returns>
    [Fact]
    public async Task 入口_子命令Help_不执行命令只打帮助()
    {
        var code = await Program.Main(new[] { "review", "--help" });

        Assert.Equal(0, code);
    }

    /// <summary>入口级联动：未知参数以退出码 2 结束。</summary>
    /// <returns>异步任务。</returns>
    [Fact]
    public async Task 入口_未知参数_退出码2()
    {
        var code = await Program.Main(new[] { "check", "--typo", "x" });

        Assert.Equal(2, code);
    }
}
