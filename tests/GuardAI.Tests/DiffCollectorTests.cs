using System.Diagnostics;
using GuardAI.AI.Review;

namespace GuardAI.Tests;

/// <summary>送审差异收集器测试（重点：敏感文件不外发）。</summary>
public class DiffCollectorTests
{
    [Fact]
    public async Task 未跟踪敏感文件_跳过送审且只记文件名()
    {
        var repo = InitRepo();
        try
        {
            File.WriteAllText(Path.Combine(repo, ".env"), "OPENAI_API_KEY=sk-live-1234567890abcdefghijklmnopqrstuv");
            File.WriteAllText(Path.Combine(repo, "appsettings.Development.json"), "{ \"ok\": true }");
            File.WriteAllText(Path.Combine(repo, "id_rsa"), "PRIVATE");

            var (text, files) = await DiffCollector.CollectAsync(repo, "changed");

            Assert.Equal(1, files);
            Assert.Contains("appsettings.Development.json", text);
            Assert.DoesNotContain("sk-live-1234567890", text); // 密钥内容不出本机
            Assert.DoesNotContain("PRIVATE", text);
            Assert.Contains(".env", text); // 跳过清单只列文件名
            Assert.Contains("已跳过送审", text);
        }
        finally
        {
            DeleteRepo(repo);
        }
    }

    [Fact]
    public async Task 内容命中密钥特征的普通文件_跳过送审()
    {
        var repo = InitRepo();
        try
        {
            File.WriteAllText(Path.Combine(repo, "note.txt"), "配置说明\n-----BEGIN PRIVATE KEY-----\nMIIEvQIBADAN\n-----END PRIVATE KEY-----");
            File.WriteAllText(Path.Combine(repo, "ok.txt"), "普通内容");

            var (text, files) = await DiffCollector.CollectAsync(repo, "changed");

            Assert.Equal(1, files); // 只有 ok.txt 入审，note.txt 因内容特征被拦
            Assert.Contains("ok.txt", text);
            Assert.DoesNotContain("BEGIN PRIVATE KEY", text);
            Assert.Contains("note.txt", text); // 跳过清单列名
        }
        finally
        {
            DeleteRepo(repo);
        }
    }

    [Fact]
    public async Task 普通代码文件名含credentials前缀_不受影响()
    {
        var repo = InitRepo();
        try
        {
            File.WriteAllText(Path.Combine(repo, "UserCredentialsService.cs"), "public class UserCredentialsService { }");

            var (text, files) = await DiffCollector.CollectAsync(repo, "changed");

            Assert.Equal(1, files);
            Assert.Contains("UserCredentialsService.cs", text);
        }
        finally
        {
            DeleteRepo(repo);
        }
    }

    [Fact]
    public async Task 敏感目录内文件_跳过送审()
    {
        var repo = InitRepo();
        try
        {
            Directory.CreateDirectory(Path.Combine(repo, ".ssh"));
            File.WriteAllText(Path.Combine(repo, ".ssh", "config"), "HOST SECRET-HOST");
            File.WriteAllText(Path.Combine(repo, "ok.txt"), "普通内容");

            var (text, files) = await DiffCollector.CollectAsync(repo, "changed");
            Assert.Equal(1, files);
            Assert.DoesNotContain("SECRET-HOST", text);
        }
        finally
        {
            DeleteRepo(repo);
        }
    }

    [Fact]
    public async Task all模式_按谓词过滤只送代码文件()
    {
        var dir = InitPlainDir();
        try
        {
            // README.md 是"答案"（描述 seeded 问题），不得进送审范围，否则回放变抄答案
            File.WriteAllText(Path.Combine(dir, "Bad.cs"), "public class bad { }");
            File.WriteAllText(Path.Combine(dir, "README.md"), "本案例的 seeded 问题是命名混乱");

            var (text, files) = await DiffCollector.CollectAsync(dir, "all", isCodeFile: p => p.EndsWith(".cs", StringComparison.OrdinalIgnoreCase));

            Assert.Equal(1, files);
            Assert.Contains("Bad.cs", text);
            Assert.DoesNotContain("seeded", text); // 答案文件不外发
        }
        finally
        {
            DeleteRepo(dir);
        }
    }

    [Fact]
    public async Task all模式_敏感过滤照走()
    {
        var dir = InitPlainDir();
        try
        {
            File.WriteAllText(Path.Combine(dir, ".env"), "OPENAI_API_KEY=sk-live-1234567890abcdefghijklmnopqrstuv");
            File.WriteAllText(Path.Combine(dir, "secret-note.cs"), "// -----BEGIN PRIVATE KEY-----\nMIIEvQIBADAN");
            File.WriteAllText(Path.Combine(dir, "ok.cs"), "public class Ok { }");

            var (text, files) = await DiffCollector.CollectAsync(dir, "all");

            Assert.Equal(1, files);
            Assert.Contains("ok.cs", text);
            Assert.DoesNotContain("sk-live-1234567890", text); // 密钥内容不出本机
            Assert.DoesNotContain("BEGIN PRIVATE KEY", text);
            Assert.Contains(".env", text); // 跳过清单只列文件名
        }
        finally
        {
            DeleteRepo(dir);
        }
    }

    [Fact]
    public async Task all模式_跳过构建与依赖目录()
    {
        var dir = InitPlainDir();
        try
        {
            Directory.CreateDirectory(Path.Combine(dir, "bin"));
            Directory.CreateDirectory(Path.Combine(dir, "node_modules"));
            File.WriteAllText(Path.Combine(dir, "bin", "Generated.cs"), "public class Generated { }");
            File.WriteAllText(Path.Combine(dir, "node_modules", "lib.js"), "var x = 1;");
            File.WriteAllText(Path.Combine(dir, "src.cs"), "public class Src { }");

            var (text, files) = await DiffCollector.CollectAsync(dir, "all");

            Assert.Equal(1, files);
            Assert.Contains("src.cs", text);
            Assert.DoesNotContain("Generated.cs", text);
            Assert.DoesNotContain("lib.js", text);
        }
        finally
        {
            DeleteRepo(dir);
        }
    }

    [Fact]
    public async Task 非git目录_changed模式_人话报错指路all()
    {
        var dir = InitPlainDir();
        try
        {
            File.WriteAllText(Path.Combine(dir, "a.cs"), "public class A { }");

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(
                () => DiffCollector.CollectAsync(dir, "changed"));

            Assert.Contains("--mode all", ex.Message); // 指对方向
            Assert.DoesNotContain("usage: git", ex.Message); // 不倾泻 git usage
            Assert.DoesNotContain("diff [<options>]", ex.Message);
        }
        finally
        {
            DeleteRepo(dir);
        }
    }

    /// <summary>建一个非 git 的临时目录（散文件/案例目录场景）。</summary>
    private static string InitPlainDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "guardai-dc-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>建一个只有一次提交的临时 git 仓库（工作区回到干净态）。</summary>
    private static string InitRepo()
    {
        var dir = Path.Combine(Path.GetTempPath(), "guardai-dc-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        Run(dir, "init");
        Run(dir, "config", "user.email", "test@test.local");
        Run(dir, "config", "user.name", "test");
        File.WriteAllText(Path.Combine(dir, "seed.txt"), "seed");
        Run(dir, "add", ".");
        Run(dir, "commit", "-m", "seed");
        return dir;
    }

    /// <summary>删除临时仓库（Windows 下 .git 内文件常带只读属性，先清属性再删，失败重试）。</summary>
    private static void DeleteRepo(string dir)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                foreach (var file in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
                {
                    File.SetAttributes(file, FileAttributes.Normal);
                }

                Directory.Delete(dir, recursive: true);
                return;
            }
            catch (Exception)
            {
                Thread.Sleep(100); // git 进程句柄释放有延迟，稍候重试
            }
        }
    }

    private static void Run(string workDir, params string[] args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = workDir,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var arg in args)
        {
            psi.ArgumentList.Add(arg);
        }

        using var p = Process.Start(psi)!;
        p.StandardOutput.ReadToEnd();
        p.StandardError.ReadToEnd();
        p.WaitForExit();
        Assert.Equal(0, p.ExitCode);
    }
}
