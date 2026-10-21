using System.Security.Cryptography;
using System.Text;
using GuardAI.Core.Engine;

namespace GuardAI.Core.Ledging;

/// <summary>
/// 违规指纹计算器：为每条违规生成跨运行稳定的短指纹。
/// </summary>
/// <remarks>
/// 指纹是闭环率追踪的基础：同一违规在多次运行中指纹不变，
/// 指纹从最新运行中消失即视为"已修复"。指纹由 规则 + 文件 + 说明 三要素哈希而来，
/// 不含行号等易变信息，因此 AI 修复时移动代码位置不会破坏追踪。
/// </remarks>
public static class Fingerprint
{
    /// <summary>指纹十六进制展示长度（SHA-256 全量 64 位截短，冲突概率可忽略）。</summary>
    private const int DisplayLength = 16;

    /// <summary>
    /// 计算违规指纹。
    /// </summary>
    /// <param name="violation">违规记录。</param>
    /// <returns>十六进制短指纹（16 字符，小写）。</returns>
    /// <exception cref="ArgumentNullException">violation 为 null 时抛出。</exception>
    public static string Compute(Violation violation)
    {
        ArgumentNullException.ThrowIfNull(violation);
        return Compute(violation.Rule, violation.File, violation.Message);
    }

    /// <summary>
    /// 计算违规指纹（三要素形式，便于台账回放时复算）。
    /// </summary>
    /// <param name="rule">命中规则名称。</param>
    /// <param name="file">违规文件相对路径。</param>
    /// <param name="message">违规说明。</param>
    /// <returns>十六进制短指纹（16 字符，小写）。</returns>
    public static string Compute(string rule, string file, string message)
    {
        // 三要素用换行拼接，避免 "ab"+"c" 与 "a"+"bc" 拼出相同输入
        var material = $"{rule}\n{file}\n{message}";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(material));
        return Convert.ToHexString(hash)[..DisplayLength].ToLowerInvariant();
    }
}
