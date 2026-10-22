namespace GuardAI.AI.Config;

/// <summary>
/// AI 模型接入配置（guardai.yml 的 <c>ai:</c> 段）。
/// </summary>
/// <param name="Provider">协议提供方：openai（OpenAI 协议，兼容 DeepSeek/mimo/vLLM 等）/ anthropic / ollama。</param>
/// <param name="BaseUrl">已归一的接口基地址（不含尾斜杠，非空）；缺省值由 <see cref="AIConfigParser"/> 按协议补齐。</param>
/// <param name="Model">模型名（如 gpt-4o-mini / claude-sonnet-4-5 / qwen3:8b）。</param>
/// <param name="ApiKeyEnv">密钥所在环境变量名（密钥绝不落配置文件）；ollama 不需要。</param>
/// <param name="TimeoutSeconds">单次调用超时秒数，默认 300（推理模型需更长，大改动建议 600）。</param>
/// <param name="MaxTokens">单次回复最大 token 数，默认 8192（推理模型需 16384 起步）。</param>
/// <param name="Temperature">采样温度，默认 0.1（审查任务要求稳定输出）。</param>
public sealed record AIConfig(
    string Provider,
    string BaseUrl,
    string Model,
    string? ApiKeyEnv,
    int TimeoutSeconds = AIDefaults.TimeoutSeconds,
    int MaxTokens = AIDefaults.MaxTokens,
    double Temperature = AIDefaults.Temperature)
{
    /// <summary>协议提供方：OpenAI 协议（兼容一切 /chat/completions 实现）。</summary>
    public const string ProviderOpenAI = "openai";

    /// <summary>协议提供方：Anthropic Messages API。</summary>
    public const string ProviderAnthropic = "anthropic";

    /// <summary>协议提供方：本地 Ollama（/api/chat，无需密钥）。</summary>
    public const string ProviderOllama = "ollama";

    /// <summary>OpenAI 协议默认基地址。</summary>
    public const string DefaultOpenAIBaseUrl = "https://api.openai.com/v1";

    /// <summary>Anthropic 默认基地址。</summary>
    public const string DefaultAnthropicBaseUrl = "https://api.anthropic.com";

    /// <summary>本地 Ollama 默认基地址。</summary>
    public const string DefaultOllamaBaseUrl = "http://localhost:11434";
}
