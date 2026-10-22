using GuardAI.AI.Config;

namespace GuardAI.AI.Llm;

/// <summary>
/// 密钥解析工具（Llm 层职责：密钥缺失属鉴权错误，与调用失败同一异常契约，由本层统一封装）。
/// </summary>
internal static class LlmKey
{
    /// <summary>
    /// 读取密钥（从 <see cref="AIConfig.ApiKeyEnv"/> 指定的环境变量）。
    /// </summary>
    /// <param name="config">AI 配置。</param>
    /// <returns>密钥文本；未配置 <see cref="AIConfig.ApiKeyEnv"/> 时返回 null。</returns>
    /// <exception cref="LlmException">配置了环境变量名但环境变量不存在或为空。</exception>
    public static string? Resolve(AIConfig config)
    {
        if (string.IsNullOrWhiteSpace(config.ApiKeyEnv))
        {
            return null;
        }

        var key = Environment.GetEnvironmentVariable(config.ApiKeyEnv);
        if (string.IsNullOrWhiteSpace(key))
        {
            throw new LlmException(
                $"环境变量 {config.ApiKeyEnv} 未设置或为空（ai.api_key_env 指定的密钥来源）。请先设置该环境变量再运行。");
        }

        return key;
    }
}
