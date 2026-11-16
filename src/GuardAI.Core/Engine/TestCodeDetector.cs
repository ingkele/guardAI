namespace GuardAI.Core.Engine;

/// <summary>
/// 测试代码识别（按命名惯例豁免），供代码级检查与层契约检查共用。
/// </summary>
/// <remarks>
/// 上提自 <c>CodeScanCheckBase.IsTestCode</c>（2026-11-16）：两处同判定，留两份会被自家重复检测逮住。
/// 命名惯例补漏（2026-11-14）：VS / xUnit 模板的 Xxx.UnitTests/UnitTest1.cs 段名无点前缀、
/// 文件名又不以 Tests.cs 结尾，早期只认 ".Tests" 会整项目误判为生产代码。
/// 只按明确段名枚举，不放宽成 EndsWith("Tests")——Contests 之类目录会被误豁免（零误报优先）。
/// </remarks>
public static class TestCodeDetector
{
    /// <summary>
    /// 判断是否测试代码。
    /// </summary>
    /// <param name="rel">仓库相对路径（正斜杠）。</param>
    /// <returns>是否测试代码（误判为测试＝漏报方向，可接受）。</returns>
    public static bool IsTestCode(string rel)
    {
        foreach (var segment in rel.Split('/'))
        {
            if (segment.Equals("tests", StringComparison.OrdinalIgnoreCase)
                || segment.Equals("test", StringComparison.OrdinalIgnoreCase)
                || segment.Equals("UnitTests", StringComparison.OrdinalIgnoreCase)
                || segment.Equals("UnitTest", StringComparison.OrdinalIgnoreCase)
                || segment.EndsWith(".Tests", StringComparison.OrdinalIgnoreCase)
                || segment.EndsWith(".Test", StringComparison.OrdinalIgnoreCase)
                || segment.EndsWith(".UnitTests", StringComparison.OrdinalIgnoreCase)
                || segment.EndsWith(".UnitTest", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return rel.EndsWith("Tests.cs", StringComparison.OrdinalIgnoreCase)
            || rel.EndsWith("Test.cs", StringComparison.OrdinalIgnoreCase);
    }
}
