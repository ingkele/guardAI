using GuardAI.AI.Config;
using GuardAI.AI.Llm;

namespace GuardAI.AI.Review;

/// <summary>
/// AI 审查引擎：收集差异 → 构造提示词 → 调模型 → 解析发现 → 汇总报告。
/// </summary>
public sealed class ReviewEngine
{
    private readonly ILlmClient _client;
    private readonly AIConfig _config;

    /// <summary>
    /// 构造审查引擎。
    /// </summary>
    /// <param name="client">大模型客户端。</param>
    /// <param name="config">AI 配置（用于报告元数据）。</param>
    public ReviewEngine(ILlmClient client, AIConfig config)
    {
        _client = client;
        _config = config;
    }

    /// <summary>
    /// 执行一次语义审查。
    /// </summary>
    /// <param name="repoPath">仓库根目录。</param>
    /// <param name="mode">收集模式：changed / staged / base / all。</param>
    /// <param name="baseRef">base 模式下的 git 引用。</param>
    /// <param name="ruleSummary">项目规则摘要（送给模型的上下文），可空。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <param name="isCodeFile">all 模式的代码文件过滤谓词；null 收全部文本文件。</param>
    /// <param name="onProgress">进度回调（可选）：差异收集完成后、模型调用前各触发一次，供 CLI 打可观察进度（Issue #4）。</param>
    /// <returns>审查报告。</returns>
    /// <exception cref="InvalidOperationException">没有可审查的改动。</exception>
    /// <exception cref="LlmException">模型调用失败。</exception>
    /// <exception cref="FormatException">模型输出无法解析。</exception>
    public async Task<ReviewReport> RunAsync(
        string repoPath,
        string mode,
        string? baseRef,
        string? ruleSummary,
        CancellationToken cancellationToken = default,
        Func<string, bool>? isCodeFile = null,
        Action<ReviewProgress>? onProgress = null)
    {
        var (diffText, files) = await DiffCollector.CollectAsync(repoPath, mode, baseRef, cancellationToken, isCodeFile).ConfigureAwait(false);

        // 差异收集完成 → 立即吐进度（原来的实现在模型返回后才打印规模，长等待期间全程静默）
        onProgress?.Invoke(new ReviewProgress(ReviewProgressStage.Collected, files, diffText.Length));
        onProgress?.Invoke(new ReviewProgress(ReviewProgressStage.CallingModel, files, diffText.Length));

        var user = ReviewPrompt.BuildUser(ruleSummary, diffText);
        var output = await _client.ChatAsync(ReviewPrompt.BuildSystem(), user, cancellationToken).ConfigureAwait(false);
        onProgress?.Invoke(new ReviewProgress(ReviewProgressStage.ModelReturned, files, diffText.Length));
        var findings = ReviewParser.Parse(output);

        return new ReviewReport(
            DateTime.Now,
            _config.Provider,
            _config.Model,
            files,
            diffText.Length,
            findings,
            diffText.Length > ReviewPrompt.MaxDiffChars ? "送审内容超过长度上限已截断，建议分批提交后审查" : null);
    }
}
