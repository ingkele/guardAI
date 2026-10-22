using System.Text;
using System.Text.Json;
using GuardAI.AI.Llm;

namespace GuardAI.AI;

/// <summary>
/// 规则发现素材摘要构建器：把 review 报告 / 违规台账 / 豁免清单聚合成给 AI 规则发现员的审计摘要。
/// </summary>
/// <remarks>
/// 三路素材对应产品定位"审计违规样本、Review 记录、exclusions 豁免清单"。
/// 只发统计与截断后的代表条目（结构事实），不发代码内容——与 review/init 的送审安全边界同一条铁律。
/// 入参全部为原始文本（JSON/JSONL/说明行），保持 AI 层对 Core 的零依赖，便于单测直接喂样本文本。
/// </remarks>
public static class LearnDigest
{
    /// <summary>每组统计最多列出的代表样本数。</summary>
    private const int MaxSamplesPerGroup = 3;

    /// <summary>反复踩坑榜最多列出的指纹数。</summary>
    private const int MaxRepeats = 15;

    /// <summary>样本消息截断长度。</summary>
    private const int SampleChars = 120;

    /// <summary>
    /// 构建审计摘要文本。
    /// </summary>
    /// <param name="reviewJsonTexts">review 报告 JSON 文本（.guard/records/reviews/*.json 的内容）。</param>
    /// <param name="ledgerJsonlLines">违规台账 JSONL 行（runs.jsonl 的每行）。</param>
    /// <param name="exclusions">豁免清单（path，reason）——"哪里总被放行"本身是立规则信号。</param>
    /// <param name="existingRuleNames">现有规则名（去重参考，避免建议已有规则）。</param>
    /// <returns>中文审计摘要（送大模型的用户提示组成部分）。</returns>
    /// <exception cref="ArgumentNullException">任一集合参数为 null 时抛出。</exception>
    public static string Build(
        IReadOnlyList<string> reviewJsonTexts,
        IReadOnlyList<string> ledgerJsonlLines,
        IReadOnlyList<(string Path, string Reason)> exclusions,
        IReadOnlyList<string> existingRuleNames)
    {
        ArgumentNullException.ThrowIfNull(reviewJsonTexts);
        ArgumentNullException.ThrowIfNull(ledgerJsonlLines);
        ArgumentNullException.ThrowIfNull(exclusions);
        ArgumentNullException.ThrowIfNull(existingRuleNames);

        var sb = new StringBuilder();
        AppendReviews(sb, reviewJsonTexts);
        AppendLedger(sb, ledgerJsonlLines);
        AppendExclusions(sb, exclusions);
        AppendExistingRules(sb, existingRuleNames);
        return sb.ToString();
    }

    /// <summary>
    /// 聚合 review 报告：按严重级别与类别计数，每类给代表样本。
    /// </summary>
    private static void AppendReviews(StringBuilder sb, IReadOnlyList<string> reviewJsonTexts)
    {
        // (类别, 级别) -> (计数, 样本列表)
        var groups = new SortedDictionary<(string Category, string Severity), (int Count, List<string> Samples)>();
        var total = 0;
        var parsed = 0;

        foreach (var text in reviewJsonTexts)
        {
            total++;
            try
            {
                using var doc = JsonDocument.Parse(text);
                if (!doc.RootElement.TryGetProperty("findings", out var findings)
                    || findings.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                parsed++;
                foreach (var finding in findings.EnumerateArray())
                {
                    var severity = ReadString(finding, "severity") ?? "warn";
                    var category = ReadString(finding, "category") ?? "其他";
                    var sample = $"{ReadString(finding, "file") ?? "?"}:" +
                        $"{ReadString(finding, "line") ?? "?"} " +
                        LlmText.Truncate(ReadString(finding, "message") ?? "", SampleChars);

                    var key = (category, severity);
                    if (!groups.TryGetValue(key, out var group))
                    {
                        groups[key] = group = (0, []);
                    }

                    // 值元组是拷贝语义：递增后必须写回字典（Samples 是引用类型，Add 会直接生效，
                    // 但 Count++ 只改本地副本——不写回则计数永远停在 0）
                    group.Count++;
                    groups[key] = group;
                    if (group.Samples.Count < MaxSamplesPerGroup)
                    {
                        group.Samples.Add(sample);
                    }
                }
            }
            catch (JsonException)
            {
                // 坏报告跳过并计数进"解析"外（不因单个坏文件放弃整个审计）
            }
        }

        sb.AppendLine($"## Review 记录（报告 {total} 份，可解析 {parsed} 份）");
        if (groups.Count == 0)
        {
            sb.AppendLine("（无可解析的 review 发现）");
            return;
        }

        sb.AppendLine("按类别/级别统计（样本为文件:行 + 消息摘要）：");
        foreach (var ((category, severity), (count, samples)) in groups)
        {
            sb.AppendLine($"- [{severity}] {category}: {count} 条");
            foreach (var sample in samples)
            {
                sb.AppendLine($"    · {sample}");
            }
        }
    }

    /// <summary>
    /// 聚合违规台账：按指纹计反复次数（whack-a-mole 强度）、按命中规则计数。
    /// </summary>
    private static void AppendLedger(StringBuilder sb, IReadOnlyList<string> ledgerJsonlLines)
    {
        // 指纹 -> (次数, 规则, 文件, 消息)
        var fingerprints = new Dictionary<string, (int Count, string Rule, string File, string Message)>();
        var ruleCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var runs = 0;
        var violations = 0;

        foreach (var line in ledgerJsonlLines)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            JsonDocument? doc = null;
            try
            {
                doc = JsonDocument.Parse(line);
                if (!doc.RootElement.TryGetProperty("violations", out var items)
                    || items.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                runs++;
                foreach (var item in items.EnumerateArray())
                {
                    violations++;
                    var rule = ReadString(item, "rule") ?? "未知规则";
                    ruleCounts[rule] = ruleCounts.GetValueOrDefault(rule) + 1;

                    var fingerprint = ReadString(item, "fingerprint") ?? "";
                    if (fingerprint.Length == 0)
                    {
                        continue;
                    }

                    if (!fingerprints.TryGetValue(fingerprint, out var entry))
                    {
                        fingerprints[fingerprint] = entry = (0, rule,
                            ReadString(item, "file") ?? "?",
                            LlmText.Truncate(ReadString(item, "message") ?? "", SampleChars));
                    }

                    entry.Count++;
                    fingerprints[fingerprint] = entry;
                }
            }
            catch (JsonException)
            {
                // 坏行跳过
            }
            finally
            {
                doc?.Dispose();
            }
        }

        sb.AppendLine();
        sb.AppendLine($"## 违规台账（运行 {runs} 次，违规 {violations} 条）");
        if (violations == 0)
        {
            sb.AppendLine("（无违规记录）");
            return;
        }

        var repeats = fingerprints.Values
            .Where(e => e.Count > 1)
            .OrderByDescending(e => e.Count)
            .Take(MaxRepeats)
            .ToList();
        if (repeats.Count > 0)
        {
            sb.AppendLine("反复违规（同一指纹出现多次 = 反复踩坑，最值得立规则）：");
            foreach (var (count, rule, file, message) in repeats)
            {
                sb.AppendLine($"- ×{count} [{rule}] {file} {message}");
            }
        }

        sb.AppendLine("按命中规则计数：");
        foreach (var (rule, count) in ruleCounts.OrderByDescending(kv => kv.Value))
        {
            sb.AppendLine($"- {rule}: {count} 次");
        }
    }

    /// <summary>
    /// 列出豁免清单：豁免多的区域 = 频繁出问题被放行 = 立规则信号。
    /// </summary>
    private static void AppendExclusions(
        StringBuilder sb, IReadOnlyList<(string Path, string Reason)> exclusions)
    {
        sb.AppendLine();
        sb.AppendLine($"## 豁免清单（{exclusions.Count} 条；豁免多的区域 = 频繁出问题被放行，本身是立规则信号）");
        if (exclusions.Count == 0)
        {
            sb.AppendLine("（无豁免）");
            return;
        }

        foreach (var (path, reason) in exclusions)
        {
            sb.AppendLine($"- {path} — {reason}");
        }
    }

    /// <summary>
    /// 列出现有规则名：避免建议已有规则（重复建议直接浪费人工确认）。
    /// </summary>
    private static void AppendExistingRules(
        StringBuilder sb, IReadOnlyList<string> existingRuleNames)
    {
        sb.AppendLine();
        sb.AppendLine($"## 现有规则名（{existingRuleNames.Count} 条，候选规则不得与之重复）：");
        foreach (var name in existingRuleNames)
        {
            sb.AppendLine($"- {name}");
        }
    }

    /// <summary>
    /// 读取 JSON 字符串字段（缺失或非字符串返回 null）。
    /// </summary>
    private static string? ReadString(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var raw) && raw.ValueKind == JsonValueKind.String
            ? raw.GetString()
            : null;
    }
}
