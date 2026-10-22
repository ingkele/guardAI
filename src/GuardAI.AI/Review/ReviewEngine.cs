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
    /// <param name="mode">收集模式：changed / staged / base。</param>
    /// <param name="baseRef">base 模式下的 git 引用。</param>
    /// <param name="ruleSummary">项目规则摘要（送给模型的上下文），可空。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>审查报告。</returns>
    /// <exception cref="InvalidOperationException">没有可审查的改动。</exception>
    /// <exception cref="LlmException">模型调用失败。</exception>
    /// <exception cref="FormatException">模型输出无法解析。</exception>
    public async Task<ReviewReport> RunAsync(
        string repoPath,
        string mode,
        string? baseRef,
        string? ruleSummary,
        CancellationToken cancellationToken = default)
    {
        var (diffText, files) = await DiffCollector.CollectAsync(repoPath, mode, baseRef, cancellationToken).ConfigureAwait(false);
        var user = ReviewPrompt.BuildUser(ruleSummary, diffText);
        var output = await _client.ChatAsync(ReviewPrompt.BuildSystem(), user, cancellationToken).ConfigureAwait(false);
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
