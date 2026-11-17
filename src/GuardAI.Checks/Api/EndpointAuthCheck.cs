using System.Text.RegularExpressions;
using GuardAI.Checks.Assets;
using GuardAI.Checks.Code;
using GuardAI.Core.Config;
using GuardAI.Core.Engine;

namespace GuardAI.Checks.Api;

/// <summary>
/// 端点鉴权检查：Minimal API 的 <c>Map*</c> 端点必须在链上声明鉴权（Issue #6）。
/// </summary>
/// <remarks>
/// 为什么值得机械执法：加了端点忘加 <c>.RequireAuthorization()</c> 是"最易发生且后果最严重"的漏项——
/// 端点一旦匿名可达，前端菜单过滤、路由守卫、按钮级权限全部形同虚设，<c>curl</c> 就能拿数据；
/// 而开发期几乎测不出来（前端带着 token 天然通过、单测不经 HTTP 管道、review 靠记性）。
/// <para>口径（只漏不误，宁漏不误）：</para>
/// <para>①未配置 <c>endpoint_auth.enabled: true</c> 整模块跳过——守住首跑零误报铁律；</para>
/// <para>②只扫 .cs（Minimal API 的宿主语言）；测试代码与 exclusions 豁免照旧见 <see cref="CodeScanCheckBase"/>；</para>
/// <para>③判定用「括号配对找语句终点 + 整条链扫方法名」，不做单行正则——真实写法常把处理器写几十行，
/// <c>.RequireAuthorization()</c> 可能在很远的地方，单行正则会大面积误报；</para>
/// <para>④判据是"刻意声明的鉴权意图"：<c>RequireAuthorization</c>/<c>AllowAnonymous</c> 之一即通过，
/// 其余自定义鉴权扩展方法（<c>RequirePermission</c> 等）在开启 <c>require_permission</c> 时另行认定；</para>
/// <para>⑤豁免须写 reason（配置解析期强制），端点路由按通配匹配。</para>
/// </remarks>
public sealed class EndpointAuthCheck : CodeScanCheckBase
{
    /// <summary>端点注册调用（MapGet/MapPost/MapPut/MapDelete/MapPatch/MapMethods）。</summary>
    private static readonly Regex MapPattern = new(
        @"\.\s*(MapGet|MapPost|MapPut|MapDelete|MapPatch|MapMethods)\s*\(",
        RegexOptions.Compiled);

    /// <summary>路由字面量（端点注册的第一个参数，通常是字符串字面量）。</summary>
    private static readonly Regex RouteLiteralPattern = new(
        "\\G\\s*(?:\\$@\"|@\"|\\$?\")([^\"]*)\"",
        RegexOptions.Compiled);

    /// <summary>鉴权声明（RequireAuthorization / AllowAnonymous——有其中任一即视为已声明鉴权意图）。</summary>
    private static readonly Regex AuthDeclarationPattern = new(
        @"\.\s*(RequireAuthorization|AllowAnonymous)\s*[(<]",
        RegexOptions.Compiled);

    /// <summary>权限码声明（require_permission 开启时，写操作端点要求其一）。</summary>
    private static readonly Regex PermissionPattern = new(
        @"\.\s*(RequirePermission|HasPermission|RequirePolicy|RequireClaim)\s*[(<]",
        RegexOptions.Compiled);

    /// <summary>写操作端点（require_permission 只对这几类要求权限码）。</summary>
    private static readonly HashSet<string> WriteMapMethods = new(StringComparer.Ordinal)
    {
        "MapPost", "MapPut", "MapDelete", "MapPatch", "MapMethods",
    };

    /// <summary>本轮配置（Run 前由基类钩子缓存）。</summary>
    private EndpointAuthRuleConfig _config = EndpointAuthRuleConfig.Default;

    /// <inheritdoc />
    public override string Name => "endpoint";

    /// <inheritdoc />
    protected override void OnBeforeRun(CheckContext context)
    {
        _config = context.Config.EndpointAuth;
    }

    /// <inheritdoc />
    protected override void ScanFile(string rel, string text, List<Violation> violations)
    {
        if (!_config.Enabled)
        {
            return; // 未启用整模块跳过：首跑零误报
        }

        // 去注释但保留字符串：路由字面量与链式方法名都住在正文里，抹掉字符串就判不了；
        // 去注释是为了括号配平不被注释里的括号带偏。
        var clean = SourceText.StripComments(text).Replace("\r\n", "\n");

        foreach (Match map in MapPattern.Matches(clean))
        {
            var methodName = map.Groups[1].Value;
            var openParen = map.Index + map.Length - 1;

            // 找 Map* 调用的收尾括号，再从那里扫到语句结束（分号）——这就是整条链
            var closeParen = ParseText.MatchingParen(clean, openParen);
            if (closeParen < 0)
            {
                continue; // 括号不配平：保守放弃（只漏不误）
            }

            var chainEnd = FindChainEnd(clean, closeParen + 1);
            var chain = clean[(closeParen + 1)..chainEnd];
            var route = ExtractRoute(clean, openParen + 1);

            if (AuthDeclarationPattern.IsMatch(chain))
            {
                // 权限码检查默认关：只有显式开启 require_permission 才对写操作端点追加要求（零误报优先）
                if (!_config.RequirePermission
                    || !NeedsPermission(methodName)
                    || PermissionPattern.IsMatch(chain)
                    || !PermissionApplies(rel, route))
                {
                    continue; // 已声明鉴权，且（若需权限码）权限码已给或豁免
                }

                violations.Add(BuildPermissionViolation(rel, LineOf(clean, map.Index), route));
                continue;
            }

            if (IsExempt(rel, route))
            {
                continue; // 豁免须在配置里写 reason（解析期强制）
            }

            violations.Add(BuildAuthViolation(rel, LineOf(clean, map.Index), methodName, route));
        }
    }

    /// <summary>
    /// 从 Map* 收尾括号之后扫到语句终点（分号或花括号收束），返回链尾位置。
    /// </summary>
    /// <param name="clean">清洗后全文。</param>
    /// <param name="start">扫描起点（收尾括号之后）。</param>
    /// <returns>链尾位置（不含）。</returns>
    /// <remarks>
    /// 遇到分号即语句结束；语句体是 lambda 块时（<c>MapGet("/x", () =&gt; { ... }).Require...();</c>）
    /// 整条链仍在同一语句内，所以只看分号即可。为防止漏判把后续代码吞进链里，
    /// 一旦遇到顶层的 <c>}</c> 或连续两个空行也收束——保守截断只可能造成漏报，不会误报。
    /// </remarks>
    private static int FindChainEnd(string clean, int start)
    {
        for (var i = start; i < clean.Length; i++)
        {
            var c = clean[i];
            if (c is ';')
            {
                return i;
            }

            if (c is '}' or '{')
            {
                // 顶层的花括号意味着已越过语句边界（lambda 体的花括号在括号配对里已消费）
                return i;
            }
        }

        return clean.Length;
    }

    /// <summary>
    /// 提取端点路由字面量（Map* 的第一个参数）。
    /// </summary>
    /// <param name="clean">清洗后全文。</param>
    /// <param name="argStart">第一个参数的起点。</param>
    /// <returns>路由文本；非字面量时返回空串（保守：不做豁免匹配）。</returns>
    private static string ExtractRoute(string clean, int argStart)
    {
        var match = RouteLiteralPattern.Match(clean, argStart);
        return match.Success ? match.Groups[1].Value : string.Empty;
    }

    /// <summary>
    /// 判断端点是否命中豁免清单（文件通配 + 路由通配双命中）。
    /// </summary>
    /// <param name="rel">文件相对路径。</param>
    /// <param name="route">端点路由。</param>
    /// <returns>命中豁免时 true。</returns>
    private bool IsExempt(string rel, string route)
    {
        foreach (var exemption in _config.ExemptionList)
        {
            if (!GlobMatcher.IsMatch(rel, exemption.Path))
            {
                continue;
            }

            if (exemption.Endpoints.Any(pattern => RouteMatches(route, pattern)))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// 判断该文件+路由是否有资格享受"免权限码"（豁免清单同时放开权限码要求）。
    /// </summary>
    /// <param name="rel">文件相对路径。</param>
    /// <param name="route">端点路由。</param>
    /// <returns>命中豁免时 true。</returns>
    private bool PermissionApplies(string rel, string route) => !IsExempt(rel, route);

    /// <summary>
    /// 路由通配匹配（支持 <c>**</c> 尾通配与完整 glob）。
    /// </summary>
    /// <param name="route">实际路由（如 /api/content）。</param>
    /// <param name="pattern">配置模式（如 /openapi/**）。</param>
    /// <returns>匹配时 true。</returns>
    private static bool RouteMatches(string route, string pattern)
    {
        if (route.Length == 0)
        {
            return false; // 路由不是字面量：不猜，交给违规报出
        }

        return GlobMatcher.IsMatch(route, pattern);
    }

    /// <summary>
    /// 判断该 Map* 方法是否属于写操作（require_permission 只对这些生效）。
    /// </summary>
    /// <param name="methodName">Map* 方法名。</param>
    /// <returns>写操作时 true。</returns>
    private static bool NeedsPermission(string methodName) => WriteMapMethods.Contains(methodName);

    /// <summary>
    /// 计算字符偏移所在行号（1 起）。
    /// </summary>
    /// <param name="clean">清洗后全文。</param>
    /// <param name="index">字符偏移。</param>
    /// <returns>行号（1 起）。</returns>
    private static int LineOf(string clean, int index)
    {
        var line = 1;
        for (var i = 0; i < index && i < clean.Length; i++)
        {
            if (clean[i] == '\n')
            {
                line++;
            }
        }

        return line;
    }

    /// <summary>构造"端点未声明鉴权"违规。</summary>
    /// <param name="rel">文件相对路径。</param>
    /// <param name="line">行号。</param>
    /// <param name="methodName">Map* 方法名。</param>
    /// <param name="route">端点路由。</param>
    /// <returns>违规记录。</returns>
    private static Violation BuildAuthViolation(string rel, int line, string methodName, string route)
        => new(
            rel,
            "端点未声明鉴权",
            $"端点 {DescribeRoute(route)}（{methodName}，第 {line} 行）未声明鉴权——链上既无 RequireAuthorization 也无 AllowAnonymous，"
            + "端点将匿名可达（前端权限体系随之失效）",
            "补 .RequireAuthorization()；确属刻意匿名（健康探针/文档站）则补 .AllowAnonymous() 并在 guardai.yml 的 "
            + "endpoint_auth.exemptions 登记 path + endpoints + reason");

    /// <summary>构造"写操作端点缺权限码"违规（require_permission 开启时才产出）。</summary>
    /// <param name="rel">文件相对路径。</param>
    /// <param name="line">行号。</param>
    /// <param name="route">端点路由。</param>
    /// <returns>违规记录。</returns>
    private static Violation BuildPermissionViolation(string rel, int line, string route)
        => new(
            rel,
            "写端点缺权限码",
            $"写操作端点 {DescribeRoute(route)}（第 {line} 行）只校验了身份、未校验功能权限——"
            + "任何登录用户都能调用（已开启 endpoint_auth.require_permission）",
            "补 .RequirePermission(\"资源:动作\") 或同类权限策略；确属管理员通用端点则登记 exemptions 并写明理由");

    /// <summary>路由可读化（非字面量时给出说明而不是空白）。</summary>
    /// <param name="route">路由文本。</param>
    /// <returns>用于报文的描述。</returns>
    private static string DescribeRoute(string route)
        => route.Length > 0 ? route : "(路由非字面量)";
}