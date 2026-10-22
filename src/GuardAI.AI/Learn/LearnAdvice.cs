using System.Text.Json;
using GuardAI.AI.Llm;

namespace GuardAI.AI;

/// <summary>
/// 候选规则条目（AI 规则发现员的产出单位）。
/// </summary>
/// <param name="Name">规则名（中文，人工确认时一眼看懂）。</param>
/// <param name="Target">目标层：yml（进 guardai.yml 由引擎执法）/ prompt（进项目指令文件约束 AI 写码）/ checker（建议新检查器，需人工评估开发）。</param>
/// <param name="Pattern">规则内容：yml 目标为可粘贴的 YAML 规则片段，prompt 目标为指令文本，checker 目标为检查逻辑描述。</param>
/// <param name="Evidence">证据：引用踩坑记录（类别/指纹/文件等），证明该模式真实发生过。</param>
/// <param name="Reason">理由：为什么值得沉淀成规则。</param>
public sealed record LearnRuleCandidate(
    string Name,
    string Target,
    string Pattern,
    string Evidence,
    string Reason);

/// <summary>
/// AI 规则发现员的候选规则建议。
/// </summary>
/// <remarks>
/// 产品铁律"规则 = AI 判断的缓存"：本结构只含候选建议，**不落地**——
/// 人工确认后才进规则资产（guardai.yml / 项目指令文件 / 检查器），
/// 之后由确定性引擎免费执法。AI 不做合规判定，也不直接改规则。
/// </remarks>
/// <param name="Rules">候选规则列表。</param>
/// <param name="DroppedNotes">被 schema 校验丢弃的条目说明（模型输出不合规时的安全网）。</param>
public sealed record LearnAdvice(
    IReadOnlyList<LearnRuleCandidate> Rules,
    IReadOnlyList<string> DroppedNotes)
{
    /// <summary>空建议（素材不足或模型无有效输出时的中性值）。</summary>
    public static LearnAdvice Empty { get; } = new([], []);

    /// <summary>目标层：进 guardai.yml，确定性引擎执法。</summary>
    public const string TargetYml = "yml";

    /// <summary>目标层：进项目指令文件（AGENTS.md 等），约束 AI 写码。</summary>
    public const string TargetPrompt = "prompt";

    /// <summary>目标层：建议新检查器（引擎能力扩展，需人工评估开发）。</summary>
    public const string TargetChecker = "checker";

    /// <summary>
    /// 输出 JSON 报告（camelCase 与 review 报告同风格；中文不转义，复用共享序列化选项）。
    /// </summary>
    /// <param name="writer">输出流。</param>
    /// <param name="generatedAt">生成时间（可空；用于报告留痕）。</param>
    /// <exception cref="ArgumentNullException">writer 为 null 时抛出。</exception>
    public void WriteJson(TextWriter writer, DateTimeOffset? generatedAt = null)
    {
        ArgumentNullException.ThrowIfNull(writer);
        var payload = new
        {
            generatedAt = (generatedAt ?? DateTimeOffset.Now).ToString("o"),
            rules = Rules.Select(r => new
            {
                name = r.Name,
                target = r.Target,
                pattern = r.Pattern,
                evidence = r.Evidence,
                reason = r.Reason,
            }),
            droppedNotes = DroppedNotes,
        };
        writer.WriteLine(JsonSerializer.Serialize(payload, LlmJson.IndentedOptions));
    }
}
