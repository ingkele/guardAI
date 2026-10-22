using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace GuardAI.AI.Review;

/// <summary>
/// 送审差异收集器：从 git 工作区收集本次改动（含未跟踪的新文件）。
/// </summary>
/// <remarks>
/// 安全边界：送审内容会外发给配置的模型服务（可能是第三方），因此疑似密钥/凭据的
/// 文件与内容一律不外发——宁可少审一个文件，也不能把密钥送出本机。
/// </remarks>
public static class DiffCollector
{
    /// <summary>单个新增文件送审的最大字符数（截断阶段按 UTF-16 字符数计，防止巨型文件吃满上下文）。</summary>
    private const int MaxFileChars = 20000;

    /// <summary>单个新增文件的预筛上限（按字节数计）：超过则整体跳过，避免把大文件整读进内存；与 <see cref="MaxFileChars"/> 是"字节预筛 / 字符截断"两个不同阶段。</summary>
    private const long MaxFileBytes = 1_000_000;

    /// <summary>疑似密钥文件名模式（文件名层面拦截，不读内容；锚定路径段边界与行尾，不误伤 .environment / CredentialsXxx.cs 等普通命名）。</summary>
    private static readonly Regex SensitiveNamePattern = new(
        @"(^|/)(\.env(\..+)?|credentials(\.[a-z]+)?|secrets?\.(yml|yaml|json)|id_(rsa|dsa|ecdsa|ed25519)(\..+)?)$" +
        @"|(\.(pem|key|p12|pfx|jks|keystore|ppk|kdbx)$)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>疑似密钥所在目录（整目录不外发；锚定路径段边界防误伤）。</summary>
    private static readonly Regex SensitiveDirPattern = new(
        @"(^|/)(\.aws|\.ssh|\.gnupg|\.kube)/",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>内容密钥特征（PEM 私钥头、长 API 令牌、AWS AccessKey）。</summary>
    private static readonly Regex SecretContentPattern = new(
        @"(BEGIN [A-Z ]*PRIVATE KEY)|(sk-[A-Za-z0-9_-]{20,})|(AKIA[0-9A-Z]{16})",
        RegexOptions.Compiled);

    /// <summary>diff 段头的文件路径提取模式（取 b/ 侧）。</summary>
    private static readonly Regex DiffPathPattern = new(
        @"^diff --git a/\S+ b/(\S+)$",
        RegexOptions.Compiled);

    /// <summary>合法收集模式（单一事实来源：CLI 与收集器共用同一校验；只读防外部改写）。</summary>
    public static IReadOnlyList<string> SupportedModes { get; } = ["changed", "staged", "base", "all"];

    /// <summary>
    /// 收集送审差异文本。
    /// </summary>
    /// <param name="repoPath">仓库根目录。</param>
    /// <param name="mode">收集模式：changed（工作区+暂存区相对 HEAD）/ staged（仅暂存区）/ base（相对指定引用）/ all（全量代码文件，无需 git）。</param>
    /// <param name="baseRef">base 模式下的 git 引用（如 origin/main）。</param>
    /// <param name="cancellationToken">取消令牌（Ctrl+C 贯通到 git 收集阶段，不只模型调用）。</param>
    /// <param name="isCodeFile">all 模式的代码文件过滤谓词（仓库相对路径，正斜杠）；null 表示不限扩展名收全部文本文件。由调用方注入（如成员解析器的扩展名注册表），收集器不依赖语言知识。</param>
    /// <returns>差异文本与统计（文本、文件数）。</returns>
    /// <exception cref="FormatException">收集模式非法。</exception>
    /// <exception cref="InvalidOperationException">git 执行失败或没有可审查的改动。</exception>
    /// <exception cref="OperationCanceledException">收集被取消。</exception>
    public static async Task<(string Text, int Files)> CollectAsync(
        string repoPath,
        string mode,
        string? baseRef = null,
        CancellationToken cancellationToken = default,
        Func<string, bool>? isCodeFile = null)
    {
        if (!SupportedModes.Contains(mode))
        {
            throw new FormatException($"未知收集模式: {mode}（可用：{string.Join(" / ", SupportedModes)}）");
        }

        if (mode == "all")
        {
            return CollectAllFiles(repoPath, isCodeFile, cancellationToken);
        }

        var diff = mode switch
        {
            "staged" => await RunGitAsync(repoPath, cancellationToken, "diff", "--cached", "--unified=3").ConfigureAwait(false),
            "base" when !string.IsNullOrWhiteSpace(baseRef) => await RunGitAsync(repoPath, cancellationToken, "diff", baseRef!, "--unified=3").ConfigureAwait(false),
            "base" => throw new InvalidOperationException("base 模式必须提供 --base <git 引用>（如 --base origin/main）。"),
            _ => await RunGitAsync(repoPath, cancellationToken, "diff", "HEAD", "--unified=3").ConfigureAwait(false),
        };

        var sb = new StringBuilder();
        var skipped = new List<string>();

        // diff 按文件段切分：敏感文件的段整体不外发（已跟踪文件的改动同样可能是密钥）
        var files = AppendDiffSegments(sb, diff, skipped);

        // 未跟踪的新文件（AI 产出的新增代码常常还没 git add，必须纳入审查）
        if (mode is "changed" or "base")
        {
            var untracked = (await RunGitAsync(repoPath, cancellationToken, "ls-files", "--others", "--exclude-standard").ConfigureAwait(false))
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            foreach (var relative in untracked)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var full = Path.Combine(repoPath, relative.Replace('/', Path.DirectorySeparatorChar));
                if (!File.Exists(full) || new FileInfo(full).Length > MaxFileBytes)
                {
                    continue;
                }

                if (IsSensitive(relative))
                {
                    skipped.Add(relative);
                    continue;
                }

                var content = File.ReadAllText(full);
                if (content.Contains('\0'))
                {
                    continue; // 二进制文件跳过
                }

                if (SecretContentPattern.IsMatch(content))
                {
                    skipped.Add(relative);
                    continue; // 内容疑似含密钥：宁可漏审也不外发
                }

                if (content.Length > MaxFileChars)
                {
                    content = content[..MaxFileChars] + "\n…（文件过长已截断）";
                }

                sb.AppendLine($"--- 新增文件 {relative}（完整内容）---");
                sb.AppendLine(content);
                files++;
            }
        }

        // 只列被跳过的文件名（不发内容），让模型与用户知道有文件未送审
        if (skipped.Count > 0)
        {
            sb.AppendLine($"--- 以下 {skipped.Count} 个文件疑似含密钥/凭据，已跳过送审（内容不出本机）：{string.Join(", ", skipped)} ---");
        }

        if (files == 0)
        {
            // 区分两种零文件：改动全被安全过滤跳过 vs 压根没有改动，提示要指对方向
            throw new InvalidOperationException(skipped.Count > 0
                ? $"本次改动的 {skipped.Count} 个文件均疑似含密钥/凭据已跳过送审（{string.Join(", ", skipped)}），无可审查内容。"
                : $"没有可审查的改动（模式：{mode}）。先改代码再跑 review。");
        }

        return (sb.ToString(), files);
    }

    /// <summary>
    /// all 模式：全量收集目录内代码文件送审（无需 git，散文件/案例目录直接可审）。
    /// </summary>
    /// <param name="repoPath">目标根目录。</param>
    /// <param name="isCodeFile">代码文件过滤谓词（仓库相对路径，正斜杠）；null 收全部文本文件。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>送审文本与文件数。</returns>
    /// <exception cref="InvalidOperationException">没有可审查的代码文件。</exception>
    private static (string Text, int Files) CollectAllFiles(string repoPath, Func<string, bool>? isCodeFile, CancellationToken cancellationToken)
    {
        var sb = new StringBuilder();
        var skipped = new List<string>();
        var files = 0;

        foreach (var full in EnumerateSourceFiles(repoPath, cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var relative = Path.GetRelativePath(repoPath, full).Replace('\\', '/');
            if (isCodeFile is not null && !isCodeFile(relative))
            {
                continue;
            }

            if (new FileInfo(full).Length > MaxFileBytes)
            {
                continue;
            }

            if (IsSensitive(relative))
            {
                skipped.Add(relative);
                continue;
            }

            var content = File.ReadAllText(full);
            if (content.Contains('\0'))
            {
                continue; // 二进制文件跳过
            }

            if (SecretContentPattern.IsMatch(content))
            {
                skipped.Add(relative);
                continue; // 内容疑似含密钥：宁可漏审也不外发
            }

            if (content.Length > MaxFileChars)
            {
                content = content[..MaxFileChars] + "\n…（文件过长已截断）";
            }

            sb.AppendLine($"--- 文件 {relative}（完整内容）---");
            sb.AppendLine(content);
            files++;
        }

        if (skipped.Count > 0)
        {
            sb.AppendLine($"--- 以下 {skipped.Count} 个文件疑似含密钥/凭据，已跳过送审（内容不出本机）：{string.Join(", ", skipped)} ---");
        }

        if (files == 0)
        {
            throw new InvalidOperationException(skipped.Count > 0
                ? $"本次改动的 {skipped.Count} 个文件均疑似含密钥/凭据已跳过送审（{string.Join(", ", skipped)}），无可审查内容。"
                : "目录里没有可送审的代码文件（模式：all）。确认目录没放错、扩展名在支持列表（如 .cs/.java/.ts/.go/.py）。");
        }

        return (sb.ToString(), files);
    }

    /// <summary>all 模式跳过的目录（版本库/构建产物/依赖包，收进送审只会稀释上下文）。</summary>
    private static readonly HashSet<string> ExcludedDirs = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", ".guard", ".vs", ".idea", ".workbuddy", "bin", "obj", "node_modules", "dist",
    };

    /// <summary>枚举目录树内全部文件（跳过构建/依赖目录，按序稳定输出保证回放可比）。</summary>
    private static IEnumerable<string> EnumerateSourceFiles(string repoPath, CancellationToken cancellationToken)
    {
        var results = new List<string>();
        var pending = new Stack<string>();
        pending.Push(repoPath);
        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var dir = pending.Pop();

            foreach (var sub in Directory.EnumerateDirectories(dir))
            {
                if (!ExcludedDirs.Contains(Path.GetFileName(sub)))
                {
                    pending.Push(sub);
                }
            }

            results.AddRange(Directory.EnumerateFiles(dir));
        }

        results.Sort(StringComparer.Ordinal); // 确定性顺序：同目录两次收集文本一致，回放可逐字比对
        return results;
    }

    /// <summary>
    /// 按文件段追加 diff 内容（敏感文件段跳过并记名）。
    /// </summary>
    /// <param name="sb">输出缓冲。</param>
    /// <param name="diff">git diff 原文。</param>
    /// <param name="skipped">被跳过的敏感文件清单（只记文件名）。</param>
    /// <returns>纳入送审的文件数。</returns>
    private static int AppendDiffSegments(StringBuilder sb, string diff, List<string> skipped)
    {
        var files = 0;
        foreach (var segment in SplitDiffSegments(diff))
        {
            var relative = ExtractDiffPath(segment);

            // 文件名命中敏感模式，或段内内容命中密钥特征（路径提取失败时的兜底闸），均不外发
            if ((relative is not null && IsSensitive(relative)) || SecretContentPattern.IsMatch(segment))
            {
                skipped.Add(relative ?? "(diff 段，路径不可辨)");
                continue;
            }

            sb.Append(segment);
            files++;
        }

        return files;
    }

    /// <summary>把 diff 原文按 "diff --git " 头切成逐文件段（无实质内容的段不产出）。</summary>
    private static IEnumerable<string> SplitDiffSegments(string diff)
    {
        var lines = diff.Split('\n');
        var current = new StringBuilder();
        foreach (var line in lines)
        {
            if (line.StartsWith("diff --git ", StringComparison.Ordinal) && current.Length > 0)
            {
                var segment = current.ToString(); // 只物化一次，避免逐段重复分配
                if (segment.Trim().Length > 0)
                {
                    yield return segment;
                }

                current.Clear();
            }

            current.Append(line).Append('\n');
        }

        var tail = current.ToString();
        if (tail.Trim().Length > 0)
        {
            yield return tail;
        }
    }

    /// <summary>从 diff 段头提取文件相对路径（取 b/ 侧；提取失败返回 null 不过滤）。</summary>
    private static string? ExtractDiffPath(string segment)
    {
        var firstLine = segment[..segment.IndexOf('\n')];
        var match = DiffPathPattern.Match(firstLine);
        return match.Success ? match.Groups[1].Value : null;
    }

    /// <summary>判断文件是否疑似密钥/凭据（文件名或所在目录命中即拦截）。</summary>
    /// <param name="relativePath">仓库内相对路径（正斜杠）。</param>
    /// <returns>疑似敏感时 true。</returns>
    private static bool IsSensitive(string relativePath)
        => SensitiveNamePattern.IsMatch(relativePath) || SensitiveDirPattern.IsMatch(relativePath + "/");

    /// <summary>
    /// 执行 git 命令并返回标准输出（UTF-8）。取消时杀掉整个 git 进程树。
    /// </summary>
    /// <param name="repoPath">仓库根目录。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <param name="args">git 参数。</param>
    /// <returns>标准输出文本。</returns>
    /// <exception cref="InvalidOperationException">git 无法启动或执行失败。</exception>
    /// <exception cref="OperationCanceledException">被取消。</exception>
    private static async Task<string> RunGitAsync(string repoPath, CancellationToken cancellationToken, params string[] args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = repoPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var arg in args)
        {
            psi.ArgumentList.Add(arg);
        }

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException("无法启动 git 命令（确认 git 已安装并在 PATH 中）。");
        // 双流并发读取再等退出：避免顺序读导致管道缓冲写满死锁
        var outTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var errTask = process.StandardError.ReadToEndAsync(cancellationToken);
        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try
            {
                process.Kill(entireProcessTree: true); // 取消要真正落到 git 进程，不留孤儿
            }
            catch (InvalidOperationException)
            {
                // 进程已自行退出，忽略
            }

            throw;
        }

        var output = await outTask.ConfigureAwait(false);
        var error = await errTask.ConfigureAwait(false);
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(DescribeGitFailure(args, error));
        }

        return output;
    }

    /// <summary>
    /// 把 git 失败转成人话报错：非 git 目录给方向性指引，其余取 stderr 真因行
    /// （git 失败常先倾泻几十行 usage 再补 "fatal:" 真因，只看首行会被 usage 误导）。
    /// </summary>
    /// <param name="args">git 参数（用于报错回显）。</param>
    /// <param name="stderr">git 标准错误全文。</param>
    /// <returns>单行可行动报错。</returns>
    private static string DescribeGitFailure(string[] args, string stderr)
    {
        // 非 git 目录：用户真正需要的是"下一步怎么办"，不是 git 的 usage 说明书
        if (stderr.Contains("not a git repository", StringComparison.OrdinalIgnoreCase))
        {
            return "目标目录不是 git 仓库，无法用 diff 模式圈定改动范围。"
                + "散文件/案例目录请用 --mode all 全量送审；确要审改动则先 git init 并提交。";
        }

        var lines = stderr.Trim().Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        // 真因行优先（fatal:/error: 开头），找不到再退回首行
        var cause = lines.FirstOrDefault(l => l.StartsWith("fatal:", StringComparison.OrdinalIgnoreCase)
            || l.StartsWith("error:", StringComparison.OrdinalIgnoreCase))
            ?? lines.FirstOrDefault()
            ?? "无错误详情";

        return $"git {string.Join(' ', args)} 执行失败：{cause}";
    }
}
