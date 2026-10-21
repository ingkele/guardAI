using GuardAI.Init;

namespace GuardAI.Tests;

/// <summary>模板供应器测试（M2-3：单文件 exe 自带模板）。</summary>
public class TemplateProviderTests
{
    [Fact]
    public void 全部模板都可从内嵌资源读取()
    {
        foreach (var rel in TemplateProvider.KnownTemplates)
        {
            var content = TemplateProvider.ReadEmbedded(rel);
            Assert.False(string.IsNullOrEmpty(content), $"内嵌模板缺失: {rel}");
        }
    }

    [Fact]
    public void 内嵌模板与磁盘模板逐字一致()
    {
        // 防漂移：磁盘 templates/ 是单一事实来源，内嵌副本必须与它一致
        var disk = TemplateProvider.FindOnDisk(null);
        Assert.NotNull(disk);
        foreach (var rel in TemplateProvider.KnownTemplates)
        {
            var embedded = TemplateProvider.ReadEmbedded(rel);
            var onDisk = File.ReadAllText(Path.Combine(disk!, rel.Replace('/', Path.DirectorySeparatorChar)));
            Assert.Equal(onDisk, embedded);
        }
    }

    [Fact]
    public void 释放内嵌模板到目标目录()
    {
        var target = Path.Combine(Path.GetTempPath(), "guardai-template-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var dir = TemplateProvider.ExtractEmbedded(target);
            Assert.Equal(Path.GetFullPath(target), Path.GetFullPath(dir));
            foreach (var rel in TemplateProvider.KnownTemplates)
            {
                Assert.True(File.Exists(Path.Combine(target, rel.Replace('/', Path.DirectorySeparatorChar))));
            }
        }
        finally
        {
            Directory.Delete(target, recursive: true);
        }
    }
}
