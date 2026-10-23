using GuardAI.Checks.Code;
using GuardAI.Core.Config;
using GuardAI.Core.Engine;

namespace GuardAI.Tests;

/// <summary>
/// perf 检查测试（数据访问性能确定性检查）。重点：
/// ①循环内查库（N+1）报违规含行号、循环头驱动查询不算；
/// ②SELECT 星号全列查询报违规（FROM / 逗号两形态）；
/// ③纯内存循环、单语句循环不查（零误报 + 只漏不误）；
/// ④测试代码豁免与豁免逃生口。
/// </summary>
public class PerfCheckTests
{
    [Fact]
    public void foreach循环内查库_报违规含行号()
    {
        using var fixture = new TempRepoFixture();
        fixture.AddFile("src/App/OrderQueryService.cs", """
            namespace Demo;

            public class OrderQueryService
            {
                public void Go()
                {
                    var orders = _db.Queryable<Order>().ToList();
                    foreach (var o in orders)
                    {
                        var user = _db.Queryable<User>().First(u => u.Id == o.UserId);
                    }
                }
            }
            """);

        var result = Run(fixture.Path);

        var violation = Assert.Single(result.Violations);
        Assert.Equal("循环内查库", violation.Rule);
        Assert.Contains("第 10 行", violation.Message);
    }

    [Fact]
    public void 循环头驱动查询_不算N加1()
    {
        using var fixture = new TempRepoFixture();
        fixture.AddFile("src/App/UserLoader.cs", """
            namespace Demo;

            public class UserLoader
            {
                public void Go()
                {
                    foreach (var u in _db.Queryable<User>().ToList())
                    {
                        Console.WriteLine(u.Name);
                    }
                }
            }
            """);

        var result = Run(fixture.Path);

        Assert.Empty(result.Violations);
    }

    [Fact]
    public void 星号全列查询_报违规()
    {
        using var fixture = new TempRepoFixture();
        fixture.AddFile("src/App/OrderDal.cs", """
            namespace Demo;

            public class OrderDal
            {
                public void Go()
                {
                    var rows = _db.Ado.SqlQuery<Order>("SELECT * FROM Orders WHERE CreatedAt > @d", DateTime.Today);
                }
            }
            """);

        var result = Run(fixture.Path);

        var violation = Assert.Single(result.Violations);
        Assert.Equal("全列查询", violation.Rule);
        Assert.Contains("第 7 行", violation.Message);
    }

    [Fact]
    public void 星号投影逗号形态_也报()
    {
        using var fixture = new TempRepoFixture();
        fixture.AddFile("src/App/OrderDal.cs", """
            namespace Demo;

            var rows = _db.Ado.SqlQuery<Order>("SELECT *, Name FROM Orders");
            """);

        var result = Run(fixture.Path);

        var violation = Assert.Single(result.Violations);
        Assert.Equal("全列查询", violation.Rule);
    }

    [Fact]
    public void 纯内存循环与内存LINQ_零误报()
    {
        using var fixture = new TempRepoFixture();
        fixture.AddFile("src/App/FileScan.cs", """
            namespace Demo;

            public class FileScan
            {
                public void Go()
                {
                    foreach (var f in files)
                    {
                        var t = File.ReadAllText(f);
                        if (list.Any(y => y == t))
                        {
                            Console.WriteLine(t.Length);
                        }
                    }
                }
            }
            """);

        var result = Run(fixture.Path);

        Assert.Empty(result.Violations);
    }

    [Fact]
    public void 单语句循环不查_只漏不误()
    {
        using var fixture = new TempRepoFixture();
        fixture.AddFile("src/App/Tiny.cs", """
            namespace Demo;

            public class Tiny
            {
                public void Go()
                {
                    foreach (var o in orders) _db.Queryable<User>().First(u => u.Id == o.UserId);
                }
            }
            """);

        var result = Run(fixture.Path);

        Assert.Empty(result.Violations);
    }

    [Fact]
    public void 注释里的数据访问_不算()
    {
        using var fixture = new TempRepoFixture();
        fixture.AddFile("src/App/Note.cs", """
            namespace Demo;

            public class Note
            {
                public void Go()
                {
                    foreach (var o in orders)
                    {
                        // var user = _db.Queryable<User>().First(u => u.Id == o.UserId);
                    }
                }
            }
            """);

        var result = Run(fixture.Path);

        Assert.Empty(result.Violations);
    }

    [Fact]
    public void 测试代码_豁免()
    {
        using var fixture = new TempRepoFixture();
        fixture.AddFile("tests/App/FakeQuery.cs", """
            namespace Demo;

            public class FakeQuery
            {
                public void Go()
                {
                    foreach (var o in orders)
                    {
                        var user = _db.Queryable<User>().First(u => u.Id == o.UserId);
                    }
                }
            }
            """);

        var result = Run(fixture.Path);

        Assert.Empty(result.Violations);
    }

    [Fact]
    public void 豁免规则命中_整体跳过()
    {
        using var fixture = new TempRepoFixture();
        fixture.AddFile("src/App/LegacyQuery.cs", """
            namespace Demo;

            var rows = _db.Ado.SqlQuery<Order>("SELECT * FROM Orders");
            """);

        var config = StructureRuleConfigLoader.Parse("""
            version: 1
            exclusions:
              - path: src/App/LegacyQuery.cs
                reason: 遗留代码待批量查询改造
            """);
        var result = Run(fixture.Path, config);

        Assert.Empty(result.Violations);
    }

    /// <summary>执行 perf 检查。</summary>
    private static CheckResult Run(string repoPath, StructureRuleConfig? config = null)
    {
        var context = new CheckContext(repoPath, CheckMode.All, config ?? StructureRuleConfigLoader.Parse("version: 1"), "guardai.yml");
        return new PerfCheck().Run(context);
    }
}
