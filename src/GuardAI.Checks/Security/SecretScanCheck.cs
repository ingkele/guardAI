using System.Text;
using System.Text.RegularExpressions;
using GuardAI.Core.Config;
using GuardAI.Core.Engine;

namespace GuardAI.Checks.Security;

/// <summary>
/// secret 检查模块：密钥泄露确定性检查——疑似凭据文件入库 + 源码硬编码密钥。
/// </summary>
/// <remarks>
/// 来源：案例回放（2026-10-22）发现送审侧 DiffCollector 的敏感过滤把密钥文件拦在 AI 审查之外——
/// AI 看不见它们，密钥问题需要引擎本地确定性兜底。特征与 DiffCollector 三道闸保持一致
/// （由同步测试锁定防漂移）：①敏感文件名 ②敏感目录 ③内容密钥特征。
/// 安全约定：违规消息只报类别与行号，绝不回显密钥内容（报告可能外传，密钥二次泄露）。
/// </remarks>
public sealed class SecretScanCheck : IGuardCheck
{
    /// <summary>
    /// 疑似密钥文件名模式文本（与送审侧 DiffCollector.SensitiveNamePatternText 逐字一致，同步测试锁定）。
    /// </summary>
    public const string SensitiveNamePatternText =
        @"(^|/)(\.env(\..+)?|credentials(\.[a-z]+)?|secrets?\.(yml|yaml|json)|id_(rsa|dsa|ecdsa|ed25519)(\..+)?)$" +
        @"|(\.(pem|key|p12|pfx|jks|keystore|ppk|kdbx)$)";

    /// <summary>
    /// 疑似密钥所在目录模式文本（与送审侧 DiffCollector.SensitiveDirPatternText 逐字一致，同步测试锁定）。
    /// </summary>
    public const string SensitiveDirPatternText =
        @"(^|/)(\.aws|\.ssh|\.gnupg|\.kube)/";

    /// <summary>
    /// 内容密钥特征模式文本（与送审侧 DiffCollector.SecretContentPatternText 逐字一致，同步测试锁定）：
    /// PEM 私钥头、长 API 令牌、AWS AccessKey。
    /// </summary>
    public const string SecretContentPatternText =
        @"(BEGIN [A-Z ]*PRIVATE KEY)|(sk-[A-Za-z0-9_-]{20,})|(AKIA[0-9A-Z]{16})";

    /// <summary>内容扫描的单文件上限（字节）：超过整体跳过，防止大文件拖垮检查（与送审侧同口径）。</summary>
    private const long MaxScanBytes = 1_000_000;

    /// <summary>疑似密钥文件名模式。</summary>
    private static readonly Regex SensitiveNamePattern = new(
        SensitiveNamePatternText,
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>疑似密钥所在目录模式。</summary>
    private static readonly Regex SensitiveDirPattern = new(
        SensitiveDirPatternText,
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>内容密钥特征。</summary>
    private static readonly Regex SecretContentPattern = new(
        SecretContentPatternText,
        RegexOptions.Compiled);

    /// <inheritdoc />
    public string Name => "secret";

    /// <inheritdoc />
    /// <remarks>
    /// 不限扩展名收集（密钥藏在任意文件里，.env / id_rsa 常无代码扩展名）；
    /// 命中文件名/目录特征的文件整体视为凭据文件，不再做内容扫描（避免同一份密钥双报）。
    /// </remarks>
    public CheckResult Run(CheckContext context)
    {
        // 密钥扫描刻意不跟随 include_extensions：凭据文件常无代码扩展名，按扩展名过滤会漏扫
        var files = TargetFileCollector.Collect(context.RepoPath, context.Mode, includeExtensions: null);
        var violations = new List<Violation>();

        foreach (var rel in files)
        {
            if (IsExcluded(rel, context.Config.Exclusions))
            {
                continue;
            }

            if (IsSensitivePath(rel))
            {
                violations.Add(new Violation(
                    rel,
                    "敏感文件",
                    "疑似密钥/凭据文件不应入库（命中敏感文件名或敏感目录特征）",
                    "移出仓库并加入 .gitignore（git rm --cached <文件>）；凭据改由环境变量或密钥管理服务注入（参考 api_key_env 模式）"));
                continue;
            }

            ScanContent(context.RepoPath, rel, violations);
        }

        return new CheckResult(Name, files.Count, violations);
    }

    /// <summary>
    /// 判断文件是否命中豁免规则。
    /// </summary>
    private static bool IsExcluded(string rel, IReadOnlyList<ExclusionRule> exclusions)
    {
        return exclusions.Any(e => GlobMatcher.IsMatch(rel, e.Path));
    }

    /// <summary>
    /// 判断路径是否命中敏感文件名或敏感目录特征。
    /// </summary>
    private static bool IsSensitivePath(string rel)
    {
        return SensitiveNamePattern.IsMatch(rel) || SensitiveDirPattern.IsMatch(rel);
    }

    /// <summary>
    /// 扫描单个文件内容中的密钥特征（逐行定位，二进制与超大文件跳过）。
    /// </summary>
    /// <remarks>违规消息只报特征类别与行号，绝不回显匹配到的密钥内容。</remarks>
    private static void ScanContent(string repoPath, string rel, List<Violation> violations)
    {
        var full = Path.Combine(repoPath, rel.Replace('/', Path.DirectorySeparatorChar));
        var info = new FileInfo(full);
        if (!info.Exists || info.Length == 0 || info.Length > MaxScanBytes || IsBinary(full))
        {
            return;
        }

        // 按 UTF-8 宽容解码（带替换字符）：源码混入个别非法字节时不抛异常，仍能扫到密钥特征
        var text = File.ReadAllText(full, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: false));
        var lines = text.Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var match = SecretContentPattern.Match(lines[i]);
            if (!match.Success)
            {
                continue;
            }

            violations.Add(new Violation(
                rel,
                "硬编码密钥",
                $"第 {i + 1} 行疑似硬编码密钥（{DescribeMatch(match)}特征命中，值已隐去）",
                "移入环境变量或密钥管理服务（参考 api_key_env 模式），代码改读配置；已提交进 Git 历史的密钥必须立即轮换"));
        }
    }

    /// <summary>
    /// 描述命中的密钥特征类别（只报类别，不回显密钥值）。
    /// </summary>
    private static string DescribeMatch(Match match)
    {
        var value = match.Value;
        if (value.StartsWith("BEGIN", StringComparison.Ordinal))
        {
            return "PEM 私钥头";
        }

        if (value.StartsWith("AKIA", StringComparison.Ordinal))
        {
            return "AWS AccessKey";
        }

        return "长 API 令牌";
    }

    /// <summary>
    /// 判断文件是否为二进制（前 8KB 含 NUL 字节即视为二进制，不做内容扫描）。
    /// </summary>
    private static bool IsBinary(string fullPath)
    {
        using var stream = File.OpenRead(fullPath);
        var buffer = new byte[Math.Min(8192, stream.Length)];
        var read = stream.Read(buffer, 0, buffer.Length);
        for (var i = 0; i < read; i++)
        {
            if (buffer[i] == 0)
            {
                return true;
            }
        }

        return false;
    }
}
