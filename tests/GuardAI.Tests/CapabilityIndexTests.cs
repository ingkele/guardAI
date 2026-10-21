using GuardAI.Checks.Assets;

namespace GuardAI.Tests;

/// <summary>
/// 能力清单测试：构建、检索、存取往返。
/// </summary>
public class CapabilityIndexTests : IDisposable
{
    private readonly string _repo = Directory.CreateTempSubdirectory("guardai-index-").FullName;

    public void Dispose()
    {
        Directory.Delete(_repo, recursive: true);
    }

    [Fact]
    public void 构建汇总仓库全部公共方法()
    {
        WriteFile("src/Demo/FileStorage.cs", """
            namespace Demo;

            public class FileStorage
            {
                public string Upload(string path)
                {
                    return path;
                }

                private void Hidden()
                {
                }
            }
            """);
        WriteFile("src/Demo/OrderService.cs", """
            namespace Demo;

            public class OrderService
            {
                public Task CreateAsync(string id)
                {
                    return Task.CompletedTask;
                }
            }
            """);

        var index = CapabilityIndex.Build(_repo);

        Assert.Collection(index.Entries,
            e =>
            {
                Assert.Equal("FileStorage", e.TypeName);
                Assert.Equal("Upload", e.Name);
                Assert.Equal("src/Demo/FileStorage.cs", e.File);
                Assert.Equal(5, e.Line);
            },
            e =>
            {
                Assert.Equal("OrderService", e.TypeName);
                Assert.Equal("CreateAsync", e.Name);
            });
    }

    [Fact]
    public void 检索按方法名优先排序且大小写不敏感()
    {
        WriteFile("A.cs", """
            public class UploadCenter
            {
                public void Save()
                {
                    Do();
                }
            }

            public class Other
            {
                public void UploadFile(string path)
                {
                    Do(path);
                }
            }
            """);

        var index = CapabilityIndex.Build(_repo);
        var matches = index.Search("upload");

        Assert.Collection(matches,
            e => Assert.Equal("UploadFile", e.Name),
            e => Assert.Equal("Save", e.Name));
    }

    [Fact]
    public void 检索无命中返回空且支持上限()
    {
        WriteFile("A.cs", """
            public class Svc
            {
                public void Ping()
                {
                    Do();
                }
            }
            """);

        var index = CapabilityIndex.Build(_repo);

        Assert.Empty(index.Search("不存在的东西"));
        // limit 只取正数（0/负数回落默认 20）
        Assert.Single(index.Search("Ping", limit: 1));
    }

    [Fact]
    public void 存取往返一致()
    {
        WriteFile("A.cs", """
            public class Svc
            {
                public void Ping()
                {
                    Do();
                }
            }
            """);

        var index = CapabilityIndex.Build(_repo);
        var path = CapabilityIndexStore.DefaultPath(_repo);
        CapabilityIndexStore.Save(path, index);
        var loaded = CapabilityIndexStore.Load(path);

        Assert.Equal(index.Entries.Count, loaded.Entries.Count);
        Assert.Equal(index.Entries[0], loaded.Entries[0]);
        Assert.Contains("capability-index.json", path);
    }

    [Fact]
    public void 空白关键词检索报错()
    {
        var index = CapabilityIndex.Build(_repo);

        Assert.Throws<ArgumentException>(() => index.Search("  "));
    }

    [Fact]
    public void 默认排除测试代码且不误伤普通文件()
    {
        // v0.3.1-③：测试方法混入能力清单会误导复用；*Tests.cs 复数后缀不误伤 Contest.cs（只漏不误）
        WriteFile("src/Demo/Keep.cs", "public class Keep\n{\n    public void Save()\n    {\n    }\n}\n");
        WriteFile("tests/Demo/StorageTests.cs", "public class StorageTests\n{\n    public void Upload()\n    {\n    }\n}\n");
        WriteFile("src/Demo/HelperTests.cs", "public class HelperTests\n{\n    public void Parse()\n    {\n    }\n}\n");
        WriteFile("src/Demo/Contest.cs", "public class Contest\n{\n    public void Match()\n    {\n    }\n}\n");

        var index = CapabilityIndex.Build(_repo);

        Assert.Equal(
            new[] { "Contest.Match", "Keep.Save" },
            index.Entries.Select(e => $"{e.TypeName}.{e.Name}").OrderBy(x => x, StringComparer.Ordinal).ToArray());
    }

    [Fact]
    public void 显式排除模式替换默认测试模式()
    {
        WriteFile("src/Demo/Keep.cs", "public class Keep\n{\n    public void Save()\n    {\n    }\n}\n");
        WriteFile("tests/Demo/StorageTests.cs", "public class StorageTests\n{\n    public void Upload()\n    {\n    }\n}\n");
        WriteFile("legacy/Old.cs", "public class Old\n{\n    public void Run()\n    {\n    }\n}\n");

        var index = CapabilityIndex.Build(_repo, ["**/legacy/**"]);

        // 显式配置说了算：legacy 排除，测试文件不再按默认模式排除
        Assert.DoesNotContain(index.Entries, e => e.File.Contains("legacy", StringComparison.Ordinal));
        Assert.Contains(index.Entries, e => e.File.Contains("tests/", StringComparison.Ordinal));
    }

    [Fact]
    public void guardai配置段控制能力清单排除()
    {
        WriteFile("tests/Demo/StorageTests.cs", "public class StorageTests\n{\n    public void Upload()\n    {\n    }\n}\n");
        WriteFile("src/Demo/Keep.cs", "public class Keep\n{\n    public void Save()\n    {\n    }\n}\n");

        // 显式空列表 = 不排除任何文件（v0.3.1-⑥ 的 [] 语法正好用上）
        WriteFile("guardai.yml", "version: 1\ncapability:\n  exclude: []\n");
        Assert.Contains(CapabilityIndex.Build(_repo).Entries, e => e.File.Contains("tests/", StringComparison.Ordinal));

        // 自定义模式生效
        WriteFile("guardai.yml", "version: 1\ncapability:\n  exclude:\n    - **/src/**\n");
        Assert.All(CapabilityIndex.Build(_repo).Entries, e => Assert.DoesNotContain("src/", e.File));
    }

    [Fact]
    public void 中文注释摘要可检索()
    {
        // v0.3.1-②：「缓存」曾搜不到 CacheService——XML 注释中文摘要进索引后命中
        WriteFile("src/Demo/CacheService.cs", """
            namespace Demo;

            public class CacheService
            {
                /// <summary>
                /// 缓存管理：写入缓存并设置过期时间。
                /// </summary>
                public void Set(string key)
                {
                }
            }
            """);

        var matches = CapabilityIndex.Build(_repo).Search("缓存");

        Assert.Single(matches);
        Assert.Equal("Set", matches[0].Name);
        Assert.Contains("缓存管理", matches[0].Summary);
    }

    [Fact]
    public void 注释摘要命中排在方法名命中之后()
    {
        WriteFile("src/Demo/CacheWarmup.cs", """
            namespace Demo;

            public class CacheWarmup
            {
                public void CacheWarmupRun()
                {
                }
            }
            """);
        WriteFile("src/Demo/Warmer.cs", """
            namespace Demo;

            public class Warmer
            {
                /// <summary>
                /// Cache 服务预热。
                /// </summary>
                public void Warm()
                {
                }
            }
            """);

        var matches = CapabilityIndex.Build(_repo).Search("cache");

        Assert.Equal(new[] { "CacheWarmupRun", "Warm" }, matches.Select(m => m.Name).ToArray());
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
}
