using System.Text;
using GuardAI.Core.Config;
using GuardAI.Core.Engine;

namespace GuardAI.Checks.Code;

/// <summary>
/// 代码级检查模块共用骨架（doc / sql / perf）：.cs 文件收集 → 豁免与测试豁免过滤 → 逐文件扫描 → 汇总。
/// </summary>
/// <remarks>
/// 抽骨架合一（2026-10-23）：三个代码级检查共用本模板，子类只写逐文件扫描逻辑
/// （<see cref="ScanFile"/>），避免同族新模块照搬骨架后被自家重复检测逮住（C46 教训）。
/// 口径（只漏不误，宁漏不误）：
/// ①只扫 .cs（多语言文档/SQL 规则后续扩，.ts/.tsx 第二步）；
/// ②测试代码按惯例豁免——测试方法名即文档（微软惯例不强制 XML 注释），
/// 且测试夹具常内嵌 SQL/密钥等反例文本，不参与代码级判定；
/// ③豁免逃生口照旧：guardai.yml exclusions 命中的文件整体跳过。
/// </remarks>
public abstract class CodeScanCheckBase : IGuardCheck
{
    /// <summary>默认参与扫描的扩展名（C#；多语言子类可 override <see cref="Extensions"/>）。</summary>
    private static readonly IReadOnlyList<string> CsExtensions = [".cs"];

    /// <summary>参与扫描的扩展名；子类可覆盖（如 layer 检查覆盖六语言）。</summary>
    protected virtual IReadOnlyList<string> Extensions => CsExtensions;

    /// <inheritdoc />
    public abstract string Name { get; }

    /// <inheritdoc />
    public CheckResult Run(CheckContext context)
    {
        OnBeforeRun(context);
        var files = TargetFileCollector.Collect(context.RepoPath, context.Mode, Extensions);
        var violations = new List<Violation>();
        foreach (var rel in files)
        {
            // 内联豁免判定（不抽公共方法：与 Security 模块的同名小方法错开形态，防重复误判）
            if (context.Config.Exclusions.Any(e => GlobMatcher.IsMatch(rel, e.Path)) || IsTestCode(rel))
            {
                continue;
            }

            var full = Path.Combine(context.RepoPath, rel.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(full))
            {
                continue;
            }

            // UTF-8 宽容解码（带替换字符）：源码混入个别非法字节时不抛异常，仍能完成扫描
            var text = File.ReadAllText(full, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: false));
            ScanFile(rel, text, violations);
        }

        return new CheckResult(Name, files.Count, violations);
    }

    /// <summary>
    /// 扫描前钩子：子类需要整轮共享的配置在此缓存（如 layer 检查缓存层契约配置）。
    /// 基类默认空实现，不影响 doc/sql/perf。
    /// </summary>
    /// <param name="context">检查上下文。</param>
    protected virtual void OnBeforeRun(CheckContext context)
    {
    }

    /// <summary>
    /// 逐文件扫描：子类实现各自的规则逻辑，命中即向 violations 追加。
    /// </summary>
    /// <param name="rel">仓库相对路径（正斜杠）。</param>
    /// <param name="text">文件全文（UTF-8 宽容解码）。</param>
    /// <param name="violations">违规收集列表。</param>
    protected abstract void ScanFile(string rel, string text, List<Violation> violations);

    /// <summary>
    /// 判断是否测试代码（按惯例豁免：tests/test 目录段、*.Tests/*.Test/*.UnitTests/*.UnitTest 项目段、
    /// 裸 UnitTests/UnitTest 目录段、*Tests.cs/*Test.cs 文件名）。
    /// </summary>
    /// <remarks>实现上提到 <see cref="TestCodeDetector"/>（2026-11-16），与层契约检查共用一份判定。</remarks>
    /// <param name="rel">仓库相对路径（正斜杠）。</param>
    /// <returns>是否测试代码（误判为测试=漏报方向，可接受）。</returns>
    protected static bool IsTestCode(string rel) => TestCodeDetector.IsTestCode(rel);

    /// <summary>
    /// 剥离行注释与块注释、保留字符串字面量（SQL/数据访问特征要看见字符串里的内容）。
    /// </summary>
    /// <remarks>实现上提到 <see cref="SourceText.StripComments"/>（2026-11-16），与层契约检查共用一份行为。</remarks>
    /// <param name="text">源码全文。</param>
    /// <returns>剥离注释后的文本（行结构保持）。</returns>
    protected static string StripComments(string text) => SourceText.StripComments(text);
}
