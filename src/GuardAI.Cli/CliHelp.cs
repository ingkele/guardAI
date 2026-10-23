namespace GuardAI.Cli;

/// <summary>
/// 帮助文本（单一来源：全局帮助与子命令 --help 共用同一批命令条目，避免两处文案漂移）。
/// </summary>
internal static class CliHelp
{
    /// <summary>命令用法条目：名称 / 用法 / 说明 / 示例。</summary>
    /// <param name="Name">命令名。</param>
    /// <param name="Usage">用法行（可多行）。</param>
    /// <param name="Description">命令说明（可多行）。</param>
    /// <param name="Examples">示例（"命令  # 说明" 形式）。</param>
    private sealed record CommandDoc(string Name, string Usage, string Description, string[] Examples)
    {
        /// <summary>渲染该命令的独立帮助（子命令 --help 用）。</summary>
        /// <returns>帮助文本。</returns>
        public string Render()
        {
            return $"""
                guardAI {Name}

                用法:
                {Expand(Usage)}

                {Expand(Description)}

                示例:
                {string.Join(Environment.NewLine, Examples)}

                退出码: 0 通过 / 1 存在违规 / 2 配置或环境错误（--help 看本用法；help 看全局帮助）
                """;
        }
    }

    /// <summary>全部命令条目（新增命令在此登记，全局帮助与子命令帮助自动同步）。</summary>
    private static readonly CommandDoc[] Docs =
    {
        new(
            "check",
            "guardAI check [--repo <路径>] [--mode changed|all] [--only structure|duplicate|secret|all]{nl}" +
            "              [--config <文件>] [--report <文件>] [--html <文件>] [--json]{nl}" +
            "              [--trigger cli|hook|ci] [--no-record] [--update-baseline] [--strict]",
            "执行检查（0=通过 / 1=违规 / 2=配置或环境错误），结果自动记入台账。{nl}" +
            "--strict 严格审计：存量重复（基线内）也报违规，案例验收用。",
            new[]
            {
                "guardAI check                       # 检查当前仓库的 git 变更",
                "guardAI check --mode all            # 全量检查（CI 用）",
                "guardAI check --only secret         # 只查密钥泄露（疑似凭据文件 / 硬编码密钥）",
                "guardAI check --html 报告.html      # 生成单页 HTML 体检报告（含与上次对比）",
                "guardAI check --strict              # 严格审计：存量重复（基线内）也报违规，案例验收用",
            }),
        new(
            "review",
            "guardAI review [--repo <路径>] [--mode changed|staged|base|all] [--base <git引用>]{nl}" +
            "               [--config <文件>] [--report <文件>] [--json] [--fail-on error|warn|info]",
            "AI 语义审查：对改动做命名/注释/分层/复用审查（大模型，支持 OpenAI 协议/Anthropic/Ollama）。{nl}" +
            "默认只出报告不卡流程（AI 不当最终裁判）；--fail-on 才按级别拦截。{nl}" +
            "模式：changed/staged/base 走 git 圈定改动；all 全量送审目录内代码文件（散文件/非 git 目录用）。{nl}" +
            "需要 guardai.yml 的 ai: 段 + 环境变量密钥（密钥只进环境变量，不落配置文件）。",
            new[]
            {
                "guardAI review                      # AI 审查当前改动（命名/注释/分层/复用）",
                "guardAI review --mode base --base origin/main --fail-on error   # 审查分支差异并卡错误级",
                "guardAI review --mode all --repo ./案例目录   # 散文件目录全量送审（无需 git）",
            }),
        new(
            "init",
            "guardAI init [--repo <路径>] [--templates <目录>] [--yes] [--force]{nl}" +
            "             [--ai [--ai-provider openai|anthropic|ollama] [--ai-model <模型>]{nl}" +
            "                   [--ai-base-url <地址>] [--ai-api-key-env <环境变量名>]]",
            "装配：扫描项目 → 生成规则配置 → 补齐 CI/pre-commit/AI hook → 首检。{nl}" +
            "--ai 用大模型分析仓库结构产配置建议（白名单/落位/豁免，每条带理由）再合并，{nl}" +
            "根治首跑误报；AI 只装配不裁判，判定永远归引擎。",
            new[]
            {
                "guardAI init --yes                  # 一条命令完成接入",
                "guardAI init --ai --yes             # AI 装配增强（需模型接入配置）",
            }),
        new(
            "learn",
            "guardAI learn [--repo <路径>] [--json] [--report <文件>]{nl}" +
            "              [--ai-provider openai|anthropic|ollama] [--ai-model <模型>]{nl}" +
            "              [--ai-base-url <地址>] [--ai-api-key-env <环境变量名>]",
            "AI 规则发现员：审计 review 记录/违规台账/豁免清单，提炼候选规则{nl}" +
            "（反复踩坑模式 → 规则建议，人工确认后进规则资产）——终结踩坑加规则。",
            new[]
            {
                "guardAI learn                       # 提炼候选规则（需模型接入配置）",
                "guardAI learn --report 候选规则.md  # 同时落盘存档",
            }),
        new(
            "doctor",
            "guardAI doctor [--repo <路径>] [--templates <目录>]",
            "环境与配置自检。",
            new[] { "guardAI doctor                      # 体检当前环境" }),
        new(
            "ui",
            "guardAI ui [--repo <路径>] [--port <端口>] [--no-open]",
            "打开本地台账网页：审查记录 / AI hook 结论 / 闭环率（记录不出本机）。",
            new[] { "guardAI ui                          # 打开本地台账网页" }),
        new(
            "mcp",
            "guardAI mcp [--repo <路径>]",
            "启动 MCP server（stdio）：给 AI 客户端搬运 scan_project / generate_config / get_report / search_capability 四工具。",
            new[] { "guardAI mcp --repo D:\\项目          # 作为 MCP server 被 AI 客户端拉起" }),
        new(
            "index",
            "guardAI index [--repo <路径>] [--output <文件>]",
            "生成能力清单（.guard/capability-index.json）：公共方法快照，写码前检索的数据基础。",
            new[] { "guardAI index                       # 生成/刷新能力清单" }),
        new(
            "search",
            "guardAI search <关键词> [--repo <路径>] [--top N]",
            "按关键词检索已有能力（写码先查，防重复造轮子）。位置参数即关键词。",
            new[] { "guardAI search 上传                 # 查有没有现成的上传能力" }),
    };

    /// <summary>
    /// 构造帮助文本。
    /// </summary>
    /// <param name="command">子命令名；null 或未登记命令时返回全局帮助。</param>
    /// <returns>帮助文本。</returns>
    internal static string Build(string? command)
    {
        var doc = command is null ? null : Docs.FirstOrDefault(d => d.Name == command);
        if (doc is not null)
        {
            return doc.Render();
        }

        return $"""
            guardAI —— AI 时代的项目骨架守卫（单入口：一条命令跑完所有检查）

            用法:
            {string.Join(Environment.NewLine, Docs.Select(d => Expand(d.Usage)))}

            命令:
            {string.Join(Environment.NewLine, Docs.Select(d => $"  {d.Name,-7} {FirstLine(d.Description)}"))}

            台账:
              每次 check 自动写入 .guard/records/runs.jsonl（--no-record 关闭）；
              违规指纹从最新运行消失即计为已修复，闭环率在 ui 网页查看。

            示例:
            {string.Join(Environment.NewLine, Docs.SelectMany(d => d.Examples))}

            详细用法: guardAI <命令> --help（子命令级）；本页为全局总览。
            """;
    }

    /// <summary>
    /// 展开换行占位符（{nl} → 平台换行）。
    /// </summary>
    /// <param name="text">含占位符的文本。</param>
    /// <returns>展开后的文本。</returns>
    private static string Expand(string text)
    {
        return text.Replace("{nl}", Environment.NewLine);
    }

    /// <summary>
    /// 取说明首行（全局帮助的命令一览用；完整说明见子命令 --help）。
    /// </summary>
    /// <param name="description">命令说明（可多行）。</param>
    /// <returns>首行文本。</returns>
    private static string FirstLine(string description)
    {
        return Expand(description).Split('\n')[0].TrimEnd('\r');
    }
}
