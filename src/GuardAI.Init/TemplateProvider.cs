using System.Reflection;

namespace GuardAI.Init;

/// <summary>
/// 闸口模板供应器：定位/读取闸口模板（磁盘 templates/ 优先，内嵌资源兜底）。
/// </summary>
/// <remarks>
/// M2-3：模板以嵌入资源打进 exe——单文件即完整产品，拷贝一个 exe 即可 init，
/// 不再要求 templates/ 目录随行。磁盘模板存在时优先磁盘（便于开发期改模板不重编译）。
/// 新增模板文件时必须同步登记 <see cref="KnownTemplates"/> 与 csproj 的 EmbeddedResource 条目。
/// </remarks>
public static class TemplateProvider
{
    /// <summary>内嵌资源名前缀（LogicalName 形如 guardai.templates.ci/github-actions.yml）。</summary>
    private const string ResourcePrefix = "guardai.templates.";

    /// <summary>模板相对路径清单（与 templates/ 目录内容一一对应）。</summary>
    public static readonly IReadOnlyList<string> KnownTemplates =
    [
        "ci/github-actions.yml",
        "ci/gitee-go-step.yml",
        "hooks/pre-commit",
        "hooks/claude-code.json",
        "hooks/codex.toml",
        "agents/AGENTS-snippet.md",
        "agents/workbuddy-skill.md",
    ];

    /// <summary>
    /// 定位磁盘模板目录：显式指定 → 程序输出目录 → 逐级向上查找。
    /// </summary>
    /// <param name="explicitDir">显式指定的模板目录（可空）。</param>
    /// <returns>模板目录绝对路径；磁盘上找不到返回 null（可回退内嵌资源）。</returns>
    public static string? FindOnDisk(string? explicitDir)
    {
        var candidates = new List<string>();
        if (!string.IsNullOrWhiteSpace(explicitDir))
        {
            candidates.Add(Path.Combine(explicitDir, "templates"));
            candidates.Add(explicitDir);
        }

        candidates.Add(Path.Combine(AppContext.BaseDirectory, "templates"));

        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 6 && dir is not null; i++)
        {
            candidates.Add(Path.Combine(dir.FullName, "templates"));
            dir = dir.Parent;
        }

        return candidates.FirstOrDefault(c =>
            Directory.Exists(c) && File.Exists(Path.Combine(c, "ci", "github-actions.yml")));
    }

    /// <summary>
    /// 解析出可用的模板目录：磁盘优先，磁盘缺失时把内嵌模板释放到临时目录。
    /// </summary>
    /// <param name="explicitDir">显式指定的模板目录（可空）。</param>
    /// <returns>模板目录绝对路径。</returns>
    /// <exception cref="DirectoryNotFoundException">磁盘与内嵌资源都不可用时抛出。</exception>
    public static string Resolve(string? explicitDir)
    {
        var onDisk = FindOnDisk(explicitDir);
        if (onDisk is not null)
        {
            return onDisk;
        }

        if (!HasEmbedded)
        {
            throw new DirectoryNotFoundException("找不到 templates 模板目录（可用 --templates 指定，或使用完整发布的 guardAI）");
        }

        return ExtractEmbedded();
    }

    /// <summary>内嵌模板是否齐备。</summary>
    public static bool HasEmbedded => KnownTemplates.All(t => ReadEmbedded(t) is not null);

    /// <summary>
    /// 从内嵌资源读取模板内容。
    /// </summary>
    /// <param name="relativePath">模板相对路径（如 ci/github-actions.yml）。</param>
    /// <returns>模板内容；未嵌入返回 null。</returns>
    public static string? ReadEmbedded(string relativePath)
    {
        var assembly = typeof(TemplateProvider).Assembly;
        using var stream = assembly.GetManifestResourceStream(ResourcePrefix + relativePath);
        if (stream is null)
        {
            return null;
        }

        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    /// <summary>
    /// 把全部内嵌模板释放到临时目录（幂等，可重复调用）。
    /// </summary>
    /// <param name="targetDir">目标目录（缺省为系统临时目录下 guardai-templates）。</param>
    /// <returns>目标目录绝对路径。</returns>
    /// <exception cref="InvalidOperationException">任一模板缺失时抛出。</exception>
    public static string ExtractEmbedded(string? targetDir = null)
    {
        var root = targetDir ?? Path.Combine(Path.GetTempPath(), "guardai-templates");
        foreach (var relative in KnownTemplates)
        {
            var content = ReadEmbedded(relative)
                ?? throw new InvalidOperationException($"内嵌模板缺失: {relative}");
            var full = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, content);
        }

        return root;
    }
}
