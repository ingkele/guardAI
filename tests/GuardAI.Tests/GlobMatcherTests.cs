using GuardAI.Core.Config;

namespace GuardAI.Tests;

/// <summary>通配符匹配器测试（语义与 guard-tool 保持一致）。</summary>
public class GlobMatcherTests
{
    [Theory]
    [InlineData("src/DemoShop.Domain/Entities/Order.cs", "src/*Domain/**", true)]
    [InlineData("src/DemoShop/Domain/Entities/Order.cs", "src/*/Domain/**", true)]
    [InlineData("src/DemoShop.Infrastructure/Utils/", "**/Utils/**", true)]
    [InlineData("src/DemoShop.Infrastructure/utils2/", "**/Utils/**", false)]
    [InlineData("UploadHelper.cs", "*Helper.cs", true)]
    [InlineData("StringHelper.cs", "*Helper.cs", true)]
    [InlineData("Helper.cs", "*Helper.cs", true)]
    [InlineData("src/a/b/FileHelper.cs", "*Helper.cs", false)] // * 不跨目录
    [InlineData("tests/DemoShop.Tests/OrderTests.cs", "tests/**", true)]
    [InlineData("SRC/DEMO.DOMAIN/x.cs", "src/*Domain/**", true)] // 不区分大小写
    [InlineData("src/DemoShop.Domain/Entities/Order.cs", "src/*/Domain/**", false)] // * 不匹配多层
    public void 通配符匹配语义(string text, string pattern, bool expected)
    {
        Assert.Equal(expected, GlobMatcher.IsMatch(text, pattern));
    }
}
