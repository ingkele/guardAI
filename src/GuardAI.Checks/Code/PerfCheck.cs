using System.Text.RegularExpressions;
using GuardAI.Checks.Assets;
using GuardAI.Core.Engine;

namespace GuardAI.Checks.Code;

/// <summary>
/// perf 检查模块：数据访问性能确定性检查——集合迭代循环内逐条查/写库（N+1）与星号全列查询当场报。
/// </summary>
/// <remarks>
/// 来源：learn 回放 91 发现人工筛补漏（2026-10-23，P2）——N+1 与全列查询是数据访问两大经典恶习，
/// "循环体内数据访问调用 / SELECT 星号投影"形态确定，适合引擎执法。
/// 口径（只漏不误，宁漏不误）：
/// ①只查 .cs；循环结构看清洗后行（字符串/注释不参与），数据访问调用看剥离注释行（SQL 住在字符串里）；
/// ②循环只认大括号体，单语句循环不查；循环头的驱动查询不算 N+1（只报循环体内的调用）；
///   N+1 只在集合迭代循环里报（foreach 恒认 / for 条件引 .Count·.Length / while 条件推 .Read()·.MoveNext()），
///   计数重试（for(;;)/i&lt;MaxRetries）与轮询（while(true)）整类不报——N 来自集合元素数（Issue #1）；
///   报文分读写：查询报「循环内查库（N+1）」，写入报「循环内逐条写库」；
/// ③数据访问调用两路强特征：DI 字段接收者（_db/_context/_repository 族）任意调用，
///   或无歧义数据 API（Queryable/SqlQuery/FromSql/Execute 族/SaveChanges）任意接收者；
///   泛名接收者（db./repo.）与内存 LINQ（Where/Any/Count）不认——零误报优先；
/// ④星号投影认「SELECT * FROM / SELECT * ,」大写形态；测试代码豁免与豁免逃生口见 <see cref="CodeScanCheckBase"/>。
/// </remarks>
public sealed class PerfCheck : CodeScanCheckBase
{
    /// <summary>循环头识别与分类（foreach/for/while，清洗后行上判；捕获组 1 = 循环种类）。</summary>
    private static readonly Regex LoopHeaderPattern = new(
        @"\b(foreach|for|while)\s*\(",
        RegexOptions.Compiled);

    /// <summary>for 循环的集合边界特征（条件段引用 .Count/.Length——N 的来源是集合元素数）。</summary>
    private static readonly Regex CollectionBoundPattern = new(
        @"\.(?:Count|Length)\b",
        RegexOptions.Compiled);

    /// <summary>while 循环的迭代器推进特征（.Read()/.MoveNext()——流式集合迭代）。</summary>
    private static readonly Regex IteratorAdvancePattern = new(
        @"\.(?:Read|MoveNext)\s*\(",
        RegexOptions.Compiled);

    /// <summary>循环体内的数据访问调用（DI 字段接收者 与 无歧义数据 API 两路并联）。</summary>
    private static readonly Regex DataAccessPattern = new(
        @"\b_(?:db|context|dbContext|repository|repo|unitOfWork|conn|connection|database)\b\s*\.\s*\w+\s*\("
        + @"|\.\s*(?:Queryable|SqlQuery|FromSql|FromSqlRaw|FromSqlInterpolated|ExecuteScalar|ExecuteScalarAsync|ExecuteNonQuery|ExecuteNonQueryAsync|ExecuteReader|SaveChanges|SaveChangesAsync)\s*[<(]",
        RegexOptions.Compiled);

    /// <summary>写库调用特征（命中数据访问的行上再判读写——写入报「逐条写库」而非「N+1」）。</summary>
    /// <remarks>
    /// 只在已命中 <see cref="DataAccessPattern"/> 的行上判（接收者已是数据上下文），
    /// 名字族按 SqlSugar/EF 写 API 收敛；ExecuteScalar/ExecuteReader 等读 API 不入列。
    /// </remarks>
    private static readonly Regex WriteCallPattern = new(
        @"\.\s*(?:Insertable|Updateable|Deleteable|ExecuteCommand|ExecuteCommandAsync|ExecuteNonQuery|ExecuteNonQueryAsync|SaveChanges|SaveChangesAsync|BulkCopy|BulkSaveChanges|Insert|Update|Delete|AddRange|UpdateRange|DeleteRange)\s*[<(]",
        RegexOptions.Compiled);

    /// <summary>星号全列投影（SELECT * FROM / SELECT * , 大写形态）。</summary>
    private static readonly Regex StarProjectionPattern = new(
        @"\bSELECT\s+\*(?:\s+FROM\b|\s*,)",
        RegexOptions.Compiled);

    /// <inheritdoc />
    public override string Name => "perf";

    /// <inheritdoc />
    protected override void ScanFile(string rel, string text, List<Violation> violations)
    {
        var callLines = StripComments(text).Replace("\r\n", "\n").Split('\n');
        var cleanLines = ParseText.Clean(text);

        // ① 星号全列查询：剥离注释行整行判（字符串可见）
        for (var i = 0; i < callLines.Length; i++)
        {
            if (StarProjectionPattern.IsMatch(callLines[i]))
            {
                violations.Add(new Violation(
                    rel,
                    "全列查询",
                    $"SELECT 星号投影全列查询（第 {i + 1} 行；不需要的大列被一起拖回）",
                    "显式列出需要的列名或投影到 DTO；大文本/大列按需取，配合分页"));
            }
        }

        // ② 循环内逐条数据访问：只在集合迭代循环里报（行号去重，嵌套循环不双报，写优先）
        //    N 的来源是集合元素数——foreach 恒认；for 认 .Count/.Length 边界；while 认 .Read()/.MoveNext() 迭代推进；
        //    计数重试（for(;;)/i<MaxRetries）与轮询（while(true)）整类不报（Issue #1：重试循环内单次写入误判 N+1）
        var hits = new Dictionary<int, bool>(); // 行号（1 起）→ 是否写调用
        for (var i = 0; i < cleanLines.Length; i++)
        {
            var header = LoopHeaderPattern.Match(cleanLines[i]);
            if (header.Success && IsIterationLoop(header.Groups[1].Value, cleanLines[i]))
            {
                CollectLoopBodyHits(cleanLines, callLines, i, hits);
            }
        }

        foreach (var (lineNo, isWrite) in hits.OrderBy(kv => kv.Key))
        {
            violations.Add(isWrite
                ? new Violation(
                    rel,
                    "循环内逐条写库",
                    $"循环体内逐条写库（第 {lineNo} 行；每条一次连接/事务，写放大）",
                    "批量写入：攒批后 BulkCopy/批量 Insert/一次事务提交；确属必要逐条处理的走 guardai.yml exclusions 豁免")
                : new Violation(
                    rel,
                    "循环内查库",
                    $"循环体内数据访问调用（第 {lineNo} 行；典型 N+1：列表页逐条查库）",
                    "批量取数：循环外一次 IN 批查/JOIN/投影取回，或先查回再内存关联；确属必要逐条处理的走 guardai.yml exclusions 豁免"));
        }
    }

    /// <summary>
    /// 判断循环头是否集合迭代（N 来自集合元素数才算 N+1；计数重试/轮询整类不是）。
    /// </summary>
    /// <param name="kind">循环种类（foreach/for/while）。</param>
    /// <param name="headerLine">循环头所在行（清洗后）。</param>
    private static bool IsIterationLoop(string kind, string headerLine) => kind switch
    {
        "foreach" => true,
        "for" => CollectionBoundPattern.IsMatch(headerLine),
        "while" => IteratorAdvancePattern.IsMatch(headerLine),
        _ => false,
    };

    /// <summary>
    /// 收集循环体内命中数据访问调用的行号（只认大括号体；循环头的驱动查询不算；写调用标记读写）。
    /// </summary>
    /// <param name="cleanLines">清洗后行（循环结构判定）。</param>
    /// <param name="callLines">剥离注释行（调用判定，字符串可见）。</param>
    /// <param name="header">循环头行下标（0 起）。</param>
    /// <param name="hits">命中行号收集（1 起，去重；值 = 是否写调用，同行为写则写优先）。</param>
    private static void CollectLoopBodyHits(string[] cleanLines, string[] callLines, int header, Dictionary<int, bool> hits)
    {
        var net = 0;
        var started = false;
        for (var j = header; j < cleanLines.Length; j++)
        {
            net += ParseText.NetBraces(cleanLines[j]);
            if (!started)
            {
                if (net > 0)
                {
                    started = true;
                }
                else if (j > header)
                {
                    return; // 头行次行都没进块 = 非块语句循环，放弃（只漏不误）
                }

                continue;
            }

            if (net <= 0)
            {
                return; // 块闭合
            }

            if (DataAccessPattern.IsMatch(callLines[j]))
            {
                var lineNo = j + 1;
                var isWrite = WriteCallPattern.IsMatch(callLines[j]);
                hits[lineNo] = (hits.TryGetValue(lineNo, out var existing) && existing) || isWrite;
            }
        }
    }
}
