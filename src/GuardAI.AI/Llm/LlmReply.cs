using System.Text.Json;

namespace GuardAI.AI.Llm;

/// <summary>
/// 模型回复处理共用件：截取 JSON、读取字段（各顾问 ParseReply 共用）。
/// </summary>
/// <remarks>
/// 单一来源（复用规则"同一语义只允许定义一次"）：InitAiAdvisor / LearnAdvisor 等顾问共用此骨架，
/// 调用流程见 <see cref="AdvisorRunner"/>，各顾问只保留提示词（BuildPrompt）与 schema 解析（ParseReply）。
/// </remarks>
internal static class LlmReply
{
    /// <summary>文本字段默认长度上限（超出截断）。</summary>
    public const int MaxTextLength = 300;

    /// <summary>
    /// 从模型回复中截取并解析 JSON 对象（容忍 ```json 代码围栏与前后杂文）。
    /// </summary>
    /// <param name="llmOutput">模型回复文本。</param>
    /// <param name="label">建议类型标签（用于错误消息，如"装配建议"）。</param>
    /// <returns>解析后的 JSON 文档（调用方负责释放）。</returns>
    /// <exception cref="FormatException">输出中找不到合法 JSON 对象。</exception>
    public static JsonDocument ParseObject(string llmOutput, string label)
    {
        var json = ExtractJson(llmOutput);
        var doc = LlmHttp.ParseJson(json, label);
        if (doc.RootElement.ValueKind != JsonValueKind.Object)
        {
            doc.Dispose();
            throw new FormatException($"模型输出的{label}不是 JSON 对象：{LlmText.Truncate(llmOutput, 200)}");
        }

        return doc;
    }

    /// <summary>
    /// 读取文本字段（trim + 空值返回 null + 超长截断）。
    /// </summary>
    /// <param name="element">所在 JSON 对象。</param>
    /// <param name="propertyName">字段名。</param>
    /// <param name="maxLength">长度上限（默认 <see cref="MaxTextLength"/>）。</param>
    /// <returns>清洗后的文本；字段缺失/非字符串/空值返回 null。</returns>
    public static string? ReadText(JsonElement element, string propertyName, int maxLength = MaxTextLength)
    {
        if (!element.TryGetProperty(propertyName, out var raw) || raw.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        var text = raw.GetString()?.Trim();
        return string.IsNullOrEmpty(text) ? null : LlmText.Truncate(text, maxLength);
    }

    /// <summary>
    /// 从回复中截取 JSON 对象文本（容忍 ```json 代码围栏与前后杂文）。
    /// </summary>
    /// <param name="llmOutput">模型回复文本。</param>
    /// <returns>首个 { 到最后一个 } 之间的文本。</returns>
    /// <exception cref="FormatException">找不到 JSON 对象。</exception>
    private static string ExtractJson(string llmOutput)
    {
        var text = llmOutput ?? string.Empty;
        var start = text.IndexOf('{');
        var end = text.LastIndexOf('}');
        if (start < 0 || end <= start)
        {
            throw new FormatException($"模型输出中找不到 JSON 对象：{LlmText.Truncate(text, 200)}");
        }

        return text[start..(end + 1)];
    }
}
