namespace GuardAI.AI;

/// <summary>
/// AI 装配建议条目（目录白名单 / 豁免共用）：路径通配 + 用途/理由说明。
/// </summary>
/// <param name="Path">相对仓库根的路径通配（支持 ** 任意层、* 单层）。</param>
/// <param name="Purpose">用途/理由说明（中文，给团队与后续 AI 看）。</param>
public sealed record InitAdviceItem(string Path, string Purpose);

/// <summary>
/// AI 装配建议的落位规则条目。
/// </summary>
/// <param name="Name">规则名（中文）。</param>
/// <param name="FilePattern">文件名通配。</param>
/// <param name="DirPattern">所在目录必须命中的通配。</param>
/// <param name="Suggest">整改建议（中文）。</param>
/// <param name="Scope">适用项目形态（app / library / all）。</param>
public sealed record InitAdvicePlacement(
    string Name, string FilePattern, string DirPattern, string Suggest, string Scope);

/// <summary>
/// AI 装配建议（guardai.yml 装配配置的增量建议）。
/// </summary>
/// <remarks>
/// 产品铁律"引擎裁判，AI 装配"：本结构只含配置建议，不含任何合规判定；
/// 合并进 guardai.yml 时可人工核对修改，判定永远由确定性引擎完成。
/// </remarks>
/// <param name="ProjectType">项目形态建议（app / library；拿不准为 null，沿用扫描判定）。</param>
/// <param name="AllowedDirs">目录白名单增量建议。</param>
/// <param name="PlacementRules">落位规则增量建议。</param>
/// <param name="Exclusions">豁免建议（生成代码/固定现场/第三方等有意放行）。</param>
/// <param name="DroppedNotes">被 schema 校验丢弃的条目说明（模型输出不合规时的安全网）。</param>
public sealed record InitAdvice(
    string? ProjectType,
    IReadOnlyList<InitAdviceItem> AllowedDirs,
    IReadOnlyList<InitAdvicePlacement> PlacementRules,
    IReadOnlyList<InitAdviceItem> Exclusions,
    IReadOnlyList<string> DroppedNotes)
{
    /// <summary>空建议（未启用 AI 装配或模型无有效输出时的中性值）。</summary>
    public static InitAdvice Empty { get; } = new(
        null, [], [], [], []);
}
