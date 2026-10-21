using System.Text.Json.Nodes;
using GuardAI.Cli;
using GuardAI.Init;

namespace GuardAI.Tests;

/// <summary>
/// MCP server 测试：协议层（JSON-RPC 握手/分发/容错）与四工具（与 CLI 同源语义）。
/// </summary>
public class McpServerTests : IDisposable
{
    private readonly string _repo = Directory.CreateTempSubdirectory("guardai-mcp-").FullName;

    public void Dispose()
    {
        Directory.Delete(_repo, recursive: true);
    }

    // ---------- 协议层 ----------

    [Fact]
    public void Initialize返回协议版本与服务器信息()
    {
        var response = Call("""{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2024-11-05"}}""");

        var result = response!["result"]!;
        Assert.Equal("2024-11-05", result["protocolVersion"]!.GetValue<string>());
        Assert.Equal(McpServer.ServerName, result["serverInfo"]!["name"]!.GetValue<string>());
        Assert.NotNull(result["capabilities"]!["tools"]);
    }

    [Fact]
    public void Initialize未带协议版本时回默认版本()
    {
        var response = Call("""{"jsonrpc":"2.0","id":1,"method":"initialize","params":{}}""");

        Assert.Equal(McpServer.DefaultProtocolVersion, response!["result"]!["protocolVersion"]!.GetValue<string>());
    }

    [Fact]
    public void ToolsList返回四工具()
    {
        var response = Call("""{"jsonrpc":"2.0","id":2,"method":"tools/list"}""");

        var tools = response!["result"]!["tools"]!.AsArray();
        var names = tools.Select(t => t!["name"]!.GetValue<string>()).ToArray();
        Assert.Equal(["scan_project", "generate_config", "get_report", "search_capability"], names);
        // 每个工具都带 JSON Schema 入参定义
        Assert.All(tools, t => Assert.Equal("object", t!["inputSchema"]!["type"]!.GetValue<string>()));
    }

    [Fact]
    public void Ping返回空结果()
    {
        var response = Call("""{"jsonrpc":"2.0","id":9,"method":"ping"}""");

        Assert.NotNull(response!["result"]);
        Assert.Null(response["error"]);
    }

    [Fact]
    public void 通知消息不回响应()
    {
        var line = McpServer.HandleMessage("""{"jsonrpc":"2.0","method":"notifications/initialized"}""", _repo);

        Assert.Null(line);
    }

    [Fact]
    public void 坏JSON返回解析错误()
    {
        var response = JsonNode.Parse(McpServer.HandleMessage("这不是JSON", _repo)!)!;

        Assert.Equal(-32700, response["error"]!["code"]!.GetValue<int>());
    }

    [Fact]
    public void 未知方法返回方法不存在错误()
    {
        var response = Call("""{"jsonrpc":"2.0","id":3,"method":"resources/list"}""");

        Assert.Equal(-32601, response!["error"]!["code"]!.GetValue<int>());
    }

    [Fact]
    public void Run消息循环逐行响应且通知不占行()
    {
        var input = new StringReader(string.Join(Environment.NewLine,
            """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2024-11-05"}}""",
            """{"jsonrpc":"2.0","method":"notifications/initialized"}""",
            """{"jsonrpc":"2.0","id":2,"method":"ping"}"""));
        var output = new StringWriter();

        McpServer.Run(input, output, _repo);

        var lines = output.ToString()
            .Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries)
            .Select(l => JsonNode.Parse(l)!)
            .ToArray();
        Assert.Equal(2, lines.Length);
        Assert.Equal(1, lines[0]["id"]!.GetValue<int>());
        Assert.Equal(2, lines[1]["id"]!.GetValue<int>());
    }

    // ---------- 工具层 ----------

    [Fact]
    public void ScanProject返回JSON报告并记台账()
    {
        WriteDefaultConfig("src/Demo/Entities/Order.cs");

        var text = CallTool("scan_project", new JsonObject { ["record"] = true });
        var report = JsonNode.Parse(text)!;

        // ReportWriter.WriteJson 的字段名是 PascalCase（与 CLI check --json 完全一致）
        Assert.Equal("all", report["Mode"]!.GetValue<string>());
        Assert.Equal(1, report["Summary"]!["FilesScanned"]!.GetValue<int>());
        Assert.False(report["Summary"]!["EmptyScan"]!.GetValue<bool>());

        // 台账留痕与 CLI 同语义（trigger=mcp）
        var runs = File.ReadAllLines(Path.Combine(_repo, ".guard", "records", "runs.jsonl"));
        Assert.Single(runs);
        Assert.Contains("mcp", runs[0]);
    }

    [Fact]
    public void ScanProjectRecordFalse不写台账()
    {
        WriteDefaultConfig("src/Demo/Entities/Order.cs");

        _ = CallTool("scan_project", new JsonObject { ["record"] = false });

        Assert.False(File.Exists(Path.Combine(_repo, ".guard", "records", "runs.jsonl")));
    }

    [Fact]
    public void ScanProject未知模式按工具错误返回()
    {
        WriteDefaultConfig("src/Demo/Entities/Order.cs");

        AssertToolError("scan_project", new JsonObject { ["mode"] = "fuzzy" }, "未知检查模式");
    }

    /// <summary>
    /// 工具错误统一契约：isError=true + 说明含定位文案（缺配置/缺参数/未知工具共用一条链路）。
    /// </summary>
    [Theory]
    [InlineData("scan_project", "guardAI init")] // 缺配置：提示先 init
    [InlineData("search_capability", "query")]   // 缺 query：提示补关键词
    [InlineData("do_magic", "未知工具")]           // 未知工具名
    public void 工具错误按统一契约返回(string toolName, string expectedText)
    {
        AssertToolError(toolName, new JsonObject(), expectedText);
    }

    [Fact]
    public void GenerateConfig返回YAML且不落盘()
    {
        Directory.CreateDirectory(Path.Combine(_repo, "src", "Demo"));
        File.WriteAllText(Path.Combine(_repo, "src", "Demo", "App.cs"), "// 测试夹具");

        var text = CallTool("generate_config", new JsonObject());

        Assert.Contains("version:", text);
        Assert.Contains("allowed_dirs:", text);
        // 铁律：MCP 只搬运不落盘，需要写盘走 guardAI init
        Assert.False(File.Exists(Path.Combine(_repo, "guardai.yml")));
    }

    [Fact]
    public void GetReport返回闭环汇总()
    {
        WriteDefaultConfig("src/Demo/Entities/Order.cs");
        _ = CallTool("scan_project", new JsonObject());

        var text = CallTool("get_report", new JsonObject());
        var report = JsonNode.Parse(text)!;

        Assert.Equal(1, report["summary"]!["totalRuns"]!.GetValue<int>());
        Assert.Single(report["recentRuns"]!.AsArray());
        Assert.NotNull(report["fingerprints"]);
    }

    // ---------- 工具四：能力检索（写码前强制检索闭环） ----------

    [Fact]
    public void SearchCapability命中已有实现并自动生成清单()
    {
        var dir = Path.Combine(_repo, "src", "Tools");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "ImageKit.cs"), """
            namespace Demo.Tools;

            public static class ImageKit
            {
                /// <summary>上传图片。</summary>
                public static string UploadImage(byte[] data)
                {
                    return Convert.ToBase64String(data);
                }
            }
            """);

        var text = CallTool("search_capability", new JsonObject { ["query"] = "Upload" });

        Assert.Contains("命中", text);
        Assert.Contains("UploadImage", text);
        // 清单缺失时自动现生成（不折腾，调一次工具就出结果）
        Assert.True(File.Exists(Path.Combine(_repo, ".guard", "capability-index.json")));
    }

    [Fact]
    public void SearchCapability无命中时提示放心新建()
    {
        var text = CallTool("search_capability", new JsonObject { ["query"] = "完全不存在的能力名" });

        Assert.Contains("放心新建", text);
    }

    [Fact]
    public void SearchCapability可限制返回条数()
    {
        var dir = Path.Combine(_repo, "src", "Tools");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "KitA.cs"), """
            namespace Demo.Tools;

            public static class KitA
            {
                /// <summary>上传 A。</summary>
                public static string UploadAlpha(byte[] data) => Convert.ToBase64String(data);
            }
            """);
        File.WriteAllText(Path.Combine(dir, "KitB.cs"), """
            namespace Demo.Tools;

            public static class KitB
            {
                /// <summary>上传 B。</summary>
                public static string UploadBeta(byte[] data) => Convert.ToBase64String(data);
            }
            """);

        var text = CallTool("search_capability", new JsonObject { ["query"] = "Upload", ["top"] = 1 });

        Assert.Contains("命中 1 个", text);
    }

    // ---------- 夹具与断言辅助 ----------

    /// <summary>
    /// 构造临时仓库：按相对路径生成代码文件，并用 init 同源生成器写出 guardai.yml。
    /// </summary>
    private void WriteDefaultConfig(params string[] relativePaths)
    {
        foreach (var rel in relativePaths)
        {
            var full = Path.Combine(_repo, rel.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, "// 测试夹具");
        }

        var yaml = ConfigGenerator.Generate(ProjectScanner.Scan(_repo));
        File.WriteAllText(Path.Combine(_repo, "guardai.yml"), yaml);
    }

    /// <summary>
    /// 发送一条 JSON-RPC 请求，解析并返回响应 JSON。
    /// </summary>
    private JsonObject? Call(string messageJson)
    {
        var line = McpServer.HandleMessage(messageJson, _repo);
        Assert.NotNull(line);
        return JsonNode.Parse(line!)!.AsObject();
    }

    /// <summary>
    /// 调用 MCP 工具，返回工具结果对象（含 content / isError）。
    /// </summary>
    private JsonObject? CallToolResponse(string toolName, JsonObject arguments)
    {
        var request = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = 1,
            ["method"] = "tools/call",
            ["params"] = new JsonObject
            {
                ["name"] = toolName,
                ["arguments"] = arguments,
            },
        };

        var response = Call(request.ToJsonString());
        Assert.NotNull(response!["result"]);
        return response!["result"]!.AsObject();
    }

    /// <summary>
    /// 调用 MCP 工具并断言成功，返回文本内容（JSON/YAML）。
    /// </summary>
    private string CallTool(string toolName, JsonObject arguments)
    {
        var result = CallToolResponse(toolName, arguments);
        Assert.False(result!["isError"]!.GetValue<bool>());
        return result["content"]![0]!["text"]!.GetValue<string>();
    }

    /// <summary>
    /// 调用 MCP 工具并断言按工具错误返回（isError=true 且说明包含指定文案）。
    /// </summary>
    private void AssertToolError(string toolName, JsonObject arguments, string expectedText)
    {
        var response = CallToolResponse(toolName, arguments);
        Assert.True(response!["isError"]!.GetValue<bool>());
        Assert.Contains(expectedText, response["content"]![0]!["text"]!.GetValue<string>());
    }
}
