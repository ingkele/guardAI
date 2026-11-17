namespace GuardAI.Tests;

/// <summary>
/// 测试共用临时仓库夹具：按相对路径生成文件/目录，退出时整体清理。
/// </summary>
/// <remarks>
/// 抽骨架合一（2026-10-22）：原 ScannerFixture / TempFixture / SecretFixture 三家高度重复，
/// 合一为本类——路径解析只留 <see cref="Resolve"/> 一份骨架，写文件/建目录/清理各自单一职责。
/// </remarks>
internal sealed class TempRepoFixture : IDisposable
{
    /// <summary>
    /// 创建临时仓库目录（可选批量生成占位文件）。
    /// </summary>
    /// <param name="relativePaths">要批量生成的文件相对路径（内容为占位注释）；不传则空目录起步。</param>
    public TempRepoFixture(params string[] relativePaths)
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "guardai-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
        foreach (var relativePath in relativePaths)
        {
            AddFile(relativePath, "// 测试夹具");
        }
    }

    /// <summary>
    /// 临时仓库根目录绝对路径。
    /// </summary>
    public string Path { get; }

    /// <summary>
    /// 按相对路径写文件内容（自动创建父目录）。
    /// </summary>
    /// <param name="relativePath">相对路径（/ 分隔）。</param>
    /// <param name="content">文件内容。</param>
    public void AddFile(string relativePath, string content)
    {
        var full = Resolve(relativePath);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
    }

    /// <summary>
    /// 按相对路径创建目录（含多层）。
    /// </summary>
    /// <param name="relativePath">相对路径（/ 分隔）。</param>
    public void AddDirectory(string relativePath)
    {
        Directory.CreateDirectory(Resolve(relativePath));
    }

    /// <summary>
    /// 把临时目录初始化成 git 仓库并提交一次（changed 模式场景用）。
    /// </summary>
    /// <remarks>
    /// 用 <c>git init -q</c> + 显式 user 配置：CI 机器常无全局 git 身份，
    /// 不显式给会导致 commit 失败、测试看似"没改动"而假绿。
    /// </remarks>
    public void InitGitRepository()
    {
        RunGit("init", "-q", ".");
        RunGit("config", "user.email", "guardai-tests@example.com");
        RunGit("config", "user.name", "guardai-tests");
        RunGit("config", "commit.gpgsign", "false");
    }

    /// <summary>
    /// 把当前全部改动提交进 git（构造"已提交的基线"）。
    /// </summary>
    /// <param name="message">提交信息。</param>
    public void CommitAll(string message = "baseline")
    {
        RunGit("add", "-A");
        RunGit("commit", "-q", "-m", message);
    }

    /// <summary>
    /// 在指定目录（相对临时根）执行 git 命令，失败时抛出带 stderr 的异常。
    /// </summary>
    /// <param name="arguments">git 参数。</param>
    public void RunGit(params string[] arguments)
    {
        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = Path,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var argument in arguments)
        {
            psi.ArgumentList.Add(argument);
        }

        using var process = System.Diagnostics.Process.Start(psi)
            ?? throw new InvalidOperationException("测试需要 git 在 PATH 中");
        var stderr = process.StandardError.ReadToEnd();
        process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"git {string.Join(' ', arguments)} 失败：{stderr}");
        }
    }

    /// <summary>
    /// 清理临时目录（清理失败不影响测试结论）。
    /// </summary>
    /// <remarks>
    /// git 仓库场景（<see cref="InitGitRepository"/>）下 .git 内有只读对象文件，
    /// 且 git 进程刚退出时可能短暂占用；先清只读位再删，仍失败就放弃
    /// （Windows 上残留临时目录远好过让断言失败变假红）。
    /// </remarks>
    public void Dispose()
    {
        if (!Directory.Exists(Path))
        {
            return;
        }

        ClearReadOnly(Path);
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 清理失败不影响测试结论（残留目录由系统临时目录清理策略回收）
        }
    }

    /// <summary>
    /// 递归清掉只读属性（.git 对象文件默认只读，Windows 上会挡住删除）。
    /// </summary>
    /// <param name="directory">目录绝对路径。</param>
    private static void ClearReadOnly(string directory)
    {
        try
        {
            foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
            {
                try
                {
                    var attributes = File.GetAttributes(file);
                    if ((attributes & FileAttributes.ReadOnly) != 0)
                    {
                        File.SetAttributes(file, attributes & ~FileAttributes.ReadOnly);
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // 单个文件处理失败不阻断整体清理
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 目录枚举失败：交给上层 Delete 的容错
        }
    }

    /// <summary>
    /// 相对路径换算为临时仓库内的绝对路径（唯一的路径解析骨架）。
    /// </summary>
    /// <param name="relativePath">相对路径（/ 分隔）。</param>
    /// <returns>绝对路径。</returns>
    private string Resolve(string relativePath)
    {
        return System.IO.Path.Combine(Path, relativePath.Replace('/', System.IO.Path.DirectorySeparatorChar));
    }
}
