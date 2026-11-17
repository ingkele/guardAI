namespace GuardAI.Core.Config;

/// <summary>
/// 端点鉴权检查配置（guardai.yml 的 <c>endpoint_auth:</c> 段，Issue #6）。
/// </summary>
/// <param name="Enabled">是否启用端点鉴权检查（缺省 false——未配置整模块跳过，守住首跑零误报）。</param>
/// <param name="Exemptions">刻意匿名端点的豁免清单（必须写 reason）。</param>
/// <param name="RequirePermission">写操作端点是否要求权限码（缺省 false：只查鉴权声明存在性，零误报优先）。</param>
public sealed record EndpointAuthRuleConfig(
    bool Enabled = false,
    IReadOnlyList<EndpointExemption>? Exemptions = null,
    bool RequirePermission = false)
{
    /// <summary>默认配置：未启用，零误报。</summary>
    public static EndpointAuthRuleConfig Default { get; } = new();

    /// <summary>豁免清单（规范化：永不为 null）。</summary>
    public IReadOnlyList<EndpointExemption> ExemptionList => Exemptions ?? [];
}

/// <summary>
/// 刻意匿名端点的豁免条目。
/// </summary>
/// <param name="Path">相对仓库根的文件路径通配模式（如 src/Api/Program.cs）。</param>
/// <param name="Endpoints">端点路由通配模式列表（如 /health、/openapi/**）。</param>
/// <param name="Reason">豁免理由（必填：缺失即报违规，防白名单变摆设）。</param>
public sealed record EndpointExemption(string Path, IReadOnlyList<string> Endpoints, string Reason);