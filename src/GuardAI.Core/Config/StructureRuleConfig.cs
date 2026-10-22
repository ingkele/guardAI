namespace GuardAI.Core.Config;

/// <summary>
/// 目录白名单规则：文件相对路径必须命中其中至少一条。
/// </summary>
/// <param name="Path">通配符路径模式（** 任意层，* 单层）。</param>
/// <param name="Purpose">给人和 AI 看的用途说明，违规时回显。</param>
public sealed record AllowedDirRule(string Path, string Purpose);

/// <summary>
/// 落位规则：文件名命中 FilePattern 时，所在目录必须同时命中 DirPattern。
/// </summary>
/// <param name="Name">规则名称（用于报告分组）。</param>
/// <param name="FilePattern">文件名通配符模式。</param>
/// <param name="DirPattern">所在目录通配符模式（匹配"目录路径/"形式）。</param>
/// <param name="Suggestion">违规时给出的修复建议。</param>
/// <param name="Scope">适用项目形态：app / library / all（缺省 all）。落位规则对类库失配率高（库入口门面类天然放模块根），由 scope 控制是否生效。</param>
public sealed record PlacementRule(string Name, string FilePattern, string DirPattern, string Suggestion, string Scope = "all");

/// <summary>
/// 豁免规则：命中 ExclusionPattern 的文件整体跳过检查（用于测试固定现场等合法例外）。
/// </summary>
/// <param name="Path">通配符路径模式。</param>
/// <param name="Reason">豁免原因（必须写明，便于审查）。</param>
public sealed record ExclusionRule(string Path, string Reason);

/// <summary>
/// 项目形态按路径覆盖（v0.3.1-⑤）：多项目仓库混 app/library 时，按文件所属项目裁决落位规则 scope。
/// </summary>
/// <param name="Path">通配符路径模式（如 AspireApp.ServiceDefaults/**）。</param>
/// <param name="ProjectType">该路径下的项目形态：app / library。</param>
public sealed record ProjectTypeOverride(string Path, string ProjectType);

/// <summary>
/// structure 检查的规则配置（规则资产，执行宿主可迁移）。
/// </summary>
/// <remarks>
/// schema 与 guard-tool 的 config/structure-allowlist.yml 保持一致（version/include_extensions/
/// banned_dirs/allowed_dirs/placement_rules），另新增 exclusions 豁免段。
/// </remarks>
public sealed class StructureRuleConfig
{
    /// <summary>规则资产版本号。</summary>
    public int Version { get; init; } = 1;

    /// <summary>项目形态：app / library（落位规则 scope 的过滤依据，init 自动判定，可人工改）。</summary>
    public string ProjectType { get; init; } = "app";

    /// <summary>
    /// 项目形态按路径覆盖（多项目仓库混 app/library）：文件命中即用覆盖值裁决 scope，
    /// 多条命中靠后的优先；未命中回落 <see cref="ProjectType"/>。
    /// </summary>
    public IReadOnlyList<ProjectTypeOverride> ProjectTypeOverrides { get; init; } = [];

    /// <summary>
    /// 参与检查的文件扩展名（小写、带点）。空 = 跟随成员解析注册表的全量支持语言
    /// （P4 多语言：C#/Java/Kotlin/TS/JS/Go/Python），显式配置则只查配置内的扩展名。
    /// </summary>
    public IReadOnlyList<string> IncludeExtensions { get; init; } = [];

    /// <summary>同义目录黑名单：路径中出现任一目录名即违规（不区分大小写）。</summary>
    public IReadOnlyList<string> BannedDirs { get; init; } = [];

    /// <summary>目录白名单。</summary>
    public IReadOnlyList<AllowedDirRule> AllowedDirs { get; init; } = [];

    /// <summary>落位规则。</summary>
    public IReadOnlyList<PlacementRule> PlacementRules { get; init; } = [];

    /// <summary>豁免规则。</summary>
    public IReadOnlyList<ExclusionRule> Exclusions { get; init; } = [];

    /// <summary>重复检测规则（guardai.yml 的 duplicates 段，缺失时用默认值）。</summary>
    public DuplicateRuleConfig Duplicates { get; init; } = DuplicateRuleConfig.Default;

    /// <summary>
    /// 能力清单排除模式（capability.exclude）：null = 缺省测试模式，显式空列表 = 不排除任何文件。
    /// </summary>
    public IReadOnlyList<string>? CapabilityExclude { get; init; }
}

/// <summary>
/// 规则配置加载器：把 YAML 规则资产解析为强类型配置。
/// </summary>
public static class StructureRuleConfigLoader
{
    /// <summary>
    /// 从 YAML 文件加载规则配置。
    /// </summary>
    /// <param name="configPath">规则配置文件路径。</param>
    /// <returns>强类型规则配置。</returns>
    /// <exception cref="FileNotFoundException">配置文件不存在时抛出。</exception>
    /// <exception cref="FormatException">必填段缺失或结构不符时抛出。</exception>
    public static StructureRuleConfig Load(string configPath)
    {
        if (!File.Exists(configPath))
        {
            throw new FileNotFoundException($"找不到结构规则配置: {configPath}", configPath);
        }

        return Parse(File.ReadAllText(configPath), Path.GetFileName(configPath));
    }

    /// <summary>
    /// 从 YAML 文本解析规则配置（供测试与内嵌配置使用）。
    /// </summary>
    /// <param name="yamlText">YAML 文本。</param>
    /// <param name="configFileName">配置文件名（仅用于报错信息）。</param>
    /// <returns>强类型规则配置。</returns>
    /// <exception cref="FormatException">必填段缺失或结构不符时抛出。</exception>
    public static StructureRuleConfig Parse(string yamlText, string configFileName = "rules.yml")
    {
        var root = GuardYaml.Parse(yamlText);

        var include = new List<string>();
        foreach (var e in AsList(root, "include_extensions", configFileName))
        {
            include.Add(AsString(e).ToLowerInvariant());
        }

        // 空 = 跟随成员解析注册表全量语言（P4 多语言），不再强填 .cs——由 StructureCheck 回落

        var banned = new List<string>();
        foreach (var e in AsList(root, "banned_dirs", configFileName))
        {
            banned.Add(AsString(e));
        }

        var allowed = new List<AllowedDirRule>();
        foreach (var e in AsList(root, "allowed_dirs", configFileName))
        {
            var map = AsMap(e, "allowed_dirs", configFileName);
            allowed.Add(new AllowedDirRule(
                AsString(map.GetValueOrDefault("path")),
                AsString(map.GetValueOrDefault("purpose"))));
        }

        var placements = new List<PlacementRule>();
        foreach (var e in AsList(root, "placement_rules", configFileName))
        {
            var map = AsMap(e, "placement_rules", configFileName);
            var scope = AsString(map.GetValueOrDefault("scope")).ToLowerInvariant();
            if (scope is not ("app" or "library" or "all"))
            {
                scope = "all"; // 缺省/非法值一律 all：不做过滤，向后兼容旧配置
            }

            placements.Add(new PlacementRule(
                AsString(map.GetValueOrDefault("name")),
                AsString(map.GetValueOrDefault("file_pattern")),
                AsString(map.GetValueOrDefault("dir_pattern")),
                AsString(map.GetValueOrDefault("suggest")),
                scope));
        }

        var exclusions = new List<ExclusionRule>();
        foreach (var e in AsList(root, "exclusions", configFileName))
        {
            var map = AsMap(e, "exclusions", configFileName);
            exclusions.Add(new ExclusionRule(
                AsString(map.GetValueOrDefault("path")),
                AsString(map.GetValueOrDefault("reason"))));
        }

        var overrides = new List<ProjectTypeOverride>();
        foreach (var e in AsList(root, "project_type_overrides", configFileName))
        {
            var map = AsMap(e, "project_type_overrides", configFileName);
            var type = AsString(map.GetValueOrDefault("type")).ToLowerInvariant();
            if (type is not ("app" or "library"))
            {
                throw new FormatException($"配置错误（{configFileName}）：'project_type_overrides' 的 type 只能是 app 或 library，收到 '{type}'");
            }

            overrides.Add(new ProjectTypeOverride(
                AsString(map.GetValueOrDefault("path")),
                type));
        }

        return new StructureRuleConfig
        {
            Version = root.TryGetValue("version", out var v) && v is int i ? i : 1,
            ProjectType = ParseProjectType(root, configFileName),
            ProjectTypeOverrides = overrides,
            IncludeExtensions = include,
            BannedDirs = banned,
            AllowedDirs = allowed,
            PlacementRules = placements,
            Exclusions = exclusions,
            Duplicates = ParseDuplicates(root, configFileName),
            CapabilityExclude = ParseCapabilityExclude(root, configFileName),
        };
    }

    /// <summary>
    /// 解析 capability.exclude 段（能力清单排除模式）；
    /// capability 整段缺失或 exclude 键缺失返回 null（用缺省测试模式），显式空列表返回空列表（不排除）。
    /// </summary>
    private static IReadOnlyList<string>? ParseCapabilityExclude(IReadOnlyDictionary<string, object?> root, string configFileName)
    {
        if (!root.TryGetValue("capability", out var raw) || raw is null)
        {
            return null;
        }

        var map = raw as IReadOnlyDictionary<string, object?>
            ?? throw new FormatException($"配置错误（{configFileName}）：'capability' 必须是键值对");

        if (!map.TryGetValue("exclude", out var listRaw) || listRaw is null)
        {
            return null;
        }

        var globs = new List<string>();
        foreach (var e in listRaw as IReadOnlyList<object?>
            ?? throw new FormatException($"配置错误（{configFileName}）：'capability.exclude' 必须是列表"))
        {
            globs.Add(AsString(e));
        }

        return globs;
    }

    /// <summary>
    /// 解析 duplicates 段（重复检测）；整段缺失用默认值，字段非法报配置错误。
    /// </summary>
    private static DuplicateRuleConfig ParseDuplicates(IReadOnlyDictionary<string, object?> root, string configFileName)
    {
        if (!root.TryGetValue("duplicates", out var raw) || raw is null)
        {
            return DuplicateRuleConfig.Default;
        }

        var map = raw as IReadOnlyDictionary<string, object?>
            ?? throw new FormatException($"配置错误（{configFileName}）：'duplicates' 必须是键值对");

        var enabled = map.GetValueOrDefault("enabled") switch
        {
            null => true,
            bool b => b,
            var other => throw new FormatException($"配置错误（{configFileName}）：'duplicates.enabled' 必须是 true/false，收到 '{other}'"),
        };

        var threshold = map.GetValueOrDefault("threshold") switch
        {
            null => 0.85,
            double d => d,
            int i => i,
            var other => throw new FormatException($"配置错误（{configFileName}）：'duplicates.threshold' 必须是 0~1 的数字，收到 '{other}'"),
        };
        if (threshold is <= 0 or > 1)
        {
            throw new FormatException($"配置错误（{configFileName}）：'duplicates.threshold' 必须在 0~1 之间，收到 '{threshold}'");
        }

        var minTokens = map.GetValueOrDefault("min_tokens") switch
        {
            null => 12,
            int i when i >= 1 => i,
            var other => throw new FormatException($"配置错误（{configFileName}）：'duplicates.min_tokens' 必须是正整数，收到 '{other}'"),
        };

        var containmentThreshold = map.GetValueOrDefault("containment_threshold") switch
        {
            null => 0.9,
            double d => d,
            int i => i,
            var other => throw new FormatException($"配置错误（{configFileName}）：'duplicates.containment_threshold' 必须是 0~1 的数字，收到 '{other}'"),
        };
        if (containmentThreshold is <= 0 or > 1)
        {
            throw new FormatException($"配置错误（{configFileName}）：'duplicates.containment_threshold' 必须在 0~1 之间，收到 '{containmentThreshold}'");
        }

        var registry = new List<string>();
        foreach (var e in AsList(map, "registry", configFileName))
        {
            registry.Add(AsString(e));
        }

        return new DuplicateRuleConfig(
            enabled,
            threshold,
            minTokens,
            registry.Count > 0 ? registry : DuplicateRuleConfig.DefaultRegistry,
            containmentThreshold);
    }

    /// <summary>
    /// 解析项目形态（app / library）；缺省 app（向后兼容旧配置），非法值报配置错误。
    /// </summary>
    private static string ParseProjectType(IReadOnlyDictionary<string, object?> root, string configFileName)
    {
        if (!root.TryGetValue("project_type", out var raw) || raw is null)
        {
            return "app";
        }

        var value = AsString(raw).ToLowerInvariant();
        return value is "app" or "library"
            ? value
            : throw new FormatException($"配置错误（{configFileName}）：'project_type' 只能是 app 或 library，收到 '{value}'");
    }

    /// <summary>
    /// 取出字典中的列表段；缺失返回空列表。
    /// </summary>
    private static IReadOnlyList<object?> AsList(IReadOnlyDictionary<string, object?> root, string key, string configFileName)
    {
        return root.TryGetValue(key, out var value)
            ? value as IReadOnlyList<object?> ?? throw new FormatException($"配置错误（{configFileName}）：'{key}' 必须是列表")
            : [];
    }

    /// <summary>
    /// 把列表项断言为字典。
    /// </summary>
    private static IReadOnlyDictionary<string, object?> AsMap(object? item, string section, string configFileName)
    {
        return item as IReadOnlyDictionary<string, object?>
            ?? throw new FormatException($"配置错误（{configFileName}）：'{section}' 的列表项必须是键值对");
    }

    /// <summary>
    /// 把标量断言为字符串（数字/布尔转成文本）。
    /// </summary>
    private static string AsString(object? value)
    {
        return value switch
        {
            null => string.Empty,
            string s => s,
            _ => value.ToString() ?? string.Empty,
        };
    }
}
