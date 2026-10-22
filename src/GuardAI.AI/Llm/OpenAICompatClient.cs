using System.Net.Http.Headers;
using System.Text.Json;

namespace GuardAI.AI.Llm;

/// <summary>
/// OpenAI 协议客户端（POST {base}/chat/completions）。
/// </summary>
/// <remarks>
/// 兼容一切实现了 OpenAI Chat Completions 接口的服务：OpenAI 官方、DeepSeek、
/// 小米 token plan、vLLM、Ollama 的 /v1 兼容端点等——只换 base_url 与 model 即可。
/// </remarks>
public sealed class OpenAICompatClient : ILlmClient
{
    private readonly HttpClient _http;
    private readonly Config.AIConfig _config;

    /// <summary>
    /// 构造客户端。
    /// </summary>
    /// <param name="http">HTTP 客户端（调用方负责生命周期，便于测试注入）。</param>
    /// <param name="config">AI 配置。</param>
    public OpenAICompatClient(HttpClient http, Config.AIConfig config)
    {
        _http = http;
        _config = config;
    }

    /// <inheritdoc />
    public async Task<string> ChatAsync(string system, string user, CancellationToken cancellationToken = default)
    {
        var key = LlmKey.Resolve(_config);
        var request = new HttpRequestMessage(HttpMethod.Post, $"{_config.BaseUrl}/chat/completions");
        if (!string.IsNullOrEmpty(key))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        }

        request.Content = LlmHttp.JsonContent(new
        {
            model = _config.Model,
            messages = new[]
            {
                new { role = "system", content = system },
                new { role = "user", content = user },
            },
            temperature = _config.Temperature,
            max_tokens = _config.MaxTokens,
        });

        var body = await LlmHttp.SendAsync(_http, request, "OpenAI 协议", cancellationToken).ConfigureAwait(false);

        using var doc = LlmHttp.ParseJson(body, "OpenAI 协议");
        var content = doc.RootElement
            .TryGetProperty("choices", out var choices) && choices.GetArrayLength() > 0
            ? choices[0].TryGetProperty("message", out var message) && message.TryGetProperty("content", out var text)
                ? text.GetString()
                : null
            : null;
        if (string.IsNullOrWhiteSpace(content))
        {
            // 推理模型（reasoning_content）常因思考耗尽 max_tokens 导致正文为空——给出可行动的报错
            var finishReason = doc.RootElement.TryGetProperty("choices", out var c) && c.GetArrayLength() > 0
                && c[0].TryGetProperty("finish_reason", out var fr) ? fr.GetString() : null;
            if (finishReason == "length")
            {
                throw new LlmException(
                    $"模型输出被 max_tokens 截断（finish_reason=length，当前 ai.max_tokens={_config.MaxTokens}）。"
                    + "推理模型会先消耗思考预算，建议把 ai.max_tokens 调大（16384 起步）后重试。");
            }

            throw new LlmException($"OpenAI 协议响应缺少 choices[0].message.content：{LlmText.Truncate(body)}");
        }

        return content;
    }

}
