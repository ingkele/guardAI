# 真实项目试用反馈分析：witchc-website（2026-10-20）

> 反馈来源：用户邮件《guardAI 试用反馈》（2026-10-20 20:10，转发至 Agent Mail）。
> 试用项目：`F:\广西维烯网络有限公司相关资料\官网\witchc-website`（Vue3 前端 + Node.js 后端网站项目）。
> 试用流程：init → check --report → ui，全流程执行无报错。

## 一、项目构成（本地取证）

| 文件类型 | 数量 | 说明 |
|---|---|---|
| .js | 52 | 前端 + Node 后端 |
| .vue | 12 | Vue3 组件 |
| .ts | 10 | TypeScript |
| .cs / .csproj | **0** | 无任何 .NET 文件 |

项目结构：frontend/（Vue：src/api、components、composables、router、views…）+ backend/（Node：src/db、src/routes、migrations、package.json）+ scripts/（prerender）+ docs/。

## 二、试用结果解读

### ✅ 好的方面（真实项目验证通过）
1. **init 全链路正常**：扫描 → 生成 guardai.yml → 5 闸口装齐（GitHub Actions / Gitee Go / pre-commit / Claude Code hook 合并保留原 hooks / AGENTS.md）→ gitignore 台账保障 → 自动首检，无一报错；
2. **M1/M2 白名单快照表现出色**：基线逐层精确到 `frontend/src/components/layout/*`、`backend/src/routes/*` 级别——存量全放行、AI 新建野目录可拦，快照语义在真实项目工作良好；
3. **M5 台账 + ui 正常**：runs.jsonl 正确记录、ui 网页可开（用户实测后 Ctrl+C 退出）。

### ❌ P0 缺陷：假绿（空扫描报"通过"）
报告输出 `[structure] 扫描 0 个文件，发现 0 处违规 / 结果: 通过`。
**门禁在"什么都没检查"的情况下报了"通过"**——这违背产品核心承诺（不合规代码进不了仓库）。对用户的危险暗示：项目被守卫着，实际零覆盖。

### ❌ P1 缺陷：生态识别盲区
- `扫描完成：生态=Unknown，骨架=Generic`——ProjectScanner 不识别 Node/Vue/前端生态；
- `include_extensions` 固定为 `.cs/.csproj`，前端文件全部漏收；
- `placement_rules` 全是 `*Helper.cs` 等 .NET 模式，对前端项目无意义（落位规则从未触发——**这就是为什么本项目收不到 M3 反馈**）。

### ⚠️ P2 瑕疵：purpose 误标
`backend/*` 白名单条目的 purpose 写成"前端工程"——InferPurpose 按内容推断时把含 .js 的目录一律归为前端，Node 后端被误标（纯文案问题）。

### 📝 设计备注（非缺陷）
通用兜底 `src/**`、`tests/**` 等条目对该网站项目是"未来可能出现的目录"兜底，意味着 AI 新建顶层 `src/` 会放行——这是 M1 设计时的已知取舍（防常规新目录误报），沿用。

## 三、结论与路线影响

1. **假绿是当轮必修**——比 M3 的误报更影响产品可信度：报"通过"的前提是真检查了东西；
2. **目录白名单/黑名单规则本质与语言无关**（只看路径），把 include_extensions 按生态放开后，前端项目即可获得"AI 私建野目录"防护（案例库案例01 的场景对前端同样成立）；
3. **M3（落位规则 scope: app|library）无法从本项目收集**——落位规则 0 触发。仍需一个 **.NET 项目**（WitchC 框架 / HY-LMS / 红白礼簿后端任一）跑 init+check 提供落位规则反馈；
4. 路线图 P4（多语言）提前部分触达：生态识别 + 扩展名适配可提前，前端专属落位规则仍留 P4。

## 四、待决策修复方案

- **方案 A（最小）**：修假绿——扫描 0 个文件时输出显著警告（报告标注"空扫描，不构成合规结论"，不再报绿）+ 修 backend purpose 误标。成本低，不改变适用范围。
- **方案 B（推荐）**：A + 生态感知 include_extensions——init 识别 Node/Vue/TS 生态（package.json / .vue / tsconfig.json），生成对应扩展名（.js/.ts/.vue/.mjs/.cjs/.jsx/.tsx），目录规则在前端项目真实生效。落位规则保持 .NET-only（前端落位规则留 P4）。
