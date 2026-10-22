using GuardAI.Checks.Assets;

namespace GuardAI.Tests;

/// <summary>
/// 成员解析器注册表测试：扩展名分发、未知语言只漏不误、扩展名单一来源。
/// </summary>
public class MemberParserRegistryTests
{
    [Fact]
    public void 按扩展名分发到对应语言适配器()
    {
        var cs = MemberParserRegistry.Parse(
            """
            public class A
            {
                public void Run()
                {
                }
            }
            """,
            "src/A.cs");

        var py = MemberParserRegistry.Parse(
            """
            def Run():
                pass
            """,
            "src/a.py");

        var go = MemberParserRegistry.Parse(
            """
            func Run() {
            }
            """,
            "src/a.go");

        Assert.Equal("Run", Assert.Single(cs).Name);
        Assert.Equal("Run", Assert.Single(py).Name);
        Assert.Equal("Run", Assert.Single(go).Name);
    }

    [Fact]
    public void 未知扩展名返回空_不抛异常()
    {
        var members = MemberParserRegistry.Parse("anything", "doc/readme.txt");

        Assert.Empty(members);
        Assert.False(MemberParserRegistry.Supports("doc/readme.txt"));
    }

    [Fact]
    public void 扩展名清单覆盖全语言()
    {
        foreach (var ext in new[] { ".cs", ".java", ".kt", ".ts", ".js", ".go", ".py" })
        {
            Assert.Contains(ext, MemberParserRegistry.Extensions);
        }
    }
}
