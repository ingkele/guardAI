using GuardAI.Core.Engine;
using GuardAI.Core.Reporting;

namespace GuardAI.Tests;

/// <summary>报告输出器测试（F1 假绿防线：空扫描不得报"通过"）。</summary>
public class ReportWriterTests
{
    [Fact]
    public void all模式空扫描不报通过并给出警告()
    {
        var writer = new StringWriter();
        ReportWriter.WriteText(writer, "/repo", CheckMode.All, "guardai.yml",
            [new CheckResult("structure", 0, [])]);

        var text = writer.ToString();
        Assert.Contains("空扫描警告", text);
        Assert.Contains("不构成合规结论", text);
        Assert.DoesNotContain("结果: 通过", text);
    }

    [Fact]
    public void all模式有文件时正常报告通过()
    {
        var writer = new StringWriter();
        ReportWriter.WriteText(writer, "/repo", CheckMode.All, "guardai.yml",
            [new CheckResult("structure", 6, [])]);

        var text = writer.ToString();
        Assert.Contains("结果: 通过 - 0 处违规", text);
        Assert.DoesNotContain("空扫描警告", text);
    }

    [Fact]
    public void changed模式零文件是正常通过()
    {
        // changed 模式 0 文件 = 无新增/改动，属正常情况，不算空扫描
        var writer = new StringWriter();
        ReportWriter.WriteText(writer, "/repo", CheckMode.Changed, "guardai.yml",
            [new CheckResult("structure", 0, [])]);

        var text = writer.ToString();
        Assert.Contains("结果: 通过 - 0 处违规", text);
        Assert.DoesNotContain("空扫描警告", text);
    }

    [Fact]
    public void json报告带emptyScan标记()
    {
        var emptyWriter = new StringWriter();
        ReportWriter.WriteJson(emptyWriter, "/repo", CheckMode.All, [new CheckResult("structure", 0, [])]);
        Assert.Contains("\"EmptyScan\": true", emptyWriter.ToString());

        var normalWriter = new StringWriter();
        ReportWriter.WriteJson(normalWriter, "/repo", CheckMode.All, [new CheckResult("structure", 3, [])]);
        Assert.Contains("\"EmptyScan\": false", normalWriter.ToString());

        var changedWriter = new StringWriter();
        ReportWriter.WriteJson(changedWriter, "/repo", CheckMode.Changed, [new CheckResult("structure", 0, [])]);
        Assert.Contains("\"EmptyScan\": false", changedWriter.ToString());
    }
}
