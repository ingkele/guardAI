using GuardAI.Checks.Assets;
using GuardAI.Checks.Structure;
using GuardAI.Cli;
using GuardAI.Core.Config;
using GuardAI.Core.Engine;
using GuardAI.Core.Ledging;
using GuardAI.Core.Reporting;
using GuardAI.Init;

/// <summary>
/// guardAI 命令行入口（单入口：一条命令跑完所有检查）。
/// </summary>
internal static class Program
{
    /// <summary>退出码：通过。</summary>
    private const int ExitOk = 0;

    /// <summary>退出码：存在违规。</summary>
    private const int ExitViolations = 1;

    /// <summary>退出码：配置或环境错误。</summary>
    private const int ExitError = 2;

    /// <summary>
    /// 程序入口。
    /// </summary>
    /// <param name="args">命令行参数。</param>
    /// <returns>退出码（0 通过 / 1 违规 / 2 配置或环境错误）。</returns>
    public static int Main(string[] args)
    {
        try
        {
            if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
            {
                PrintHelp();
                return ExitOk;
            }

            var command = args[0];
            var options = ParseOptions(args[1..]);
            return command switch
            {
                "check" => RunCheck(options),
                "init" => RunInit(options),
                "doctor" => RunDoctor(options),
                "ui" => RunUi(options),
                "mcp" => RunMcp(options),
                "index" => RunIndex(options),
                "search" => RunSearch(options),
                _ => Fail($"未知命令: {command}（可用：check / init / doctor / ui / mcp / index / search）"),
            };
        }
        catch (Exception ex) when (ex is FormatException or FileNotFoundException
            or InvalidOperationException or DirectoryNotFoundException)
        {
            Console.Error.WriteLine($"错误: {ex.Message}");
            return ExitError;
        }
    }

    /// <summary>
    /// 执行检查命令。
    /// </summary>
    private static int RunCheck(Dictionary<string, string?> options)
    {
        var repo = Path.GetFullPath(Get(options, "repo") ?? ".");
        var mode = ParseMode(Get(options, "mode"));
        var configPath = Get(options, "config") is { } explicitConfig
            ? Path.GetFullPath(explicitConfig)
            : FindConfig(repo);
        var json = options.ContainsKey("json");
        var reportPath = Get(options, "report");
        var htmlPath = Get(options, "html");
        // 严格审计模式：存量重复（基线内）也报违规、不读写基线——案例验收/审计用
        var strict = options.ContainsKey("strict");

        var config = StructureRuleConfigLoader.Load(configPath);
        var context = new CheckContext(repo, mode, config, Path.GetFileName(configPath), strict);

        // 检查模块插槽：新增模块在此注册即可（调用方式永远不变）
        var checks = new List<IGuardCheck> { new StructureCheck(), new DuplicateCheck() };
        if (Get(options, "only") is { } only && only != "all")
        {
            checks = checks.Where(c => string.Equals(c.Name, only, StringComparison.OrdinalIgnoreCase)).ToList();
            if (checks.Count == 0)
            {
                return Fail($"未知检查模块: {only}（可用：structure / duplicate / all）");
            }
        }

        // 接受现状：把当前存量重复对收进基线，之后只报新增（P3-M2）
        if (options.ContainsKey("update-baseline"))
        {
            var folded = DuplicateCheck.AcceptExistingPairs(context);
            Console.WriteLine($"已把 {folded} 对存量重复收进基线（{DuplicateBaseline.FileName}），本次起只报新增");
        }

        var results = checks.Select(c => c.Run(context)).ToList();

        if (json)
        {
            ReportWriter.WriteJson(Console.Out, repo, mode, results);
        }
        else
        {
            ReportWriter.WriteText(Console.Out, repo, mode, Path.GetFileName(configPath), results);
        }

        if (!string.IsNullOrWhiteSpace(reportPath))
        {
            var full = Path.GetFullPath(reportPath);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            using var writer = new StreamWriter(full);
            if (json)
            {
                ReportWriter.WriteJson(writer, repo, mode, results);
            }
            else
            {
                ReportWriter.WriteText(writer, repo, mode, Path.GetFileName(configPath), results);
            }

            Console.WriteLine($"报告已写入: {full}");
        }

        // HTML 体检报告：--html 开关，单页零外部依赖；与上次运行对比取台账既有记录（不含本次）
        if (!string.IsNullOrWhiteSpace(htmlPath))
        {
            var full = Path.GetFullPath(htmlPath);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            using var writer = new StreamWriter(full);
            HtmlReportWriter.Write(writer, repo, mode, Path.GetFileName(configPath), results, Ledger.Read(repo));
            Console.WriteLine($"HTML 报告已写入: {full}");
        }

        // 台账留痕：每次检查都记录（.guard/records/，记录不出本机）。--no-record 可关闭。
        if (!options.ContainsKey("no-record"))
        {
            var trigger = Get(options, "trigger") ?? Environment.GetEnvironmentVariable("GUARDAI_TRIGGER") ?? "cli";
            var run = Ledger.CreateRun(repo, mode, trigger, results);
            var ledgerPath = Ledger.Append(repo, run);
            if (!json)
            {
                Console.WriteLine($"台账已记录: {ledgerPath}");
            }
        }

        // 空扫描防线（假绿修复）：all 模式 0 文件 = 配置未覆盖代码文件，结论不成立，退出码 2；
        // changed 模式 0 文件是正常情况（无新增/改动），保持退出码 0。
        if (mode == CheckMode.All && results.Sum(r => r.Scanned) == 0)
        {
            return ExitError;
        }

        return results.Sum(r => r.Violations.Count) > 0 ? ExitViolations : ExitOk;
    }

    /// <summary>
    /// 执行台账网页命令：本地起网页看审查记录与闭环率（Ctrl+C 退出）。
    /// </summary>
    private static int RunUi(Dictionary<string, string?> options)
    {
        var repo = Path.GetFullPath(Get(options, "repo") ?? ".");
        int? port = Get(options, "port") is { } portText
            ? int.Parse(portText)
            : null;
        var openBrowser = !options.ContainsKey("no-open");
        return LocalUiServer.Run(repo, port, openBrowser, Console.Out);
    }

    /// <summary>
    /// 执行 MCP server 命令：stdio JSON-RPC 循环，把裁判能力搬运给 AI 客户端。
    /// </summary>
    /// <param name="options">命令选项（repo：默认仓库路径）。</param>
    /// <returns>退出码（输入流结束返回 0）。</returns>
    private static int RunMcp(Dictionary<string, string?> options)
    {
        var repo = Path.GetFullPath(Get(options, "repo") ?? ".");
        McpServer.Run(Console.In, Console.Out, repo);
        return ExitOk;
    }

    /// <summary>
    /// 执行能力清单命令：扫描公共方法生成 capability-index.json（写码前检索的数据基础）。
    /// </summary>
    /// <param name="options">命令选项（repo：目标仓库；output：自定义输出路径）。</param>
    /// <returns>退出码（0 成功 / 2 配置或环境错误）。</returns>
    private static int RunIndex(Dictionary<string, string?> options)
    {
        var repo = Path.GetFullPath(Get(options, "repo") ?? ".");
        if (!Directory.Exists(repo))
        {
            return Fail($"目标目录不存在: {repo}");
        }

        var output = Get(options, "output") is { } outPath
            ? Path.GetFullPath(outPath)
            : CapabilityIndexStore.DefaultPath(repo);
        var index = CapabilityIndex.Build(repo);
        CapabilityIndexStore.Save(output, index);
        Console.WriteLine($"能力清单已生成: {output}（{index.Entries.Count} 个能力）");
        return ExitOk;
    }

    /// <summary>
    /// 执行能力检索命令：按关键词查已有能力（写码先查，防重复造轮子）。
    /// </summary>
    /// <param name="options">命令选项（位置参数=关键词；repo：目标仓库；top：返回上限）。</param>
    /// <returns>退出码（0 成功 / 2 配置或环境错误）。</returns>
    private static int RunSearch(Dictionary<string, string?> options)
    {
        // 位置参数（无值键）即检索关键词：guardAI search 上传 --repo D:\项目
        var keyword = options.FirstOrDefault(kv => kv.Value is null).Key;
        if (string.IsNullOrWhiteSpace(keyword))
        {
            return Fail("用法: guardAI search <关键词> [--repo <路径>] [--top N]");
        }

        var repo = Path.GetFullPath(Get(options, "repo") ?? ".");
        if (!Directory.Exists(repo))
        {
            return Fail($"目标目录不存在: {repo}");
        }

        var limit = Get(options, "top") is { } topText && int.TryParse(topText, out var top) ? top : 20;
        var indexPath = CapabilityIndexStore.DefaultPath(repo);
        CapabilityIndex index;
        if (File.Exists(indexPath))
        {
            index = CapabilityIndexStore.Load(indexPath);
        }
        else
        {
            // 清单缺失时现场生成——不折腾，一条命令出结果
            index = CapabilityIndex.Build(repo);
            CapabilityIndexStore.Save(indexPath, index);
            Console.WriteLine($"（能力清单缺失，已自动生成: {indexPath}）");
        }

        var matches = index.Search(keyword, limit);
        if (matches.Count == 0)
        {
            Console.WriteLine($"没有匹配「{keyword}」的已有能力——可以放心新建，记得先想想是否该扩展现有类。");
            return ExitOk;
        }

        Console.WriteLine($"「{keyword}」命中 {matches.Count} 个已有能力（写码前先复用，别重复造轮子）：");
        var rank = 0;
        foreach (var entry in matches)
        {
            rank++;
            Console.WriteLine($"  {rank}. {entry.File}:{entry.Line}  [{entry.TypeName}] {entry.Signature}");
        }

        return ExitOk;
    }

    /// <summary>
    /// 执行装配命令：扫描 → 生成规则配置 → 补齐闸口 → 首检。
    /// </summary>
    private static int RunInit(Dictionary<string, string?> options)
    {
        var repo = Path.GetFullPath(Get(options, "repo") ?? ".");
        var yes = options.ContainsKey("yes");
        var force = options.ContainsKey("force");

        Console.WriteLine($"guardAI init：目标仓库 {repo}");

        var result = GuardAiInitializer.Run(repo, Get(options, "templates"), force, Console.WriteLine);
        foreach (var note in result.Notes)
        {
            Console.WriteLine($"提示: {note}");
        }

        if (!yes && !Console.IsInputRedirected)
        {
            Console.Write("以上配置将写入目标仓库，确认执行？[Y/n] ");
            var answer = Console.ReadLine();
            if (!string.IsNullOrWhiteSpace(answer)
                && answer.Trim().StartsWith("n", StringComparison.OrdinalIgnoreCase))
            {
                Console.WriteLine("已取消（重新运行并加 --yes 可跳过确认）");
                return ExitOk;
            }
        }

        var written = result.GateResults.Count(r => r.Written);
        Console.WriteLine();
        Console.WriteLine($"装配完成：规则配置 {(result.ConfigWritten ? "已生成" : "已存在")}，闸口写入 {written} 项");

        Console.WriteLine();
        Console.WriteLine("正在执行首次检查（全量模式）...");
        var checkOptions = new Dictionary<string, string?>(options)
        {
            ["mode"] = "all",
        };
        checkOptions.Remove("yes");
        checkOptions.Remove("force");
        return RunCheck(checkOptions);
    }

    /// <summary>
    /// 执行自检命令：环境与配置体检。
    /// </summary>
    private static int RunDoctor(Dictionary<string, string?> options)
    {
        var repo = Path.GetFullPath(Get(options, "repo") ?? ".");
        Console.WriteLine("guardAI doctor");
        Console.WriteLine($"目标仓库: {repo}");

        var ok = true;
        ok &= Report("目标目录存在", Directory.Exists(repo));
        ok &= Report("git 可用（changed 模式依赖）", IsGitAvailable(repo));
        ok &= Report("模板可用（磁盘目录或内嵌资源）",
            GateInstaller.FindTemplatesDir(Get(options, "templates")) is not null || TemplateProvider.HasEmbedded);

        try
        {
            var configPath = FindConfig(repo);
            _ = StructureRuleConfigLoader.Load(configPath);
            ok &= Report($"规则配置可解析（{Path.GetFileName(configPath)}）", true);
        }
        catch (Exception ex) when (ex is FormatException or FileNotFoundException)
        {
            Console.WriteLine($"  x 规则配置：{ex.Message}");
            ok = false;
        }

        Console.WriteLine(ok ? "体检通过" : "体检发现问题（见上）");
        return ok ? ExitOk : ExitError;
    }

    /// <summary>
    /// 打印一行体检结论。
    /// </summary>
    private static bool Report(string item, bool passed)
    {
        Console.WriteLine($"  [{(passed ? "OK" : "X ")}] {item}");
        return passed;
    }

    /// <summary>
    /// 判断目标目录下 git 是否可用。
    /// </summary>
    private static bool IsGitAvailable(string repo)
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "git",
                Arguments = "rev-parse --is-inside-work-tree",
                WorkingDirectory = repo,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var p = System.Diagnostics.Process.Start(psi);
            if (p is null)
            {
                return false;
            }

            p.WaitForExit();
            return p.ExitCode == 0;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    /// <summary>
    /// 查找规则配置：guardai.yml → config/structure-allowlist.yml（兼容 guard-tool）。
    /// </summary>
    /// <exception cref="FileNotFoundException">都不存在时抛出（提示先跑 init）。</exception>
    private static string FindConfig(string repo)
    {
        foreach (var candidate in new[] { "guardai.yml", Path.Combine("config", "structure-allowlist.yml") })
        {
            var path = Path.Combine(repo, candidate);
            if (File.Exists(path))
            {
                return path;
            }
        }

        throw new FileNotFoundException("找不到规则配置（guardai.yml）。请先运行 'guardAI init' 生成配置。");
    }

    /// <summary>
    /// 解析检查模式参数。
    /// </summary>
    /// <exception cref="FormatException">取值非法时抛出。</exception>
    private static CheckMode ParseMode(string? value)
    {
        return (value ?? "changed").ToLowerInvariant() switch
        {
            "changed" => CheckMode.Changed,
            "all" => CheckMode.All,
            _ => throw new FormatException($"未知检查模式: {value}（可用：changed / all）"),
        };
    }

    /// <summary>
    /// 解析 "--key value / --flag" 形式的参数。
    /// </summary>
    private static Dictionary<string, string?> ParseOptions(string[] args)
    {
        var options = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < args.Length; i++)
        {
            var key = args[i].TrimStart('-');
            if (i + 1 < args.Length && !args[i + 1].StartsWith("--"))
            {
                options[key] = args[i + 1];
                i++;
            }
            else
            {
                options[key] = null;
            }
        }

        return options;
    }

    /// <summary>
    /// 取出带值选项。
    /// </summary>
    private static string? Get(Dictionary<string, string?> options, string key)
    {
        return options.TryGetValue(key, out var value) ? value : null;
    }

    /// <summary>
    /// 打印错误并返回配置错误退出码。
    /// </summary>
    private static int Fail(string message)
    {
        Console.Error.WriteLine($"错误: {message}");
        return ExitError;
    }

    /// <summary>
    /// 打印使用说明。
    /// </summary>
    private static void PrintHelp()
    {
        Console.WriteLine("""
            guardAI —— AI 时代的项目骨架守卫（单入口：一条命令跑完所有检查）

            用法:
              guardAI check [--repo <路径>] [--mode changed|all] [--only structure|duplicate|all]
                            [--config <文件>] [--report <文件>] [--html <文件>] [--json]
                            [--trigger cli|hook|ci] [--no-record] [--update-baseline] [--strict]
              guardAI init  [--repo <路径>] [--templates <目录>] [--yes] [--force]
              guardAI doctor [--repo <路径>] [--templates <目录>]
              guardAI ui [--repo <路径>] [--port <端口>] [--no-open]
              guardAI mcp [--repo <路径>]
              guardAI index [--repo <路径>] [--output <文件>]
              guardAI search <关键词> [--repo <路径>] [--top N]

            命令:
              check    执行检查（0=通过 / 1=违规 / 2=配置或环境错误），结果自动记入台账
              init     装配：扫描项目 → 生成规则配置 → 补齐 CI/pre-commit/AI hook → 首检
              doctor   环境与配置自检
              ui       打开本地台账网页：审查记录 / AI hook 结论 / 闭环率（记录不出本机）
              mcp      启动 MCP server（stdio）：给 AI 客户端搬运 scan_project / generate_config / get_report 三工具
              index    生成能力清单（.guard/capability-index.json）：公共方法快照，写码前检索的数据基础
              search   按关键词检索已有能力（写码先查，防重复造轮子）

            台账:
              每次 check 自动写入 .guard/records/runs.jsonl（--no-record 关闭）；
              违规指纹从最新运行消失即计为已修复，闭环率在 ui 网页查看。

            示例:
              guardAI check                       # 检查当前仓库的 git 变更
              guardAI check --mode all            # 全量检查（CI 用）
              guardAI check --html 报告.html      # 生成单页 HTML 体检报告（含与上次对比）
              guardAI check --strict              # 严格审计：存量重复（基线内）也报违规，案例验收用
              guardAI init --yes                  # 一条命令完成接入
              guardAI ui                          # 打开本地台账网页
            """);
    }
}
