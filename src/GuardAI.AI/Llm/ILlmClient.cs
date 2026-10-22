namespace GuardAI.AI.Llm;

/// <summary>
/// 大模型对话客户端抽象：一套调用面，屏蔽 OpenAI 协议 / Anthropic / Ollama 的报文差异。
/// </summary>
public interface ILlmClient
{
    /// <summary>
    /// 发起一轮对话并返回模型的文本回复。
    /// </summary>
    /// <param name="system">系统提示词（角色与输出约束）。</param>
    /// <param name="user">用户提示词（任务内容）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>模型回复的文本内容。</returns>
    /// <exception cref="LlmException">网络、鉴权或响应格式错误。</exception>
    Task<string> ChatAsync(string system, string user, CancellationToken cancellationToken = default);
}

/// <summary>
/// 大模型调用异常（网络、鉴权、响应格式等）。
/// </summary>
public sealed class LlmException : Exception
{
    /// <summary>
    /// 构造异常。
    /// </summary>
    /// <param name="message">中文错误描述。</param>
    /// <param name="innerException">内部异常（可空）。</param>
    public LlmException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }
}
