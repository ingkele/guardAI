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

    /// <summary>Issue #7 复现用 C# 一侧：静态方法 + 常量清单。</summary>
    private const string Issue7CSharp = """
        namespace Demo.Protocols;

        /// <summary>协议清单。</summary>
        public static class ProviderProtocols
        {
            /// <summary>列出全部已支持协议的配置值。</summary>
            /// <returns>配置值数组。</returns>
            public static IReadOnlyList<string> SupportedConfigValues() =>
                [OpenAiCompatible, Anthropic, Gemini, ArkImage, OpenAiImage, DashScopeImage, DashScopeAsyncImage];

            /// <summary>OpenAI 兼容协议。</summary>
            public const string OpenAiCompatible = "openai-compatible";

            /// <summary>Anthropic 协议。</summary>
            public const string Anthropic = "anthropic";

            /// <summary>Gemini 协议。</summary>
            public const string Gemini = "gemini";

            /// <summary>火山方舟图像。</summary>
            public const string ArkImage = "ark-image";

            /// <summary>OpenAI 图像。</summary>
            public const string OpenAiImage = "openai-image";

            /// <summary>百炼图像。</summary>
            public const string DashScopeImage = "dashscope-image";

            /// <summary>百炼异步图像。</summary>
            public const string DashScopeAsyncImage = "dashscope-async-image";
        }
        """;

    /// <summary>Issue #7 复现用 TS 一侧：React hook，与上面的 C# 方法毫无复用关系。</summary>
    private const string Issue7TypeScript = """
        import React from 'react'

        export function useDebouncedValue<T>(value: T, delayMs: number): T {
          const [debounced, setDebounced] = React.useState<T>(value)
          const lastChangeAt = React.useRef(Date.now())

          React.useEffect(() => {
            lastChangeAt.current = Date.now()
            if (delayMs <= 0) {
              setDebounced(() => value)
              return
            }
            const timer = globalThis.setTimeout(() => {
              if (isSettled(lastChangeAt.current, delayMs, Date.now())) setDebounced(() => value)
            }, delayMs)
            return () => {
              globalThis.clearTimeout(timer)
            }
          }, [value, delayMs])

          return debounced
        }
        """;

    /// <summary>跨语言不比对：C# 静态方法 vs TS hook 不得判重复（Issue #7 主症状，原为相似度 100%）。</summary>
    [Fact]
    public void 跨语言成员_不判重复()
    {
        WriteFile("src/ProviderProtocols.cs", Issue7CSharp);
        WriteFile("web/hook.ts", Issue7TypeScript);

        var result = Run(strict: true);

        Assert.Empty(result.Violations);
    }

    /// <summary>反向顺序同样不判：语言隔离必须对称，不能只隔离一侧（candidates 与参照池都要过闸）。</summary>
    [Fact]
    public void 跨语言成员_反向顺序也不判()
    {
        WriteFile("web/hook.ts", Issue7TypeScript);
        WriteFile("src/ProviderProtocols.cs", Issue7CSharp);

        var result = Run(strict: true);

        Assert.Empty(result.Violations);
    }

    /// <summary>跨语言隔离不得削弱同语言检测：C# ↔ C# 换皮对仍抓。</summary>
    [Fact]
    public void 同语言CSharp换皮_仍抓()
    {
        WriteFile("src/A/FileStorage.cs", OriginalUpload);
        WriteFile("src/Z/ImageStorage.cs", RewrittenUpload);

        var result = Run(strict: true);

        var violation = Assert.Single(result.Violations);
        Assert.Contains("ImageStorage", violation.File);
        Assert.Contains("FileStorage", violation.Message);
    }

    /// <summary>跨语言隔离不得削弱同语言检测：TS ↔ TS 换皮对仍抓。</summary>
    [Fact]
    public void 同语言TypeScript换皮_仍抓()
    {
        WriteFile("web/a/fetcher.ts", """
            export function fetchUser(id: string) {
              const res = await client.get('/users/' + id)
              const data = res.data
              if (!data) { throw new Error('missing') }
              return data
            }
            """);
        WriteFile("web/z/loader.ts", """
            export function loadMember(key: string) {
              const response = await client.get('/users/' + key)
              const payload = response.data
              if (!payload) { throw new Error('missing') }
              return payload
            }
            """);

        var result = Run(strict: true);

        var violation = Assert.Single(result.Violations);
        Assert.Contains("loader.ts", violation.File);
    }

    /// <summary>tsx 与 ts 属同一语言（TypeScript），互相之间仍可比对——同族扩展名不能误隔离。</summary>
    [Fact]
    public void ts与tsx同属TypeScript_仍可比对()
    {
        WriteFile("web/a/one.ts", """
            export function fetchUser(id: string) {
              const res = await client.get('/users/' + id)
              const data = res.data
              if (!data) { throw new Error('missing') }
              return data
            }
            """);
        WriteFile("web/z/two.tsx", """
            export function loadMember(key: string) {
              const response = await client.get('/users/' + key)
              const payload = response.data
              if (!payload) { throw new Error('missing') }
              return payload
            }
            """);

        var result = Run(strict: true);

        Assert.Single(result.Violations);
    }

    /// <summary>语言身份派生：注册表覆盖全部可解析扩展名（漏一个即退化成跨语言可比）。</summary>
    [Theory]
    [InlineData("a.cs", "csharp")]
    [InlineData("a.java", "java")]
    [InlineData("a.kt", "kotlin")]
    [InlineData("a.kts", "kotlin")]
    [InlineData("a.ts", "typescript")]
    [InlineData("a.tsx", "typescript")]
    [InlineData("a.js", "javascript")]
    [InlineData("a.jsx", "javascript")]
    [InlineData("a.go", "go")]
    [InlineData("a.py", "python")]
    public void 语言身份派生_覆盖全部可解析扩展名(string path, string expected)
    {
        Assert.Equal(expected, MemberParserRegistry.LanguageOf(path));
    }

    /// <summary>未知扩展名不归任何语言（返回 null，保守放行由阈值兜底）。</summary>
    [Fact]
    public void 未知扩展名_无语言身份()
    {
        Assert.Null(MemberParserRegistry.LanguageOf("notes.txt"));
    }

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

    /// <summary>JSX 小布局组件（Row）——Issue #1 探针的被复制对象。</summary>
    private const string JsxRow = """
        export function Row({ label, children }: { label: string; children: React.ReactNode }) {
          return (
            <div className="flex flex-wrap items-center gap-3 py-2">
              <span className="w-[86px] shrink-0 text-[11px] text-soft">{label}</span>
              <div className="flex min-w-0 flex-1 flex-wrap items-center gap-2.5">{children}</div>
            </div>
          );
        }
        """;

    /// <summary>逐字复制 Row 结构的探针组件（Issue #1 增量探针实验）。</summary>
    private const string JsxProbeRowCopy = """
        export function ProbeRowCopy({ label, children }: { label: string; children: React.ReactNode }) {
          return (
            <div className="flex flex-wrap items-center gap-3 py-2">
              <span className="w-[86px] shrink-0 text-[11px] text-soft">{label}</span>
              <div className="flex min-w-0 flex-1 flex-wrap items-center gap-2.5">{children}</div>
            </div>
          );
        }
        """;

    /// <summary>整页布局组件（AuthShell）——与 Row 毫无血缘，纯 JSX 骨架相似。</summary>
    private const string JsxAuthShell = """
        export function AuthShell({ title, children, footer }) {
          return (
            <div className="flex min-h-screen items-center justify-center bg-canvas p-6">
              <div className="flex w-full max-w-[880px] rounded-2xl border border-line bg-panel shadow-xl">
                <aside className="hidden w-[360px] flex-col justify-between bg-brand-deep p-8 text-white md:flex">
                  <div className="flex items-center gap-2">
                    <Logo size={28} />
                    <span className="text-lg font-semibold">Console</span>
                  </div>
                  <p className="text-xs text-white/50">© 2026</p>
                </aside>
                <main className="flex flex-1 flex-col justify-center p-10">
                  <h1 className="text-xl font-semibold">{title}</h1>
                  <div className="flex flex-col gap-4">{children}</div>
                  <div className="flex flex-col gap-2 text-xs text-soft">{footer}</div>
                </main>
              </div>
            </div>
          );
        }
        """;

    /// <summary>整页路由组件（DashboardPage）——同为 JSX 骨架，业务完全不同。</summary>
    private const string JsxDashboardPage = """
        export function DashboardPage() {
          const { data, loading } = useDashboardQuery();
          return (
            <div className="flex h-full flex-col gap-4 p-6">
              <header className="flex items-center justify-between border-b border-line pb-4">
                <h1 className="text-xl font-semibold">工作台</h1>
                <Button onClick={refresh}>刷新</Button>
              </header>
              <section className="grid grid-cols-4 gap-4">
                {data.metrics.map((m) => (
                  <MetricCard key={m.id} title={m.title} value={m.value} />
                ))}
              </section>
              <footer className="flex items-center justify-between border-t border-line pt-3">
                <span>更新于 {data.updatedAt}</span>
                <Pagination current={data.page} total={data.total} />
              </footer>
            </div>
          );
        }
        """;

    [Fact]
    public void JSX组件骨架互撞_近似通道豁免()
    {
        // Issue #1 复现：归一化把标签/属性名抹成 id，任意返回 JSX 的函数 token 流天然重叠——
        // 结构相似 ≠ 语义重复，近似重复通道对 JSX 方法整体豁免（探针实验的 6 对全静默）
        WriteFile("web/src/components/shared/row.tsx", JsxRow);
        WriteFile("web/src/components/shared/auth-shell.tsx", JsxAuthShell);
        WriteFile("web/src/routes/dashboard.tsx", JsxDashboardPage);
        Assert.Empty(Run().Violations); // 首跑建基线

        WriteFile("web/src/components/probe.tsx", JsxProbeRowCopy);
        Assert.Empty(Run().Violations);
        Assert.Empty(Run(strict: true).Violations); // 严格审计同样静默：骨架相似不判语义重复
    }

    [Fact]
    public void JSX整组件逐字复制_按设计不报_只漏不误()
    {
        // 探针逐字复制 Row 结构也不报——JSX 骨架相似度不携带语义区分度，宁漏不误；
        // 组件级重复的语义维度（props/职责/导出名）是后续 learn 候选，不在近似通道硬猜
        WriteFile("web/src/components/shared/row.tsx", JsxRow);
        Assert.Empty(Run().Violations);

        WriteFile("web/src/components/probe.tsx", JsxProbeRowCopy);
        Assert.Empty(Run().Violations);
    }

    [Fact]
    public void JSX豁免不牵连CSharp换皮检测()
    {
        // 同一仓库里 JSX 豁免只对 JSX 方法生效：C# 换皮重写照样当场拦（门禁有效性）
        WriteFile("src/A/FileStorage.cs", OriginalUpload);
        WriteFile("web/src/components/shared/row.tsx", JsxRow);
        WriteFile("web/src/components/probe.tsx", JsxProbeRowCopy);
        Assert.Empty(Run().Violations); // 首跑建基线

        WriteFile("src/Z/ImageStorage.cs", RewrittenUpload);
        var violation = Assert.Single(Run().Violations);
        Assert.Contains("近似重复", violation.Rule);
        Assert.Contains("ImageStorage", violation.File);
    }

    [Fact]
    public void JSX方法仍受高频方法名注册表约束()
    {
        // 近似通道豁免 JSX，但「又写一遍同名轮子」的名字通道保留
        WriteFile("web/src/components/upload-avatar.tsx", """
            export function UploadAvatar({ file }) {
              return (
                <div className="flex items-center gap-2">
                  <Avatar src={file.url} />
                  <span className="text-sm">{file.name}</span>
                </div>
              );
            }
            """);
        Assert.Empty(Run().Violations);

        WriteFile("web/src/components/upload-image.tsx", """
            export function UploadImage({ image }) {
              return (
                <div className="flex items-center gap-2">
                  <Avatar src={image.url} />
                  <span className="text-sm">{image.name}</span>
                </div>
              );
            }
            """);
        var violation = Assert.Single(Run().Violations);
        Assert.Contains("高频方法名", violation.Rule);
    }

    [Fact]
    public void CSharp泛型尖括号_不误判JSX()
    {
        // <string, int> 这类泛型尖括号不是 JSX 标记（无 </Tag> 闭合 / <Tag /> 自闭合）——
        // 带泛型的 C# 换皮照报，防豁免口径放宽误伤
        WriteFile("src/A/TagCounter.cs", """
            namespace Demo;

            public class TagCounter
            {
                public Dictionary<string, int> CountByTag(List<string> tags)
                {
                    var map = new Dictionary<string, int>();
                    foreach (var tag in tags)
                    {
                        map[tag] = map.TryGetValue(tag, out var n) ? n + 1 : 1;
                    }
                    return map;
                }
            }
            """);
        Assert.Empty(Run().Violations);

        WriteFile("src/Z/LabelTally.cs", """
            namespace Demo;

            public class LabelTally
            {
                public Dictionary<string, int> TallyByLabel(List<string> labels)
                {
                    var result = new Dictionary<string, int>();
                    foreach (var label in labels)
                    {
                        result[label] = result.TryGetValue(label, out var c) ? c + 1 : 1;
                    }
                    return result;
                }
            }
            """);
        Assert.Single(Run().Violations);
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
