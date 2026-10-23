using System.Diagnostics;

namespace GuardAI.Core.Engine;

/// <summary>
/// 待检查文件收集器。
/// </summary>
/// <remarks>
/// 来源：移植自本项目自研的 guard-tool（PowerShell 版 Get-GuardTargetFiles），语义保持一致。
/// 关键点：changed 模式必须用 git status --porcelain -uall 展开未跟踪目录里的文件，
/// 否则会漏报（git 默认只报目录名）——这是 guard-tool 开发期踩过的真坑。
/// M2 新增：.gitignore 感知（被忽略文件不进检查）+ 噪声目录清单与 ProjectScanner 共用一份。
/// </remarks>
public static class TargetFileCollector
{
    /// <summary>不算项目骨架的噪声目录（构建产物/工具元数据/依赖缓存），与 init 扫描共用同一份清单。</summary>
    public static readonly string[] NoiseDirectories =
        [".git", ".github", ".workflow", ".claude", ".codex", ".idea", ".vs", ".vscode", ".workbuddy",
         "bin", "obj", "node_modules", "dist", "build", "target", ".guard"];

    /// <summary>
    /// 收集待检查的文件清单（仓库相对路径、正斜杠、去重排序）。
    /// </summary>
    /// <param name="repoPath">目标仓库根目录。</param>
    /// <param name="mode">收集模式。</param>
    /// <param name="includeExtensions">参与检查的扩展名（小写、带点）；null = 不限扩展名收集全部文件（密钥扫描用——.env / id_rsa 等凭据文件常无代码扩展名）。</param>
    /// <returns>文件相对路径列表。</returns>
    /// <exception cref="InvalidOperationException">changed 模式下目标不是 git 仓库时抛出。</exception>
    public static IReadOnlyList<string> Collect(string repoPath, CheckMode mode, IReadOnlyList<string>? includeExtensions)
    {
        var files = new List<string>();
        if (mode == CheckMode.Changed)
        {
            CollectChanged(repoPath, files);
        }
        else
        {
            CollectAll(repoPath, files);
        }

        var ignore = GitIgnoreFilter.Load(repoPath);
        var filtered = files
            .Where(f => includeExtensions is null || includeExtensions.Contains(Path.GetExtension(f).ToLowerInvariant()))
            .Where(f => !ignore.IsIgnored(f))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
            .ToList();
        return filtered;
    }

    /// <summary>
    /// changed 模式：从 git status 收集新增/改动文件（跳过已删除）。
    /// </summary>
    private static void CollectChanged(string repoPath, List<string> files)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "git",
            Arguments = "status --porcelain -uall",
            WorkingDirectory = repoPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException("无法启动 git，请确认已安装 git 并在 PATH 中。");
        var output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException("目标目录不是 git 仓库，changed 模式不可用。请在 git 仓库中运行，或改用 --mode all");
        }

        foreach (var entry in output.Split('\n'))
        {
            var s = entry.TrimEnd('\r');
            if (s.Length < 4)
            {
                continue;
            }

            var status = s[..2];
            if (status.Contains('D'))
            {
                continue; // 已删除文件不检查
            }

            var path = s[3..];
            if (path.Contains(" -> "))
            {
                path = path.Split(" -> ")[^1];
            }

            path = path.Trim('"').Replace('\\', '/');
            files.Add(path);
        }
    }

    /// <summary>
    /// all 模式：递归遍历仓库文件（跳过噪声目录）。
    /// </summary>
    private static void CollectAll(string repoPath, List<string> files)
    {
        foreach (var file in Directory.EnumerateFiles(repoPath, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(repoPath, file).Replace('\\', '/');
            if (IsInNoiseDirectory(rel))
            {
                continue;
            }

            files.Add(rel);
        }
    }

    /// <summary>
    /// 判断相对路径是否位于噪声目录内（任一层命中即算）。
    /// </summary>
    private static bool IsInNoiseDirectory(string relativePath)
    {
        var segments = relativePath.Split('/');
        for (var i = 0; i < segments.Length - 1; i++)
        {
            if (NoiseDirectories.Contains(segments[i], StringComparer.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
