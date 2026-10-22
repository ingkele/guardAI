using GuardAI.AI.Llm;

namespace GuardAI.AI;

/// <summary>
/// 顾问调用骨架：组提示词 → 调大模型 → 解析回复，全流程只此一份。
/// </summary>
/// <remarks>
/// 单一来源（复用规则"同一语义只允许定义一次"）：InitAiAdvisor / LearnAdvisor 等顾问
/// 各自只提供 BuildPrompt（提示词）与 ParseReply（schema 解析），调用流程统一走本类，
/// 不在各顾问复制第二份 AdviseAsync 包装。
/// </remarks>
public static class AdvisorRunner
{
    /// <summary>
    /// 统一顾问调用流程：组提示词 → 调大模型 → 解析回复。
    /// </summary>
    /// <typeparam name="T">建议契约类型（如 InitAdvice / LearnAdvice）。</typeparam>
    /// <param name="client">大模型客户端。</param>
    /// <param name="prompt">（系统提示，用户提示）。</param>
    /// <param name="parseReply">回复解析器（各顾问的 ParseReply）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>解析后的建议对象。</returns>
    /// <exception cref="ArgumentNullException">client 或 parseReply 为 null。</exception>
    /// <exception cref="LlmException">网络、鉴权或响应格式错误。</exception>
    /// <exception cref="FormatException">模型输出的 JSON 无法解析。</exception>
    /// <exception cref="OperationCanceledException">调用被取消。</exception>
    public static async Task<T> AdviseAsync<T>(
        ILlmClient client, (string System, string User) prompt,
        Func<string, T> parseReply, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(parseReply);
        var reply = await client.ChatAsync(prompt.System, prompt.User, cancellationToken);
        return parseReply(reply);
    }
}
