namespace GuardAI.AI.Llm;

/// <summary>
/// 大模型客户端工厂：按配置的协议派发到对应实现。
/// </summary>
public static class LlmClientFactory
{
    /// <summary>
    /// 创建客户端。
    /// </summary>
    /// <param name="http">HTTP 客户端（调用方负责生命周期）。</param>
    /// <param name="config">AI 配置。</param>
    /// <returns>对应协议的客户端实例。</returns>
    /// <exception cref="FormatException">协议名不被支持。</exception>
    public static ILlmClient Create(HttpClient http, Config.AIConfig config)
        => config.Provider switch
        {
            Config.AIConfig.ProviderOpenAI => new OpenAICompatClient(http, config),
            Config.AIConfig.ProviderAnthropic => new AnthropicClient(http, config),
            Config.AIConfig.ProviderOllama => new OllamaClient(http, config),
            _ => throw new FormatException($"不支持的 ai.provider: {config.Provider}（可用：openai / anthropic / ollama）。"),
        };
}
