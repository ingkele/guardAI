using System.Text.Json;

namespace GuardAI.AI.Review;

/// <summary>
/// 模型输出解析器：把 LLM 返回的 JSON 文本容错解析为发现列表。
/// </summary>
public static class ReviewParser
{
    /// <summary>
    /// 解析模型输出为发现列表。
    /// </summary>
    /// <param name="llmOutput">模型回复文本（允许带代码块围栏或前后杂文）。</param>
    /// <returns>发现列表（按输出顺序）。</returns>
    /// <exception cref="FormatException">输出中找不到合法 JSON 或字段类型错误。</exception>
    public static IReadOnlyList<ReviewFinding> Parse(string llmOutput)
    {
        var json = ExtractJson(llmOutput);
        using var doc = ParseDocument(json, llmOutput);
        var root = doc.RootElement;

        // 兼容两种形态：{"findings":[...]} 或直接 [...]
        var items = root.ValueKind switch
        {
            JsonValueKind.Array => root,
            JsonValueKind.Object when root.TryGetProperty("findings", out var findings) => findings,
            _ => throw new FormatException($"模型输出的 JSON 缺少 findings 数组：{Preview(llmOutput)}"),
        };

        if (items.ValueKind != JsonValueKind.Array)
        {
            throw new FormatException($"模型输出的 findings 不是数组：{Preview(llmOutput)}");
        }

        var results = new List<ReviewFinding>();
        foreach (var item in items.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var message = GetString(item, "message") ?? GetString(item, "msg");
            if (string.IsNullOrWhiteSpace(message))
            {
                continue; // 没有描述的条目不可行动，丢弃
            }

            results.Add(new ReviewFinding(
                ReviewFinding.NormalizeSeverity(GetString(item, "severity")),
                string.IsNullOrWhiteSpace(GetString(item, "category")) ? "其他" : GetString(item, "category")!.Trim(),
                GetString(item, "file") ?? "(未指明)",
                GetInt(item, "line"),
                message.Trim(),
                GetString(item, "suggestion") ?? GetString(item, "fix") ?? "（模型未给出建议，请结合上下文判断）"));
        }

        return results;
    }

    /// <summary>
    /// 解析 JSON 文本（JsonException 统一转 FormatException，保证 Parse 的异常契约单一）。
    /// </summary>
    /// <param name="json">已提取的 JSON 文本。</param>
    /// <param name="llmOutput">模型原始输出（报错预览用）。</param>
    /// <returns>解析出的 JSON 文档。</returns>
    /// <exception cref="FormatException">JSON 格式非法。</exception>
    private static JsonDocument ParseDocument(string json, string llmOutput)
    {
        try
        {
            return JsonDocument.Parse(json);
        }
        catch (JsonException ex)
        {
            throw new FormatException($"模型输出的 JSON 格式非法：{Preview(llmOutput)}", ex);
        }
    }

    /// <summary>
    /// 从模型输出中提取 JSON 文本：剥代码块围栏，取第一个 '{' 或 '[' 到最后一个 '}' 或 ']'。
    /// </summary>
    private static string ExtractJson(string llmOutput)
    {
        if (string.IsNullOrWhiteSpace(llmOutput))
        {
            throw new FormatException("模型输出为空。");
        }

        var text = llmOutput.Trim();
        if (text.StartsWith("```"))
        {
            // 剥掉 ```json ... ``` 围栏
            var firstNewline = text.IndexOf('\n');
            var lastFence = text.LastIndexOf("```", StringComparison.Ordinal);
            if (firstNewline > 0 && lastFence > firstNewline)
            {
                text = text[(firstNewline + 1)..lastFence].Trim();
            }
        }

        var start = text.IndexOfAny(new[] { '{', '[' });
        var end = text.LastIndexOfAny(new[] { '}', ']' });
        if (start < 0 || end <= start)
        {
            throw new FormatException($"模型输出中找不到 JSON：{Preview(llmOutput)}");
        }

        return text[start..(end + 1)];
    }

    /// <summary>读取字符串字段（缺省 null）。</summary>
    private static string? GetString(JsonElement item, string key)
        => item.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    /// <summary>读取整数字段（缺省 null，容忍字符串数字）。</summary>
    private static int? GetInt(JsonElement item, string key)
    {
        if (!item.TryGetProperty(key, out var value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetInt32(out var n) => n,
            JsonValueKind.String when int.TryParse(value.GetString(), out var n) => n,
            _ => null,
        };
    }

    /// <summary>截断输出用于报错展示（复用 Llm 共用截断，避免重复实现）。</summary>
    private static string Preview(string text)
        => Llm.LlmText.Truncate(text, 200);
}
