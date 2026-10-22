using System.Text;
using System.Text.Json;

namespace GuardAI.AI.Llm;

/// <summary>
/// Anthropic 客户端（POST {base}/v1/messages，x-api-key 鉴权）。
/// </summary>
public sealed class AnthropicClient : ILlmClient
{
    /// <summary>Anthropic API 版本头的取值。</summary>
    private const string ApiVersion = "2023-06-01";

    private readonly HttpClient _http;
    private readonly Config.AIConfig _config;

    /// <summary>
    /// 构造客户端。
    /// </summary>
    /// <param name="http">HTTP 客户端（调用方负责生命周期，便于测试注入）。</param>
    /// <param name="config">AI 配置。</param>
    public AnthropicClient(HttpClient http, Config.AIConfig config)
    {
        _http = http;
        _config = config;
    }

    /// <inheritdoc />
    public async Task<string> ChatAsync(string system, string user, CancellationToken cancellationToken = default)
    {
        var key = LlmKey.Resolve(_config)
            ?? throw new LlmException("Anthropic 协议需要 api_key_env 指定密钥环境变量。");
        // 兼容两种基址习惯：根地址（官方 https://api.anthropic.com）走 /v1/messages；
        // 已带 /v1 的网关地址走 /messages，避免叠出 /v1/v1/
        var url = _config.BaseUrl.EndsWith("/v1", StringComparison.Ordinal)
            ? $"{_config.BaseUrl}/messages"
            : $"{_config.BaseUrl}/v1/messages";
        var request = new HttpRequestMessage(HttpMethod.Post, url);
        request.Headers.Add("x-api-key", key);
        request.Headers.Add("anthropic-version", ApiVersion);

        request.Content = LlmHttp.JsonContent(new
        {
            model = _config.Model,
            max_tokens = _config.MaxTokens,
            temperature = _config.Temperature,
            system,
            messages = new[] { new { role = "user", content = user } },
        });

        var body = await LlmHttp.SendAsync(_http, request, "Anthropic", cancellationToken).ConfigureAwait(false);

        using var doc = LlmHttp.ParseJson(body, "Anthropic");
        if (!doc.RootElement.TryGetProperty("content", out var blocks) || blocks.GetArrayLength() == 0)
        {
            throw new LlmException($"Anthropic 响应缺少 content 块：{LlmText.Truncate(body)}");
        }

        // content 是块数组，拼接全部 text 块
        var sb = new StringBuilder();
        foreach (var block in blocks.EnumerateArray())
        {
            if (block.TryGetProperty("type", out var type) && type.GetString() == "text"
                && block.TryGetProperty("text", out var text))
            {
                sb.Append(text.GetString());
            }
        }

        if (sb.Length == 0)
        {
            throw new LlmException($"Anthropic 响应没有 text 内容块：{LlmText.Truncate(body)}");
        }

        return sb.ToString();
    }

}
