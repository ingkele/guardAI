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
}
