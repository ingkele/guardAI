using GuardAI.Core.Engine;
using GuardAI.Core.Ledging;
using GuardAI.Init;

namespace GuardAI.Tests;

/// <summary>
/// 违规指纹测试：跨运行稳定性与区分度。
/// </summary>
public class FingerprintTests
{
    [Fact]
    public void 同一违规多次计算指纹稳定()
    {
        var v = new Violation("src/Api/Foo.cs", "structure.file-location", "落位错误", "移到 src/Services/");

        var a = Fingerprint.Compute(v);
        var b = Fingerprint.Compute(v);

        Assert.Equal(a, b);
        Assert.Equal(16, a.Length);
    }

    [Fact]
    public void 不同违规指纹不同()
    {
        var baseV = new Violation("a.cs", "rule1", "msg", "fix");
        var diffFile = new Violation("b.cs", "rule1", "msg", "fix");
        var diffRule = new Violation("a.cs", "rule2", "msg", "fix");
        var diffMessage = new Violation("a.cs", "rule1", "other", "fix");

        var f = Fingerprint.Compute(baseV);
        Assert.NotEqual(f, Fingerprint.Compute(diffFile));
        Assert.NotEqual(f, Fingerprint.Compute(diffRule));
        Assert.NotEqual(f, Fingerprint.Compute(diffMessage));
    }

    [Fact]
    public void 拼接边界不会产生相同指纹()
    {
        // "ab"+"c" 与 "a"+"bc" 若简单拼接会撞指纹，换行分隔后必须不同
        var a = Fingerprint.Compute("r", "ab", "c");
        var b = Fingerprint.Compute("r", "a", "bc");
        Assert.NotEqual(a, b);
    }
}

/// <summary>
/// 台账读写测试：JSONL 追加、回读、损坏行容忍。
/// </summary>
public class LedgerTests : IDisposable
{
    private readonly string _repo = Directory.CreateTempSubdirectory("guardai-ledger-").FullName;

    public void Dispose()
    {
        Directory.Delete(_repo, recursive: true);
    }

    [Fact]
    public void 追加后可完整回读()
    {
        var results = new List<CheckResult>
        {
            new("structure", 3,
            [
                new Violation("src/Api/Foo.cs", "structure.file-location", "落位错误", "移到 src/Services/"),
            ]),
        };

        var run = Ledger.CreateRun(_repo, CheckMode.All, "cli", results);
        Ledger.Append(_repo, run);
        var read = Ledger.Read(_repo);

        var single = Assert.Single(read);
        Assert.Equal("all", single.Mode);
        Assert.Equal("cli", single.Trigger);
        Assert.Equal("fail", single.Outcome);
        Assert.Equal(3, single.FilesScanned);
        Assert.Equal(1, single.ViolationCount);
        var v = Assert.Single(single.Violations);
        Assert.Equal("structure", v.Check);
        Assert.Equal(Fingerprint.Compute("structure.file-location", "src/Api/Foo.cs", "落位错误"), v.Fingerprint);
    }

    [Fact]
    public void 多次追加按时间顺序回读()
    {
        Ledger.Append(_repo, Ledger.CreateRun(_repo, CheckMode.Changed, "hook",
            [new CheckResult("structure", 1, [new Violation("a.cs", "r", "m", "s")])]));
        Ledger.Append(_repo, Ledger.CreateRun(_repo, CheckMode.Changed, "cli",
            [new CheckResult("structure", 1, [])]));

        var read = Ledger.Read(_repo);
        Assert.Equal(2, read.Count);
        Assert.Equal("fail", read[0].Outcome);
        Assert.Equal("pass", read[1].Outcome);
    }

    [Fact]
    public void 空扫描不记pass而记empty()
    {
        // F1：假绿防线同样适用于台账——all 模式 0 文件 = 结论不成立
        Ledger.Append(_repo, Ledger.CreateRun(_repo, CheckMode.All, "cli",
            [new CheckResult("structure", 0, [])]));
        Ledger.Append(_repo, Ledger.CreateRun(_repo, CheckMode.Changed, "cli",
            [new CheckResult("structure", 0, [])]));

        var read = Ledger.Read(_repo);
        Assert.Equal("empty", read[0].Outcome); // all 空扫描 → empty
        Assert.Equal("pass", read[1].Outcome); // changed 零文件是正常通过
    }

    [Fact]
    public void 损坏行自动跳过不影响其余记录()
    {
        var path = Ledger.RecordsPath(_repo);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.AppendAllText(path, "{\"id\":\"x\", broken json" + Environment.NewLine);
        Ledger.Append(_repo, Ledger.CreateRun(_repo, CheckMode.All, "ci",
            [new CheckResult("structure", 0, [])]));

        var single = Assert.Single(Ledger.Read(_repo));
        Assert.Equal("ci", single.Trigger);
    }

    [Fact]
    public void 台账不存在时返回空列表()
    {
        Assert.Empty(Ledger.Read(_repo));
    }
}

/// <summary>
/// 闭环分析测试：指纹消失=已修复、复发回退。
/// </summary>
public class LedgerAnalyzerTests
{
    /// <summary>构造一次带指定违规指纹的运行。</summary>
    private static LedgerRun RunAt(DateTimeOffset ts, params string[] fingerprints)
    {
        var violations = fingerprints
            .Select(f => new LedgerViolation("structure", $"f-{f}.cs", "rule", "msg", "fix", f))
            .ToList();
        return new LedgerRun("id", ts, "cli", "all", 10, violations.Count,
            violations.Count == 0 ? "pass" : "fail", violations);
    }

    [Fact]
    public void 指纹消失计为已修复并输出闭环率()
    {
        var t0 = new DateTimeOffset(2026, 10, 20, 10, 0, 0, TimeSpan.FromHours(8));
        var runs = new List<LedgerRun>
        {
            RunAt(t0, "aaa", "bbb"),
            RunAt(t0.AddHours(1), "aaa"),
        };

        var s = LedgerAnalyzer.Analyze(runs);

        Assert.Equal(2, s.TotalRuns);
        Assert.Equal(1, s.OpenCount);
        Assert.Equal(1, s.FixedCount);
        Assert.Equal(0.5, s.ClosureRate, 3);
        Assert.Equal(LedgerAnalyzer.StatusOpen, s.Fingerprints.Single(f => f.Fingerprint == "aaa").Status);
        Assert.Equal(LedgerAnalyzer.StatusFixed, s.Fingerprints.Single(f => f.Fingerprint == "bbb").Status);
        // 未闭环排前
        Assert.Equal("aaa", s.Fingerprints[0].Fingerprint);
    }

    [Fact]
    public void 最新运行为空则历史全部闭环()
    {
        var t0 = new DateTimeOffset(2026, 10, 20, 10, 0, 0, TimeSpan.FromHours(8));
        var runs = new List<LedgerRun>
        {
            RunAt(t0, "aaa"),
            RunAt(t0.AddHours(1)),
        };

        var s = LedgerAnalyzer.Analyze(runs);

        Assert.Equal(0, s.OpenCount);
        Assert.Equal(1, s.FixedCount);
        Assert.Equal(1.0, s.ClosureRate, 3);
    }

    [Fact]
    public void 修复后复发状态回到未闭环()
    {
        var t0 = new DateTimeOffset(2026, 10, 20, 10, 0, 0, TimeSpan.FromHours(8));
        var runs = new List<LedgerRun>
        {
            RunAt(t0, "aaa"),
            RunAt(t0.AddHours(1)),                 // 修复
            RunAt(t0.AddHours(2), "aaa"),          // 复发
        };

        var s = LedgerAnalyzer.Analyze(runs);

        Assert.Equal(1, s.OpenCount);
        Assert.Equal(0, s.FixedCount);
        Assert.Equal(0.0, s.ClosureRate, 3);
        var f = Assert.Single(s.Fingerprints);
        Assert.Equal(2, f.Occurrences); // 出现次数 = 覆盖的运行数（中间的空运行不计）
        Assert.Equal(t0, f.FirstSeen);
        Assert.Equal(t0.AddHours(2), f.LastSeen);
    }

    [Fact]
    public void 空台账闭环率视为满分()
    {
        var s = LedgerAnalyzer.Analyze([]);
        Assert.Equal(0, s.TotalRuns);
        Assert.Null(s.FirstRun);
        Assert.Null(s.LastRun);
        Assert.Equal(1.0, s.ClosureRate, 3);
        Assert.Empty(s.Fingerprints);
    }
}

/// <summary>
/// init 的台账 gitignore 保障测试：记录不出本机。
/// </summary>
public class LedgerGitignoreTests : IDisposable
{
    private readonly string _repo = Directory.CreateTempSubdirectory("guardai-ledger-git-").FullName;

    public void Dispose()
    {
        Directory.Delete(_repo, recursive: true);
    }

    [Fact]
    public void 无gitignore时创建并写入忽略条目()
    {
        Assert.True(GuardAiInitializer.EnsureLedgerIgnored(_repo));

        var text = File.ReadAllText(Path.Combine(_repo, ".gitignore"));
        Assert.Contains(".guard/records/", text);
    }

    [Fact]
    public void 已有忽略条目时不重复写入()
    {
        File.WriteAllText(Path.Combine(_repo, ".gitignore"), "bin/" + Environment.NewLine + ".guard/records/" + Environment.NewLine);

        Assert.False(GuardAiInitializer.EnsureLedgerIgnored(_repo));

        var text = File.ReadAllText(Path.Combine(_repo, ".gitignore"));
        Assert.Equal(1, text.Split(".guard/records/", StringSplitOptions.None).Length - 1);
    }

    [Fact]
    public void 追加到已有gitignore末尾()
    {
        File.WriteAllText(Path.Combine(_repo, ".gitignore"), "bin/" + Environment.NewLine + "obj/" + Environment.NewLine);

        Assert.True(GuardAiInitializer.EnsureLedgerIgnored(_repo));

        var lines = File.ReadAllLines(Path.Combine(_repo, ".gitignore"));
        Assert.Equal("bin/", lines[0]);
        Assert.Equal("obj/", lines[1]);
        Assert.Contains(lines, l => l == ".guard/records/");
    }
}
