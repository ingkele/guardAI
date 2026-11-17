using GuardAI.AI.Config;
using GuardAI.AI.Review;
using GuardAI.Cli;
using Xunit;

namespace GuardAI.Tests;

/// <summary>
/// review 进度输出测试（Issue #4）：长等待必须可观察，且绝不污染 stdout。
/// </summary>
/// <remarks>
/// 原现象：从进入模型调用到出报告之间零输出，5 分钟静默被误判卡死强杀（退出码 124、零字节）。
/// 本组锁住三点：进度写 stderr、规模在调模型前打印、模型返回后给耗时。
/// </remarks>
public class ReviewProgressTests
{
    /// <summary>构造测试用 AI 配置。</summary>
    private static AIConfig TestConfig() => new(
        "openai", "https://api.example.com/v1", "mimo-v2.6-flash", null, TimeoutSeconds: 300);

    /// <summary>收集完成：打印送审规模（文件数/字符数/模式/模型）。</summary>
    [Fact]
    public void 收集完成_打印送审规模()
    {
        using var stderr = new StringWriter();
        using var reporter = new ReviewProgressReporter(stderr, TestConfig(), "changed");

        reporter.Report(new ReviewProgress(ReviewProgressStage.Collected, 17, 60583));

        var text = stderr.ToString();
        Assert.Contains("17 个文件", text);
        Assert.Contains("60583 字符", text);
        Assert.Contains("mode=changed", text);
        Assert.Contains("mimo-v2.6-flash", text);
    }

    /// <summary>调用模型：打印等待提示与超时上限（用户知道可以 Ctrl+C）。</summary>
    [Fact]
    public void 调用模型_打印超时上限与中断提示()
    {
        using var stderr = new StringWriter();
        using var reporter = new ReviewProgressReporter(stderr, TestConfig(), "all");

        reporter.Report(new ReviewProgress(ReviewProgressStage.CallingModel, 3, 1200));

        var text = stderr.ToString();
        Assert.Contains("模型调用中", text);
        Assert.Contains("300s", text);
        Assert.Contains("Ctrl+C", text);
    }

    /// <summary>模型返回：打印耗时，并停掉心跳（不再有后续输出）。</summary>
    [Fact]
    public void 模型返回_打印耗时且停表()
    {
        using var stderr = new StringWriter();
        using var reporter = new ReviewProgressReporter(stderr, TestConfig(), "all");

        reporter.Report(new ReviewProgress(ReviewProgressStage.CallingModel, 3, 1200));
        reporter.Report(new ReviewProgress(ReviewProgressStage.ModelReturned, 3, 1200));

        var text = stderr.ToString();
        Assert.Contains("模型已返回", text);
        Assert.Contains("解析结果", text);
    }

    /// <summary>Dispose 幂等：未启动心跳也能安全释放（异常路径下 finally 会调）。</summary>
    [Fact]
    public void Dispose_未启动心跳也安全()
    {
        using var stderr = new StringWriter();
        var reporter = new ReviewProgressReporter(stderr, TestConfig(), "all");

        reporter.Dispose();
        reporter.Dispose();
    }
}