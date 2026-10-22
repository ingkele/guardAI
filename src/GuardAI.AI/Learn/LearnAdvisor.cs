using System.Text.Json;
using GuardAI.AI.Llm;

namespace GuardAI.AI;

/// <summary>
/// AI 规则发现员：审计踩坑素材（review 记录 / 违规台账 / 豁免清单），提炼候选规则。
/// </summary>
/// <remarks>
/// 产品铁律"规则 = AI 判断的缓存"：从"踩坑加规则"变成"AI 报案加规则"。
/// 职责边界（引擎裁判，AI 装配）：本类只产候选建议，**不落地不改规则**——
/// 人工确认后候选才进规则资产（guardai.yml / 项目指令文件 / 检查器），之后由确定性引擎免费执法。
/// 模型输出经 schema 校验并与现有规则去重，不合规条目一律丢弃并记录。
/// </remarks>
public static class LearnAdvisor
{
    /// <summary>候选规则条目上限：规则资产要精，宁缺毋滥。</summary>
    private const int MaxRules = 20;

    /// <summary>
    /// 组装送审提示词（系统提示定角色与输出约束，用户提示给审计摘要与 JSON 格式）。
    /// </summary>
    /// <param name="digest">审计摘要（LearnDigest 产出）。</param>
    /// <returns>（系统提示，用户提示）。</returns>
    /// <exception cref="ArgumentException">摘要为空。</exception>
    public static (string System, string User) BuildPrompt(string digest)
    {
        if (string.IsNullOrWhiteSpace(digest))
        {
            throw new ArgumentException("审计摘要不能为空。", nameof(digest));
        }

        const string system = """
            你是 guardAI 的规则发现员。guardAI 是项目骨架守卫：
            你的任务是审计项目的踩坑记录（review 发现 / 反复违规 / 豁免清单），
            从反复出现的模式里提炼候选规则，供人工确认后沉淀进规则资产——之后由确定性引擎免费执法。
            你是报案人，不是裁判：只提候选规则建议，不做合规判定、不改任何文件。
            输出必须是单个 JSON 对象，不要任何多余文字、不要代码围栏。
            """;

        var user = $$"""
            以下是项目的踩坑审计摘要：
            {{digest}}

            请提炼候选规则，JSON 格式：
            {
              "rules": [
                {
                  "name": "规则名（中文，一眼看懂）",
                  "target": "yml 或 prompt 或 checker",
                  "pattern": "规则内容",
                  "evidence": "证据（引用摘要里的踩坑记录）",
                  "reason": "理由（为什么值得沉淀成规则）"
                }
              ]
            }

            目标层含义与 pattern 写法：
            - yml：进 guardai.yml 由引擎执法。pattern 写可直接粘贴的 YAML 规则片段，
              如 "placement_rules:\n  - name: ...\n    file_pattern: ...\n    dir_pattern: ..."，
              也可是 banned_dirs / allowed_dirs / exclusions 条目。必须是合法 YAML。
            - prompt：进项目指令文件（AGENTS.md）约束 AI 写码。pattern 写一句可直接粘贴的指令文本。
            - checker：现有规则表达不了、需要新检查器的模式。pattern 写检查逻辑描述（供人工评估开发）。

            要求：
            - 只提"反复踩坑、证据充分"的模式；宁缺毋滥，没有就给空数组（"rules": []）。
            - 每条必须有 evidence（指向摘要中的统计或样本）与 reason；证据不足的不要提。
            - 不得与摘要中的"现有规则名"重复；不重复提同一个模式。
            - 优先级：反复违规（同一指纹多次）> 高频类别 review 发现 > 豁免聚集信号。
            """;

        return (system, user);
    }

    /// <summary>
    /// 解析并校验模型输出的候选规则（schema 不合规或与现有规则重复的条目丢弃并记录）。
    /// </summary>
    /// <param name="llmOutput">模型回复文本（允许被代码围栏包裹）。</param>
    /// <param name="existingRuleNames">现有规则名（去重参考）。</param>
    /// <returns>校验后的候选规则建议。</returns>
    /// <exception cref="FormatException">输出中找不到合法 JSON 对象。</exception>
    public static LearnAdvice ParseReply(string llmOutput, IReadOnlyList<string> existingRuleNames)
    {
        ArgumentNullException.ThrowIfNull(existingRuleNames);
        using var doc = LlmReply.ParseObject(llmOutput, "候选规则");

        var dropped = new List<string>();
        var rules = new List<LearnRuleCandidate>();

        if (!doc.RootElement.TryGetProperty("rules", out var array) || array.ValueKind != JsonValueKind.Array)
        {
            dropped.Add("缺少 rules 数组，按空建议处理");
            return new LearnAdvice([], dropped);
        }

        // 去重参考名归一化（去空/trim）：脏名不参与去重比对
        var existing = new HashSet<string>(
            existingRuleNames.Where(n => !string.IsNullOrWhiteSpace(n)).Select(n => n.Trim()),
            StringComparer.OrdinalIgnoreCase);

        foreach (var element in array.EnumerateArray())
        {
            if (rules.Count >= MaxRules)
            {
                dropped.Add($"候选规则超过 {MaxRules} 条上限，多余条目已丢弃（规则资产要精，宁缺毋滥）");
                break;
            }

            if (element.ValueKind != JsonValueKind.Object)
            {
                dropped.Add("rules 中的非对象条目已丢弃");
                continue;
            }

            var name = LlmReply.ReadText(element, "name");
            var target = LlmReply.ReadText(element, "target")?.ToLowerInvariant() switch
            {
                LearnAdvice.TargetYml => LearnAdvice.TargetYml,
                LearnAdvice.TargetPrompt => LearnAdvice.TargetPrompt,
                LearnAdvice.TargetChecker => LearnAdvice.TargetChecker,
                _ => null,
            };
            var pattern = LlmReply.ReadText(element, "pattern");
            var evidence = LlmReply.ReadText(element, "evidence");
            var reason = LlmReply.ReadText(element, "reason");

            if (name is null || target is null || pattern is null || evidence is null || reason is null)
            {
                dropped.Add($"候选规则缺少必填字段或 target 非法" +
                    $"（name={LlmText.Truncate(name ?? "无", 30)}），已丢弃");
                continue;
            }

            if (existing.Contains(name) || rules.Any(r => string.Equals(r.Name, name, StringComparison.OrdinalIgnoreCase)))
            {
                dropped.Add($"候选规则与现有规则重复，已丢弃: {name}");
                continue;
            }

            rules.Add(new LearnRuleCandidate(name, target, pattern, evidence, reason));
        }

        return new LearnAdvice(rules, dropped);
    }
}
