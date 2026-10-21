using GuardAI.Init;

namespace GuardAI.Tests;

/// <summary>项目扫描器测试（顶层目录发现与用途推断——M1 首跑零误报的关键）。</summary>
public class ProjectScannerTests
{
    [Fact]
    public void 发现真实顶层目录并排除噪声目录()
    {
        using var fixture = new ScannerFixture();
        fixture.AddFile("framework/WitchC/WitchC.csproj", "<Project />");
        fixture.AddFile("framework/WitchC/Scoped.cs", "// 代码");
        fixture.AddFile("samples/Demo/Program.cs", "// 代码");
        fixture.AddFile("bin/Debug/net10.0/Drop.cs", "// 构建产物");
        fixture.AddFile("node_modules/lib/index.js", "// 依赖");
        fixture.AddFile("docs/readme.md", "# 文档");

        var scan = ProjectScanner.Scan(fixture.Path);

        var names = scan.TopDirectories.Select(d => d.Name).ToList();
        Assert.Contains("framework", names);
        Assert.Contains("samples", names);
        Assert.DoesNotContain("bin", names);
        Assert.DoesNotContain("node_modules", names);

        // 纯文档目录不进白名单（检查只看代码文件）
        Assert.DoesNotContain("docs", names);
    }

    [Fact]
    public void 多项目混形态产出形态覆盖建议()
    {
        // v0.3.1-⑤：AspireApp 场景——同仓库混 app（Web/Api）与 library（ServiceDefaults），单值表达不了
        using var fixture = new ScannerFixture();
        fixture.AddFile("AspireApp.ApiService/AspireApp.ApiService.csproj", "<Project Sdk=\"Microsoft.NET.Sdk.Web\" />");
        fixture.AddFile("AspireApp.ServiceDefaults/AspireApp.ServiceDefaults.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\" />");

        var scan = ProjectScanner.Scan(fixture.Path);

        Assert.Equal(ProjectTypeKind.App, scan.ProjectType);
        var overrideRule = Assert.Single(scan.ProjectTypeOverrides);
        Assert.Equal("AspireApp.ServiceDefaults/**", overrideRule.Path);
        Assert.Equal("library", overrideRule.ProjectType);
    }

    [Fact]
    public void 按目录内容推断用途()
    {
        using var fixture = new ScannerFixture();
        fixture.AddFile("framework/WitchC/WitchC.csproj", "<Project />");
        fixture.AddFile("utils/Md5.cs", "// 代码");

        var scan = ProjectScanner.Scan(fixture.Path);

        Assert.Equal("源码模块", scan.TopDirectories.First(d => d.Name == "framework").Purpose);
        Assert.Equal("代码", scan.TopDirectories.First(d => d.Name == "utils").Purpose);
    }

    [Fact]
    public void 根目录有代码文件时标记()
    {
        using var fixture = new ScannerFixture();
        fixture.AddFile("Program.cs", "// 根目录入口");

        var scan = ProjectScanner.Scan(fixture.Path);

        Assert.True(scan.HasRootCodeFiles);
    }

    [Fact]
    public void 根目录无代码文件时不标记()
    {
        using var fixture = new ScannerFixture();
        fixture.AddFile("README.md", "# 说明");
        fixture.AddFile("src/App/App.csproj", "<Project />");

        var scan = ProjectScanner.Scan(fixture.Path);

        Assert.False(scan.HasRootCodeFiles);
    }

    [Fact]
    public void 目录快照逐层记录并排除噪声()
    {
        using var fixture = new ScannerFixture();
        fixture.AddFile("framework/WitchC/WitchC.csproj", "<Project />");
        fixture.AddFile("framework/WitchC/Utils/Md5.cs", "// 代码");
        fixture.AddFile("framework/bin/Debug/Drop.cs", "// 构建产物");

        var scan = ProjectScanner.Scan(fixture.Path);

        Assert.Contains("framework", scan.KnownDirectories);
        Assert.Contains("framework/WitchC", scan.KnownDirectories);
        Assert.Contains("framework/WitchC/Utils", scan.KnownDirectories);
        Assert.DoesNotContain("framework/bin", scan.KnownDirectories);
        Assert.DoesNotContain("framework/bin/Debug", scan.KnownDirectories);
    }

    [Fact]
    public void gitignore忽略的目录不算骨架()
    {
        using var fixture = new ScannerFixture();
        fixture.AddFile(".gitignore", ".verify/\n");
        fixture.AddFile(".verify/Fixtures/Bad.cs", "// 被忽略的违规现场");
        fixture.AddFile("src/App/App.csproj", "<Project />");

        var scan = ProjectScanner.Scan(fixture.Path);

        var names = scan.TopDirectories.Select(d => d.Name).ToList();
        Assert.DoesNotContain(".verify", names);
        Assert.DoesNotContain(".verify", scan.KnownDirectories);
        Assert.Contains("src", names);
    }

    [Fact]
    public void 空目录进基线快照不被误报为AI新建()
    {
        // F2：moon-adorn 实测缺口——Services/ 空目录漏出快照，往里写合法代码被误报
        using var fixture = new ScannerFixture();
        fixture.AddFile("Program.cs", "// 入口");
        fixture.AddDirectory("Services"); // 既有空目录（团队预留）
        fixture.AddDirectory("Data/Empty"); // 空目录树也应进快照

        var scan = ProjectScanner.Scan(fixture.Path);

        Assert.Contains("Services", scan.TopDirectories.Select(d => d.Name));
        Assert.Contains("Services", scan.KnownDirectories);
        Assert.Contains("Data", scan.KnownDirectories);
        Assert.Contains("Data/Empty", scan.KnownDirectories);
    }

    [Fact]
    public void 有文件但无代码的目录仍不进白名单()
    {
        // 空目录进快照 ≠ 文档目录进快照：只有图片/文档的目录维持不收（检查只看代码文件）
        using var fixture = new ScannerFixture();
        fixture.AddFile("Program.cs", "// 入口");
        fixture.AddFile("assets/logo.png", "图片");

        var scan = ProjectScanner.Scan(fixture.Path);

        Assert.DoesNotContain("assets", scan.TopDirectories.Select(d => d.Name));
    }

    [Fact]
    public void 实测代码扩展名收进扫描结果()
    {
        // F3：witchc-website 实测缺口——52 js/12 vue 的仓库被写死 .cs/.csproj 导致空扫描假绿
        using var fixture = new ScannerFixture();
        fixture.AddFile("src/App.vue", "<template />");
        fixture.AddFile("src/main.mjs", "// 入口");
        fixture.AddFile("src/util.cjs", "// 工具");
        fixture.AddFile("src/App.ts", "// 代码");
        fixture.AddFile("readme.md", "# 文档");

        var scan = ProjectScanner.Scan(fixture.Path);

        Assert.Equal([".cjs", ".mjs", ".ts", ".vue"], scan.CodeFileExtensions);
    }

    [Fact]
    public void 目录名语义优先backend标后端工程()
    {
        // F5：moon-adorn 实测缺口——backend/ 里有 package.json 被误标"前端工程"
        // 真实场景是 package.json 与代码文件共存（Node 后端），目录名语义应优先于内容探测
        using var fixture = new ScannerFixture();
        fixture.AddFile("backend/package.json", "{}");
        fixture.AddFile("backend/server.js", "// 后端入口");
        fixture.AddFile("frontend/package.json", "{}");
        fixture.AddFile("frontend/App.vue", "<template />");

        var scan = ProjectScanner.Scan(fixture.Path);

        Assert.Equal("后端工程", scan.TopDirectories.First(d => d.Name == "backend").Purpose);
        Assert.Equal("前端工程", scan.TopDirectories.First(d => d.Name == "frontend").Purpose);
    }

    [Fact]
    public void 纯类库项目判为library()
    {
        // F4：WitchC 试用实测——类库项目吃 scope: app 落位规则会误报门面类
        using var fixture = new ScannerFixture();
        fixture.AddFile("framework/WitchC/WitchC.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        fixture.AddFile("framework/WitchC/WitchCExtensions.cs", "// 门面类");

        var scan = ProjectScanner.Scan(fixture.Path);

        Assert.Equal(ProjectTypeKind.Library, scan.ProjectType);
    }

    [Fact]
    public void web应用判为app()
    {
        using var fixture = new ScannerFixture();
        fixture.AddFile("MoonAdorn.Api/MoonAdorn.Api.csproj", "<Project Sdk=\"Microsoft.NET.Sdk.Web\" />");
        fixture.AddFile("MoonAdorn.Api/Program.cs", "// 入口");

        var scan = ProjectScanner.Scan(fixture.Path);

        Assert.Equal(ProjectTypeKind.App, scan.ProjectType);
    }

    [Fact]
    public void 示例目录里的应用项目不影响形态判定()
    {
        // samples/ 里的 Web 示例是"演示用的应用"，不代表仓库本体形态
        using var fixture = new ScannerFixture();
        fixture.AddFile("framework/WitchC/WitchC.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        fixture.AddFile("samples/Demo.Web/Demo.Web.csproj", "<Project Sdk=\"Microsoft.NET.Sdk.Web\" />");

        var scan = ProjectScanner.Scan(fixture.Path);

        Assert.Equal(ProjectTypeKind.Library, scan.ProjectType);
    }

    [Fact]
    public void 模板与验证目录里的应用项目不影响形态判定()
    {
        // WitchC 回归实测：templates/（模板资产）与 .verify/（包验证现场）的示例项目曾把类库仓库误判为 app
        using var fixture = new ScannerFixture();
        fixture.AddFile("framework/WitchC/WitchC.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        fixture.AddFile("templates/witchc-api/WitchC.WebApi.csproj", "<Project Sdk=\"Microsoft.NET.Sdk.Web\" />");
        fixture.AddFile(".verify/Verify/Verify.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\" />");

        var scan = ProjectScanner.Scan(fixture.Path);

        Assert.Equal(ProjectTypeKind.Library, scan.ProjectType);
    }

    /// <summary>临时仓库夹具：按相对路径生成文件，退出时整体清理。</summary>
    private sealed class ScannerFixture : IDisposable
    {
        public ScannerFixture()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "guardai-scan-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void AddFile(string relativePath, string content)
        {
            var full = System.IO.Path.Combine(Path, relativePath.Replace('/', System.IO.Path.DirectorySeparatorChar));
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!);
            File.WriteAllText(full, content);
        }

        public void AddDirectory(string relativePath)
        {
            Directory.CreateDirectory(System.IO.Path.Combine(Path, relativePath.Replace('/', System.IO.Path.DirectorySeparatorChar)));
        }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch (IOException)
            {
                // 清理失败不影响测试结论
            }
        }
    }
}
