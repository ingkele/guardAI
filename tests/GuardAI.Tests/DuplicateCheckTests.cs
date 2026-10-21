using GuardAI.Checks.Assets;
using GuardAI.Core.Config;
using GuardAI.Core.Engine;

namespace GuardAI.Tests;

/// <summary>
/// 重复检测测试：近似重复 / 高频方法名 / baseline 零误报 / 豁免与配置。
/// </summary>
public class DuplicateCheckTests : IDisposable
{
    /// <summary>原版上传实现（基线里的"已有实现"）。</summary>
    private const string OriginalUpload = """
        namespace Demo;

        public class FileStorage
        {
            public string Upload(string path)
            {
                var name = Path.GetFileName(path);
                var target = Path.Combine(Root, name);
                File.WriteAllBytes(target, Bytes);
                return target;
            }
        }
        """;

    /// <summary>换皮重写：局部变量全改名，实现骨架照抄。</summary>
    private const string RewrittenUpload = """
        namespace Demo;

        public class ImageStorage
        {
            public string UploadAvatar(string input)
            {
                var fileName = Path.GetFileName(input);
                var fullPath = Path.Combine(Root, fileName);
                File.WriteAllBytes(fullPath, Bytes);
                return fullPath;
            }
        }
        """;

    private readonly string _repo = Directory.CreateTempSubdirectory("guardai-dup-").FullName;

    public void Dispose()
    {
        Directory.Delete(_repo, recursive: true);
    }

    [Fact]
    public void 首跑把存量重复收进基线不报违规()
    {
        WriteFile("src/A/FileStorage.cs", OriginalUpload);
        WriteFile("src/Z/ImageStorage.cs", RewrittenUpload);

        var result = Run();

        Assert.Empty(result.Violations);
        var baseline = DuplicateBaseline.Load(_repo);
        Assert.NotNull(baseline);
        Assert.Single(baseline!.Pairs);
    }

    [Fact]
    public void 基线建立后新增近似重复被检出且指向已有实现()
    {
        WriteFile("src/A/FileStorage.cs", OriginalUpload);
        Assert.Empty(Run().Violations); // 首跑建基线

        WriteFile("src/Z/ImageStorage.cs", RewrittenUpload);
        var result = Run();

        var violation = Assert.Single(result.Violations);
        Assert.Equal("src/Z/ImageStorage.cs", violation.File);
        Assert.Contains("近似重复", violation.Rule);
        Assert.Contains("相似度", violation.Message);
        Assert.Contains("复用已有实现", violation.Suggestion);
        Assert.Contains("src/A/FileStorage.cs", violation.Suggestion);
        Assert.Contains("Upload", violation.Suggestion);
    }

    [Fact]
    public void 高频方法名重复被检出()
    {
        WriteFile("src/A/TokenService.cs", """
            public class TokenService
            {
                public string GetToken(string user)
                {
                    var claims = BuildClaims(user);
                    var token = Signer.Sign(claims);
                    return token;
                }
            }
            """);
        Assert.Empty(Run().Violations);

        // 不同实现、同注册表名（GetToken*）——注册表规则拦下
        WriteFile("src/Z/AdminTokenService.cs", """
            public class AdminTokenService
            {
                public string GetTokenAsync(string admin)
                {
                    var profile = LoadProfile(admin);
                    var ticket = Issuer.Issue(profile);
                    Cache.Put(ticket);
                    return ticket;
                }
            }
            """);
        var result = Run();

        var violation = Assert.Single(result.Violations);
        Assert.Contains("高频方法名", violation.Rule);
        Assert.Contains("GetTokenAsync", violation.Message);
        Assert.Contains("复用已有实现", violation.Suggestion);
    }

    [Fact]
    public void 过短方法体不参与近似检测()
    {
        WriteFile("src/A/PingService.cs", """
            public class PingService
            {
                public void Ping()
                {
                    Do();
                }
            }
            """);
        Assert.Empty(Run().Violations);

        WriteFile("src/Z/PingAgainService.cs", """
            public class PingAgainService
            {
                public void PingAgain()
                {
                    Do();
                }
            }
            """);

        // 方法体过短（< min_tokens）且方法名不在注册表——不报，防结构性撞车误伤
        Assert.Empty(Run().Violations);
    }

    [Fact]
    public void 阈值配置控制近似判定松紧()
    {
        WriteFile("src/A/CalcService.cs", """
            public class CalcService
            {
                public int Calc(int x)
                {
                    var a = ops.One(x);
                    var b = ops.Two(x);
                    var c = ops.Three(x);
                    var d = ops.Four(x);
                    return a + b + c + d;
                }
            }
            """);
        Assert.Empty(Run().Violations);

        // 同结构、四个调用只换一个——相似度中等（约 0.7）
        WriteFile("src/Z/CalcService2.cs", """
            public class CalcService2
            {
                public int Calc2(int x)
                {
                    var a = ops.One(x);
                    var b = ops.Nine(x);
                    var c = ops.Three(x);
                    var d = ops.Four(x);
                    return a + b + c + d;
                }
            }
            """);

        // 默认阈值 0.85 不报；调低到 0.4 即报——阈值真实生效
        Assert.Empty(Run().Violations);
        Assert.Single(Run("duplicates:\n  threshold: 0.4\n").Violations);
    }

    [Fact]
    public void 豁免路径跳过重复检测()
    {
        WriteFile("src/A/FileStorage.cs", OriginalUpload);
        Assert.Empty(Run().Violations);

        WriteFile("tests/Fixtures/ImageStorage.cs", RewrittenUpload);
        var exclusions = """
            exclusions:
              - path: tests/**/Fixtures/**
                reason: 回归测试固定现场
            """;

        Assert.Empty(Run(exclusions).Violations);
    }

    [Fact]
    public void 开关关闭不检测()
    {
        WriteFile("src/A/FileStorage.cs", OriginalUpload);
        WriteFile("src/Z/ImageStorage.cs", RewrittenUpload);

        var result = Run("duplicates:\n  enabled: false\n");

        Assert.Empty(result.Violations);
        Assert.Equal(0, result.Scanned);
    }

    [Fact]
    public void 手动收编基线后存量不再报()
    {
        WriteFile("src/A/FileStorage.cs", OriginalUpload);
        WriteFile("src/Z/ImageStorage.cs", RewrittenUpload);
        var context = BuildContext();

        var folded = DuplicateCheck.AcceptExistingPairs(context);

        Assert.Equal(1, folded);
        Assert.Empty(new DuplicateCheck().Run(context).Violations);
        Assert.Empty(new DuplicateCheck().Run(context).Violations);
    }

    [Fact]
    public void 扫描口径按文件数不按成员数()
    {
        // v0.3.1-④：与 structure 统一口径——Scanned 报本次扫描文件数（1 个文件 2 个成员仍是 1）
        WriteFile("src/A/FileStorage.cs", OriginalUpload);

        var result = Run();

        Assert.Equal(1, result.Scanned);
    }

    /// <summary>最小可用配置（duplicates 段缺失 → 用默认值）。</summary>
    private const string DefaultYaml = """
        version: 1
        include_extensions:
          - .cs
        """;

    /// <summary>
    /// 在临时仓库写入文件（按相对路径自动建目录）。
    /// </summary>
    private void WriteFile(string relativePath, string content)
    {
        var full = Path.Combine(_repo, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
    }

    [Fact]
    public void 严格模式报基线内存量重复()
    {
        WriteFile("src/A/FileStorage.cs", OriginalUpload);
        WriteFile("src/Z/ImageStorage.cs", RewrittenUpload);
        Assert.Empty(Run().Violations); // 首跑建基线：存量对收编

        var strictResult = Run(strict: true);

        var violation = Assert.Single(strictResult.Violations);
        Assert.Contains("复用已有实现", violation.Suggestion);
    }

    [Fact]
    public void 严格模式首跑全量报违规且不写基线()
    {
        WriteFile("src/A/FileStorage.cs", OriginalUpload);
        WriteFile("src/Z/ImageStorage.cs", RewrittenUpload);

        var strictResult = Run(strict: true);

        Assert.Single(strictResult.Violations);
        // 纯只读审计：不写基线，之后普通模式首跑仍按铁律收编
        Assert.Null(DuplicateBaseline.Load(_repo));
    }

    [Fact]
    public void 删行复制的子集型重复被检出()
    {
        // 案例03 形态：手写映射样板复制时漏字段（子集型复制，Jaccard 通道漏、包含度通道逮）
        WriteFile("src/A/OrderService.cs", """
            namespace Demo;

            public class OrderService
            {
                public OrderDto GetDto(int id)
                {
                    var order = LoadOrder(id);
                    return new OrderDto
                    {
                        Id = order.Id,
                        OrderNo = order.OrderNo,
                        Amount = order.Amount,
                        CreatedAt = order.CreatedAt,
                        Remark = order.Remark
                    };
                }
            }
            """);
        Assert.Empty(Run().Violations); // 首跑建基线

        WriteFile("src/Z/ReportService.cs", """
            namespace Demo;

            public class ReportService
            {
                public OrderDto GetLine(int id)
                {
                    var order = LoadOrder(id);
                    return new OrderDto
                    {
                        Id = order.Id,
                        OrderNo = order.OrderNo,
                        Amount = order.Amount,
                        CreatedAt = order.CreatedAt
                    };
                }
            }
            """);
        var result = Run();

        var violation = Assert.Single(result.Violations);
        Assert.Contains("复用已有实现", violation.Suggestion);
        Assert.Contains("GetDto", violation.Suggestion);
    }

    [Fact]
    public void 检测能力升级首跑自动收编新通道存量债()
    {
        // 旧检测能力（v1）只见过一对，基线里只有它；子集型复制对是 v2 新通道才发现的"存量债"
        WriteFile("src/A/FileStorage.cs", OriginalUpload);
        WriteFile("src/Z/ImageStorage.cs", RewrittenUpload);
        var oldPairs = new DuplicateBaseline(
            DateTimeOffset.Now.ToString("o"),
            [new DuplicatePairRecord(
                DuplicateBaseline.PairKey("ImageStorage.UploadAvatar@src/Z/ImageStorage.cs", "FileStorage.Upload@src/A/FileStorage.cs"),
                "ImageStorage.UploadAvatar@src/Z/ImageStorage.cs",
                "FileStorage.Upload@src/A/FileStorage.cs")],
            DetectorVersion: 1);
        DuplicateBaseline.Save(_repo, oldPairs);

        WriteFile("src/B/OrderService.cs", """
            namespace Demo;

            public class OrderService
            {
                public OrderDto GetDto(int id)
                {
                    var order = LoadOrder(id);
                    return new OrderDto
                    {
                        Id = order.Id,
                        OrderNo = order.OrderNo,
                        Amount = order.Amount,
                        CreatedAt = order.CreatedAt,
                        Remark = order.Remark
                    };
                }
            }
            """);
        WriteFile("src/Z/ReportService.cs", """
            namespace Demo;

            public class ReportService
            {
                public OrderDto GetLine(int id)
                {
                    var order = LoadOrder(id);
                    return new OrderDto
                    {
                        Id = order.Id,
                        OrderNo = order.OrderNo,
                        Amount = order.Amount,
                        CreatedAt = order.CreatedAt
                    };
                }
            }
            """);

        // 升级首跑：不报违规（存量不骚扰），新通道发现的对自动并入基线，版本升到当前
        var result = Run();
        Assert.Empty(result.Violations);
        var upgraded = DuplicateBaseline.Load(_repo);
        Assert.Equal(DuplicateCheck.CurrentDetectorVersion, upgraded!.DetectorVersion);
        Assert.Equal(2, upgraded.Pairs.Count);

        // 升级收编后：新增的子集型复制仍当场拦
        WriteFile("src/Y/ThirdService.cs", """
            namespace Demo;

            public class ThirdService
            {
                public OrderDto GetRow(int id)
                {
                    var order = LoadOrder(id);
                    return new OrderDto
                    {
                        Id = order.Id,
                        OrderNo = order.OrderNo,
                        Amount = order.Amount,
                        CreatedAt = order.CreatedAt
                    };
                }
            }
            """);
        Assert.NotEmpty(Run().Violations);
    }

    /// <summary>
    /// 构造检查上下文（extraYaml 为根级追加配置段，如 "duplicates:\n  threshold: 0.4\n"）。
    /// </summary>
    private CheckContext BuildContext(string extraYaml = "", bool strict = false)
    {
        var config = StructureRuleConfigLoader.Parse(DefaultYaml + "\n" + extraYaml);
        return new CheckContext(_repo, CheckMode.All, config, "guardai.yml", strict);
    }

    /// <summary>
    /// 全量模式执行重复检测。
    /// </summary>
    private CheckResult Run(string extraYaml = "", bool strict = false)
    {
        return new DuplicateCheck().Run(BuildContext(extraYaml, strict));
    }
}

/// <summary>
/// duplicates 配置段解析测试。
/// </summary>
public class DuplicateRuleConfigTests
{
    [Fact]
    public void 缺段用默认值()
    {
        var config = StructureRuleConfigLoader.Parse("version: 1");

        Assert.True(config.Duplicates.Enabled);
        Assert.Equal(0.85, config.Duplicates.Threshold);
        Assert.Equal(12, config.Duplicates.MinTokens);
        Assert.Equal(0.9, config.Duplicates.ContainmentThreshold);
        Assert.Equal(DuplicateRuleConfig.DefaultRegistry, config.Duplicates.Registry);
    }

    [Fact]
    public void 自定义段解析()
    {
        var config = StructureRuleConfigLoader.Parse("""
            version: 1
            duplicates:
              enabled: false
              threshold: 0.7
              min_tokens: 20
              containment_threshold: 0.8
              registry:
                - Upload*
                - Handle*
            """);

        Assert.False(config.Duplicates.Enabled);
        Assert.Equal(0.7, config.Duplicates.Threshold);
        Assert.Equal(20, config.Duplicates.MinTokens);
        Assert.Equal(0.8, config.Duplicates.ContainmentThreshold);
        Assert.Equal(["Upload*", "Handle*"], config.Duplicates.Registry);
    }

    [Fact]
    public void 非法阈值报配置错误()
    {
        var ex = Assert.Throws<FormatException>(() => StructureRuleConfigLoader.Parse("""
            version: 1
            duplicates:
              threshold: 2
            """));

        Assert.Contains("threshold", ex.Message);
    }

    [Fact]
    public void 非法最小token数报配置错误()
    {
        Assert.Throws<FormatException>(() => StructureRuleConfigLoader.Parse("""
            version: 1
            duplicates:
              min_tokens: 0
            """));
    }

    [Fact]
    public void 非法包含度阈值报配置错误()
    {
        var ex = Assert.Throws<FormatException>(() => StructureRuleConfigLoader.Parse("""
            version: 1
            duplicates:
              containment_threshold: 1.5
            """));

        Assert.Contains("containment_threshold", ex.Message);
    }

    [Fact]
    public void 非法开关报配置错误()
    {
        Assert.Throws<FormatException>(() => StructureRuleConfigLoader.Parse("""
            version: 1
            duplicates:
              enabled: yes
            """));
    }
}
