using GuardAI.Cli;
using Xunit;

namespace GuardAI.Tests;

/// <summary>
/// review 收集模式解析测试（Issue #3）：--base 不得被静默忽略。
/// </summary>
/// <remarks>
/// 原行为：只说 --base 时 mode 默认 changed，基线被静默丢弃，报「没有可审查的改动。先改代码」
/// ——用户明明指定了基线却被指向错误方向。本组测试锁住「自动推导 + 冲突报错」两条路。
/// </remarks>
public class ReviewModeResolveTests
{
    /// <summary>只给 --base：自动推导 base 模式并给出提示。</summary>
    [Fact]
    public void 仅给base_推导为base模式且提示()
    {
        var mode = Program.ResolveReviewMode(Options(baseRef: "origin/main"), out var hint);

        Assert.Equal("base", mode);
        Assert.NotNull(hint);
        Assert.Contains("mode=base", hint);
    }

    /// <summary>不给任何参数：维持 changed 默认，不提示。</summary>
    [Fact]
    public void 无参数_默认changed不提示()
    {
        var mode = Program.ResolveReviewMode(Options(), out var hint);

        Assert.Equal("changed", mode);
        Assert.Null(hint);
    }

    /// <summary>显式 --mode base + --base：正常通过，不需要推导提示。</summary>
    [Fact]
    public void 显式base模式_不重复提示()
    {
        var mode = Program.ResolveReviewMode(Options(mode: "base", baseRef: "origin/main"), out var hint);

        Assert.Equal("base", mode);
        Assert.Null(hint);
    }

    /// <summary>显式非 base 模式又带 --base：直接报错，绝不静默丢弃基线。</summary>
    [Fact]
    public void base与changed冲突_报错不静默()
    {
        var ex = Assert.Throws<FormatException>(
            () => Program.ResolveReviewMode(Options(mode: "changed", baseRef: "origin/main"), out _));

        Assert.Contains("--base 仅用于 --mode base", ex.Message);
    }

    /// <summary>显式 --mode staged + --base 同样报错。</summary>
    [Fact]
    public void base与staged冲突_报错()
    {
        var ex = Assert.Throws<FormatException>(
            () => Program.ResolveReviewMode(Options(mode: "staged", baseRef: "HEAD~1"), out _));

        Assert.Contains("--base 仅用于 --mode base", ex.Message);
    }

    /// <summary>空白 --base 视同未给：维持默认 changed。</summary>
    [Fact]
    public void 空白base_视同未给()
    {
        var mode = Program.ResolveReviewMode(Options(baseRef: "   "), out var hint);

        Assert.Equal("changed", mode);
        Assert.Null(hint);
    }

    /// <summary>构造选项字典。</summary>
    /// <param name="mode">--mode 值。</param>
    /// <param name="baseRef">--base 值。</param>
    /// <returns>选项字典。</returns>
    private static Dictionary<string, string?> Options(string? mode = null, string? baseRef = null)
    {
        var options = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        if (mode is not null)
        {
            options["mode"] = mode;
        }

        if (baseRef is not null)
        {
            options["base"] = baseRef;
        }

        return options;
    }
}