using GuardAI.AI.Config;
using GuardAI.Core.Config;

namespace GuardAI.Tests;

/// <summary>AI 配置解析测试。</summary>
public class AIConfigParserTests
{
    private static IReadOnlyDictionary<string, object?> Yaml(string text) => GuardYaml.Parse(text);

    [Fact]
    public void 缺ai段_返回null()
    {
        var config = AIConfigParser.Parse(Yaml("allowed_dirs: [src]"));
        Assert.Null(config);
    }

    [Fact]
    public void openai完整配置_解析成功()
    {
        var config = AIConfigParser.Parse(Yaml("""
            ai:
              provider: openai
              model: gpt-4o-mini
              api_key_env: OPENAI_API_KEY
              base_url: https://api.example.com/v1
              timeout_seconds: 60
              max_tokens: 8192
              temperature: 0.2
            """));
        Assert.NotNull(config);
        Assert.Equal("openai", config!.Provider);
        Assert.Equal("gpt-4o-mini", config.Model);
        Assert.Equal("OPENAI_API_KEY", config.ApiKeyEnv);
        Assert.Equal("https://api.example.com/v1", config.BaseUrl);
        Assert.Equal(60, config.TimeoutSeconds);
        Assert.Equal(8192, config.MaxTokens);
        Assert.Equal(0.2, config.Temperature);
    }

    [Fact]
    public void openai省略可选项_取默认值()
    {
        var config = AIConfigParser.Parse(Yaml("""
            ai:
              provider: openai
              model: gpt-4o-mini
              api_key_env: OPENAI_API_KEY
            """));
        Assert.NotNull(config);
        Assert.Equal(AIConfig.DefaultOpenAIBaseUrl, config!.BaseUrl);
        Assert.Equal(300, config.TimeoutSeconds);
        Assert.Equal(8192, config.MaxTokens);
        Assert.Equal(0.1, config.Temperature);
    }

    [Fact]
    public void base_url_去尾斜杠()
    {
        var config = AIConfigParser.Parse(Yaml("""
            ai:
              provider: anthropic
              model: claude-sonnet-4-5
              api_key_env: ANTHROPIC_API_KEY
              base_url: https://proxy.example.com/
            """));
        Assert.Equal("https://proxy.example.com", config!.BaseUrl);
    }

    [Fact]
    public void ollama_无需密钥_默认本地地址()
    {
        var config = AIConfigParser.Parse(Yaml("""
            ai:
              provider: ollama
              model: qwen3:8b
            """));
        Assert.NotNull(config);
        Assert.Equal(AIConfig.DefaultOllamaBaseUrl, config!.BaseUrl);
        Assert.Null(config.ApiKeyEnv);
    }

    [Fact]
    public void openai缺api_key_env_放行_本地无鉴权端点()
    {
        var config = AIConfigParser.Parse(Yaml("""
            ai:
              provider: openai
              model: gpt-4o-mini
            """));
        Assert.NotNull(config);
        Assert.Null(config!.ApiKeyEnv);
    }

    [Fact]
    public void 温度NaN或Infinity_报错_不溜过范围校验()
    {
        foreach (var bad in new[] { "NaN", "Infinity", "-Infinity" })
        {
            Assert.Throws<FormatException>(() => AIConfigParser.Parse(Yaml($"""
                ai:
                  provider: ollama
                  model: qwen3:8b
                  temperature: {bad}
                """)));
        }
    }

    [Fact]
    public void anthropic缺api_key_env_报错()
    {
        var ex = Assert.Throws<FormatException>(() => AIConfigParser.Parse(Yaml("""
            ai:
              provider: anthropic
              model: claude-sonnet-4-5
            """)));
        Assert.Contains("api_key_env", ex.Message);
    }

    [Fact]
    public void 未知协议_报错()
    {
        var ex = Assert.Throws<FormatException>(() => AIConfigParser.Parse(Yaml("""
            ai:
              provider: gemini
              model: gemini-pro
              api_key_env: X
            """)));
        Assert.Contains("openai / anthropic / ollama", ex.Message);
    }

    [Fact]
    public void 缺model_报错()
    {
        Assert.Throws<FormatException>(() => AIConfigParser.Parse(Yaml("""
            ai:
              provider: ollama
            """)));
    }

    [Fact]
    public void 温度越界_报错()
    {
        Assert.Throws<FormatException>(() => AIConfigParser.Parse(Yaml("""
            ai:
              provider: ollama
              model: qwen3:8b
              temperature: 3.5
            """)));
    }

    [Fact]
    public void 密钥从环境变量读取_缺变量时抛错()
    {
        var config = new AIConfig("openai", "https://api.openai.com/v1", "m", "GUARDAI_TEST_MISSING_KEY_ENV");
        var ex = Assert.Throws<GuardAI.AI.Llm.LlmException>(() => GuardAI.AI.Llm.LlmKey.Resolve(config));
        Assert.Contains("GUARDAI_TEST_MISSING_KEY_ENV", ex.Message);
    }

    [Fact]
    public void 密钥从环境变量读取_成功()
    {
        Environment.SetEnvironmentVariable("GUARDAI_TEST_KEY_ENV", "sk-test-123");
        try
        {
            var config = new AIConfig("openai", "https://api.openai.com/v1", "m", "GUARDAI_TEST_KEY_ENV");
            Assert.Equal("sk-test-123", GuardAI.AI.Llm.LlmKey.Resolve(config));
        }
        finally
        {
            Environment.SetEnvironmentVariable("GUARDAI_TEST_KEY_ENV", null);
        }
    }
}
