using System.Text.Json;
using System.Text.Json.Nodes;
using GuardAI.Checks.Assets;
using GuardAI.Checks.Structure;
using GuardAI.Core.Config;
using GuardAI.Core.Engine;
using GuardAI.Core.Ledging;
using GuardAI.Core.Reporting;
using GuardAI.Init;

namespace GuardAI.Cli;

/// <summary>
/// MCP 服务器（stdio 通道）：JSON-RPC 2.0 按行收发，把 guardAI 的裁判能力搬运给 AI 客户端。
/// </summary>
/// <remarks>
/// 产品铁律：引擎裁判，AI 装配——MCP 只做能力搬运，不做任何分析判断；
/// 全程本地 stdio，不上传任何代码内容；零外部依赖（协议用 System.Text.Json 手写）。
/// 四工具与 CLI 完全同源：scan_project 等价 check --json，generate_config 等价 init 的配置生成段，
/// get_report 读台账闭环汇总（M5 的 .guard/records/），search_capability 等价 search（写码前强制检索）。
/// </remarks>
public static class McpServer
{
    /// <summary>服务器名（MCP serverInfo）。</summary>
    public const string ServerName = "guardAI";

    /// <summary>默认 MCP 协议版本（客户端给出版本时回显其请求版本）。</summary>
    public const string DefaultProtocolVersion = "2024-11-05";

    /// <summary>
    /// 运行 stdio 消息循环：逐行读 JSON-RPC 请求、逐行写响应（通知不回写），输入结束即退出。
    /// </summary>
    /// <param name="input">标准输入。</param>
    /// <param name="output">标准输出（协议通道，不得混入其他文本）。</param>
    /// <param name="defaultRepoPath">工具参数省略 repo 时的默认仓库路径。</param>
    /// <exception cref="ArgumentNullException">input、output 或 defaultRepoPath 为 null 时抛出。</exception>
    public static void Run(TextReader input, TextWriter output, string defaultRepoPath)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(defaultRepoPath);

        while (input.ReadLine() is { } line)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            var response = HandleMessage(line, defaultRepoPath);
            if (response is not null)
            {
                output.WriteLine(response);
                output.Flush();
            }
        }
    }

    /// <summary>
    /// 处理单条 JSON-RPC 消息（供消息循环与单测共用）。
    /// </summary>
    /// <param name="messageJson">一行 JSON-RPC 消息。</param>
    /// <param name="defaultRepoPath">工具参数省略 repo 时的默认仓库路径。</param>
    /// <returns>响应 JSON 文本；通知类消息（无 id）返回 null。</returns>
    /// <exception cref="ArgumentNullException">messageJson 或 defaultRepoPath 为 null 时抛出。</exception>
    public static string? HandleMessage(string messageJson, string defaultRepoPath)
    {
        ArgumentNullException.ThrowIfNull(messageJson);
        ArgumentNullException.ThrowIfNull(defaultRepoPath);

        JsonNode? node;
        try
        {
            node = JsonNode.Parse(messageJson);
        }
        catch (JsonException)
        {
            return Error(null, -32700, "解析错误：不是合法 JSON");
        }

        if (node is not JsonObject request)
        {
            return Error(null, -32600, "无效请求：消息必须是 JSON 对象");
        }

        var id = request["id"]?.DeepClone();
        var method = request["method"]?.GetValue<string>();
        if (string.IsNullOrEmpty(method))
        {
            // 无 method：可能是客户端回传的响应帧，静默忽略；带 id 的畸形请求要报错
            return id is null ? null : Error(id, -32600, "无效请求：缺少 method");
        }

        // 通知类消息（无 id）：按协议不回响应
        if (id is null)
        {
            return null;
        }

        var parameters = request["params"];
        try
        {
            return method switch
            {
                "initialize" => Ok(id, InitializeResult(parameters)),
                "ping" => Ok(id, new JsonObject()),
                "tools/list" => Ok(id, ToolsListResult()),
                "tools/call" => Ok(id, ToolsCallResult(parameters, defaultRepoPath)),
                _ => Error(id, -32601, $"未知方法: {method}"),
            };
        }
        catch (ToolException ex)
        {
            // 工具执行失败按 MCP 约定放 result.isError，不是协议错误
            return Ok(id, ToolErrorResult(ex.Message));
        }
        catch (Exception ex) when (ex is FormatException or FileNotFoundException
            or InvalidOperationException or DirectoryNotFoundException or ArgumentException)
        {
            return Ok(id, ToolErrorResult(ex.Message));
        }
    }

    /// <summary>
    /// 工具执行失败异常（携带给客户端的中文说明）。
    /// </summary>
    private sealed class ToolException(string message) : Exception(message);

    /// <summary>
    /// initialize 结果：协议版本、能力声明、服务器信息。
    /// </summary>
    private static JsonObject InitializeResult(JsonNode? parameters)
    {
        var requested = (parameters as JsonObject)?["protocolVersion"]?.GetValue<string>();
        return new JsonObject
        {
            ["protocolVersion"] = string.IsNullOrEmpty(requested) ? DefaultProtocolVersion : requested,
            ["capabilities"] = new JsonObject { ["tools"] = new JsonObject() },
            ["serverInfo"] = new JsonObject
            {
                ["name"] = ServerName,
                ["version"] = typeof(McpServer).Assembly.GetName().Version?.ToString(3) ?? "1.0.0",
            },
            ["instructions"] = "guardAI 项目骨架守卫：引擎裁判，AI 装配。"
                + "写新方法/新类之前先调 search_capability 查能力清单（命中必须复用，禁止换皮重写）；"
                + "scan_project 跑结构检查（等价 guardAI check --json）；"
                + "generate_config 生成规则配置 YAML（不落盘）；"
                + "get_report 读台账闭环汇总。发现违规时按返回的修复指令逐条修复后复检。",
        };
    }

    /// <summary>
    /// tools/list 结果：四工具定义（名称、说明、入参 schema）。
    /// </summary>
    private static JsonObject ToolsListResult()
    {
        return new JsonObject
        {
            ["tools"] = new JsonArray
            {
                new JsonObject
                {
                    ["name"] = "scan_project",
                    ["description"] = "执行项目骨架结构检查并返回 JSON 报告（等价 guardAI check --json）："
                        + "违规清单、规则归属、给 AI 的修复指令、空扫描标记。检查结果会记入本地台账（record=false 可关闭）。",
                    ["inputSchema"] = new JsonObject
                    {
                        ["type"] = "object",
                        ["properties"] = new JsonObject
                        {
                            ["repo"] = new JsonObject { ["type"] = "string", ["description"] = "目标仓库路径（省略则用连接时指定的默认仓库）" },
                            ["mode"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray("all", "changed"), ["description"] = "检查模式：all=全部文件，changed=仅 git 新增/改动（省略默认 all）" },
                            ["record"] = new JsonObject { ["type"] = "boolean", ["description"] = "是否记入本地台账（省略默认 true）" },
                        },
                    },
                },
                new JsonObject
                {
                    ["name"] = "generate_config",
                    ["description"] = "扫描项目并生成 guardai.yml 规则配置 YAML 文本（等价 guardAI init 的配置生成段，**不写入磁盘**）："
                        + "目录白名单基线快照、include_extensions、落位规则（含 project_type/scope）。需要落盘请用 guardAI init。",
                    ["inputSchema"] = new JsonObject
                    {
                        ["type"] = "object",
                        ["properties"] = new JsonObject
                        {
                            ["repo"] = new JsonObject { ["type"] = "string", ["description"] = "目标仓库路径（省略则用连接时指定的默认仓库）" },
                        },
                    },
                },
                new JsonObject
                {
                    ["name"] = "get_report",
                    ["description"] = "读取本地台账（.guard/records/）并返回闭环汇总 JSON：总运行数、未闭环/已闭环指纹数、闭环率、"
                        + "指纹追踪明细（open/fixed）与最近运行历史。闭环判定：违规指纹从最新运行消失 = 已修复。",
                    ["inputSchema"] = new JsonObject
                    {
                        ["type"] = "object",
                        ["properties"] = new JsonObject
                        {
                            ["repo"] = new JsonObject { ["type"] = "string", ["description"] = "目标仓库路径（省略则用连接时指定的默认仓库）" },
                        },
                    },
                },
                new JsonObject
                {
                    ["name"] = "search_capability",
                    ["description"] = "写码前强制检索：按关键词查项目能力清单（等价 guardAI search，清单缺失自动现生成），"
                        + "返回已有实现的位置与签名。命中时必须优先复用/扩展已有实现，禁止换皮重写（近似重复检测会拦截）。",
                    ["inputSchema"] = new JsonObject
                    {
                        ["type"] = "object",
                        ["properties"] = new JsonObject
                        {
                            ["query"] = new JsonObject { ["type"] = "string", ["description"] = "检索关键词（方法名/类型名/签名片段，如「上传」「Upload」「GetToken」）" },
                            ["top"] = new JsonObject { ["type"] = "integer", ["description"] = "返回条数上限（省略默认 20）" },
                            ["repo"] = new JsonObject { ["type"] = "string", ["description"] = "目标仓库路径（省略则用连接时指定的默认仓库）" },
                        },
                        ["required"] = new JsonArray { "query" },
                    },
                },
            },
        };
    }

    /// <summary>
    /// tools/call 结果：按工具名分发，内容为文本块（JSON/YAML）。
    /// </summary>
    private static JsonObject ToolsCallResult(JsonNode? parameters, string defaultRepoPath)
    {
        if (parameters is not JsonObject call)
        {
            throw new ToolException("tools/call 缺少 params.name");
        }

        var name = call["name"]?.GetValue<string>();
        var args = call["arguments"] as JsonObject;
        var repo = ResolveRepo(args, defaultRepoPath);

        var text = name switch
        {
            "scan_project" => ScanProject(args, repo),
            "generate_config" => GenerateConfig(repo),
            "get_report" => GetReport(repo),
            "search_capability" => SearchCapability(args, repo),
            _ => throw new ToolException($"未知工具: {name}（可用：scan_project / generate_config / get_report / search_capability）"),
        };

        return new JsonObject
        {
            ["content"] = new JsonArray { new JsonObject { ["type"] = "text", ["text"] = text } },
            ["isError"] = false,
        };
    }

    /// <summary>
    /// 构造工具执行失败结果（MCP 约定：result.isError=true + 说明文本）。
    /// </summary>
    private static JsonObject ToolErrorResult(string message)
    {
        return new JsonObject
        {
            ["content"] = new JsonArray { new JsonObject { ["type"] = "text", ["text"] = message } },
            ["isError"] = true,
        };
    }

    /// <summary>
    /// 工具一：结构检查（等价 check --json）。
    /// </summary>
    private static string ScanProject(JsonObject? args, string repo)
    {
        var modeText = args?["mode"]?.GetValue<string>();
        var mode = string.IsNullOrEmpty(modeText) || modeText == "all" ? CheckMode.All
            : modeText == "changed" ? CheckMode.Changed
            : throw new ToolException($"未知检查模式: {modeText}（可用：all / changed）");

        var configPath = FindConfig(repo);
        var config = StructureRuleConfigLoader.Load(configPath);
        var context = new CheckContext(repo, mode, config, Path.GetFileName(configPath));

        // 与 CLI check 完全同源：全部检查模块插槽都跑
        var results = new List<CheckResult> { new StructureCheck().Run(context), new DuplicateCheck().Run(context) };

        var writer = new StringWriter();
        ReportWriter.WriteJson(writer, repo, mode, results);

        // 台账留痕与 CLI 同语义（trigger=mcp），record=false 可关闭
        if (args?["record"]?.GetValue<bool>() != false)
        {
            Ledger.Append(repo, Ledger.CreateRun(repo, mode, "mcp", results));
        }

        return writer.ToString();
    }

    /// <summary>
    /// 工具二：生成规则配置 YAML（只返回文本，不落盘）。
    /// </summary>
    private static string GenerateConfig(string repo)
    {
        return ConfigGenerator.Generate(ProjectScanner.Scan(repo));
    }

    /// <summary>
    /// 工具三：台账闭环汇总（含最近运行历史）。
    /// </summary>
    private static string GetReport(string repo)
    {
        var runs = Ledger.Read(repo);
        var summary = LedgerAnalyzer.Analyze(runs);
        var recent = runs
            .TakeLast(10)
            .Select(r => new
            {
                r.Id,
                r.Timestamp,
                r.Trigger,
                r.Mode,
                r.FilesScanned,
                r.ViolationCount,
                r.Outcome,
            })
            .Reverse()
            .ToList();

        var payload = new
        {
            repoPath = repo,
            recordsPath = Ledger.RecordsPath(repo),
            summary = new
            {
                summary.TotalRuns,
                summary.FirstRun,
                summary.LastRun,
                summary.OpenCount,
                summary.FixedCount,
                summary.ClosureRate,
            },
            fingerprints = summary.Fingerprints,
            recentRuns = recent,
        };

        return JsonSerializer.Serialize(payload, new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        });
    }

    /// <summary>
    /// 工具四：能力清单检索（等价 search，写码前强制检索闭环的入口）。
    /// </summary>
    /// <param name="args">工具入参（query：关键词；top：返回上限，缺省 20）。</param>
    /// <param name="repo">目标仓库路径。</param>
    /// <returns>检索结果文本（与 CLI search 输出同源）。</returns>
    /// <exception cref="ToolException">缺少 query 或关键词为空时抛出。</exception>
    private static string SearchCapability(JsonObject? args, string repo)
    {
        var query = args?["query"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(query))
        {
            throw new ToolException("search_capability 缺少 query（检索关键词）");
        }

        var top = args?["top"]?.GetValue<int>() ?? 0;
        var limit = top > 0 ? top : 20;
        var indexPath = CapabilityIndexStore.DefaultPath(repo);
        CapabilityIndex index;
        if (File.Exists(indexPath))
        {
            index = CapabilityIndexStore.Load(indexPath);
        }
        else
        {
            // 清单缺失时现场生成——不折腾，调一次工具就出结果
            index = CapabilityIndex.Build(repo);
            CapabilityIndexStore.Save(indexPath, index);
        }

        var matches = index.Search(query, limit);
        var writer = new StringWriter();
        if (matches.Count == 0)
        {
            writer.WriteLine($"没有匹配「{query}」的已有能力——可以放心新建，记得先想想是否该扩展现有类。");
            return writer.ToString();
        }

        writer.WriteLine($"「{query}」命中 {matches.Count} 个已有能力（写码前先复用，别重复造轮子）：");
        var rank = 0;
        foreach (var entry in matches)
        {
            rank++;
            writer.WriteLine($"  {rank}. {entry.File}:{entry.Line}  [{entry.TypeName}] {entry.Signature}");
        }

        return writer.ToString();
    }

    /// <summary>
    /// 解析工具入参里的 repo（省略时用默认仓库）。
    /// </summary>
    private static string ResolveRepo(JsonObject? args, string defaultRepoPath)
    {
        var repo = args?["repo"]?.GetValue<string>();
        return string.IsNullOrWhiteSpace(repo)
            ? Path.GetFullPath(defaultRepoPath)
            : Path.GetFullPath(repo);
    }

    /// <summary>
    /// 定位规则配置（与 Program.FindConfig 同语义：guardai.yml 优先，config/structure-allowlist.yml 兜底）。
    /// </summary>
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

        throw new ToolException("找不到规则配置（guardai.yml）。请先运行 'guardAI init' 生成配置。");
    }

    /// <summary>
    /// 构造 JSON-RPC 成功响应。
    /// </summary>
    private static string Ok(JsonNode id, JsonNode result)
    {
        return new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id,
            ["result"] = result,
        }.ToJsonString();
    }

    /// <summary>
    /// 构造 JSON-RPC 错误响应。
    /// </summary>
    private static string Error(JsonNode? id, int code, string message)
    {
        return new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id,
            ["error"] = new JsonObject
            {
                ["code"] = code,
                ["message"] = message,
            },
        }.ToJsonString();
    }
}
