using System.Text.Json;
using GuardAI.AI.Llm;

namespace GuardAI.AI;

/// <summary>
/// AI 装配顾问：根据仓库结构摘要产出 guardai.yml 装配建议（消灭首跑误报的根治入口）。
/// </summary>
/// <remarks>
/// 职责边界（产品铁律"引擎裁判，AI 装配"）：本类只产出配置建议——
/// AI 是装配员不是裁判，合规判定 100% 归确定性引擎；
/// 模型输出经 schema 校验后确定性合并进 guardai.yml，不合规条目一律丢弃并记录。
/// </remarks>
public static class InitAiAdvisor
{
    /// <summary>单类建议条目上限：防止模型放飞产出巨量条目拖垮配置可读性。</summary>
    private const int MaxItemsPerList = 40;

    /// <summary>路径通配长度上限。</summary>
    private const int MaxPathLength = 200;

    /// <summary>说明文字长度上限（超出截断）。</summary>
    private const int MaxTextLength = 300;

    /// <summary>
    /// 组装送审提示词（系统提示定角色与输出约束，用户提示给仓库摘要与 JSON 格式）。
    /// </summary>
    /// <param name="digest">仓库结构摘要（InitDigest 产出）。</param>
    /// <returns>（系统提示，用户提示）。</returns>
    /// <exception cref="ArgumentException">摘要为空。</exception>
    public static (string System, string User) BuildPrompt(string digest)
    {
        if (string.IsNullOrWhiteSpace(digest))
        {
            throw new ArgumentException("仓库结构摘要不能为空。", nameof(digest));
        }

        const string system = """
            你是 guardAI 的仓库装配顾问。guardAI 是项目骨架守卫：
            引擎负责确定性判定（目录白名单、落位规则、豁免），你只根据仓库真实结构产出 guardai.yml 装配建议。
            你是装配员，不是裁判——不做合规判定、不评价代码质量。
            输出必须是单个 JSON 对象，不要任何多余文字、不要代码围栏。
            """;

        var user = $$"""
            以下是仓库结构摘要：
            {{digest}}

            请产出 guardai.yml 装配建议，JSON 格式：
            {
              "project_type": "app 或 library（拿不准就省略此字段）",
              "allowed_dirs": [{"path": "相对路径通配（** 任意层，* 单层）", "purpose": "用途说明（中文）"}],
              "placement_rules": [{"name": "规则名（中文）", "file_pattern": "文件名通配", "dir_pattern": "所在目录通配", "suggest": "整改建议（中文）", "scope": "app|library|all"}],
              "exclusions": [{"path": "相对路径通配", "reason": "豁免理由（中文）"}]
            }

            要求：
            - 只建议"常规白名单覆盖不到、但仓库确实需要"的条目；宁缺毋滥，没有就给空数组。
            - purpose / reason / suggest 必须说清为什么（如"生成代码目录，布局固定，允许放行"）。
            - path 必须是相对仓库根的通配式，不得含 .. 或盘符。
            - 通配语法：** 任意层、* 单层、| 多选一（dir_pattern 可写 "src/*|tests/*"）；
              dir_pattern 匹配的是文件所在目录，尾斜杠可省略。
            - 首跑零误报铁律：落位规则只约束"未来新文件"。输出前逐条自查——
              按仓库现状，若有任何既有文件命中 file_pattern 却不在 dir_pattern 目录下，该规则就是错的，不要输出。
              反例（不要这样）：file_pattern "*.csproj" + dir_pattern "tests/**" 会把 src/ 下的项目文件全部判违规。
              正例：file_pattern "*.csproj" + dir_pattern "src/*|tests/*"（项目文件固定在各项目根）。
            - 典型场景：生成代码、第三方 vendored 代码、测试固定现场、约定俗成的特殊目录 → exclusions 或 allowed_dirs；
              仓库特有的文件落位约定（如"门面类放模块根"）→ placement_rules。
            """;

        return (system, user);
    }

    /// <summary>
    /// 调用大模型产出装配建议（输出经 schema 校验后才进配置）。
    /// </summary>
    /// <param name="client">大模型客户端。</param>
    /// <param name="digest">仓库结构摘要。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>装配建议（含被丢弃条目的说明）。</returns>
    /// <exception cref="LlmException">网络、鉴权或响应格式错误。</exception>
    /// <exception cref="FormatException">模型输出的 JSON 无法解析。</exception>
    /// <exception cref="OperationCanceledException">调用被取消。</exception>
    public static async Task<InitAdvice> AdviseAsync(
        ILlmClient client, string digest, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(client);
        var (system, user) = BuildPrompt(digest);
        var reply = await client.ChatAsync(system, user, cancellationToken);
        return ParseReply(reply);
    }

    /// <summary>
    /// 解析并校验模型输出的装配建议（schema 不合规的条目丢弃并记录，不整体失败）。
    /// </summary>
    /// <param name="llmOutput">模型回复文本（允许被代码围栏包裹）。</param>
    /// <returns>校验后的装配建议。</returns>
    /// <exception cref="FormatException">输出中找不到合法 JSON 对象。</exception>
    public static InitAdvice ParseReply(string llmOutput)
    {
        var json = ExtractJson(llmOutput);
        using var doc = LlmHttp.ParseJson(json, "装配建议");
        if (doc.RootElement.ValueKind != JsonValueKind.Object)
        {
            throw new FormatException($"模型输出的装配建议不是 JSON 对象：{LlmText.Truncate(llmOutput, 200)}");
        }

        var dropped = new List<string>();

        var projectType = ReadProjectType(doc.RootElement, dropped);
        var allowedDirs = ReadItems(doc.RootElement, "allowed_dirs", dropped);
        var placements = ReadPlacements(doc.RootElement, dropped);
        var exclusions = ReadItems(doc.RootElement, "exclusions", dropped);

        return new InitAdvice(projectType, allowedDirs, placements, exclusions, dropped);
    }

    /// <summary>
    /// 从回复中截取 JSON 对象（容忍 ```json 代码围栏与前后杂文）。
    /// </summary>
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

    /// <summary>
    /// 读取 project_type 建议（只接受 app / library）。
    /// </summary>
    private static string? ReadProjectType(JsonElement root, List<string> dropped)
    {
        if (!root.TryGetProperty("project_type", out var raw) || raw.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        var value = raw.ValueKind == JsonValueKind.String ? raw.GetString()?.Trim().ToLowerInvariant() : null;
        return value switch
        {
            "app" or "library" => value,
            _ => Drop(dropped, $"project_type 值不合法（{LlmText.Truncate(raw.ToString(), 40)}），沿用扫描判定"),
        };
    }

    /// <summary>
    /// 读取 path + purpose/reason 结构的建议列表。
    /// </summary>
    private static IReadOnlyList<InitAdviceItem> ReadItems(
        JsonElement root, string propertyName, List<string> dropped)
    {
        var items = new List<InitAdviceItem>();
        if (!TryReadArray(root, propertyName, dropped, out var array))
        {
            return items;
        }

        foreach (var element in array.EnumerateArray())
        {
            if (items.Count >= MaxItemsPerList)
            {
                dropped.Add($"{propertyName} 超过 {MaxItemsPerList} 条上限，多余条目已丢弃");
                break;
            }

            if (element.ValueKind != JsonValueKind.Object)
            {
                dropped.Add($"{propertyName} 中的非对象条目已丢弃");
                continue;
            }

            var path = SanitizePath(element, propertyName, dropped);
            var purpose = ReadText(element, propertyName == "exclusions" ? "reason" : "purpose")
                ?? ReadText(element, "purpose") ?? ReadText(element, "reason");
            if (path is null || purpose is null)
            {
                continue;
            }

            if (items.Any(i => string.Equals(i.Path, path, StringComparison.OrdinalIgnoreCase)))
            {
                dropped.Add($"{propertyName} 重复条目已去重: {path}");
                continue;
            }

            items.Add(new InitAdviceItem(path, purpose));
        }

        return items;
    }

    /// <summary>
    /// 读取落位规则建议列表。
    /// </summary>
    private static IReadOnlyList<InitAdvicePlacement> ReadPlacements(JsonElement root, List<string> dropped)
    {
        var items = new List<InitAdvicePlacement>();
        if (!TryReadArray(root, "placement_rules", dropped, out var array))
        {
            return items;
        }

        foreach (var element in array.EnumerateArray())
        {
            if (items.Count >= MaxItemsPerList)
            {
                dropped.Add($"placement_rules 超过 {MaxItemsPerList} 条上限，多余条目已丢弃");
                break;
            }

            if (element.ValueKind != JsonValueKind.Object)
            {
                dropped.Add("placement_rules 中的非对象条目已丢弃");
                continue;
            }

            var name = ReadText(element, "name");
            var filePattern = SanitizePath(element, "file_pattern", dropped);
            var dirPattern = SanitizePath(element, "dir_pattern", dropped);
            var suggest = ReadText(element, "suggest");
            if (name is null || filePattern is null || dirPattern is null || suggest is null)
            {
                continue;
            }

            // scope 只接受三值，未知回落 all（最宽适配，不误卡）
            var scope = ReadText(element, "scope")?.ToLowerInvariant() switch
            {
                "app" or "library" or "all" => ReadText(element, "scope")!.ToLowerInvariant(),
                _ => "all",
            };

            if (items.Any(i => string.Equals(i.Name, name, StringComparison.OrdinalIgnoreCase)))
            {
                dropped.Add($"placement_rules 重复规则名已去重: {name}");
                continue;
            }

            items.Add(new InitAdvicePlacement(name, filePattern, dirPattern, suggest, scope));
        }

        return items;
    }

    /// <summary>
    /// 读取并校验建议数组属性。
    /// </summary>
    private static bool TryReadArray(
        JsonElement root, string propertyName, List<string> dropped, out JsonElement array)
    {
        if (!root.TryGetProperty(propertyName, out array))
        {
            dropped.Add($"缺少 {propertyName} 字段，按空列表处理");
            return false;
        }

        if (array.ValueKind != JsonValueKind.Array)
        {
            dropped.Add($"{propertyName} 不是数组，已整体丢弃");
            return false;
        }

        return true;
    }

    /// <summary>
    /// 校验路径通配（相对、无盘符、无 ..；反斜杠归一为 /）。
    /// </summary>
    /// <returns>合法路径；非法返回 null 并记录丢弃原因。</returns>
    private static string? SanitizePath(JsonElement element, string propertyName, List<string> dropped)
    {
        var path = ReadText(element, "path") ?? ReadText(element, propertyName);
        if (path is null)
        {
            dropped.Add($"{propertyName} 条目缺少路径字段，已丢弃");
            return null;
        }

        path = path.Replace('\\', '/');
        if (path.Length > MaxPathLength || path.Contains("..", StringComparison.Ordinal)
            || path.StartsWith('/') || path.Contains(':'))
        {
            dropped.Add($"{propertyName} 路径不合法（须相对仓库根、无 .. 与盘符）: {LlmText.Truncate(path, 60)}");
            return null;
        }

        return path;
    }

    /// <summary>
    /// 读取文本字段（trim + 空值返回 null + 超长截断）。
    /// </summary>
    private static string? ReadText(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var raw) || raw.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        var text = raw.GetString()?.Trim();
        return string.IsNullOrEmpty(text) ? null : LlmText.Truncate(text, MaxTextLength);
    }

    /// <summary>
    /// 记录一条丢弃原因（安全网：不合规条目不进配置，但要让用户知道）。
    /// </summary>
    /// <returns>null（便于 switch 表达式直接返回）。</returns>
    private static string? Drop(List<string> dropped, string reason)
    {
        dropped.Add(reason);
        return null;
    }
}
