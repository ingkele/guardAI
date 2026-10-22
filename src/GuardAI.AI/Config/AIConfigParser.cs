using System.Globalization;

namespace GuardAI.AI.Config;

/// <summary>
/// AI 配置解析器：从 guardai.yml 的 <c>ai:</c> 段构建 <see cref="AIConfig"/>。
/// </summary>
public static class AIConfigParser
{
    /// <summary>
    /// 解析 <c>ai:</c> 段为 <see cref="AIConfig"/>。
    /// </summary>
    /// <param name="root">guardai.yml 解析出的根字典。</param>
    /// <param name="configFileName">配置文件名（报错用）。</param>
    /// <returns>AI 配置；未配置 <c>ai:</c> 段时返回 null（表示未启用 AI 能力）。</returns>
    /// <exception cref="FormatException">字段非法（未知协议、数值越界、缺 model 等）。</exception>
    public static AIConfig? Parse(IReadOnlyDictionary<string, object?> root, string configFileName = "guardai.yml")
    {
        if (!root.TryGetValue("ai", out var raw) || raw is null)
        {
            return null;
        }

        if (raw is not IReadOnlyDictionary<string, object?> ai)
        {
            // 有 ai: 段但不是映射（缩进错/写成字符串列表）：报真实原因，别误导成"缺少 ai: 段"
            throw new FormatException($"{configFileName}: ai: 段必须是键值映射（检查缩进与字段格式）。");
        }

        var provider = GetString(ai, "provider")?.Trim().ToLowerInvariant();
        if (provider is not (AIConfig.ProviderOpenAI or AIConfig.ProviderAnthropic or AIConfig.ProviderOllama))
        {
            throw new FormatException(
                $"{configFileName}: ai.provider 必须是 openai / anthropic / ollama 之一（当前值：{GetString(ai, "provider") ?? "空"}）。");
        }

        var model = GetString(ai, "model")?.Trim();
        if (string.IsNullOrWhiteSpace(model))
        {
            throw new FormatException($"{configFileName}: ai.model 必填（如 gpt-4o-mini / claude-sonnet-4-5 / qwen3:8b）。");
        }

        var baseUrl = NormalizeBaseUrl(GetString(ai, "base_url"), provider);
        var apiKeyEnv = GetString(ai, "api_key_env")?.Trim();

        // 密钥策略统一：openai / ollama 允许无密钥（本地 vLLM、内网无鉴权网关；客户端按需带头），
        // anthropic 的 API 强制 x-api-key，缺密钥来源直接报错
        if (provider == AIConfig.ProviderAnthropic && string.IsNullOrWhiteSpace(apiKeyEnv))
        {
            throw new FormatException(
                $"{configFileName}: anthropic 协议必须配置 ai.api_key_env（其 API 强制 x-api-key；密钥放环境变量而不是配置文件）。");
        }

        var timeout = GetInt(ai, "timeout_seconds", AIDefaults.TimeoutSeconds, configFileName, min: 1, max: 3600);
        var maxTokens = GetInt(ai, "max_tokens", AIDefaults.MaxTokens, configFileName, min: 256, max: 200000);
        var temperature = GetDouble(ai, "temperature", AIDefaults.Temperature, configFileName);

        return new AIConfig(provider, baseUrl, model, apiKeyEnv, timeout, maxTokens, temperature);
    }

    /// <summary>
    /// 归一化基地址：空则取协议默认值，去尾斜杠。
    /// </summary>
    private static string NormalizeBaseUrl(string? value, string provider)
    {
        var url = string.IsNullOrWhiteSpace(value)
            ? provider switch
            {
                AIConfig.ProviderAnthropic => AIConfig.DefaultAnthropicBaseUrl,
                AIConfig.ProviderOllama => AIConfig.DefaultOllamaBaseUrl,
                _ => AIConfig.DefaultOpenAIBaseUrl,
            }
            : value.Trim();
        return url.TrimEnd('/');
    }

    /// <summary>读取字符串字段（缺省 null）。</summary>
    private static string? GetString(IReadOnlyDictionary<string, object?> map, string key)
        => map.TryGetValue(key, out var v) ? v?.ToString() : null;

    /// <summary>读取整数字段（缺省用默认值，越界报错）。</summary>
    private static int GetInt(IReadOnlyDictionary<string, object?> map, string key, int fallback, string configFileName, int min, int max)
    {
        if (!map.TryGetValue(key, out var raw) || raw is null)
        {
            return fallback;
        }

        if (!int.TryParse(raw.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
        {
            throw new FormatException($"{configFileName}: ai.{key} 必须是整数（当前值：{raw}）。");
        }

        if (value < min || value > max)
        {
            throw new FormatException($"{configFileName}: ai.{key} 取值范围 [{min}, {max}]（当前值：{value}）。");
        }

        return value;
    }

    /// <summary>读取小数字段（缺省用默认值，越界报错）。</summary>
    private static double GetDouble(IReadOnlyDictionary<string, object?> map, string key, double fallback, string configFileName)
    {
        if (!map.TryGetValue(key, out var raw) || raw is null)
        {
            return fallback;
        }

        if (!double.TryParse(raw.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
        {
            throw new FormatException($"{configFileName}: ai.{key} 必须是数字（当前值：{raw}）。");
        }

        // NaN / Infinity 与任何值比较均为 false，能溜过范围校验——先拦死
        if (!double.IsFinite(value))
        {
            throw new FormatException($"{configFileName}: ai.{key} 必须是有限数字（当前值：{raw}）。");
        }

        if (value < 0 || value > 2)
        {
            throw new FormatException($"{configFileName}: ai.{key} 取值范围 [0, 2]（当前值：{value}）。");
        }

        return value;
    }
}
