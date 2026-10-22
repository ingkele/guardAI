using System.Reflection;

namespace GuardAI.Init;

/// <summary>
/// guardAI 程序调用命令解析：闸口模板里的占位符在安装时替换为实际调用命令。
/// 优先级：自包含单文件 exe 绝对路径 → dotnet 宿主 + 入口程序集 → PATH 名 guardAI 兜底。
/// 解决"钩子裸调 guardAI 依赖 PATH"问题——装到哪台机器都能直接跑。
/// </summary>
public static class GuardAiCommand
{
    /// <summary>闸口模板中的调用命令占位符。</summary>
    public const string Placeholder = "{{GUARDAI_CMD}}";

    /// <summary>
    /// 解析当前运行的 guardAI 调用命令（钩子/脚本可直接执行的字符串）。
    /// </summary>
    /// <returns>调用命令（绝对路径优先并带引号；解析不出时回落 PATH 名 guardAI）。</returns>
    public static string Resolve()
    {
        // 单文件自包含发布：入口程序集 Location 为空，进程路径即 GuardAI.Cli.exe
        var entry = Assembly.GetEntryAssembly()?.Location;
        if (string.IsNullOrEmpty(entry))
        {
            var process = Environment.ProcessPath;
            return string.IsNullOrEmpty(process) ? "guardAI" : Quote(process);
        }

        // 框架依赖 / dotnet run：入口是 GuardAI.Cli.dll，借 dotnet 宿主启动
        return entry.EndsWith("GuardAI.Cli.dll", StringComparison.OrdinalIgnoreCase)
            ? $"dotnet {Quote(entry)}"
            : "guardAI";
    }

    /// <summary>
    /// 渲染模板文本：把调用命令占位符替换为实际命令。
    /// </summary>
    /// <param name="templateContent">含占位符的模板文本。</param>
    /// <returns>占位符已替换的文本。</returns>
    public static string Render(string templateContent)
    {
        return templateContent.Replace(Placeholder, Resolve(), StringComparison.Ordinal);
    }

    /// <summary>
    /// 引用路径（统一正斜杠，Git Bash / PowerShell 双可用）。
    /// </summary>
    /// <param name="path">文件路径。</param>
    /// <returns>带双引号的正斜杠路径。</returns>
    private static string Quote(string path)
    {
        return $"\"{path.Replace('\\', '/')}\"";
    }
}
