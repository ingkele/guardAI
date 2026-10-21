using GuardAI.Core.Config;
using GuardAI.Core.Engine;

namespace GuardAI.Init;

/// <summary>
/// 项目语言生态。
/// </summary>
public enum ProjectKind
{
    /// <summary>.NET 项目（有 .csproj / .sln）。</summary>
    DotNet,

    /// <summary>Node 前端/全栈项目（有 package.json）。</summary>
    Node,

    /// <summary>未知生态，按通用骨架处理。</summary>
    Unknown,
}

/// <summary>
/// 项目形态。
/// </summary>
public enum ProjectTypeKind
{
    /// <summary>应用（可运行的服务/站点/程序）：落位规则 scope: app 适用。</summary>
    App,

    /// <summary>类库（被引用的框架/包）：编码习惯与应用不同，scope: app 的落位规则不适用。</summary>
    Library,
}

/// <summary>
/// 项目骨架写法。
/// </summary>
public enum SkeletonKind
{
    /// <summary>项目即分层：src/MyApp.Domain/、src/MyApp.Application/。</summary>
    ProjectAsLayer,

    /// <summary>嵌套分层：src/MyApp/Domain/、src/MyApp/Application/。</summary>
    NestedLayer,

    /// <summary>通用骨架（工具/类库型仓库，如 guardAI 自身）。</summary>
    Generic,
}

/// <summary>
/// 顶层目录信息：目录名 + 按内容推断的用途说明（用于生成目录白名单）。
/// </summary>
/// <param name="Name">目录名（相对仓库根）。</param>
/// <param name="Purpose">按目录内容推断的用途说明，写入白名单给人和 AI 看。</param>
public sealed record TopDirectoryInfo(string Name, string Purpose);

/// <summary>
/// 项目扫描结果：guardAI init 的装配依据。
/// </summary>
/// <param name="RepoPath">仓库根目录。</param>
/// <param name="ProjectName">项目名。</param>
/// <param name="Kind">语言生态。</param>
/// <param name="ProjectType">项目形态（app/library，落位规则 scope 的过滤依据）。</param>
/// <param name="Skeleton">骨架写法。</param>
/// <param name="SolutionFiles">解决方案文件。</param>
/// <param name="ProjectFileCount">项目文件数。</param>
/// <param name="CodeFileExtensions">仓库实测到的代码文件扩展名（生成 include_extensions 的依据）。</param>
/// <param name="TopDirectories">顶层目录信息。</param>
/// <param name="KnownDirectories">目录基线快照。</param>
/// <param name="HasRootCodeFiles">根目录是否有代码文件。</param>
/// <param name="HasGitHubWorkflow">是否有 GitHub 工作流。</param>
/// <param name="HasGiteeWorkflow">是否有 Gitee 工作流。</param>
/// <param name="HasPreCommitHook">是否有 pre-commit 钩子。</param>
/// <param name="HasClaudeSettings">是否有 Claude 设置。</param>
/// <param name="HasCodexConfig">是否有 Codex 配置。</param>
/// <param name="HasAgentsMd">是否有 AGENTS.md。</param>
/// <param name="HasGuardAiConfig">是否已有 guardai.yml。</param>
/// <param name="HasTestFixtures">是否有测试固定现场。</param>
/// <param name="ProjectTypeOverrides">项目形态按路径覆盖（多项目仓库混 app/library 时与仓库级形态不一致的项目目录，v0.3.1-⑤）。</param>
public sealed record ProjectScanResult(
    string RepoPath,
    string ProjectName,
    ProjectKind Kind,
    ProjectTypeKind ProjectType,
    SkeletonKind Skeleton,
    IReadOnlyList<string> SolutionFiles,
    int ProjectFileCount,
    IReadOnlyList<string> CodeFileExtensions,
    IReadOnlyList<TopDirectoryInfo> TopDirectories,
    IReadOnlyList<string> KnownDirectories,
    bool HasRootCodeFiles,
    bool HasGitHubWorkflow,
    bool HasGiteeWorkflow,
    bool HasPreCommitHook,
    bool HasClaudeSettings,
    bool HasCodexConfig,
    bool HasAgentsMd,
    bool HasGuardAiConfig,
    bool HasTestFixtures,
    IReadOnlyList<ProjectTypeOverride> ProjectTypeOverrides);

/// <summary>
/// 项目扫描器：识别语言生态、骨架写法、已有闸口与配置。
/// </summary>
/// <remarks>纯确定性目录探测，不调用任何模型——AI 装配层只做增强（现阶段走模板装配）。</remarks>
public static class ProjectScanner
{
    /// <summary>
    /// 扫描目标仓库。
    /// </summary>
    /// <param name="repoPath">目标仓库根目录。</param>
    /// <returns>扫描结果。</returns>
    /// <exception cref="DirectoryNotFoundException">目录不存在时抛出。</exception>
    public static ProjectScanResult Scan(string repoPath)
    {
        if (!Directory.Exists(repoPath))
        {
            throw new DirectoryNotFoundException($"目标目录不存在: {repoPath}");
        }

        var root = Path.GetFullPath(repoPath);
        var ignore = GitIgnoreFilter.Load(root);
        var solutionFiles = Directory.GetFiles(root, "*.sln", SearchOption.TopDirectoryOnly)
            .Select(Path.GetFileName)
            .Where(n => n is not null)
            .Cast<string>()
            .ToList();

        var projectFileCount = Directory.EnumerateFiles(root, "*.csproj", SearchOption.AllDirectories)
            .Count(f => !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")
                     && !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"));

        var kind = ProjectKind.Unknown;
        if (projectFileCount > 0 || solutionFiles.Count > 0)
        {
            kind = ProjectKind.DotNet;
        }
        else if (File.Exists(Path.Combine(root, "package.json")))
        {
            kind = ProjectKind.Node;
        }

        var topDirectories = ScanTopDirectories(root, ignore);
        var knownDirectories = ScanKnownDirectories(root, topDirectories.Select(d => d.Name), ignore);
        var hasRootCodeFiles = Directory.EnumerateFiles(root)
            .Any(f => CodeFileExtensions.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase));

        // 实测扩展名（F3）：按仓库实际存在的代码文件扩展名生成 include_extensions，
        // 绕过"生态探测只看根目录"的盲区（如 package.json 在 backend/ 子目录的仓库）
        var observedExtensions = ScanCodeFileExtensions(root, ignore);
        var (projectType, projectTypeOverrides) = DetectProjectTypes(root, ignore);

        return new ProjectScanResult(
            root,
            Path.GetFileName(Path.TrimEndingDirectorySeparator(root)),
            kind,
            projectType,
            DetectSkeleton(root),
            solutionFiles,
            projectFileCount,
            observedExtensions,
            topDirectories,
            knownDirectories,
            hasRootCodeFiles,
            HasAnyFile(Path.Combine(root, ".github", "workflows"), "*.yml"),
            Directory.Exists(Path.Combine(root, ".workflow")),
            File.Exists(Path.Combine(root, ".git", "hooks", "pre-commit")),
            File.Exists(Path.Combine(root, ".claude", "settings.json")),
            File.Exists(Path.Combine(root, ".codex", "config.toml")),
            File.Exists(Path.Combine(root, "AGENTS.md")),
            File.Exists(Path.Combine(root, "guardai.yml")),
            Directory.EnumerateDirectories(root, "Fixtures", SearchOption.AllDirectories).Any(),
            projectTypeOverrides);
    }

    /// <summary>代码文件扩展名（用于识别"目录里装的是代码"，含主流前后端生态）。</summary>
    private static readonly string[] CodeFileExtensions =
        [".cs", ".csproj", ".ts", ".tsx", ".js", ".jsx", ".vue", ".mjs", ".cjs", ".py", ".go", ".java"];

    /// <summary>
    /// 扫描仓库真实顶层目录并推断用途——这是"首跑零误报"的关键：
    /// 白名单不是写死的预设，而是 init 时刻仓库既有骨架的基线快照。
    /// </summary>
    /// <param name="repoPath">仓库根目录。</param>
    /// <param name="ignore">gitignore 过滤器（被忽略目录不算骨架）。</param>
    /// <returns>含代码文件的顶层目录信息列表（噪声目录与 gitignore 已排除）。</returns>
    private static List<TopDirectoryInfo> ScanTopDirectories(string repoPath, GitIgnoreFilter ignore)
    {
        var result = new List<TopDirectoryInfo>();
        foreach (var dir in Directory.EnumerateDirectories(repoPath))
        {
            var name = Path.GetFileName(dir);
            if (name is null
                || TargetFileCollector.NoiseDirectories.Contains(name, StringComparer.OrdinalIgnoreCase)
                || ignore.IsIgnored(name, isDirectory: true))
            {
                continue;
            }

            // 收录两类目录：
            // ① 装代码的目录（检查以代码文件为准）；
            // ② 完全空的目录树（git 可以有空目录约定，漏出快照会被误报"AI 新建"，必须进基线）。
            // 有文件但无代码的目录（纯文档/资产）维持不收——检查只看代码文件。
            var allFiles = Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories).ToList();
            if (allFiles.Count > 0
                && !allFiles.Any(f => CodeFileExtensions.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase)))
            {
                continue;
            }

            result.Add(new TopDirectoryInfo(name, InferPurpose(dir)));
        }

        return result;
    }

    /// <summary>
    /// 生成 init 时刻的目录快照（基线白名单的精确依据）：
    /// 被采纳的顶层目录及其全部子孙目录，逐层记录——之后 AI 新建的目录不在快照内，天然被门禁拦下。
    /// </summary>
    /// <param name="repoPath">仓库根目录。</param>
    /// <param name="topNames">已采纳的顶层目录名。</param>
    /// <param name="ignore">gitignore 过滤器。</param>
    /// <returns>目录相对路径列表（正斜杠，排序稳定）。</returns>
    private static List<string> ScanKnownDirectories(string repoPath, IEnumerable<string> topNames, GitIgnoreFilter ignore)
    {
        var result = new List<string>();
        foreach (var top in topNames)
        {
            result.Add(top);
            var topFull = Path.Combine(repoPath, top);
            foreach (var dir in Directory.EnumerateDirectories(topFull, "*", SearchOption.AllDirectories))
            {
                var rel = Path.GetRelativePath(repoPath, dir).Replace('\\', '/');
                var segments = rel.Split('/');
                if (segments.Any(s => TargetFileCollector.NoiseDirectories.Contains(s, StringComparer.OrdinalIgnoreCase))
                    || ignore.IsIgnored(rel, isDirectory: true))
                {
                    continue;
                }

                result.Add(rel);
            }
        }

        result.Sort(StringComparer.OrdinalIgnoreCase);
        return result;
    }

    /// <summary>
    /// 收集仓库实际存在的代码文件扩展名（F3 生态感知）：
    /// init 时实测，绕开"生态探测只看根目录 package.json"的盲区（如 package.json 在子目录的仓库）。
    /// </summary>
    /// <param name="repoPath">仓库根目录。</param>
    /// <param name="ignore">gitignore 过滤器。</param>
    /// <returns>实测到的扩展名（小写、带点、去重排序）；仓库无代码文件时为空列表（由生成器回落默认）。</returns>
    private static List<string> ScanCodeFileExtensions(string repoPath, GitIgnoreFilter ignore)
    {
        var observed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in Directory.EnumerateFiles(repoPath, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(repoPath, file).Replace('\\', '/');
            var segments = rel.Split('/');
            if (segments.Any(s => TargetFileCollector.NoiseDirectories.Contains(s, StringComparer.OrdinalIgnoreCase))
                || ignore.IsIgnored(rel))
            {
                continue;
            }

            var ext = Path.GetExtension(file);
            if (CodeFileExtensions.Contains(ext, StringComparer.OrdinalIgnoreCase))
            {
                observed.Add(ext.ToLowerInvariant());
            }
        }

        return observed.Order(StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>
    /// 判定项目形态（F4 落位规则 scope 的过滤依据）：
    /// 非 samples/tests 路径下有 Web/Worker/Exe 项目 → app；只有类库项目 → library；
    /// 判不准时保守取 library（落位规则对类库失配率高，宁可少查不冤枉）。
    /// 同时按项目目录输出形态覆盖建议（v0.3.1-⑤）：多项目仓库混 app/library 时，
    /// 与仓库级形态不一致的项目目录进 project_type_overrides（根目录项目无目录边界，不产覆盖）。
    /// </summary>
    /// <param name="repoPath">仓库根目录。</param>
    /// <param name="ignore">gitignore 过滤器。</param>
    /// <returns>仓库级形态 + 形态覆盖建议（按路径排序，输出稳定）。</returns>
    private static (ProjectTypeKind RepoType, IReadOnlyList<ProjectTypeOverride> Overrides) DetectProjectTypes(string repoPath, GitIgnoreFilter ignore)
    {
        var appSignals = new[] { "Microsoft.NET.Sdk.Web", "Microsoft.NET.Sdk.Worker", "<OutputType>Exe</OutputType>", "<OutputType>WinExe</OutputType>" };

        // 示例/测试/模板/验证工程是"演示用的应用"，不代表仓库本体形态（WitchC 回归实测：templates/.verify 的示例项目曾把类库仓库误判为 app）
        var excludedSegments = new[]
        {
            "samples", "sample", "examples", "example", "demos", "demo",
            "tests", "test", "benchmarks", "benchmark",
            "templates", "template", ".verify", "verify", "fixtures",
        };

        // 按项目目录归组：目录内任一项目带 app 信号 → 该目录是 app
        var dirIsApp = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        var hasLibraryProject = false;
        foreach (var proj in Directory.EnumerateFiles(repoPath, "*.csproj", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(repoPath, proj).Replace('\\', '/');
            var segments = rel.Split('/');
            if (segments.Any(s => TargetFileCollector.NoiseDirectories.Contains(s, StringComparer.OrdinalIgnoreCase))
                || ignore.IsIgnored(rel))
            {
                continue;
            }

            // 示例/测试工程是"演示用的应用"，不代表仓库本体形态
            if (segments[..^1].Any(s => excludedSegments.Contains(s, StringComparer.OrdinalIgnoreCase)))
            {
                continue;
            }

            var isApp = appSignals.Any(sig => File.ReadAllText(proj).Contains(sig, StringComparison.OrdinalIgnoreCase));
            var dir = string.Join('/', segments[..^1]);
            dirIsApp[dir] = (dirIsApp.TryGetValue(dir, out var existing) && existing) || isApp;
            if (!isApp)
            {
                hasLibraryProject = true;
            }
        }

        // 无 .csproj 的生态（Node 等）按 app 处理：应用型仓库为主流，落位规则默认应生效
        var repoType = dirIsApp.Values.Any(v => v)
            ? ProjectTypeKind.App
            : hasLibraryProject ? ProjectTypeKind.Library : ProjectTypeKind.App;

        // 形态覆盖建议：只列与仓库级形态不一致的项目目录（一致的不用写，回落即可）
        var repoText = repoType == ProjectTypeKind.App ? "app" : "library";
        var overrides = dirIsApp
            .Where(kv => kv.Key.Length > 0)
            .Select(kv => new ProjectTypeOverride($"{kv.Key}/**", kv.Value ? "app" : "library"))
            .Where(o => o.ProjectType != repoText)
            .OrderBy(o => o.Path, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return (repoType, overrides);
    }

    /// <summary>
    /// 按目录实际内容推断用途说明（写进白名单的 purpose 字段，违规时回显给人和 AI）。
    /// 目录名语义优先（backend/ 就是后端工程，不因里面有 package.json 就标"前端工程"），内容兜底。
    /// </summary>
    /// <param name="dir">待推断的目录。</param>
    /// <returns>用途说明。</returns>
    private static string InferPurpose(string dir)
    {
        // ① 目录名优先：常见命名自带明确语义，比内容探测更可靠
        var name = Path.GetFileName(dir);
        if (name is not null)
        {
            if (name.Equals("backend", StringComparison.OrdinalIgnoreCase)
                || name.Equals("server", StringComparison.OrdinalIgnoreCase)
                || name.Equals("api", StringComparison.OrdinalIgnoreCase))
            {
                return "后端工程";
            }

            if (name.Equals("frontend", StringComparison.OrdinalIgnoreCase)
                || name.Equals("web", StringComparison.OrdinalIgnoreCase)
                || name.Equals("client", StringComparison.OrdinalIgnoreCase))
            {
                return "前端工程";
            }
        }

        // ② 内容兜底：目录名无明确语义时按实际文件推断
        bool Has(string pattern) => Directory.EnumerateFiles(dir, pattern, SearchOption.AllDirectories).Any();

        if (!Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories).Any())
        {
            return "空目录（既有骨架，预留）";
        }

        if (Has("*.csproj") || Has("*.sln"))
        {
            return "源码模块";
        }

        if (Has("*.cs"))
        {
            return "代码";
        }

        if (Has("package.json"))
        {
            return "前端工程";
        }

        return "代码目录";
    }

    /// <summary>
    /// 探测骨架写法：src 下按项目名后缀分层 → 项目即分层；src 下按层名目录 → 嵌套分层；否则通用。
    /// </summary>
    private static SkeletonKind DetectSkeleton(string root)
    {
        var src = Path.Combine(root, "src");
        if (!Directory.Exists(src))
        {
            return SkeletonKind.Generic;
        }

        var layerSuffixes = new[] { ".Domain", ".Application", ".Infrastructure", ".Web" };
        var topDirs = Directory.GetDirectories(src).Select(Path.GetFileName).Where(n => n is not null).Cast<string>().ToList();

        if (topDirs.Any(d => layerSuffixes.Any(s => d.EndsWith(s, StringComparison.OrdinalIgnoreCase))))
        {
            return SkeletonKind.ProjectAsLayer;
        }

        var layerNames = new[] { "Domain", "Application", "Infrastructure", "Web" };
        var nested = topDirs
            .Select(d => Path.Combine(src, d))
            .Where(Directory.Exists)
            .Any(d => Directory.GetDirectories(d).Select(Path.GetFileName).Where(n => n is not null).Cast<string>()
                .Any(sub => layerNames.Contains(sub, StringComparer.OrdinalIgnoreCase)));

        return nested ? SkeletonKind.NestedLayer : SkeletonKind.Generic;
    }

    /// <summary>
    /// 判断目录下是否存在匹配指定通配符的文件。
    /// </summary>
    private static bool HasAnyFile(string dir, string pattern)
    {
        return Directory.Exists(dir) && Directory.EnumerateFiles(dir, pattern).Any();
    }
}
