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
}
