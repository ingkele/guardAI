using GuardAI.AI.Review;
using GuardAI.Core.Config;

namespace GuardAI.Tests;

/// <summary>审查提示词构造测试。</summary>
public class ReviewPromptTests
{
    [Fact]
    public void 系统提示_包含JSON输出约束()
    {
        var system = ReviewPrompt.BuildSystem();
        Assert.Contains("\"findings\"", system);
        Assert.Contains("宁漏勿误", system);
    }

    [Fact]
    public void 用户提示_包含规则摘要与差异()
    {
        var user = ReviewPrompt.BuildUser("project_type: classlib", "diff --git a/A.cs b/A.cs");
        Assert.Contains("project_type: classlib", user);
        Assert.Contains("diff --git a/A.cs b/A.cs", user);
    }

    [Fact]
    public void 用户提示_差异为空_抛ArgumentException()
    {
        Assert.Throws<ArgumentException>(() => ReviewPrompt.BuildUser(null, "  "));
    }

    [Fact]
    public void 超长差异_截断并附注()
    {
        var longDiff = new string('x', ReviewPrompt.MaxDiffChars + 100);
        var user = ReviewPrompt.BuildUser(null, longDiff);
        Assert.Contains("截断", user);
        // 送审正文被截到上限：x 的数量正好等于上限
        Assert.Equal(ReviewPrompt.MaxDiffChars, user.Count(c => c == 'x'));
    }

    [Fact]
    public void 规则摘要_提炼关键约定()
    {
        var root = GuardYaml.Parse("""
            project_type: classlib
            allowed_dirs: [framework, module]
            ai:
              provider: ollama
              model: qwen3:8b
            """);
        var summary = ReviewPrompt.BuildRuleSummary(root);
        Assert.NotNull(summary);
        Assert.Contains("project_type: classlib", summary);
        Assert.Contains("allowed_dirs", summary);
        Assert.Contains("framework", summary);
        Assert.DoesNotContain("ollama", summary); // ai 段是接入配置不是代码规范
    }

    [Fact]
    public void 规则摘要_无关键约定返回null()
    {
        Assert.Null(ReviewPrompt.BuildRuleSummary(GuardYaml.Parse("ai:")));
        Assert.Null(ReviewPrompt.BuildRuleSummary(null));
    }
}
