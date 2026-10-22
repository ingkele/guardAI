using System.Text;
using System.Text.Json;

namespace GuardAI.AI.Llm;

/// <summary>
/// 本地 Ollama 客户端（POST {base}/api/chat，无需鉴权）。
/// </summary>
/// <remarks>
/// 走 Ollama 原生接口而非 OpenAI 兼容端点：本地零配置、不受兼容层版本差异影响。
/// </remarks>
public sealed class OllamaClient : ILlmClient
{
    private readonly HttpClient _http;
    private readonly Config.AIConfig _config;

    /// <summary>
    /// 构造客户端。
    /// </summary>
    /// <param name="http">HTTP 客户端（调用方负责生命周期，便于测试注入）。</param>
    /// <param name="config">AI 配置。</param>
    public OllamaClient(HttpClient http, Config.AIConfig config)
    {
        _http = http;
        _config = config;
    }

    /// <inheritdoc />
    public async Task<string> ChatAsync(string system, string user, CancellationToken cancellationToken = default)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"{_config.BaseUrl}/api/chat");
        request.Content = LlmHttp.JsonContent(new
        {
            model = _config.Model,
            messages = new[]
            {
                new { role = "system", content = system },
                new { role = "user", content = user },
            },
            stream = false,
            options = new { temperature = _config.Temperature, num_predict = _config.MaxTokens },
        });

        var body = await LlmHttp.SendAsync(
            _http, request, "Ollama", cancellationToken,
            hint: "。确认本地 Ollama 已启动（默认 http://localhost:11434）。").ConfigureAwait(false);

        using var doc = LlmHttp.ParseJson(body, "Ollama");
        var content = doc.RootElement.TryGetProperty("message", out var message)
            && message.TryGetProperty("content", out var text)
                ? text.GetString()
                : null;
        if (string.IsNullOrWhiteSpace(content))
        {
            throw new LlmException($"Ollama 响应缺少 message.content：{LlmText.Truncate(body)}");
        }

        return content;
    }

}
