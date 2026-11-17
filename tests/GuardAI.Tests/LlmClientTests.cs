using System.Net;
using System.Text;
using GuardAI.AI.Config;
using GuardAI.AI.Llm;

namespace GuardAI.Tests;

/// <summary>三协议 LLM 客户端测试（假 HttpMessageHandler 验证请求形状与响应解析）。</summary>
public class LlmClientTests
{
    /// <summary>假 HTTP 处理器：记录请求，返回预设响应。</summary>
    private sealed class FakeHandler : HttpMessageHandler
    {
        public HttpRequestMessage? LastRequest;
        public string? LastBody;
        private readonly string _responseBody;
        private readonly HttpStatusCode _statusCode;

        public FakeHandler(string responseBody, HttpStatusCode statusCode = HttpStatusCode.OK)
        {
            _responseBody = responseBody;
            _statusCode = statusCode;
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequest = request;
            LastBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(_statusCode)
            {
                Content = new StringContent(_responseBody, Encoding.UTF8, "application/json"),
            };
        }
    }

    private static AIConfig Config(string provider, string? keyEnv = null)
        => new(provider, provider == "ollama" ? "http://localhost:11434" : "https://api.example.com/v1", "test-model", keyEnv);

    [Fact]
    public async Task openai协议_请求形状与响应解析正确()
    {
        Environment.SetEnvironmentVariable("GUARDAI_LLM_TEST_KEY", "sk-abc");
        try
        {
            var handler = new FakeHandler("""{"choices":[{"message":{"content":"模型回复"}}]}""");
            using var http = new HttpClient(handler);
            var client = new OpenAICompatClient(http, Config("openai", "GUARDAI_LLM_TEST_KEY"));

            var result = await client.ChatAsync("系统提示", "用户提示");

            Assert.Equal("模型回复", result);
            Assert.Equal(HttpMethod.Post, handler.LastRequest!.Method);
            Assert.Equal("https://api.example.com/v1/chat/completions", handler.LastRequest.RequestUri!.ToString());
            Assert.Equal("Bearer", handler.LastRequest.Headers.Authorization!.Scheme);
            Assert.Equal("sk-abc", handler.LastRequest.Headers.Authorization!.Parameter);
            Assert.Contains("\"test-model\"", handler.LastBody);
            Assert.Contains("系统提示", handler.LastBody);
            Assert.Contains("用户提示", handler.LastBody);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GUARDAI_LLM_TEST_KEY", null);
        }
    }

    [Fact]
    public async Task anthropic协议_请求形状与响应解析正确()
    {
        Environment.SetEnvironmentVariable("GUARDAI_LLM_TEST_KEY", "sk-ant");
        try
        {
            var handler = new FakeHandler("""{"content":[{"type":"text","text":"审查结论"}]}""");
            using var http = new HttpClient(handler);
            var client = new AnthropicClient(http, Config("anthropic", "GUARDAI_LLM_TEST_KEY"));

            var result = await client.ChatAsync("系统提示", "用户提示");

            Assert.Equal("审查结论", result);
            Assert.Equal("https://api.example.com/v1/messages", handler.LastRequest!.RequestUri!.ToString());
            Assert.True(handler.LastRequest.Headers.Contains("x-api-key"));
            Assert.Contains("anthropic-version", handler.LastRequest.Headers.Select(h => h.Key));
            Assert.Contains("\"system\"", handler.LastBody);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GUARDAI_LLM_TEST_KEY", null);
        }
    }

    [Fact]
    public async Task ollama协议_无鉴权_请求形状与响应解析正确()
    {
        var handler = new FakeHandler("""{"message":{"content":"本地模型回复"}}""");
        using var http = new HttpClient(handler);
        var client = new OllamaClient(http, Config("ollama"));

        var result = await client.ChatAsync("系统提示", "用户提示");

        Assert.Equal("本地模型回复", result);
        Assert.Equal("http://localhost:11434/api/chat", handler.LastRequest!.RequestUri!.ToString());
        Assert.Null(handler.LastRequest.Headers.Authorization);
        Assert.Contains("\"stream\":false", handler.LastBody!.Replace(" ", string.Empty));
    }

    [Fact]
    public async Task openai协议_HTTP错误_抛LlmException带状态码()
    {
        var handler = new FakeHandler("""{"error":"bad key"}""", HttpStatusCode.Unauthorized);
        using var http = new HttpClient(handler);
        var client = new OpenAICompatClient(http, Config("openai"));

        var ex = await Assert.ThrowsAsync<LlmException>(() => client.ChatAsync("s", "u"));
        Assert.Contains("401", ex.Message);
    }

    [Fact]
    public async Task openai协议_响应缺内容_抛LlmException()
    {
        var handler = new FakeHandler("""{"choices":[]}""");
        using var http = new HttpClient(handler);
        var client = new OpenAICompatClient(http, Config("openai"));

        await Assert.ThrowsAsync<LlmException>(() => client.ChatAsync("s", "u"));
    }

    [Fact]
    public void 工厂_按协议派发正确实现()
    {
        using var http = new HttpClient();
        Assert.IsType<OpenAICompatClient>(LlmClientFactory.Create(http, Config("openai")));
        Assert.IsType<AnthropicClient>(LlmClientFactory.Create(http, Config("anthropic")));
        Assert.IsType<OllamaClient>(LlmClientFactory.Create(http, Config("ollama")));
    }

    /// <summary>
    /// 空正文按真实原因分路报错（Issue #2）：有思考内容 vs 纯空正文必须给不同结论，且都不许怪截断。
    /// </summary>
    /// <param name="responseBody">模型响应体（覆盖 reasoning_content / reasoning 两种厂商字段名，及 content 的两种空形态）。</param>
    /// <param name="expectedText">该情形下报错必须包含的关键结论。</param>
    [Theory]
    [InlineData("""{"choices":[{"finish_reason":"stop","message":{"content":"","reasoning_content":"我先分析一下…"}}]}""", "只返回了思考内容")]
    [InlineData("""{"choices":[{"finish_reason":"stop","message":{"content":null,"reasoning":"思考中…"}}]}""", "只返回了思考内容")]
    [InlineData("""{"choices":[{"finish_reason":"stop","message":{"content":""}}]}""", "模型返回空正文")]
    public async Task openai协议_空正文_按原因分路报错且不怪截断(string responseBody, string expectedText)
    {
        var handler = new FakeHandler(responseBody);
        using var http = new HttpClient(handler);
        var client = new OpenAICompatClient(http, Config("openai"));

        var ex = await Assert.ThrowsAsync<LlmException>(() => client.ChatAsync("s", "u"));

        Assert.Contains(expectedText, ex.Message);
        Assert.Contains("finish_reason=stop", ex.Message);
        Assert.DoesNotContain("截断", ex.Message);
    }

    /// <summary>finish_reason=length：保留截断语义，但不再写死「16384 起步」，只报实际配置值。</summary>
    [Fact]
    public async Task openai协议_长度截断_报实际配置值不写死起步值()
    {
        var handler = new FakeHandler("""{"choices":[{"finish_reason":"length","message":{"content":""}}]}""");
        using var http = new HttpClient(handler);
        var client = new OpenAICompatClient(http, new AIConfig(
            "openai", "https://api.example.com/v1", "test-model", null, MaxTokens: 32768));

        var ex = await Assert.ThrowsAsync<LlmException>(() => client.ChatAsync("s", "u"));

        Assert.Contains("截断", ex.Message);
        Assert.Contains("ai.max_tokens=32768", ex.Message);
        Assert.DoesNotContain("16384 起步", ex.Message);
    }

    /// <summary>空正文报错不再回显整个响应体（避免大体 JSON 淹没真因）。</summary>
    [Fact]
    public async Task openai协议_空正文报错_不回显原始响应体()
    {
        var handler = new FakeHandler(
            """{"id":"chatcmpl-verylongidentifier","object":"chat.completion","model":"m","choices":[{"finish_reason":"stop","message":{"content":""}}]}""");
        using var http = new HttpClient(handler);
        var client = new OpenAICompatClient(http, Config("openai"));

        var ex = await Assert.ThrowsAsync<LlmException>(() => client.ChatAsync("s", "u"));

        Assert.DoesNotContain("chatcmpl-verylongidentifier", ex.Message);
    }
}
