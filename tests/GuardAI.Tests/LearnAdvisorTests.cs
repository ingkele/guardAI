using System.Net;
using System.Text;
using GuardAI.AI;
using GuardAI.AI.Config;
using GuardAI.AI.Llm;

namespace GuardAI.Tests;

/// <summary>AI 规则发现员测试（素材聚合、候选规则 schema 校验与去重）。</summary>
public class LearnAdvisorTests
{
    /// <summary>假 HTTP 处理器：记录请求，返回预设响应（与 LlmClientTests 同一范式）。</summary>
    private sealed class FakeHandler : HttpMessageHandler
    {
        public string? LastBody;
        private readonly string _responseBody;

        public FakeHandler(string responseBody)
        {
            _responseBody = responseBody;
        }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastBody = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(_responseBody, Encoding.UTF8, "application/json"),
            };
        }
    }

    private const string ReviewSample = """{"findings":[{"severity":"warn","category":"复用","file":"src/A.cs","line":"10","message":"逻辑重复"},{"severity":"info","category":"注释","file":"src/B.cs","line":"20","message":"注释不一致"}]}""";

    private const string LedgerRepeatLine1 =
        """{"id":"1","violations":[{"check":"structure","file":"src/A.cs","rule":"落位规则：工具类必须放 Utils","message":"放错目录","suggestion":"移动","fingerprint":"fp1"}]}""";

    private const string LedgerRepeatLine2 =
        """{"id":"2","violations":[{"check":"structure","file":"src/B.cs","rule":"落位规则：工具类必须放 Utils","message":"又放错","suggestion":"移动","fingerprint":"fp1"}]}""";

    [Fact]
    public void 摘要聚合_类别统计与反复违规榜()
    {
        var digest = LearnDigest.Build(
            [ReviewSample],
            [LedgerRepeatLine1, LedgerRepeatLine2, """{"id":"3","violations":[]}"""],
            [("tests/**/Fixtures/**", "回归测试固定现场")],
            ["工具类必须放 Utils 目录"]);

        Assert.Contains("报告 1 份", digest);
        Assert.Contains("[warn] 复用: 1 条", digest);
        Assert.Contains("src/A.cs:10", digest);
        Assert.Contains("运行 3 次，违规 2 条", digest);
        Assert.Contains("×2", digest); // 同指纹两次 = 反复踩坑
        Assert.Contains("落位规则：工具类必须放 Utils: 2 次", digest);
        Assert.Contains("tests/**/Fixtures/**", digest);
        Assert.Contains("工具类必须放 Utils 目录", digest);
    }

    [Fact]
    public void 摘要聚合_坏JSON不炸_空素材有友好占位()
    {
        var digest = LearnDigest.Build(["不是JSON"], ["坏行", ""], [], []);

        Assert.Contains("可解析 0 份", digest);
        Assert.Contains("（无违规记录）", digest);
        Assert.Contains("（无豁免）", digest);
    }

    [Fact]
    public void 提示词含审计摘要与输出约束()
    {
        var (system, user) = LearnAdvisor.BuildPrompt("## 违规台账（运行 2 次，违规 2 条）");

        Assert.Contains("报案人", system);
        Assert.Contains("JSON", system);
        Assert.Contains("违规台账", user);
        Assert.Contains("宁缺毋滥", user);
        Assert.Contains("现有规则名", user);
    }

    [Fact]
    public void 摘要为空_报错()
    {
        Assert.Throws<ArgumentException>(() => LearnAdvisor.BuildPrompt(" "));
    }

    [Fact]
    public void 解析_三目标层与围栏容忍()
    {
        var advice = LearnAdvisor.ParseReply("""
            以下是候选：
            ```json
            {"rules": [
              {"name": "工具类归位", "target": "yml", "pattern": "placement_rules:\n  - name: x", "evidence": "×3 反复违规", "reason": "反复踩坑"},
              {"name": "注释必须中文", "target": "prompt", "pattern": "所有注释使用中文", "evidence": "注释类 11 条", "reason": "高频"},
              {"name": "字节字符口径", "target": "checker", "pattern": "校验阈值口径检查", "evidence": "review 抓到", "reason": "引擎能力缺口"}
            ]}
            ```
            """, []);

        Assert.Equal(3, advice.Rules.Count);
        Assert.Equal(["yml", "prompt", "checker"], advice.Rules.Select(r => r.Target).ToArray());
    }

    [Fact]
    public void 解析_缺字段或target非法_丢弃并记录()
    {
        var advice = LearnAdvisor.ParseReply("""
            {"rules": [
              {"name": "缺字段", "target": "yml", "pattern": "x"},
              {"name": "目标层瞎写", "target": "magic", "pattern": "x", "evidence": "e", "reason": "r"},
              {"name": "合法条目", "target": "prompt", "pattern": "p", "evidence": "e", "reason": "r"}
            ]}
            """, []);

        Assert.Single(advice.Rules);
        Assert.Equal("合法条目", advice.Rules[0].Name);
        Assert.Equal(2, advice.DroppedNotes.Count);
    }

    [Fact]
    public void 解析_与现有规则重复_丢弃()
    {
        var advice = LearnAdvisor.ParseReply("""
            {"rules": [
              {"name": "工具类必须放 Utils 目录", "target": "yml", "pattern": "p", "evidence": "e", "reason": "r"},
              {"name": "新规则", "target": "yml", "pattern": "p", "evidence": "e", "reason": "r"}
            ]}
            """, ["工具类必须放 Utils 目录"]);

        Assert.Single(advice.Rules);
        Assert.Contains(advice.DroppedNotes, n => n.Contains("重复"));
    }

    [Fact]
    public void 解析_无rules数组_按空建议处理()
    {
        var advice = LearnAdvisor.ParseReply("""{"note": "没提规则"}""", []);

        Assert.Empty(advice.Rules);
        Assert.Contains(advice.DroppedNotes, n => n.Contains("rules"));
    }

    [Fact]
    public void 输出不是JSON_抛格式错误()
    {
        Assert.Throws<FormatException>(() => LearnAdvisor.ParseReply("很抱歉，无法完成", []));
    }

    [Fact]
    public async Task AdviseAsync端到端_摘要进请求_候选出解析()
    {
        // OpenAI 报文外壳：choices[0].message.content 里才是候选规则 JSON 文本
        var handler = new FakeHandler(
            """{"choices":[{"message":{"content":"{\"rules\": [{\"name\": \"新规则\", \"target\": \"yml\", \"pattern\": \"p\", \"evidence\": \"e\", \"reason\": \"r\"}]}"}}]}""");
        using var http = new HttpClient(handler);
        var client = new OpenAICompatClient(http,
            new AIConfig(AIConfig.ProviderOpenAI, "https://api.example.com/v1", "test-model", null));

        var advice = await LearnAdvisor.AdviseAsync(client, "审计摘要内容", ["已有规则"]);

        Assert.Single(advice.Rules);
        Assert.Contains("审计摘要内容", handler.LastBody);
    }
}
