using GuardAI.Checks.Assets;

namespace GuardAI.Tests;

/// <summary>
/// 指纹引擎测试：归一化策略与相似度判定。
/// </summary>
public class CodeSimilarityTests
{
    [Fact]
    public void 改局部变量名不改指纹()
    {
        var a = CodeSimilarity.Tokenize("var name = Load ( ) ; return name ;");
        var b = CodeSimilarity.Tokenize("var result = Load ( ) ; return result ;");

        Assert.Equal(1.0, CodeSimilarity.Jaccard(a, b), 3);
    }

    [Fact]
    public void 改数字常量不改指纹()
    {
        var a = CodeSimilarity.Tokenize("var x = 1 + Compute ( 2 ) ;");
        var b = CodeSimilarity.Tokenize("var x = 99 + Compute ( 88 ) ;");

        Assert.Equal(1.0, CodeSimilarity.Jaccard(a, b), 3);
    }

    [Fact]
    public void 成员调用序列是区分点()
    {
        var a = CodeSimilarity.Tokenize("return _store . Upload ( data ) ;");
        var b = CodeSimilarity.Tokenize("return _store . Delete ( data ) ;");

        Assert.True(CodeSimilarity.Jaccard(a, b) < 0.9);
    }

    [Fact]
    public void 无关方法相似度低()
    {
        var a = CodeSimilarity.Tokenize(
            "for ( int i = 0 ; i < 10 ; i ++ ) { total = total + items [ i ] ; } return total ;");
        var b = CodeSimilarity.Tokenize(
            "var cached = Cache . Get ( key ) ; if ( cached != null ) { return cached ; }");

        Assert.True(CodeSimilarity.Jaccard(a, b) < 0.5);
    }

    [Fact]
    public void 空方法体相似度为零()
    {
        var empty = CodeSimilarity.Tokenize(string.Empty);
        var some = CodeSimilarity.Tokenize("return 1 ;");

        Assert.Empty(empty);
        Assert.Equal(0, CodeSimilarity.Jaccard(empty, some), 3);
    }

    [Fact]
    public void 成员访问名保留而局部标识符归一化()
    {
        var tokens = CodeSimilarity.Tokenize("var tmp = svc . SaveAsync ( input ) ;");

        // svc 是局部标识符归一化为 id；.SaveAsync 是成员访问名保留
        Assert.Contains("SaveAsync", tokens);
        Assert.Contains("id", tokens);
        Assert.DoesNotContain("svc", tokens);
    }

    [Fact]
    public void 删行复制包含度高而Jaccard被稀释()
    {
        // 案例03 形态：逐字段映射样板，复制时漏了 Remark 一行（子集型复制）
        var full = CodeSimilarity.Tokenize(
            "var order = LoadOrder(id); return new OrderDto { Id = order.Id, OrderNo = order.OrderNo, Amount = order.Amount, CreatedAt = order.CreatedAt, Remark = order.Remark };");
        var dropped = CodeSimilarity.Tokenize(
            "var order = LoadOrder(id); return new OrderDto { Id = order.Id, OrderNo = order.OrderNo, Amount = order.Amount, CreatedAt = order.CreatedAt };");

        // Jaccard 被并集稀释到 0.85 阈值之下（实测 0.77），包含度以较小方为分母清晰命中
        Assert.True(CodeSimilarity.Jaccard(full, dropped) < 0.85);
        Assert.True(CodeSimilarity.Containment(full, dropped) >= 0.9);
    }

    [Fact]
    public void 无关方法包含度低()
    {
        var a = CodeSimilarity.Tokenize("var cache = Cache.Get(key); return cache ?? LoadFromDb(id);");
        var b = CodeSimilarity.Tokenize("var list = new List<int>(); foreach (var x in src) { list.Add(x); } return list;");

        Assert.True(CodeSimilarity.Containment(a, b) < 0.5);
    }

    [Fact]
    public void 不同长度字符串字面量区分相似度()
    {
        // 回归锁：清洗产物带长度标记 "N"——两段不同提示词（长度不同）曾被归一成同一 token 流判 100% 重复
        var a = CodeSimilarity.Tokenize("const string system = \"200\" ; var user = \"800\" ; return ( system , user ) ;");
        var b = CodeSimilarity.Tokenize("const string system = \"260\" ; var user = \"1100\" ; return ( system , user ) ;");

        Assert.True(CodeSimilarity.Jaccard(a, b) < 0.85);
        Assert.True(CodeSimilarity.Containment(a, b) < 0.9);
    }

    [Fact]
    public void 同长度字符串微调仍被吸收()
    {
        // 同长度内容微调（换词不换长）保持原语义：内容抹除后仍判完全相似
        var a = CodeSimilarity.Tokenize("var title = \"12\" ; Save ( title ) ;");
        var b = CodeSimilarity.Tokenize("var title = \"12\" ; Save ( title ) ;");

        Assert.Equal(1.0, CodeSimilarity.Jaccard(a, b), 3);
    }

    [Fact]
    public void 空串占位不带长度token()
    {
        // 空串沿用 "" 占位（无长度标记），与既有语义一致
        var tokens = CodeSimilarity.Tokenize("var x = \"\" ;");

        Assert.Contains("str", tokens);
        Assert.DoesNotContain(tokens, t => t.StartsWith("len_", StringComparison.Ordinal));
        Assert.DoesNotContain(tokens, t => t.StartsWith("lenb_", StringComparison.Ordinal));
    }
}
