using GuardAI.Core.Engine;

namespace GuardAI.Tests;

/// <summary>
/// 引用扫描测试：五语言 import 识别、注释/字符串不误判、外部包原样返回。
/// </summary>
public class ReferenceScannerTests
{
    /// <summary>单条 import 语句 → 单一引用目标：五语言参数化，避免同骨架测试换皮。</summary>
    [Theory]
    [InlineData(".cs", "using MyApp.Infrastructure;\n\npublic class X { }\n", "MyApp.Infrastructure")]
    [InlineData(".ts", "import { Order } from '@app/infrastructure';\n", "@app/infrastructure")]
    [InlineData(".tsx", "const repo = require('@app/infrastructure');\n", "@app/infrastructure")]
    [InlineData(".go", "package main\n\nimport \"github.com/acme/infra\"\n", "github.com/acme/infra")]
    [InlineData(".py", "from myapp.infrastructure import OrderRepository\n", "myapp.infrastructure")]
    [InlineData(".py", "import myapp.infrastructure\n", "myapp.infrastructure")]
    public void 单条Import识别(string extension, string code, string expected)
    {
        Assert.Equal(new[] { expected }, ReferenceScanner.Scan(code, extension));
    }

    [Fact]
    public void Java静态Import识别()
    {
        var refs = ReferenceScanner.Scan("import static java.util.Collections.emptyList;\nimport com.acme.Web;\n", ".java");

        Assert.Contains("java.util.Collections", refs);
        Assert.Contains("com.acme.Web", refs);
    }

    [Fact]
    public void GoImport块识别()
    {
        var refs = ReferenceScanner.Scan("package main\n\nimport (\n    \"fmt\"\n    \"github.com/acme/infra\"\n)\n", ".go");

        Assert.Contains("fmt", refs);
        Assert.Contains("github.com/acme/infra", refs);
    }

    [Fact]
    public void 行注释与块注释内的Import不算()
    {
        var code = "// using MyApp.Infrastructure;\n/* import com.acme.Web; */\nusing MyApp.Application;\n";
        var refs = ReferenceScanner.Scan(code, ".cs");

        Assert.Equal(new[] { "MyApp.Application" }, refs);
    }

    [Fact]
    public void 字符串字面量里的Using不算()
    {
        var code = "public class X\n{\n    private string S = @\"\nusing MyApp.Infrastructure;\n\";\n}\n";
        var refs = ReferenceScanner.Scan(code, ".cs");

        Assert.DoesNotContain("MyApp.Infrastructure", refs);
    }

    [Fact]
    public void 同一引用去重且保留顺序()
    {
        var code = "using MyApp.Infrastructure;\nusing MyApp.Application;\nusing MyApp.Infrastructure;\n";
        var refs = ReferenceScanner.Scan(code, ".cs");

        Assert.Equal(new[] { "MyApp.Infrastructure", "MyApp.Application" }, refs);
    }

    [Fact]
    public void 未知扩展名返回空()
    {
        Assert.Empty(ReferenceScanner.Scan("using MyApp.Infrastructure;\n", ".rb"));
    }
}
