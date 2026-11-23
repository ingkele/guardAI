using System.Text.RegularExpressions;
using GuardAI.Core.Config;
using GuardAI.Core.Engine;

namespace GuardAI.Checks.Assets;

/// <summary>
/// duplicate 检查模块：资产级规范检查——重复造轮子当场拦下。
/// </summary>
/// <remarks>
/// 两条规则（每个重复对只报一条，近似优先）：
///   1. 近似重复：双通道并联——Jaccard ≥ 阈值抓"换皮重写"（改名/改常量再抄一遍），
///      Containment ≥ 包含度阈值抓"子集型复制"（删行/加行，如映射样板复制时漏字段）；
///   2. 高频方法名重复：两侧方法名命中同一注册表模式（Upload* / GetToken* ...）——抓"又写一遍同名轮子"。
/// JSX 语义豁免（Issue #1）：含 JSX 标记的方法整体退出规则 1——归一化把标签/属性名抹成 id、
/// 字符串抹成长度标记后，任意返回 JSX 的函数 token 流天然高度重叠（骨架相似 ≠ 语义重复），
/// 探针实测任何组件两两 50~100% 撞车；规则 2（名字通道）不受影响。
/// 跨语言同一病症（Issue #7）：不同语言的成员在归一化后 token 流几乎一致（TS 的 const/function
/// 与 C# 的 public/static 都抹成 id），会撞出"相似度 100%"的假阳性。两条修复并用——
/// 比对前按语言身份键隔离（不同语言物理上不可能复用，直接跳过），
/// 归一化时按语言选关键字表（语言特征词保留原样，恢复同语言内的判别力）。
/// 零误报三件套：baseline 快照（存量不报，只报新增）+ 阈值/门槛配置 + exclusions 豁免；
/// 检测能力升级首跑自动把新通道挖出的存量对并入基线（不轰炸存量债），升级前后语义一致。
/// 修复指令直指已有实现（文件:行），让 AI 当场改成复用。
/// </remarks>
public sealed class DuplicateCheck : IGuardCheck
{
    /// <summary>
    /// 当前检测能力版本：1 = 仅 Jaccard；2 = Jaccard + Containment 双通道；
    /// 3 = 字符串字面量带长度 token + 方法名解析修正（元组返回值）；
    /// 4 = 多语言成员解析（Java/Kotlin/TS/JS/Go/Python）+ 声明形态闸——检测面与成员键语义变化，
    /// 旧基线重键的存量对靠首跑收编吸收（见 Run 的升级分支），不轰炸存量债；
    /// 5 = JSX 语义豁免——含 JSX 标记的方法退出近似重复通道（Issue #1）；
    /// 6 = 跨语言隔离 + 按语言选归一化关键字表——不同语言成员不再互相比对，语言特征词保留原样（Issue #7）。
    /// </summary>
    public const int CurrentDetectorVersion = 6;

    /// <inheritdoc />
    public string Name => "duplicate";

    /// <summary>近似检测用的代码文件扩展名 = 注册表全量支持语言（P4 多语言，单一来源在 MemberParserRegistry）。</summary>
    private static readonly IReadOnlyList<string> CodeExtensions = MemberParserRegistry.Extensions;

    /// <summary>
    /// JSX 标记：闭合标签（&lt;/Tag&gt;、片段 &lt;/&gt;）或自闭合标签（&lt;Tag ... /&gt;）。
    /// </summary>
    /// <remarks>
    /// 只认带斜杠形态是有意的：单个开标签 &lt;Tag&gt; 与 C# 泛型 &lt;int&gt; 无法区分，会误伤；
    /// 闭合/自闭合带斜杠形态在非 JSX 语言的清洗后方法体里不会出现（字符串已抹空）。
    /// 万一误中，代价只是该方法退出近似重复通道（只漏不误），方向安全。
    /// </remarks>
    private static readonly Regex JsxMarkup = new(
        @"</|<\s*[A-Za-z_][\w.]*\s*[^<>]*?/>",
        RegexOptions.Compiled);

    /// <inheritdoc />
    public CheckResult Run(CheckContext context)
    {
        var config = context.Config.Duplicates;
        if (!config.Enabled)
        {
            return new CheckResult(Name, 0, []);
        }

        var all = LoadMembers(context.RepoPath, context.Config.Exclusions);
        var candidateFiles = TargetFileCollector.Collect(context.RepoPath, context.Mode, CodeExtensions)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var candidates = all.Where(m => candidateFiles.Contains(m.File)).ToList();

        // 口径统一（v0.3.1-④）：Scanned 一律报"本次扫描的文件数"（与 structure 同口径），不再把成员数当文件数
        // 严格审计模式（check --strict）：不读写基线豁免，存量重复（含基线内）全量报违规——案例验收/审计用
        if (context.Strict)
        {
            var strictViolations = DetectPairs(candidates, all, config).Select(ToViolation).ToList();
            return new CheckResult(Name, candidateFiles.Count, strictViolations);
        }

        var baseline = DuplicateBaseline.Load(context.RepoPath);
        if (baseline is null)
        {
            // 首跑零误报：全量扫描把存量重复对收进基线，本轮不报（只报新增的前置条件）
            var existing = DetectPairs(all, all, config);
            DuplicateBaseline.Save(context.RepoPath, new DuplicateBaseline(
                DateTimeOffset.Now.ToString("o"),
                existing.Select(p => new DuplicatePairRecord(p.Key, MemberKeyOf(p.Target), MemberKeyOf(p.Reference))).ToList(),
                CurrentDetectorVersion));
            return new CheckResult(Name, candidateFiles.Count, []);
        }

        // 检测能力升级首跑（存量不骚扰的延伸）：新通道挖出的存量对自动并入基线、不报违规，
        // 下一次改动起才拦新增；想看全量债用 check --strict 审计
        if (baseline.DetectorVersion < CurrentDetectorVersion)
        {
            var discovered = DetectPairs(all, all, config);
            var upgraded = DuplicateBaseline.Merge(baseline, discovered
                .Select(p => new DuplicatePairRecord(p.Key, MemberKeyOf(p.Target), MemberKeyOf(p.Reference)))
                .ToList());
            DuplicateBaseline.Save(context.RepoPath, upgraded with { DetectorVersion = CurrentDetectorVersion });
            return new CheckResult(Name, candidateFiles.Count, []);
        }

        var accepted = baseline.Pairs.Select(p => p.Key).ToHashSet(StringComparer.Ordinal);
        var violations = new List<Violation>();
        foreach (var pair in DetectPairs(candidates, all, config))
        {
            if (accepted.Contains(pair.Key))
            {
                continue;
            }

            violations.Add(ToViolation(pair));
        }

        return new CheckResult(Name, candidateFiles.Count, violations);
    }

    /// <summary>
    /// 把当前全部存量重复对收进基线（check --update-baseline 用：接受现状，只查新增）。
    /// </summary>
    /// <param name="context">检查上下文。</param>
    /// <exception cref="ArgumentNullException">context 为 null 时抛出。</exception>
    /// <returns>本次收进基线的新增对数。</returns>
    public static int AcceptExistingPairs(CheckContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var config = context.Config.Duplicates;
        var all = LoadMembers(context.RepoPath, context.Config.Exclusions);
        var pairs = DetectPairs(all, all, config);
        var records = pairs
            .Select(p => new DuplicatePairRecord(p.Key, MemberKeyOf(p.Target), MemberKeyOf(p.Reference)))
            .ToList();

        var before = DuplicateBaseline.Load(context.RepoPath)?.Pairs.Count ?? 0;
        var merged = DuplicateBaseline.Merge(DuplicateBaseline.Load(context.RepoPath), records);
        // 主动接受现状：版本同步到当前检测能力（升级收编语义一致）
        DuplicateBaseline.Save(context.RepoPath, merged with { DetectorVersion = CurrentDetectorVersion });
        return merged.Pairs.Count - before;
    }

    /// <summary>
    /// 检测候选成员相对参照成员的重复对（对称对去重，近似优先于注册表）。
    /// </summary>
    private static List<DetectedPair> DetectPairs(
        IReadOnlyList<CodeMember> candidates,
        IReadOnlyList<CodeMember> all,
        DuplicateRuleConfig config)
    {
        var tokenCache = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        var candidateKeys = candidates.Select(MemberKeyOf).ToHashSet(StringComparer.Ordinal);

        // JSX 成员集合：含 JSX 标记的方法整体退出近似重复通道（骨架相似度无语义区分度，Issue #1）
        var jsxKeys = all.Where(m => JsxMarkup.IsMatch(m.BodyText)).Select(MemberKeyOf).ToHashSet(StringComparer.Ordinal);

        // 语言身份缓存（Issue #7）：不同语言的成员物理上不可能互相复用，跨语言比对必须跳过。
        // 只靠归一化区分不了——TS 的 const/function 与 C# 的 public/static 在抹成 id 后流形一致，
        // 会撞出"相似度 100%"的假阳性（ProviderProtocols.SupportedConfigValues vs useDebouncedValue）。
        var languages = new Dictionary<string, string?>(StringComparer.Ordinal);

        string? LanguageOfMember(CodeMember member)
        {
            var key = MemberKeyOf(member);
            if (!languages.TryGetValue(key, out var language))
            {
                language = MemberParserRegistry.LanguageOf(member.File);
                languages[key] = language;
            }

            return language;
        }

        IReadOnlyList<string> TokensOf(CodeMember member)
        {
            var key = MemberKeyOf(member);
            if (!tokenCache.TryGetValue(key, out var tokens))
            {
                // 按语言选关键字表（Issue #7 方案 B）：语言特征词保留原样，扩大同语言内的判别力
                var keywords = MemberParserRegistry.ProfileOf(member.File)?.Keywords;
                tokens = CodeSimilarity.Tokenize(member.BodyText, keywords);
                tokenCache[key] = tokens;
            }

            return tokens;
        }

        var results = new Dictionary<string, DetectedPair>(StringComparer.Ordinal);

        foreach (var candidate in candidates)
        {
            var candidateKey = MemberKeyOf(candidate);
            var candidateLanguage = LanguageOfMember(candidate);
            foreach (var reference in all)
            {
                var referenceKey = MemberKeyOf(reference);

                // 同名同类型的重载/自身不是重复
                if (referenceKey == candidateKey)
                {
                    continue;
                }

                // 跨语言隔离（Issue #7）：语言键不同即不可能复用；任一侧键未知则保守放行（只漏不误方向由阈值兜底）
                if (candidateLanguage is not null
                    && LanguageOfMember(reference) is { } referenceLanguage
                    && !string.Equals(candidateLanguage, referenceLanguage, StringComparison.Ordinal))
                {
                    continue;
                }

                // 对称对去重 + 目标选定：恰好一方是候选（改动方）时记它头上；
                // 双方都是候选时，靠后者（文件/行号排序在后）当目标——"后出现的实现"担责
                if (candidateKeys.Contains(referenceKey)
                    && CompareMemberOrder(candidate, reference) < 0)
                {
                    continue;
                }

                var key = DuplicateBaseline.PairKey(candidateKey, referenceKey);
                if (results.ContainsKey(key))
                {
                    continue;
                }

                var tokensCandidate = TokensOf(candidate);
                var tokensReference = TokensOf(reference);

                // 规则 1：近似重复（过短方法体不参与，防结构性撞车；任一侧含 JSX 标记即豁免——骨架相似 ≠ 语义重复）
                // 双通道并联：Jaccard 抓"换皮重写"（整段改皮），Containment 抓"子集型复制"（删行/加行，如映射样板漏字段）
                if (!jsxKeys.Contains(candidateKey) && !jsxKeys.Contains(referenceKey)
                    && tokensCandidate.Count >= config.MinTokens && tokensReference.Count >= config.MinTokens)
                {
                    var similarity = CodeSimilarity.Jaccard(tokensCandidate, tokensReference);
                    var containment = CodeSimilarity.Containment(tokensCandidate, tokensReference);
                    if (similarity >= config.Threshold || containment >= config.ContainmentThreshold)
                    {
                        results[key] = new DetectedPair(key, candidate, reference, Math.Max(similarity, containment), ByName: false);
                        continue;
                    }
                }

                // 规则 2：高频方法名重复（两侧命中同一注册表模式）
                if (RegistryCollision(candidate.Name, reference.Name, config.Registry))
                {
                    results[key] = new DetectedPair(key, candidate, reference, 0, ByName: true);
                }
            }
        }

        return results.Values.ToList();
    }

    /// <summary>
    /// 把重复对转成违规（修复指令直指已有实现）。
    /// </summary>
    private static Violation ToViolation(DetectedPair pair)
    {
        var target = pair.Target;
        var reference = pair.Reference;
        var reuse = $"复用已有实现 {reference.File}:{reference.Line} 的「{reference.TypeName}.{reference.Name}」"
            + "（guardAI search 可查能力清单）；确有差异请改写体现实质差异";

        return pair.ByName
            ? new Violation(
                target.File,
                "重复检测：高频方法名重复",
                $"高频方法名「{target.Name}」已有实现 {reference.File}:{reference.Line}（「{reference.TypeName}.{reference.Name}」）——这个名下很容易重复造轮子",
                reuse)
            : new Violation(
                target.File,
                "重复检测：近似重复",
                $"「{target.TypeName}.{target.Name}」与已有实现「{reference.TypeName}.{reference.Name}」高度相似（相似度 {pair.Similarity:P0}）",
                reuse);
    }

    /// <summary>
    /// 判断两个方法名是否命中同一注册表模式（高频方法名重复）。
    /// </summary>
    private static bool RegistryCollision(string nameA, string nameB, IReadOnlyList<string> registry)
    {
        foreach (var pattern in registry)
        {
            if (NameMatches(nameA, pattern) && NameMatches(nameB, pattern))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// 方法名通配符匹配（* 任意串，大小写敏感——C# 命名惯例）。
    /// </summary>
    private static bool NameMatches(string name, string pattern)
    {
        if (pattern == "*")
        {
            return true;
        }

        var regex = "^" + string.Join(".*", pattern.Split('*').Select(Regex.Escape)) + "$";
        return Regex.IsMatch(name, regex);
    }

    /// <summary>
    /// 加载仓库成员快照（gitignore 感知、豁免过滤）。
    /// </summary>
    private static List<CodeMember> LoadMembers(string repoPath, IReadOnlyList<ExclusionRule> exclusions)
    {
        var files = TargetFileCollector.Collect(repoPath, CheckMode.All, CodeExtensions);
        var members = new List<CodeMember>();
        foreach (var file in files)
        {
            if (exclusions.Any(e => GlobMatcher.IsMatch(file, e.Path)))
            {
                continue;
            }

            var text = File.ReadAllText(Path.Combine(repoPath, file.Replace('/', Path.DirectorySeparatorChar)));
            members.AddRange(MemberParserRegistry.Parse(text, file));
        }

        return members;
    }

    /// <summary>
    /// 成员顺序比较（文件名 + 行号）：靠前者视作"已有实现"，靠后者视作"后出现的重复"。
    /// </summary>
    private static int CompareMemberOrder(CodeMember a, CodeMember b)
    {
        var byFile = string.Compare(a.File, b.File, StringComparison.OrdinalIgnoreCase);
        return byFile != 0 ? byFile : a.Line.CompareTo(b.Line);
    }

    /// <summary>
    /// 成员键：TypeName.Name@File（不含行号——行号漂移不改变身份）。
    /// </summary>
    private static string MemberKeyOf(CodeMember member)
    {
        return $"{member.TypeName}.{member.Name}@{member.File}";
    }

    /// <summary>
    /// 检出的重复对（目标=违规记账方，参照=建议复用的已有实现）。
    /// </summary>
    private sealed record DetectedPair(
        string Key,
        CodeMember Target,
        CodeMember Reference,
        double Similarity,
        bool ByName);
}
