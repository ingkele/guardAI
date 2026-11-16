using GuardAI.Checks.Code;
using GuardAI.Core.Config;
using GuardAI.Core.Engine;

namespace GuardAI.Checks.Structure;

/// <summary>
/// 层契约检查：文件所属层 → 引用目标所属层 → 依赖方向判定 + 实体暴露判定。
/// </summary>
/// <remarks>
/// 继承 <see cref="CodeScanCheckBase"/> 复用「文件收集 → exclusions/测试代码豁免 → UTF-8 宽容读取」骨架，
/// 只实现层判定本身（骨架合一，避免同族检查被自家重复检测逮住）。
/// 首跑零误报三道保险：未配置 layers 整模块跳过；文件未命中任何层跳过；
/// 未配置 allowed_dependencies 时方向检查不启用（init 只生成 layers 骨架的中间态不会误伤）。
/// 引用→层用点分段连续匹配（Web 不会误命中 WebApi），无法归类的外部包一律放行。
/// </remarks>
public sealed class LayerContractCheck : CodeScanCheckBase
{
    /// <inheritdoc />
    public override string Name => "layer";

    /// <summary>MVP 覆盖 C# 黄金样本 + 四种常见后端/前端语言的引用识别。</summary>
    protected override IReadOnlyList<string> Extensions => [".cs", ".java", ".ts", ".tsx", ".go", ".py"];

    /// <summary>实体命名空间片段（明确枚举，不放宽成 EndsWith("Entity")）。</summary>
    private static readonly HashSet<string> EntitySegments = new(StringComparer.OrdinalIgnoreCase)
    {
        "Entities", "Persistence",
    };

    /// <summary>禁止直接暴露实体的外层（按层名匹配）。</summary>
    private static readonly HashSet<string> OuterLayers = new(StringComparer.OrdinalIgnoreCase)
    {
        "Web", "Application",
    };

    /// <summary>本轮配置（Run 前由基类钩子缓存，逐文件扫描时只读）。</summary>
    private StructureRuleConfig _config = new();

    /// <inheritdoc />
    protected override void OnBeforeRun(CheckContext context)
    {
        _config = context.Config;
    }

    /// <inheritdoc />
    protected override void ScanFile(string rel, string text, List<Violation> violations)
    {
        var layers = _config.Layers;
        if (layers.Count == 0)
        {
            return;
        }

        var from = LayerResolver.Resolve(rel, layers);
        if (from is null)
        {
            return; // 未命中层 = 首跑零误报
        }

        foreach (var reference in ReferenceScanner.Scan(text, Path.GetExtension(rel)))
        {
            var to = ResolveReferenceLayer(reference, layers);
            if (to is null || to.Name.Equals(from.Name, StringComparison.OrdinalIgnoreCase))
            {
                continue; // 外部包 / 同层引用不判
            }

            var dependencies = _config.AllowedDependencies;
            var allowed = dependencies.Any(d =>
                d.From.Equals(from.Name, StringComparison.OrdinalIgnoreCase)
                && d.To.Any(t => t.Equals(to.Name, StringComparison.OrdinalIgnoreCase)));
            if (dependencies.Count > 0 && !allowed)
            {
                var permitted = dependencies
                    .Where(d => d.From.Equals(from.Name, StringComparison.OrdinalIgnoreCase))
                    .SelectMany(d => d.To)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
                var permittedText = permitted.Count == 0 ? "任何层" : string.Join(" / ", permitted);
                violations.Add(new Violation(
                    rel,
                    "层依赖方向",
                    $"{from.Name} 层不允许依赖 {to.Name}（命中引用 {reference}）",
                    $"{from.Name} 层允许依赖：{permittedText}；把跨层访问经接口/服务收口，不要直接引用 {to.Name}"));
            }

            if (_config.ForbidDtoEntity
                && OuterLayers.Contains(from.Name)
                && HasEntitySegment(reference))
            {
                violations.Add(new Violation(
                    rel,
                    "出参禁止暴露实体",
                    $"{from.Name} 层出参禁止直接暴露持久化实体（命中实体命名空间 {reference}）",
                    "用 DTO / 读模型隔离持久化实体，避免 Entity / Persistence 类型穿透到外层"));
            }
        }
    }

    /// <summary>
    /// 引用目标（包/命名空间/模块路径）映射到声明的层：点分段连续包含，大小写不敏感。
    /// </summary>
    private static LayerDef? ResolveReferenceLayer(string reference, IReadOnlyList<LayerDef> layers)
    {
        var segments = reference.Replace('/', '.').Split('.', StringSplitOptions.RemoveEmptyEntries);
        foreach (var layer in layers)
        {
            foreach (var candidate in Candidates(layer))
            {
                var needle = candidate.Replace('/', '.').Split('.', StringSplitOptions.RemoveEmptyEntries);
                if (ContainsConsecutive(segments, needle))
                {
                    return layer;
                }
            }
        }

        return null;
    }

    /// <summary>
    /// 层的匹配候选：层名 + path 中的项目段（排除 src/tests 等通用段与 glob 段）。
    /// </summary>
    private static IEnumerable<string> Candidates(LayerDef layer)
    {
        yield return layer.Name;
        foreach (var segment in layer.Path.Split('/'))
        {
            if (segment.Length == 0 || segment.Contains('*'))
            {
                continue;
            }

            if (segment.Equals("src", StringComparison.OrdinalIgnoreCase)
                || segment.Equals("test", StringComparison.OrdinalIgnoreCase)
                || segment.Equals("tests", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            yield return segment;
        }
    }

    /// <summary>
    /// 判断 needle 是否作为 haystack 的连续子序列出现（整段相等，不做子串匹配）。
    /// </summary>
    private static bool ContainsConsecutive(string[] haystack, string[] needle)
    {
        if (needle.Length == 0 || needle.Length > haystack.Length)
        {
            return false;
        }

        for (var start = 0; start <= haystack.Length - needle.Length; start++)
        {
            var matched = true;
            for (var i = 0; i < needle.Length; i++)
            {
                if (!haystack[start + i].Equals(needle[i], StringComparison.OrdinalIgnoreCase))
                {
                    matched = false;
                    break;
                }
            }

            if (matched)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>引用目标是否含明确的实体命名空间片段。</summary>
    private static bool HasEntitySegment(string reference)
    {
        return reference.Replace('/', '.')
            .Split('.', StringSplitOptions.RemoveEmptyEntries)
            .Any(segment => EntitySegments.Contains(segment));
    }
}
