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
            throw BuildEmptyContentError(doc.RootElement);
        }

        return content;
    }

    /// <summary>
    /// 正文为空时按真实原因给出可行动的报错（Issue #2）。
    /// </summary>
    /// <param name="root">响应 JSON 根元素。</param>
    /// <returns>按原因分类的异常（调用方 throw）。</returns>
    /// <remarks>
    /// 原实现把「正文为空」一律归因为「max_tokens 截断」，且文案写死「16384 起步」——
    /// 用户把 max_tokens 调到 32768 仍看到同一句话，被指向错误的修复方向。三路区分：
    /// ①finish_reason=length → 确属截断，但只报实际配置值，不再给写死的起步值；
    /// ②正文空但 reasoning_content 非空 → 推理模型只出了思考、没出正文；
    /// ③其余 → 就是模型返回了空正文，不再复述「截断」，也不把整个 JSON 塞进报错。
    /// 部分厂商字段名为 <c>reasoning</c>（DeepSeek-R1 系），一并认。
    /// </remarks>
    private LlmException BuildEmptyContentError(JsonElement root)
    {
        var choice = root.TryGetProperty("choices", out var choices) && choices.GetArrayLength() > 0
            ? choices[0]
            : (JsonElement?)null;

        var finishReason = choice?.TryGetProperty("finish_reason", out var fr) == true ? fr.GetString() : null;
        var reasoning = ReadReasoning(choice);

        if (finishReason == "length")
        {
            return new LlmException(
                $"模型输出被 max_tokens 截断（finish_reason=length，当前 ai.max_tokens={_config.MaxTokens}）。"
                + "推理模型会先消耗思考预算；请按需调大 ai.max_tokens，或减少单次送审的代码量后重试。");
        }

        if (!string.IsNullOrWhiteSpace(reasoning))
        {
            return new LlmException(
                $"模型只返回了思考内容、未返回正文（finish_reason={finishReason ?? "未知"}，"
                + $"ai.max_tokens={_config.MaxTokens}）。"
                + "常见于推理模型思考预算耗尽即止：换非推理模型，或调大 ai.max_tokens，或减少送审代码量。");
        }

        return new LlmException(
            $"模型返回空正文（finish_reason={finishReason ?? "未知"}，ai.max_tokens={_config.MaxTokens}）。"
            + "检查该模型的 chat/completions 响应是否符合 OpenAI 协议"
            + "（choices[0].message.content 应为字符串），或换模型后重试。");
    }

    /// <summary>
    /// 读取思考内容（兼容 <c>reasoning_content</c> 与 <c>reasoning</c> 两种厂商字段名）。
    /// </summary>
    /// <param name="choice">choices[0] 元素（可为空）。</param>
    /// <returns>思考内容；不存在或为空时返回 null。</returns>
    private static string? ReadReasoning(JsonElement? choice)
    {
        if (choice is not { } element || !element.TryGetProperty("message", out var message))
        {
            return null;
        }

        foreach (var field in ReasoningFields)
        {
            if (message.TryGetProperty(field, out var value) && value.ValueKind == JsonValueKind.String)
            {
                var text = value.GetString();
                if (!string.IsNullOrWhiteSpace(text))
                {
                    return text;
                }
            }
        }

        return null;
    }

    /// <summary>思考内容的厂商字段名（按优先级）。</summary>
    private static readonly string[] ReasoningFields = ["reasoning_content", "reasoning"];
}
