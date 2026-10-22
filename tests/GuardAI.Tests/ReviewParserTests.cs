using GuardAI.AI.Review;

namespace GuardAI.Tests;

/// <summary>模型输出解析测试（容错能力是审查员可用性的生命线）。</summary>
public class ReviewParserTests
{
    [Fact]
    public void 标准findings对象_解析成功()
    {
        var findings = ReviewParser.Parse("""
            {"findings":[{"severity":"error","category":"命名","file":"src/A.cs","line":12,"message":"命名不达意","suggestion":"改为 UserService"}]}
            """);
        var finding = Assert.Single(findings);
        Assert.Equal(ReviewFinding.SeverityError, finding.Severity);
        Assert.Equal("命名", finding.Category);
        Assert.Equal("src/A.cs", finding.File);
        Assert.Equal(12, finding.Line);
        Assert.Equal("命名不达意", finding.Message);
        Assert.Equal("改为 UserService", finding.Suggestion);
    }

    [Fact]
    public void 代码块围栏包裹_剥壳后解析成功()
    {
        var findings = ReviewParser.Parse("""
            好的，以下是审查结论：
            ```json
            {"findings":[{"severity":"warn","category":"注释","file":"B.cs","message":"缺注释","suggestion":"补 XML 注释"}]}
            ```
            """);
        Assert.Single(findings);
    }

    [Fact]
    public void 裸数组形态_兼容解析()
    {
        var findings = ReviewParser.Parse("""
            [{"severity":"info","category":"其他","file":"C.cs","message":"提示","suggestion":"可忽略"}]
            """);
        Assert.Single(findings);
    }

    [Fact]
    public void 空findings_返回空列表()
    {
        Assert.Empty(ReviewParser.Parse("""{"findings":[]}"""));
    }

    [Fact]
    public void 无message条目_丢弃()
    {
        var findings = ReviewParser.Parse("""
            {"findings":[{"severity":"error","file":"X.cs"},{"severity":"warn","category":"其他","file":"Y.cs","message":"有效条目"}]}
            """);
        Assert.Single(findings);
        Assert.Equal("有效条目", findings[0].Message);
    }

    [Fact]
    public void 纯文本无JSON_抛FormatException()
    {
        Assert.Throws<FormatException>(() => ReviewParser.Parse("这是一段没有 JSON 的回答。"));
    }

    [Fact]
    public void 空输出_抛FormatException()
    {
        Assert.Throws<FormatException>(() => ReviewParser.Parse("  "));
    }

    [Theory]
    [InlineData("error", "error")]
    [InlineData("严重", "error")]
    [InlineData("warning", "warn")]
    [InlineData("警告", "warn")]
    [InlineData("info", "info")]
    [InlineData("提示", "info")]
    [InlineData("乱写的", "info")] // 无法识别按 info 处理（只漏勿误：未知项不拦截）
    public void 严重级别归一(string raw, string expected)
    {
        Assert.Equal(expected, ReviewFinding.NormalizeSeverity(raw));
    }
}
