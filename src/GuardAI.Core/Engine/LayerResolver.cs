using GuardAI.Core.Config;

namespace GuardAI.Core.Engine;

/// <summary>
/// 层归属判定：把仓库相对路径映射到声明式层定义（纯函数、确定性）。
/// </summary>
/// <remarks>
/// 顺序即优先级：配置里靠前的层先匹配，命中即返回。未命中任何层返回 null——
/// 这是首跑零误报的关键（配置不完整的存量仓库只会少报，不会误伤）。
/// </remarks>
public static class LayerResolver
{
    /// <summary>
    /// 解析文件所属的层。
    /// </summary>
    /// <param name="rel">仓库相对路径（正斜杠）。</param>
    /// <param name="layers">层定义列表（顺序即优先级）。</param>
    /// <returns>命中的层定义；未命中返回 null。</returns>
    public static LayerDef? Resolve(string rel, IReadOnlyList<LayerDef> layers)
    {
        foreach (var layer in layers)
        {
            if (GlobMatcher.IsMatch(rel, layer.Path))
            {
                return layer;
            }
        }

        return null;
    }
}
