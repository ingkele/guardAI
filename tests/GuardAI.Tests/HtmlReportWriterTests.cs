using GuardAI.Core.Engine;
using GuardAI.Core.Ledging;
using GuardAI.Core.Reporting;

namespace GuardAI.Tests;

/// <summary>HTML 报告输出器测试（M4：单页体检报告与运行对比）。</summary>
public class HtmlReportWriterTests
{
    [Fact]
    public void 违规明细与修复指令都进报告()
    {
        var writer = new StringWriter();
        HtmlReportWriter.Write(writer, "/repo/Demo", CheckMode.All, "guardai.yml",
            [new CheckResult("structure", 3,
            [
                new Violation("extras/Foo.cs", "目录白名单", "不在白名单内", "移动到 src/"),
            ])]);

        var html = writer.ToString();
        Assert.Contains("<!DOCTYPE html>", html);
        Assert.Contains("extras/Foo.cs", html);
        Assert.Contains("目录白名单", html);
        Assert.Contains("给 AI 的修复指令", html);
        Assert.Contains("移动到 src/", html);
    }

    [Fact]
    public void 零违规显示空状态()
    {
        var writer = new StringWriter();
        HtmlReportWriter.Write(writer, "/repo/Demo", CheckMode.All, "guardai.yml",
            [new CheckResult("structure", 6, [])]);

        var html = writer.ToString();
        Assert.Contains("未发现违规", html);
        Assert.Contains(">通过<", html);
        Assert.DoesNotContain("给 AI 的修复指令", html);
    }

    [Fact]
    public void 首次运行无对比数据()
    {
        var writer = new StringWriter();
        HtmlReportWriter.Write(writer, "/repo/Demo", CheckMode.All, "guardai.yml",
            [new CheckResult("structure", 1, [new Violation("a.cs", "r", "m", "s")])],
            []);

        Assert.Contains("首次运行", writer.ToString());
    }

    [Fact]
    public void 对比上次运行分出新增已修复遗留()
    {
        // 上次报 a.cs / b.cs，本次报 a.cs / c.cs → 新增 c.cs、已修复 b.cs、遗留 1
        var prev = new LedgerRun("20261020-000001", DateTimeOffset.Now.AddMinutes(-10), "cli", "all", 3, 2, "fail",
            [
                new LedgerViolation("structure", "a.cs", "r", "m", "s", Fingerprint.Compute("r", "a.cs", "m")),
                new LedgerViolation("structure", "b.cs", "r2", "m2", "s2", Fingerprint.Compute("r2", "b.cs", "m2")),
            ]);

        var writer = new StringWriter();
        HtmlReportWriter.Write(writer, "/repo/Demo", CheckMode.All, "guardai.yml",
            [new CheckResult("structure", 3,
            [
                new Violation("a.cs", "r", "m", "s"),
                new Violation("c.cs", "r3", "m3", "s3"),
            ])],
            [prev]);

        var html = writer.ToString();
        Assert.Contains("+1", html);  // 新增
        Assert.Contains("-1", html);  // 已修复
        Assert.Contains("c.cs", html); // 新增明细
        Assert.Contains("b.cs", html); // 已修复明细
        Assert.Contains("遗留未修", html);
    }

    [Fact]
    public void 空扫描带警告横幅且结论不写通过()
    {
        var writer = new StringWriter();
        HtmlReportWriter.Write(writer, "/repo/Demo", CheckMode.All, "guardai.yml",
            [new CheckResult("structure", 0, [])]);

        var html = writer.ToString();
        Assert.Contains("空扫描警告", html);
        Assert.DoesNotContain(">通过<", html);
    }

    [Fact]
    public void 违规内容做html编码防破版()
    {
        var writer = new StringWriter();
        HtmlReportWriter.Write(writer, "/repo/Demo", CheckMode.All, "guardai.yml",
            [new CheckResult("structure", 1,
                [new Violation("a<b>.cs", "r", "说明 <script>", "建议 & 说明")])]);

        var html = writer.ToString();
        Assert.DoesNotContain("<script>", html);
        Assert.Contains("&lt;script&gt;", html);
        Assert.Contains("a&lt;b&gt;.cs", html);
    }
}
