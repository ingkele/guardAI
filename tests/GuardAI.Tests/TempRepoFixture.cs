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
    /// 清理临时目录（清理失败不影响测试结论）。
    /// </summary>
    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch (IOException)
        {
            // 清理失败不影响测试结论（含目录已不存在）
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
