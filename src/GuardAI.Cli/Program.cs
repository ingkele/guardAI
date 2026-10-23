using System.Text.Json;
using GuardAI.AI;
using GuardAI.AI.Config;
using GuardAI.AI.Llm;
using GuardAI.AI.Review;
using GuardAI.Checks.Assets;
using GuardAI.Checks.Security;
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
    public static async Task<int> Main(string[] args)
    {
        try
        {
            if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
            {
                PrintHelp();
                return ExitOk;
            }

            var command = args[0];
            var parsed = CliArgs.Parse(command, args[1..]);
            if (parsed.HelpRequested)
            {
                // 子命令 -h/--help：只打印该命令用法，绝不静默执行命令（防误触）
                Console.WriteLine(CliHelp.Build(command));
                return ExitOk;
            }

            if (parsed.Error is not null)
            {
                return Fail(parsed.Error);
            }

            var options = parsed.Options;
            return command switch
            {
                "check" => RunCheck(options),
                "review" => await RunReview(options),
                "init" => await RunInit(options),
                "learn" => await RunLearn(options),
                "doctor" => RunDoctor(options),
                "ui" => RunUi(options),
                "mcp" => RunMcp(options),
                "index" => RunIndex(options),
                "search" => RunSearch(options, parsed.Positional),
                _ => Fail($"未知命令: {command}（可用：check / review / init / learn / doctor / ui / mcp / index / search）"),
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
        var checks = new List<IGuardCheck> { new StructureCheck(), new DuplicateCheck(), new SecretScanCheck() };
        if (Get(options, "only") is { } only && only != "all")
        {
            checks = checks.Where(c => string.Equals(c.Name, only, StringComparison.OrdinalIgnoreCase)).ToList();
            if (checks.Count == 0)
            {
                return Fail($"未知检查模块: {only}（可用：structure / duplicate / secret / all）");
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
    /// 执行语义审查命令：AI 审查员对改动做命名/注释/分层/复用等语义审查（引擎管机械项，AI 管语义项）。
    /// </summary>
    /// <param name="options">命令选项（repo/mode/base/config/report/json/fail-on）。</param>
    /// <returns>退出码（0 默认仅报告 / --fail-on 命中时 1 / 2 配置或环境错误）。</returns>
    private static async Task<int> RunReview(Dictionary<string, string?> options)
    {
        var repo = Path.GetFullPath(Get(options, "repo") ?? ".");
        if (!Directory.Exists(repo))
        {
            return Fail($"目标目录不存在: {repo}");
        }

        var mode = (Get(options, "mode") ?? "changed").Trim().ToLowerInvariant();
        if (!DiffCollector.SupportedModes.Contains(mode))
        {
            throw new FormatException($"未知收集模式: {mode}（可用：{string.Join(" / ", DiffCollector.SupportedModes)}）");
        }

        // --config 显式指定配置文件；缺省向上找 guardai.yml（与 check 同口径）
        var configPath = Get(options, "config") is { } explicitConfig
            ? Path.GetFullPath(explicitConfig)
            : FindConfig(repo);
        var root = GuardYaml.Parse(File.ReadAllText(configPath));
        var aiConfig = AIConfigParser.Parse(root, Path.GetFileName(configPath));
        if (aiConfig is null)
        {
            return Fail("""
                guardai.yml 缺少 ai: 配置段（AI 审查需要模型接入）。示例：
                  ai:
                    provider: openai          # openai（OpenAI 协议）/ anthropic / ollama
                    model: gpt-4o-mini
                    api_key_env: OPENAI_API_KEY   # 密钥放环境变量，不落配置文件（anthropic 必填；openai 无鉴权端点可省略）
                    # base_url: https://api.openai.com/v1   # 可省略；兼容端点填自己的地址
                    # ollama 示例：provider: ollama, model: qwen3:8b（无需 api_key_env）
                """);
        }

        // AI 只做语义审查与建议（检察官），退出码默认 0 不卡流程——最终裁判是确定性引擎。
        // Ctrl+C 取消：模型调用可中断，不干等 timeout_seconds
        using var cts = new CancellationTokenSource();
        ConsoleCancelEventHandler onCancel = (_, e) => { e.Cancel = true; cts.Cancel(); };
        Console.CancelKeyPress += onCancel;
        try
        {
            return await RunReviewCoreAsync(options, aiConfig, root, repo, mode, cts);
        }
        finally
        {
            // 注销处理器：cts 释放后若再触发 Ctrl+C，残留处理器会踩 ObjectDisposedException
            Console.CancelKeyPress -= onCancel;
        }
    }

    /// <summary>
    /// review 命令主体（调模型、出报告、判退出码）。
    /// </summary>
    /// <param name="options">命令选项。</param>
    /// <param name="aiConfig">AI 配置。</param>
    /// <param name="root">guardai.yml 根字典（提炼规则摘要用）。</param>
    /// <param name="repo">仓库根目录。</param>
    /// <param name="mode">收集模式。</param>
    /// <param name="cts">取消源（Ctrl+C 贯通）。</param>
    /// <returns>退出码。</returns>
    private static async Task<int> RunReviewCoreAsync(
        Dictionary<string, string?> options, AIConfig aiConfig,
        IReadOnlyDictionary<string, object?> root, string repo, string mode, CancellationTokenSource cts)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(aiConfig.TimeoutSeconds) };
        var client = LlmClientFactory.Create(http, aiConfig);
        var engine = new ReviewEngine(client, aiConfig);

        ReviewReport report;
        try
        {
            report = await engine.RunAsync(repo, mode, Get(options, "base"), ReviewPrompt.BuildRuleSummary(root), cts.Token,
                isCodeFile: mode == "all" ? MemberParserRegistry.Supports : null);
        }
        catch (LlmException ex)
        {
            return Fail(ex.Message);
        }
        catch (InvalidOperationException ex)
        {
            // 无改动/环境异常等可控分支：给可行动提示，不炸原始堆栈
            return Fail(ex.Message);
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            // 用户 Ctrl+C 取消与真超时是两种因，别把用户往 timeout_seconds 上引
            Console.Error.WriteLine("已取消: 用户中断审查。");
            return ExitError;
        }
        catch (Exception ex) when (ex is OperationCanceledException or TimeoutException)
        {
            return Fail($"模型调用超时（当前 ai.timeout_seconds={aiConfig.TimeoutSeconds}）。"
                + "推理模型审查大改动需要更长时间——建议调大 ai.timeout_seconds（600 秒起步）或分批提交后审查。");
        }

        if (options.ContainsKey("json"))
        {
            report.WriteJson(Console.Out);
        }
        else
        {
            report.WriteText(Console.Out);
        }

        // 审查报告落台账区（.guard/records/reviews/，记录不出本机）
        var reportPath = Get(options, "report") is { } custom
            ? Path.GetFullPath(custom)
            : Path.Combine(repo, Ledger.RecordsDir, "reviews", $"review-{report.GeneratedAt:yyyyMMdd-HHmmss}.json");
        Directory.CreateDirectory(Path.GetDirectoryName(reportPath)!);
        using (var writer = new StreamWriter(reportPath))
        {
            report.WriteJson(writer);
        }

        // 提示走 stderr：--json 模式 stdout 必须是纯 JSON（机器消费不断流）
        Console.Error.WriteLine($"审查报告已写入: {reportPath}");

        // --fail-on error|warn|info：发现达到该级别才退出码 1（默认不拦截）
        return Get(options, "fail-on") is { } failOn
            ? (HitsFailLevel(report, failOn) ? ExitViolations : ExitOk)
            : ExitOk;
    }

    /// <summary>
    /// 判断报告中是否存在达到拦截级别的发现。
    /// </summary>
    /// <param name="report">审查报告。</param>
    /// <param name="failOn">拦截级别（error / warn / info）。</param>
    /// <returns>存在达标发现时 true。</returns>
    /// <exception cref="FormatException">级别名非法。</exception>
    private static bool HitsFailLevel(ReviewReport report, string failOn)
    {
        var threshold = failOn.Trim().ToLowerInvariant() switch
        {
            "error" => ReviewFinding.SeverityRank(ReviewFinding.SeverityError),
            "warn" or "warning" => ReviewFinding.SeverityRank(ReviewFinding.SeverityWarn),
            "info" => ReviewFinding.SeverityRank(ReviewFinding.SeverityInfo),
            _ => throw new FormatException($"未知拦截级别: {failOn}（可用：error / warn / info）"),
        };
        return report.Findings.Any(f => ReviewFinding.SeverityRank(f.Severity) <= threshold);
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
    /// <param name="options">命令选项（repo：目标仓库；top：返回上限）。</param>
    /// <param name="keyword">位置参数=检索关键词。</param>
    /// <returns>退出码（0 成功 / 2 配置或环境错误）。</returns>
    private static int RunSearch(Dictionary<string, string?> options, string? keyword)
    {
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
    /// 执行装配命令：扫描 → 生成规则配置（--ai 时含 AI 装配建议）→ 补齐闸口 → 首检。
    /// </summary>
    /// <param name="options">命令选项（repo/templates/yes/force/ai/ai-*）。</param>
    /// <returns>退出码（0 成功 / 1 首检发现违规 / 2 配置或环境错误）。</returns>
    private static async Task<int> RunInit(Dictionary<string, string?> options)
    {
        var repo = Path.GetFullPath(Get(options, "repo") ?? ".");
        var yes = options.ContainsKey("yes");
        var force = options.ContainsKey("force");

        Console.WriteLine($"guardAI init：目标仓库 {repo}");

        InitAdvice? advice = null;
        AIConfig? usedAiConfig = null;
        ProjectScanResult? prescan = null;

        // AI 装配（--ai）：模型分析仓库结构产配置建议，schema 校验后确定性合并
        // AI 只装配不裁判——合规判定永远归引擎
        if (options.ContainsKey("ai"))
        {
            if (File.Exists(Path.Combine(repo, "guardai.yml")) && !force)
            {
                // 建议合不进已存在的配置，不值得花模型调用
                Console.WriteLine("提示: guardai.yml 已存在且未加 --force，跳过 AI 装配建议（避免空跑模型）");
            }
            else
            {
                var aiConfig = ResolveAiConfig(options, repo);
                prescan = ProjectScanner.Scan(repo);
                Console.WriteLine("正在请求 AI 装配建议（大模型分析仓库结构，只发结构摘要不发代码内容）...");
                var advised = await CallAiWithCancelAsync(aiConfig, "AI 装配建议",
                    (client, ct) => AdvisorRunner.AdviseAsync(
                        client, InitAiAdvisor.BuildPrompt(InitDigest.Build(prescan)), InitAiAdvisor.ParseReply, ct));
                if (advised is null)
                {
                    return ExitError;
                }

                advice = advised;
                usedAiConfig = aiConfig;
                Console.WriteLine($"AI 装配建议：目录白名单 +{advice.AllowedDirs.Count}，" +
                    $"落位规则 +{advice.PlacementRules.Count}，豁免 +{advice.Exclusions.Count}");
                foreach (var note in advice.DroppedNotes)
                {
                    Console.WriteLine($"提示: AI 装配建议（已丢弃）: {note}");
                }
            }
        }

        var result = GuardAiInitializer.Run(repo, Get(options, "templates"), force, Console.WriteLine,
            advice, usedAiConfig, prescan);
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
    /// 执行规则发现命令：审计 review 记录 / 违规台账 / 豁免清单，AI 提炼候选规则供人工确认。
    /// </summary>
    /// <remarks>
    /// "规则 = AI 判断的缓存"：从"踩坑加规则"变成"AI 报案加规则"。
    /// 只产候选建议不落地——人工确认后才进规则资产，之后由确定性引擎免费执法。
    /// </remarks>
    /// <param name="options">命令选项（repo/json/report/ai-*）。</param>
    /// <returns>退出码（0 完成 / 2 配置或环境错误）。</returns>
    private static async Task<int> RunLearn(Dictionary<string, string?> options)
    {
        var repo = Path.GetFullPath(Get(options, "repo") ?? ".");
        if (!Directory.Exists(repo))
        {
            return Fail($"目标目录不存在: {repo}");
        }

        // 1. 素材收集（全部本地读取，记录不出本机）
        var reviewsDir = Path.Combine(repo, Ledger.RecordsDir, "reviews");
        var reviewTexts = Directory.Exists(reviewsDir)
            ? Directory.GetFiles(reviewsDir, "review-*.json").OrderBy(f => f, StringComparer.Ordinal)
                .Select(File.ReadAllText).ToList()
            : [];
        var ledgerPath = Ledger.RecordsPath(repo);
        var ledgerLines = File.Exists(ledgerPath) ? File.ReadAllLines(ledgerPath) : [];

        if (reviewTexts.Count == 0 && ledgerLines.Length == 0)
        {
            return Fail("没有可审计的素材（review 报告与违规台账均为空）——先跑 review / check 留下记录再 learn。");
        }

        var configPath = FindConfig(repo);
        var config = StructureRuleConfigLoader.Load(configPath);
        var exclusions = config.Exclusions.Select(e => (e.Path, e.Reason)).ToList();
        var existingNames = config.PlacementRules.Select(r => r.Name)
            .Concat(config.Exclusions.Select(e => e.Path))
            .Concat(config.BannedDirs)
            .ToList();

        var digest = LearnDigest.Build(reviewTexts, ledgerLines, exclusions, existingNames);

        // 机器可读模式 stdout 纯 JSON：素材统计提示走 stderr（--json 契约）
        Console.Error.WriteLine($"素材：review 报告 {reviewTexts.Count} 份 / 台账 {ledgerLines.Length} 行 / 豁免 {exclusions.Count} 条");

        var aiConfig = ResolveAiConfig(options, repo);
        var advice = await CallAiWithCancelAsync(aiConfig, "规则发现",
            (client, ct) => AdvisorRunner.AdviseAsync(
                client, LearnAdvisor.BuildPrompt(digest), reply => LearnAdvisor.ParseReply(reply, existingNames), ct));
        if (advice is null)
        {
            return ExitError;
        }

        if (options.ContainsKey("json"))
        {
            advice.WriteJson(Console.Out);
        }
        else
        {
            WriteLearnText(Console.Out, advice);
        }

        if (Get(options, "report") is { } custom)
        {
            var full = Path.GetFullPath(custom);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            using (var writer = new StreamWriter(full))
            {
                advice.WriteJson(writer);
            }

            Console.Error.WriteLine($"候选规则建议已写入: {full}");
        }

        return ExitOk;
    }

    /// <summary>
    /// 输出候选规则文本报告（给人看）。
    /// </summary>
    /// <param name="writer">输出流。</param>
    /// <param name="advice">候选规则建议。</param>
    private static void WriteLearnText(TextWriter writer, LearnAdvice advice)
    {
        writer.WriteLine($"候选规则 {advice.Rules.Count} 条（人工确认后才进规则资产）：");
        if (advice.Rules.Count == 0)
        {
            writer.WriteLine("  （素材里没有值得立规则的反复模式——踩坑不重复，不用加规则）");
        }

        var rank = 0;
        foreach (var rule in advice.Rules)
        {
            rank++;
            writer.WriteLine();
            writer.WriteLine($"  {rank}. [{rule.Target}] {rule.Name}");
            writer.WriteLine($"     规则内容: {rule.Pattern}");
            writer.WriteLine($"     证据: {rule.Evidence}");
            writer.WriteLine($"     理由: {rule.Reason}");
        }

        foreach (var note in advice.DroppedNotes)
        {
            writer.WriteLine($"提示: 候选规则（已丢弃）: {note}");
        }

        writer.WriteLine();
        writer.WriteLine("确认方式：人工核对后——yml 片段粘进 guardai.yml、prompt 指令粘进 AGENTS.md 等项目指令文件；");
        writer.WriteLine("checker 类候选需评估开发新检查器。AI 只报案，落地由人和引擎把关。");
    }

    /// <summary>
    /// 带 Ctrl+C 取消贯通的 AI 调用外壳（init --ai / learn 共用，与 review 同一套取消/超时语义）。
    /// </summary>
    /// <typeparam name="T">返回类型（record 引用类型）。</typeparam>
    /// <param name="aiConfig">AI 接入配置。</param>
    /// <param name="label">调用用途（进错误提示）。</param>
    /// <param name="call">实际调用（接收客户端与取消令牌）。</param>
    /// <returns>调用结果；取消或调用失败返回 null（错误信息已输出）。</returns>
    private static async Task<T?> CallAiWithCancelAsync<T>(
        AIConfig aiConfig, string label, Func<ILlmClient, CancellationToken, Task<T>> call) where T : class
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(aiConfig.TimeoutSeconds) };
        var client = LlmClientFactory.Create(http, aiConfig);
        using var cts = new CancellationTokenSource();
        ConsoleCancelEventHandler onCancel = (_, e) => { e.Cancel = true; cts.Cancel(); };
        Console.CancelKeyPress += onCancel;
        try
        {
            return await call(client, cts.Token);
        }
        catch (LlmException ex)
        {
            Fail(ex.Message);
            return null;
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            Console.Error.WriteLine($"已取消: 用户中断{label}。");
            return null;
        }
        catch (Exception ex) when (ex is OperationCanceledException or TimeoutException)
        {
            Fail($"{label}调用超时（当前 ai.timeout_seconds={aiConfig.TimeoutSeconds}），"
                + "建议调大 timeout_seconds 后重试。");
            return null;
        }
        finally
        {
            // 注销处理器：cts 释放后残留处理器会踩 ObjectDisposedException
            Console.CancelKeyPress -= onCancel;
        }
    }

    /// <summary>
    /// 解析 AI 功能（init --ai / learn）的模型接入配置：命令行 --ai-* 选项优先，其次已有 guardai.yml 的 ai: 段。
    /// </summary>
    /// <param name="options">命令选项。</param>
    /// <param name="repo">目标仓库根目录。</param>
    /// <returns>AI 接入配置。</returns>
    /// <exception cref="FormatException">接入信息不足或协议名非法。</exception>
    private static AIConfig ResolveAiConfig(Dictionary<string, string?> options, string repo)
    {
        var provider = (Get(options, "ai-provider") ?? AIConfig.ProviderOpenAI).Trim().ToLowerInvariant();
        if (provider is not (AIConfig.ProviderOpenAI or AIConfig.ProviderAnthropic or AIConfig.ProviderOllama))
        {
            throw new FormatException($"不支持的 ai.provider: {provider}（可用：openai / anthropic / ollama）");
        }

        if (Get(options, "ai-provider") is not null || Get(options, "ai-model") is not null
            || Get(options, "ai-base-url") is not null || Get(options, "ai-api-key-env") is not null)
        {
            if (Get(options, "ai-model") is not { } model || string.IsNullOrWhiteSpace(model))
            {
                throw new FormatException("命令行接入必须给 --ai-model（如 mimo-v2.6-pro）");
            }

            var baseUrl = Get(options, "ai-base-url") ?? provider switch
            {
                AIConfig.ProviderAnthropic => AIConfig.DefaultAnthropicBaseUrl,
                AIConfig.ProviderOllama => AIConfig.DefaultOllamaBaseUrl,
                _ => AIConfig.DefaultOpenAIBaseUrl,
            };
            var apiKeyEnv = Get(options, "ai-api-key-env");
            if (provider == AIConfig.ProviderAnthropic && string.IsNullOrWhiteSpace(apiKeyEnv))
            {
                throw new FormatException("anthropic 协议必须给 --ai-api-key-env（API 强制鉴权头）");
            }

            return new AIConfig(provider, baseUrl, model, apiKeyEnv);
        }

        // 回落已有 guardai.yml 的 ai: 段（review 用的同一套配置）
        var configPath = FindConfig(repo);
        var root = GuardYaml.Parse(File.ReadAllText(configPath));
        return AIConfigParser.Parse(root, Path.GetFileName(configPath))
            ?? throw new FormatException("""
                AI 功能需要模型接入配置。两种给法：
                ① 命令行选项：--ai-provider openai --ai-model <模型名> --ai-base-url <地址> --ai-api-key-env <环境变量名>
                ② 在 guardai.yml 写 ai: 段（provider / model / api_key_env / base_url）
                """);
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
    /// 打印使用说明（文案单一来源在 <see cref="CliHelp"/>，全局帮助与子命令 --help 共用）。
    /// </summary>
    private static void PrintHelp()
    {
        Console.WriteLine(CliHelp.Build(null));
    }
}
