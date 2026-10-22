using GuardAI.Checks.Assets;

namespace GuardAI.Tests;

/// <summary>
/// C# 轻量语法适配器测试：抽取正确性与"只漏不误"原则。
/// </summary>
public class CSharpMemberParserTests
{
    [Fact]
    public void 抽取公共方法的类型签名行号与方法体()
    {
        var source = """
            namespace Demo;

            public class FileStorageService
            {
                public string Upload(string path)
                {
                    var name = Path.GetFileName(path);
                    return name;
                }
            }
            """;

        var members = CSharpMemberParser.Parse(source, "src/Demo/FileStorageService.cs");

        var member = Assert.Single(members);
        Assert.Equal("FileStorageService", member.TypeName);
        Assert.Equal("Upload", member.Name);
        Assert.Equal("public string Upload(string path)", member.Signature);
        Assert.Equal("src/Demo/FileStorageService.cs", member.File);
        Assert.Equal(5, member.Line);
        Assert.Contains("Path.GetFileName", member.BodyText);
    }

    [Fact]
    public void 私有方法与无方法体声明不产生条目()
    {
        var source = """
            public interface IRepo
            {
                Task SaveAsync(string data);
            }

            public class Repo : IRepo
            {
                private void Hidden()
                {
                    DoStuff();
                }

                internal void AlsoHidden()
                {
                    DoStuff();
                }

                public async Task SaveAsync(string data)
                {
                    await WriteAsync(data);
                }
            }
            """;

        var members = CSharpMemberParser.Parse(source, "Repo.cs");

        var member = Assert.Single(members);
        Assert.Equal("SaveAsync", member.Name);
    }

    [Fact]
    public void 构造函数与属性不产生条目()
    {
        var source = """
            public class Widget
            {
                public Widget(string name)
                {
                    Name = name;
                }

                public string Name { get; set; }

                public int Count => Compute();
            }
            """;

        Assert.Empty(CSharpMemberParser.Parse(source, "Widget.cs"));
    }

    [Fact]
    public void 表达式体方法抽取归一化方法体()
    {
        var source = """
            public class Calc
            {
                public int Add(int a, int b)   =>   a + b;
            }
            """;

        var members = CSharpMemberParser.Parse(source, "Calc.cs");

        var member = Assert.Single(members);
        Assert.Equal("Add", member.Name);
        Assert.Equal("a + b", member.BodyText);
    }

    [Fact]
    public void 多行签名与多行方法体正常抽取()
    {
        var source = """
            public class Svc
            {
                public async Task<string> BuildAsync(
                    string a,
                    int b)
                {
                    var x = await LoadAsync(a);
                    return x + b.ToString();
                }
            }
            """;

        var members = CSharpMemberParser.Parse(source, "Svc.cs");

        var member = Assert.Single(members);
        Assert.Equal("BuildAsync", member.Name);
        Assert.Equal("public async Task<string> BuildAsync( string a, int b)", member.Signature);
        Assert.Contains("LoadAsync", member.BodyText);
    }

    [Fact]
    public void 注释与字符串中的伪方法不产生条目()
    {
        var source = """
            public class Doc
            {
                // public void InComment(string x) { Fake(); }
                /* public void InBlock(int y) { Fake(); } */
                public void Real(string z)
                {
                    var text = "public void InString() { Fake(); }";
                    Use(text);
                }
            }
            """;

        var members = CSharpMemberParser.Parse(source, "Doc.cs");

        var member = Assert.Single(members);
        Assert.Equal("Real", member.Name);
    }

    [Fact]
    public void 方法体归一化忽略注释与字符串差异()
    {
        var a = """
            public class A
            {
                public int Calc(int x)
                {
                    // 加一
                    return x + 1;
                }
            }
            """;
        var b = """
            public class B
            {
                public int Calc(int x)
                {
                    return x + 1; // 改了注释
                }
            }
            """;

        var memberA = Assert.Single(CSharpMemberParser.Parse(a, "A.cs"));
        var memberB = Assert.Single(CSharpMemberParser.Parse(b, "B.cs"));

        Assert.Equal(memberA.BodyHash, memberB.BodyHash);
    }

    [Fact]
    public void 不同方法体哈希不同()
    {
        var source = """
            public class A
            {
                public int One(int x)
                {
                    return x + 1;
                }

                public int Two(int x)
                {
                    return x + 2;
                }
            }
            """;

        var members = CSharpMemberParser.Parse(source, "A.cs");

        Assert.Collection(members,
            m => Assert.Equal("One", m.Name),
            m => Assert.Equal("Two", m.Name));
        Assert.NotEqual(members[0].BodyHash, members[1].BodyHash);
    }

    [Fact]
    public void 本地函数不重复计数()
    {
        var source = """
            public class Outer
            {
                public int Run(int x)
                {
                    int Local(int y)
                    {
                        return y * 2;
                    }

                    return Local(x);
                }
            }
            """;

        var members = CSharpMemberParser.Parse(source, "Outer.cs");

        var member = Assert.Single(members);
        Assert.Equal("Run", member.Name);
        // 本地函数体混进外层方法体不致命——它就在外层方法体内
        Assert.Contains("Local", member.BodyText);
    }

    [Fact]
    public void 多行主构造函数的record不丢类型归属()
    {
        // 回归现场：record 参数列表跨行时，参数行花括号深度仍为 0，
        // 修复前类型栈被误弹、方法 typeName 变空
        var source = """
            public sealed record Snapshot(
                string GeneratedAt,
                string RepoPath,
                int Count)
            {
                public string Render()
                {
                    return RepoPath;
                }
            }
            """;

        var member = Assert.Single(CSharpMemberParser.Parse(source, "Snapshot.cs"));

        Assert.Equal("Snapshot", member.TypeName);
        Assert.Equal("Render", member.Name);
    }

    [Fact]
    public void 同级多个类型的方法各自归属()
    {
        var source = """
            public class Alpha
            {
                public void RunA()
                {
                    Do();
                }
            }

            public class Beta
            {
                public void RunB()
                {
                    Do();
                }
            }
            """;

        var members = CSharpMemberParser.Parse(source, "Two.cs");

        Assert.Collection(members,
            m =>
            {
                Assert.Equal("Alpha", m.TypeName);
                Assert.Equal("RunA", m.Name);
            },
            m =>
            {
                Assert.Equal("Beta", m.TypeName);
                Assert.Equal("RunB", m.Name);
            });
    }

    [Fact]
    public void 元组返回值方法名正确解析()
    {
        // 回归锁：返回值是元组时首个 '(' 不是参数表，方法名曾被解析成 "static"
        var source = """
            public class Advisor
            {
                public static (string System, string User) BuildPrompt(string digest)
                {
                    return ("s", "u");
                }
            }
            """;

        var members = CSharpMemberParser.Parse(source, "src/Demo/Advisor.cs");

        var member = Assert.Single(members);
        Assert.Equal("BuildPrompt", member.Name);
        Assert.Equal("Advisor", member.TypeName);
    }
}
