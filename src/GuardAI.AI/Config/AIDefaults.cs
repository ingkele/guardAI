namespace GuardAI.AI.Config;

/// <summary>
/// AI 配置缺省值（单一事实来源：record 构造默认与解析 fallback 共用）。
/// </summary>
public static class AIDefaults
{
    /// <summary>默认调用超时秒数（推理模型需更长，大改动建议 600）。</summary>
    public const int TimeoutSeconds = 300;

    /// <summary>默认回复最大 token 数 8192；使用推理模型时建议在配置中调大到 16384 起步。</summary>
    public const int MaxTokens = 8192;

    /// <summary>默认采样温度（审查任务要求稳定输出）。</summary>
    public const double Temperature = 0.1;
}
