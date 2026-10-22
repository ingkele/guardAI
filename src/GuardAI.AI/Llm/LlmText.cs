namespace GuardAI.AI.Llm;

/// <summary>
/// LLM 层文本共用工具：报错展示截断（与 JSON 选项分开，各司其职）。
/// </summary>
internal static class LlmText
{
    /// <summary>
    /// 截断文本用于报错展示（统一实现与统一长度）。
    /// </summary>
    /// <param name="text">原始文本。</param>
    /// <param name="maxLength">最大展示字符数，默认 300。</param>
    /// <returns>不超过 <paramref name="maxLength"/> 的展示文本（超长追加省略号）。</returns>
    public static string Truncate(string text, int maxLength = 300)
        => text.Length <= maxLength ? text : text[..maxLength] + "…";
}
