using GuardAI.Checks.Api;
using GuardAI.Core.Config;
using GuardAI.Core.Engine;
using Xunit;

namespace GuardAI.Tests;

/// <summary>
/// 端点鉴权检查测试（Issue #6）：Minimal API 的 Map* 端点必须声明鉴权意图。
/// </summary>
/// <remarks>
/// 锁住的关键口径：
/// ①未启用整模块跳过（首跑零误报）；
/// ②跨行链式调用能判（真实写法处理器几十行、RequireAuthorization 可能在很远）；
/// ③显式 AllowAnonymous 通过；豁免须写 reason（解析期强制）；
/// ④require_permission 默认关，开启后写操作端点才要求权限码。
/// </remarks>
public class EndpointAuthCheckTests
{
    /// <summary>启用检查的最小配置。</summary>
    private const string EnabledYaml = """
        version: 1
        include_extensions:
          - .cs
        endpoint_auth:
          enabled: true
        """;

    /// <summary>启用检查并要求权限码的配置（require_permission 必须在 endpoint_auth 段内）。</summary>
    private const string EnabledWithPermissionYaml = """
        version: 1
        include_extensions:
          - .cs
        endpoint_auth:
          enabled: true
          require_permission: true
        """;

    /// <summary>未启用配置：整模块跳过，不报任何违规。</summary>
    [Fact]
    public void 未启用_整模块跳过()
    {
        using var fixture = new TempRepoFixture();
        fixture.AddFile("src/Api/Program.cs", """
            namespace Demo;

            var app = builder.Build();
            app.MapGet("/api/content", () => Results.Ok("data"));
            """);

        var result = Run(fixture.Path, "version: 1\ninclude_extensions:\n  - .cs");

        Assert.Empty(result.Violations);
    }

    /// <summary>端点无鉴权声明 → 报违规含路由与行号。</summary>
    [Fact]
    public void 端点无鉴权_报违规()
    {
        using var fixture = new TempFixtureWithEndpoint("""
            namespace Demo;

            var app = builder.Build();
            app.MapGet("/api/content", () => Results.Ok("data"));
            """);

        var result = Run(fixture.Path);

        var violation = Assert.Single(result.Violations);
        Assert.Equal("端点未声明鉴权", violation.Rule);
        Assert.Contains("/api/content", violation.Message);
        Assert.Contains("MapGet", violation.Message);
        Assert.Contains("第 4 行", violation.Message);
    }

    /// <summary>链上声明 RequireAuthorization → 通过。</summary>
    [Fact]
    public void 链上声明RequireAuthorization_通过()
    {
        using var fixture = new TempFixtureWithEndpoint("""
            namespace Demo;

            var app = builder.Build();
            app.MapGet("/api/me", () => Results.Ok("me")).RequireAuthorization();
            """);

        var result = Run(fixture.Path);

        Assert.Empty(result.Violations);
    }

    /// <summary>显式 AllowAnonymous → 通过。</summary>
    [Fact]
    public void 显式AllowAnonymous_通过()
    {
        using var fixture = new TempFixtureWithEndpoint("""
            namespace Demo;

            var app = builder.Build();
            app.MapGet("/health", () => Results.Ok()).AllowAnonymous();
            """);

        var result = Run(fixture.Path);

        Assert.Empty(result.Violations);
    }

    /// <summary>跨行链式调用（处理器几十行，鉴权在末尾）→ 仍能判为已声明（Issue 明确要求）。</summary>
    [Fact]
    public void 跨行链式调用_仍能判已声明()
    {
        using var fixture = new TempFixtureWithEndpoint("""
            namespace Demo;

            var app = builder.Build();
            app.MapPost("/api/content/generate", (Request req) =>
                {
                    var a = 1;
                    var b = 2;
                    return Results.Ok(a + b);
                })
                .WithName("GenerateContent")
                .RequireAuthorization()
                .RequirePermission("content:create");
            """);

        var result = Run(fixture.Path);

        Assert.Empty(result.Violations);
    }

    /// <summary>跨行链式调用但漏鉴权 → 仍要报（不能因为跨行就漏）。</summary>
    [Fact]
    public void 跨行链式调用漏鉴权_报违规()
    {
        using var fixture = new TempFixtureWithEndpoint("""
            namespace Demo;

            var app = builder.Build();
            app.MapPost("/api/content/generate", (Request req) =>
                {
                    var a = 1;
                    return Results.Ok(a);
                })
                .WithName("GenerateContent");
            """);

        var result = Run(fixture.Path);

        var violation = Assert.Single(result.Violations);
        Assert.Equal("端点未声明鉴权", violation.Rule);
        Assert.Contains("/api/content/generate", violation.Message);
    }

    /// <summary>豁免命中（文件 + 路由双匹配）→ 不报。</summary>
    [Fact]
    public void 豁免命中_不报()
    {
        using var fixture = new TempFixtureWithEndpoint("""
            namespace Demo;

            var app = builder.Build();
            app.MapGet("/health", () => Results.Ok()).AllowAnonymous();
            app.MapGet("/health/ready", () => Results.Ok("ready"));
            """);

        var config = """
            version: 1
            include_extensions:
              - .cs
            endpoint_auth:
              enabled: true
              exemptions:
                - path: src/Api/Program.cs
                  endpoints:
                    - /health/ready
                  reason: 容器与外部监控探针，必须匿名；无任何业务数据
            """;
        var result = Run(fixture.Path, config);

        Assert.Empty(result.Violations);
    }

    /// <summary>豁免只对登记的端点生效：同文件其他端点漏鉴权照报（防止豁免外溢）。</summary>
    [Fact]
    public void 豁免不外溢_同文件其他端点仍报()
    {
        using var fixture = new TempFixtureWithEndpoint("""
            namespace Demo;

            var app = builder.Build();
            app.MapGet("/health/ready", () => Results.Ok("ready"));
            app.MapGet("/api/orders", () => Results.Ok("orders"));
            """);

        var config = """
            version: 1
            include_extensions:
              - .cs
            endpoint_auth:
              enabled: true
              exemptions:
                - path: src/Api/Program.cs
                  endpoints:
                    - /health/ready
                  reason: 监控探针
            """;
        var result = Run(fixture.Path, config);

        var violation = Assert.Single(result.Violations);
        Assert.Contains("/api/orders", violation.Message);
    }

    /// <summary>豁免缺 reason → 配置解析期直接报错（Issue 要求，防白名单变摆设）。</summary>
    [Fact]
    public void 豁免缺reason_配置解析报错()
    {
        var yaml = """
            version: 1
            include_extensions:
              - .cs
            endpoint_auth:
              enabled: true
              exemptions:
                - path: src/Api/Program.cs
                  endpoints:
                    - /health
            """;

        var ex = Assert.Throws<FormatException>(() => StructureRuleConfigLoader.Parse(yaml, "guardai.yml"));

        Assert.Contains("缺少 reason", ex.Message);
        Assert.Contains("白名单架空", ex.Message);
    }

    /// <summary>require_permission 默认关：写操作端点缺权限码不报。</summary>
    [Fact]
    public void 权限码默认关_写端点缺权限码不报()
    {
        using var fixture = new TempFixtureWithEndpoint("""
            namespace Demo;

            var app = builder.Build();
            app.MapPost("/api/orders", () => Results.Ok()).RequireAuthorization();
            """);

        var result = Run(fixture.Path);

        Assert.Empty(result.Violations);
    }

    /// <summary>require_permission 开启：写操作端点缺权限码 → 报「写端点缺权限码」。</summary>
    [Fact]
    public void 权限码开启_写端点缺权限码报违规()
    {
        using var fixture = new TempFixtureWithEndpoint("""
            namespace Demo;

            var app = builder.Build();
            app.MapPost("/api/orders", () => Results.Ok()).RequireAuthorization();
            """);

        var config = EnabledWithPermissionYaml;
        var result = Run(fixture.Path, config);

        var violation = Assert.Single(result.Violations);
        Assert.Equal("写端点缺权限码", violation.Rule);
        Assert.Contains("/api/orders", violation.Message);
    }

    /// <summary>require_permission 开启但已给权限码 → 通过。</summary>
    [Fact]
    public void 权限码开启且已声明_通过()
    {
        using var fixture = new TempFixtureWithEndpoint("""
            namespace Demo;

            var app = builder.Build();
            app.MapPost("/api/orders", () => Results.Ok())
                .RequireAuthorization()
                .RequirePermission("orders:create");
            """);

        var config = EnabledWithPermissionYaml;
        var result = Run(fixture.Path, config);

        Assert.Empty(result.Violations);
    }

    /// <summary>require_permission 开启：读端点（MapGet）不要求权限码。</summary>
    [Fact]
    public void 权限码开启_读端点不要求权限码()
    {
        using var fixture = new TempFixtureWithEndpoint("""
            namespace Demo;

            var app = builder.Build();
            app.MapGet("/api/orders", () => Results.Ok()).RequireAuthorization();
            """);

        var config = EnabledWithPermissionYaml;
        var result = Run(fixture.Path, config);

        Assert.Empty(result.Violations);
    }

    /// <summary>注释里的 Map* 不算（注释掉的端点不该报）。</summary>
    [Fact]
    public void 注释里的端点_不算()
    {
        using var fixture = new TempFixtureWithEndpoint("""
            namespace Demo;

            var app = builder.Build();
            // app.MapGet("/api/legacy", () => Results.Ok());
            app.MapGet("/api/me", () => Results.Ok()).RequireAuthorization();
            """);

        var result = Run(fixture.Path);

        Assert.Empty(result.Violations);
    }

    /// <summary>测试代码豁免：测试夹具里的 Map* 不参与判定。</summary>
    [Fact]
    public void 测试代码_豁免()
    {
        using var fixture = new TempFixtureWithEndpoint("""
            namespace Demo;

            var app = builder.Build();
            app.MapGet("/api/x", () => Results.Ok("data"));
            """, relativePath: "tests/Api/EndpointFixture.cs");

        var result = Run(fixture.Path);

        Assert.Empty(result.Violations);
    }

    /// <summary>跑 endpoint 检查。</summary>
    /// <param name="repoPath">仓库目录。</param>
    /// <param name="yaml">规则配置（缺省为启用配置）。</param>
    /// <returns>检查结果。</returns>
    private static CheckResult Run(string repoPath, string? yaml = null)
    {
        var config = StructureRuleConfigLoader.Parse(yaml ?? EnabledYaml);
        var context = new CheckContext(repoPath, CheckMode.All, config, "guardai.yml");
        return new EndpointAuthCheck().Run(context);
    }

    /// <summary>带默认端点文件的夹具（省略重复的 AddFile 样板）。</summary>
    private sealed class TempFixtureWithEndpoint : IDisposable
    {
        private readonly TempRepoFixture _fixture;

        public TempFixtureWithEndpoint(string content, string relativePath = "src/Api/Program.cs")
        {
            _fixture = new TempRepoFixture();
            Path = _fixture.Path;
            _fixture.AddFile(relativePath, content);
        }

        public string Path { get; }

        public void Dispose() => _fixture.Dispose();
    }
}