using System.Text;

namespace GuardAI.AI.Llm;

/// <summary>
/// LLM 协议 HTTP 收发共用封装：发送请求、读响应体、非 2xx 统一转 <see cref="LlmException"/>。
/// </summary>
internal static class LlmHttp
{
    /// <summary>
    /// 发送请求并返回响应体文本（非 2xx 抛 <see cref="LlmException"/>，报错带截断响应体）。
    /// </summary>
    /// <param name="http">HTTP 客户端。</param>
    /// <param name="request">已构造好的请求（本方法负责释放）。</param>
    /// <param name="label">协议标签（进报错文案，如 "OpenAI 协议"）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <param name="hint">附加排障提示（可空，如 Ollama 的启动提示）。</param>
    /// <returns>响应体原文。</returns>
    /// <exception cref="LlmException">HTTP 非 2xx。</exception>
    public static async Task<string> SendAsync(
        HttpClient http,
        HttpRequestMessage request,
        string label,
        CancellationToken cancellationToken = default,
        string? hint = null)
    {
        using (request)
        using (var response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false))
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                throw new LlmException(
                    $"{label}调用失败（HTTP {(int)response.StatusCode}）：{LlmText.Truncate(body)}{hint}");
            }

            return body;
        }
    }

    /// <summary>
    /// 构造 JSON 请求体内容（UTF-8 / application/json，中文不转义）。
    /// </summary>
    /// <param name="payload">请求体对象（匿名对象即可）。</param>
    /// <returns>字符串内容。</returns>
    public static StringContent JsonContent(object payload)
        => new(System.Text.Json.JsonSerializer.Serialize(payload, LlmJson.Options), Encoding.UTF8, "application/json");

    /// <summary>
    /// 解析响应体 JSON（畸形 JSON 统一转 <see cref="LlmException"/>，不让 JsonException 冲破契约）。
    /// </summary>
    /// <param name="body">响应体原文。</param>
    /// <param name="label">协议标签（进报错文案）。</param>
    /// <returns>解析出的 JSON 文档（调用方负责释放）。</returns>
    /// <exception cref="LlmException">响应体不是合法 JSON。</exception>
    public static System.Text.Json.JsonDocument ParseJson(string body, string label)
    {
        try
        {
            return System.Text.Json.JsonDocument.Parse(body);
        }
        catch (System.Text.Json.JsonException ex)
        {
            // 网关返回 HTML 错误页等场景：响应体不是 JSON，给可行动提示而不是原始堆栈
            throw new LlmException($"{label}响应不是合法 JSON：{LlmText.Truncate(body)}", ex);
        }
    }
}
