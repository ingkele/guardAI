using System.Net;
using System.Text;
using GuardAI.AI;
using GuardAI.AI.Config;
using GuardAI.AI.Llm;
using GuardAI.Core.Config;
using GuardAI.Init;

namespace GuardAI.Tests;

/// <summary>AI 装配顾问测试（提示词组装、schema 校验、确定性合并）。</summary>
public class InitAiAdvisorTests
{
    /// <summary>假 HTTP 处理器：记录请求，返回预设响应（与 LlmClientTests 同一范式）。</summary>
    private sealed class FakeHandler : HttpMessageHandler
    {
        public string? LastBody;
        private readonly string _responseBody;

        public FakeHandler(string responseBody)
        {
            _responseBody = responseBody;
        }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastBody = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(_responseBody, Encoding.UTF8, "application/json"),
            };
        }
    }

    private static ProjectScanResult BuildScan() => new(
        RepoPath: "x", ProjectName: "Demo", Kind: ProjectKind.DotNet,
        ProjectType: ProjectTypeKind.Library, Skeleton: SkeletonKind.Generic,
        SolutionFiles: ["Demo.sln"], ProjectFileCount: 2,
        CodeFileExtensions: [".cs"],
        TopDirectories: [new TopDirectoryInfo("src", "源代码"), new TopDirectoryInfo("gen", "生成代码")],
        KnownDirectories: ["src", "gen", "src/Inner"],
        HasRootCodeFiles: false,
        HasGitHubWorkflow: true, HasGiteeWorkflow: false, HasPreCommitHook: false,
        HasClaudeSettings: false, HasCodexConfig: false, HasAgentsMd: true,
        HasGuardAiConfig: false, HasTestFixtures: false,
        ProjectTypeOverrides: []);

    [Fact]
    public void 提示词含仓库摘要与输出格式约束()
    {
        var (system, user) = InitAiAdvisor.BuildPrompt(InitDigest.Build(BuildScan()));

        Assert.Contains("装配员", system);
        Assert.Contains("JSON", system);
        Assert.Contains("仓库: Demo", user);
        Assert.Contains("gen — 生成代码", user);
        Assert.Contains("allowed_dirs", user);
        Assert.Contains("不得含 ..", user);
    }

    [Fact]
    public void 摘要为空_报错()
    {
        Assert.Throws<ArgumentException>(() => InitAiAdvisor.BuildPrompt("  "));
    }

    [Fact]
    public void 模型回复带代码围栏也能解析()
    {
        var advice = InitAiAdvisor.ParseReply("""
            好的，以下是建议：
            ```json
            {"project_type": "library", "allowed_dirs": [{"path": "gen/*", "purpose": "生成代码目录"}],
             "placement_rules": [], "exclusions": [{"path": "gen/**", "reason": "生成代码允许固定布局"}]}
            ```
            """);

        Assert.Equal("library", advice.ProjectType);
        Assert.Single(advice.AllowedDirs);
        Assert.Equal("gen/*", advice.AllowedDirs[0].Path);
        Assert.Single(advice.Exclusions);
    }

    [Fact]
    public void 不合规条目丢弃并记录原因()
    {
        var advice = InitAiAdvisor.ParseReply("""
            {"project_type": "monolith",
             "allowed_dirs": [
               {"path": "/abs/path", "purpose": "绝对路径"},
               {"path": "../escape", "purpose": "逃逸"},
               {"path": "C:/drive", "purpose": "盘符"},
               {"path": "gen/*", "purpose": "生成代码目录"},
               {"path": "", "purpose": "空路径"}
             ],
             "placement_rules": [
               {"name": "缺字段", "file_pattern": "*X.cs"},
               {"name": "工具类落位", "file_pattern": "*Helper.cs", "dir_pattern": "**/Utils/**", "suggest": "移动到 Utils", "scope": "weird"}
             ],
             "exclusions": "不是数组"}
            """);

        Assert.Null(advice.ProjectType);
        Assert.Single(advice.AllowedDirs);
        Assert.Equal("gen/*", advice.AllowedDirs[0].Path);
        Assert.Single(advice.PlacementRules);
        Assert.Equal("all", advice.PlacementRules[0].Scope); // 未知 scope 回落 all
        Assert.Empty(advice.Exclusions); // 非数组整体丢弃
        Assert.Contains(advice.DroppedNotes, n => n.Contains("project_type"));
        Assert.Contains(advice.DroppedNotes, n => n.Contains("exclusions 不是数组"));
    }

    [Fact]
    public void 重复条目去重()
    {
        var advice = InitAiAdvisor.ParseReply("""
            {"allowed_dirs": [
               {"path": "gen/*", "purpose": "第一条"},
               {"path": "GEN/*", "purpose": "大小写重复"}
             ], "placement_rules": [], "exclusions": []}
            """);

        Assert.Single(advice.AllowedDirs);
        Assert.Contains(advice.DroppedNotes, n => n.Contains("去重"));
    }

    [Fact]
    public void 输出不是JSON_抛格式错误()
    {
        Assert.Throws<FormatException>(() => InitAiAdvisor.ParseReply("抱歉，我无法完成这个任务"));
    }

    [Fact]
    public async Task AdviseAsync端到端_摘要进请求_建议出解析()
    {
        // OpenAI 报文外壳：choices[0].message.content 里才是装配建议 JSON 文本
        var handler = new FakeHandler(
            """{"choices":[{"message":{"content":"{\"project_type\": \"app\", \"allowed_dirs\": [], \"placement_rules\": [], \"exclusions\": []}"}}]}""");
        using var http = new HttpClient(handler);
        var client = new OpenAICompatClient(http,
            new AIConfig(AIConfig.ProviderOpenAI, "https://api.example.com/v1", "test-model", null));

        var advice = await AdvisorRunner.AdviseAsync(
            client, InitAiAdvisor.BuildPrompt("仓库: Demo 摘要内容"), InitAiAdvisor.ParseReply);

        Assert.Equal("app", advice.ProjectType);
        Assert.Contains("仓库: Demo 摘要内容", handler.LastBody);
    }

    [Fact]
    public void AI建议合并进配置_模板基线保留_重复去重()
    {
        var advice = new InitAdvice(
            ProjectType: "library",
            AllowedDirs:
            [
                new InitAdviceItem("src/*", "与基线重复，应丢弃"),
                new InitAdviceItem("gen/Generated/*", "生成代码产物目录（AI 装配建议）"),
            ],
            PlacementRules:
            [
                new InitAdvicePlacement("门面类放模块根", "*Facade.cs", "*/*.cs", "保持在模块根", "library"),
            ],
            Exclusions:
            [
                new InitAdviceItem("gen/**", "生成代码允许固定布局"),
            ],
            DroppedNotes: []);

        var config = StructureRuleConfigLoader.Parse(
            ConfigGenerator.Generate(BuildScan(), advice), "guardai.yml");

        // 模板基线保留 + AI 增量进配置且去重
        Assert.Contains(config.AllowedDirs, r => r.Path == "src/*");
        Assert.Contains(config.AllowedDirs,
            r => r.Path == "gen/Generated/*" && r.Purpose.Contains("AI 装配建议"));
        Assert.Equal(1, config.AllowedDirs.Count(r => r.Path == "src/*"));
        Assert.Equal("library", config.ProjectType);
        Assert.Contains(config.PlacementRules, r => r.Name == "门面类放模块根");
        Assert.Contains(config.Exclusions, e => e.Path == "gen/**");
    }

    [Fact]
    public void 传入AI配置时写入ai段_未传不写()
    {
        var withAi = ConfigGenerator.Generate(BuildScan(), null,
            new AIConfig(AIConfig.ProviderOpenAI, "https://token-plan.example.com/v1", "mimo-test", "MIMO_KEY"));
        Assert.Contains("ai:", withAi);
        Assert.Contains("api_key_env: MIMO_KEY", withAi);
        Assert.Contains("https://token-plan.example.com/v1", withAi);

        var withoutAi = ConfigGenerator.Generate(BuildScan());
        Assert.DoesNotContain("\nai:", withoutAi);
    }

    [Fact]
    public void 摘要只含结构事实_不含代码内容()
    {
        var digest = InitDigest.Build(BuildScan());

        Assert.Contains("仓库: Demo", digest);
        Assert.Contains("gen — 生成代码", digest);
        Assert.Contains("src/Inner", digest);
        Assert.Contains("GitHub Actions 有", digest);
    }

    [Fact]
    public void 会误报存量的落位建议被确定性丢弃()
    {
        // 首跑零误报铁律的引擎兜底：AI 建议也不许冤枉既有代码
        var advice = new InitAdvice(null, [],
        [
            new InitAdvicePlacement("测试项目落位 tests", "*.csproj", "tests/**", "移动到 tests", "all"),
            new InitAdvicePlacement("项目文件固定在项目根", "*.csproj", "src/*|tests/*", "保持", "all"),
            new InitAdvicePlacement("类库专用规则", "*X.cs", "nowhere/**", "x", "library"),
        ], [], []);

        var files = new List<string> { "src/Proj/A.csproj", "tests/Tests/T.csproj", "src/Proj/X.cs" };
        var (filtered, dropped) = InitAdviceValidator.FilterForFiles(advice, files, "app");

        // 坏规则丢弃并给证据；scope 不适用的规则不经现状校验、原样保留
        Assert.Equal(2, filtered.PlacementRules.Count);
        Assert.Contains(filtered.PlacementRules, r => r.Name == "项目文件固定在项目根");
        Assert.Contains(filtered.PlacementRules, r => r.Name == "类库专用规则");
        Assert.Contains(dropped, n => n.Contains("首跑零误报") && n.Contains("A.csproj"));
        Assert.DoesNotContain(filtered.PlacementRules, r => r.Name == "测试项目落位 tests");
    }
}
