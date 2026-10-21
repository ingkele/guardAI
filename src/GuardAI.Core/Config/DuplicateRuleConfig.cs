namespace GuardAI.Core.Config;

/// <summary>
/// 重复检测规则配置（guardai.yml 的 duplicates 段；整段缺失时用默认值）。
/// </summary>
/// <remarks>
/// 语义：AI 重写已有实现时当场拦下——近似检测抓"换皮重写"，注册表抓"高频方法名又写一遍"。
/// 规则放配置、逻辑做薄：阈值/门槛/注册表全部可人工调整，引擎只负责执行。
/// </remarks>
/// <param name="Enabled">是否启用重复检测（默认 true）。</param>
/// <param name="Threshold">近似判定阈值（0~1，token 三元组 Jaccard 相似度，默认 0.85）。</param>
/// <param name="MinTokens">参与近似检测的最小方法体 token 数（过短方法跳过，防误报，默认 12）。</param>
/// <param name="Registry">高频方法名注册表（通配符模式；两侧方法名命中同一模式才判定重复）。</param>
/// <param name="ContainmentThreshold">子集包含度阈值（0~1，默认 0.9）：抓"删行/加行的子集型复制"（Jaccard 会漏的那类，如逐字段映射样板复制时漏字段）。</param>
public sealed record DuplicateRuleConfig(
    bool Enabled,
    double Threshold,
    int MinTokens,
    IReadOnlyList<string> Registry,
    double ContainmentThreshold = 0.9)
{
    /// <summary>
    /// 默认高频方法名注册表——AI 最爱重写的一批轮子（可按项目在 YAML 里增删）。
    /// </summary>
    public static readonly IReadOnlyList<string> DefaultRegistry =
        ["Upload*", "SaveFile*", "GetToken*", "SendSms*", "SendEmail*"];

    /// <summary>
    /// 默认配置（duplicates 段缺失时使用）。
    /// </summary>
    public static readonly DuplicateRuleConfig Default = new(true, 0.85, 12, DefaultRegistry);
}
