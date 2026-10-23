using GuardAI.Checks.Code;
using GuardAI.Core.Config;
using GuardAI.Core.Engine;

namespace GuardAI.Tests;

/// <summary>
/// sql 检查测试（SQL 拼接确定性检查）。重点：
/// ①三种构造（插值含洞 / 与标识符拼接 / string.Format）报违规含行号、值不回显；
/// ②纯字面量拼接、注释里的 SQL、小写日志文案不算（零误报）；
/// ③EF 参数化插值豁免；
/// ④测试代码豁免与豁免逃生口。
/// </summary>
public class SqlSafetyCheckTests
{
    [Fact]
    public void 插值SQL含洞_报违规含行号()
    {
        using var fixture = new TempRepoFixture();
        fixture.AddFile("src/App/UserDal.cs", """
            namespace Demo;

            var sql = $"SELECT id FROM Users WHERE name = '{name}'";
            """);

        var result = Run(fixture.Path);

        var violation = Assert.Single(result.Violations);
        Assert.Equal("SQL 拼接", violation.Rule);
        Assert.Contains("第 3 行", violation.Message);
    }

    [Fact]
    public void 字符串与标识符拼接_报违规()
    {
        using var fixture = new TempRepoFixture();
        fixture.AddFile("src/App/UserDal.cs", """
            namespace Demo;

            var sql = "SELECT id FROM Users WHERE id = " + id;
            """);

        var result = Run(fixture.Path);

        var violation = Assert.Single(result.Violations);
        Assert.Equal("SQL 拼接", violation.Rule);
    }

    [Fact]
    public void stringFormat拼进语句_报违规()
    {
        using var fixture = new TempRepoFixture();
        fixture.AddFile("src/App/UserDal.cs", """
            namespace Demo;

            var sql = string.Format("SELECT id FROM Users WHERE id = {0}", id);
            """);

        var result = Run(fixture.Path);

        var violation = Assert.Single(result.Violations);
        Assert.Equal("SQL 拼接", violation.Rule);
    }

    [Fact]
    public void 纯字面量拼接_不报()
    {
        using var fixture = new TempRepoFixture();
        fixture.AddFile("src/App/UserDal.cs", """
            namespace Demo;

            var sql = "SELECT id FROM Users" + " WHERE active = 1";
            """);

        var result = Run(fixture.Path);

        Assert.Empty(result.Violations);
    }

    [Fact]
    public void 注释里的SQL拼接_不算()
    {
        using var fixture = new TempRepoFixture();
        fixture.AddFile("src/App/UserDal.cs", """
            namespace Demo;

            // var sql = "SELECT id FROM Users WHERE id = " + id;
            /* var bad = $"SELECT id FROM Users WHERE id = {id}"; */
            """);

        var result = Run(fixture.Path);

        Assert.Empty(result.Violations);
    }

    [Fact]
    public void FromSqlInterpolated参数化插值_豁免()
    {
        using var fixture = new TempRepoFixture();
        fixture.AddFile("src/App/UserDal.cs", """
            namespace Demo;

            var q = db.Users.FromSqlInterpolated($"SELECT id FROM Users WHERE id = {id}");
            """);

        var result = Run(fixture.Path);

        Assert.Empty(result.Violations);
    }

    [Fact]
    public void 小写日志文案与普通拼接_零误报()
    {
        using var fixture = new TempRepoFixture();
        fixture.AddFile("src/App/Log.cs", """
            namespace Demo;

            logger.LogInformation("user deleted from the list");
            var msg = "hello " + name;
            """);

        var result = Run(fixture.Path);

        Assert.Empty(result.Violations);
    }

    [Fact]
    public void 测试代码_豁免()
    {
        using var fixture = new TempRepoFixture();
        fixture.AddFile("tests/App/FakeDal.cs", """
            namespace Demo;

            var sql = "SELECT id FROM Users WHERE id = " + id;
            """);

        var result = Run(fixture.Path);

        Assert.Empty(result.Violations);
    }

    [Fact]
    public void 豁免规则命中_整体跳过()
    {
        using var fixture = new TempRepoFixture();
        fixture.AddFile("src/App/LegacyDal.cs", """
            namespace Demo;

            var sql = "SELECT id FROM Users WHERE id = " + id;
            """);

        var config = StructureRuleConfigLoader.Parse("""
            version: 1
            exclusions:
              - path: src/App/LegacyDal.cs
                reason: 遗留代码待参数化改造
            """);
        var result = Run(fixture.Path, config);

        Assert.Empty(result.Violations);
    }

    /// <summary>执行 sql 检查。</summary>
    private static CheckResult Run(string repoPath, StructureRuleConfig? config = null)
    {
        var context = new CheckContext(repoPath, CheckMode.All, config ?? StructureRuleConfigLoader.Parse("version: 1"), "guardai.yml");
        return new SqlSafetyCheck().Run(context);
    }
}
