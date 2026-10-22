using System.Text.Encodings.Web;
using System.Text.Json;

namespace GuardAI.AI.Llm;

/// <summary>
/// AI 层 JSON 序列化选项共用（报文、报告、解析报错统一引用，避免各处重复实现）；文本截断见 <see cref="LlmText"/>。
/// </summary>
internal static class LlmJson
{
    /// <summary>紧凑 JSON 序列化选项：中文不转义（报文可读、调试友好，UTF-8 传输无碍）。</summary>
    public static readonly JsonSerializerOptions Options = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>缩进 JSON 序列化选项：报告落盘/机器消费用，中文同样不转义。</summary>
    public static readonly JsonSerializerOptions IndentedOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = true,
    };
}
