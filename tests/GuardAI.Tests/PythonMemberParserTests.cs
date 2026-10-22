using GuardAI.Checks.Assets;

namespace GuardAI.Tests;

/// <summary>
/// Python 适配器测试：缩进体、docstring 摘要、下划线可见性、嵌套函数不外漏。
/// </summary>
public class PythonMemberParserTests
{
    [Fact]
    public void 类方法与docstring摘要_私有跳过()
    {
        var source = """
            class Repo:
                def Save(self, entity):
                    '''保存实体到存储。'''
                    self.db.write(entity)

                def _helper(self):
                    pass
            """;

        var members = PythonMemberParser.Parse(source, "app/repo.py");

        var member = Assert.Single(members);
        Assert.Equal("Save", member.Name);
        Assert.Equal("Repo", member.TypeName);
        Assert.Equal("保存实体到存储。", member.Summary);
    }

    [Fact]
    public void 嵌套函数不外漏()
    {
        var source = """
            def Outer(items):
                def Inner(x):
                    return x * 2
                return [Inner(i) for i in items]
            """;

        var members = PythonMemberParser.Parse(source, "util.py");

        // Inner 在 Outer 的缩进体内，整段消费防外漏
        var member = Assert.Single(members);
        Assert.Equal("Outer", member.Name);
        Assert.Equal(string.Empty, member.TypeName);
    }

    [Fact]
    public void 构造方法与下划线成员跳过_顶层函数收()
    {
        var source = """
            class Foo:
                def __init__(self):
                    self.x = 1

                def Run(self):
                    return self.x

            # 聚合全部条目
            def BuildAll(items):
                return list(items)
            """;

        var members = PythonMemberParser.Parse(source, "build.py");

        Assert.Collection(members,
            m =>
            {
                Assert.Equal("Run", m.Name);
                Assert.Equal("Foo", m.TypeName);
            },
            m =>
            {
                Assert.Equal("BuildAll", m.Name);
                Assert.Equal("聚合全部条目", m.Summary);
            });
    }

    [Fact]
    public void 异步方法命中_多行签名()
    {
        var source = """
            class Worker:
                async def Fetch(
                    self,
                    url: str,
                ) -> str:
                    return await get(url)
            """;

        var members = PythonMemberParser.Parse(source, "worker.py");

        var member = Assert.Single(members);
        Assert.Equal("Fetch", member.Name);
        Assert.Equal("Worker", member.TypeName);
    }
}
