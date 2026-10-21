using GuardAI.Core.Engine;

namespace GuardAI.Tests;

/// <summary>gitignore 过滤器测试（M2-1：被忽略文件不进检查）。</summary>
public class GitIgnoreFilterTests
{
    [Fact]
    public void 通配模式任意层命中()
    {
        var filter = Parse("*.log", "build/");

        Assert.True(filter.IsIgnored("x.log"));
        Assert.True(filter.IsIgnored("a/b/x.log"));
        Assert.False(filter.IsIgnored("x.cs"));
    }

    [Fact]
    public void 目录模式忽略其下所有文件()
    {
        var filter = Parse("build/");

        Assert.True(filter.IsIgnored("build/a/b.cs"));
        Assert.True(filter.IsIgnored("src/build/c.cs"));
        Assert.False(filter.IsIgnored("mybuild/x.cs"));
        Assert.False(filter.IsIgnored("src/build.cs"));
    }

    [Fact]
    public void 含斜杠模式锚定根目录()
    {
        var filter = Parse("/root.txt", "doc/frotz");

        Assert.True(filter.IsIgnored("root.txt"));
        Assert.False(filter.IsIgnored("a/root.txt"));
        Assert.True(filter.IsIgnored("doc/frotz"));
        Assert.False(filter.IsIgnored("x/doc/frotz"));
    }

    [Fact]
    public void 反取模式最后命中定夺()
    {
        var filter = Parse("*.log", "!keep.log");

        Assert.True(filter.IsIgnored("x.log"));
        Assert.False(filter.IsIgnored("keep.log"));
    }

    [Fact]
    public void 父目录被忽略后无法反取子文件()
    {
        // git 语义：不允许跨过被忽略的父目录反向重新包含
        var filter = Parse("build/", "!build/keep.cs");

        Assert.True(filter.IsIgnored("build/keep.cs"));
    }

    [Fact]
    public void 跨层通配与单字符通配()
    {
        var filter = Parse("**/foo/bar", "a?c.log");

        Assert.True(filter.IsIgnored("x/y/foo/bar"));
        Assert.True(filter.IsIgnored("foo/bar"));
        Assert.True(filter.IsIgnored("abc.log"));
        Assert.False(filter.IsIgnored("ac.log"));
    }

    [Fact]
    public void 注释空行与转义()
    {
        var filter = Parse("# 注释", "", @"\#file.txt", @"\!bang.txt");

        Assert.False(filter.IsIgnored("# 注释"));
        Assert.True(filter.IsIgnored("#file.txt"));
        Assert.True(filter.IsIgnored("!bang.txt"));
    }

    [Fact]
    public void 文件收集器跳过被忽略文件()
    {
        var dir = Path.Combine(Path.GetTempPath(), "guardai-ignore-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, ".gitignore"), ".verify/\n*.g.cs\n");
            Directory.CreateDirectory(Path.Combine(dir, ".verify"));
            File.WriteAllText(Path.Combine(dir, ".verify", "Bad.cs"), "// 被忽略");
            File.WriteAllText(Path.Combine(dir, "Gen.g.cs"), "// 被忽略");
            File.WriteAllText(Path.Combine(dir, "App.cs"), "// 参与检查");

            var files = TargetFileCollector.Collect(dir, CheckMode.All, [".cs"]);

            Assert.Equal(["App.cs"], files);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    /// <summary>按行构造过滤器。</summary>
    private static GitIgnoreFilter Parse(params string[] lines)
    {
        var filter = new GitIgnoreFilter();
        filter.Parse(lines);
        return filter;
    }
}
