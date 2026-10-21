# guardAI 真实仓库试用报告——WitchC 框架

> 试用时间：2026-10-19 | 仓库：E:\WitchC（master，.NET 工具包框架，多模块类库）
> 目的：把 guardAI 丢进真实项目，收集违规检出质量与误报，为 P2 规则调优提供依据。

## 一、试用过程与数据

| 阶段 | 扫描文件数 | 违规数 | 说明 |
|---|---|---|---|
| init 自动首检（默认配置） | 235 | **220** | 几乎全军覆没——误报洪流 |
| 调优 allowed_dirs 后复扫 | 235 | **7** | 真信号浮出 |
| exclusions 豁免后终扫 | 235 | **0** | 闭环收敛 ✅ |

退出码全程符合约定（0=通过 / 1=违规）。

## 二、误报洪流根因（220 → 7）

**215 处「目录白名单」误报的唯一根因**：`guardAI init` 的 ConfigGenerator 只生成通用 6 项 allowed_dirs（src/tests/config/templates/scripts/docs），**没有识别仓库真实顶层目录**。WitchC 实际结构是：

```
framework/  module/  samples/  DOCS/  local-feed/  nupkgs/  src/  templates/  tests/
```

`framework/**`（框架主战场）、`module/**`、`samples/**` 等全部不在白名单 → 215 个合法文件全被误判"AI 自由发挥新建目录"。

**产品级改进点（P2 候选）**：init 生成配置时应扫描仓库真实顶层目录自动纳入 allowed_dirs（带用途提示），而不是给一套写死的通用预设。当前 workaround 是人工改配置——本次实测证明这个逃生口好用（一次编辑消灭 215 误报），但**首次报警零误报**的铁律被默认配置破坏了，首跑体验不合格。

## 三、剩余 7 处分析（真信号 + 规则边界）

### 目录白名单 2 处——合法豁免
- `.verify/Verify/*`（2 个文件）：本地验证现场，已被 .gitignore 忽略。
- **规则边界发现**：检查器不感知 .gitignore，被忽略的目录也会被扫。P2 改进：all 模式跳过 .gitignore 忽略路径。

### 落位规则 5 处——规则对类库项目失配
全部是 `*Extensions.cs` 不在 `**/Extensions/**` 触发，但逐一核实**没有一处该移动**：

| 文件 | 不动的理由 |
|---|---|
| `framework/WitchC/WitchCExtensions.cs` | 框架主入口门面类，放模块根是 .NET 库惯例 |
| `framework/WitchC.UnifyResult/UnifyResultExtensions.cs` | 同上，模块入口 |
| `framework/WitchC.Schedule/Dashboard/ScheduleDashboardExtensions.cs` | 与 Dashboard 功能区内聚，拆走反而割裂 |
| `framework/WitchC.DatabaseAccessor.SqlSugar/YearDatabase/YearDatabaseExtensions.cs` | 与 YearDatabase 功能区内聚 |
| `samples/witchc-web-api/WitchC.ServiceDefaults/Extensions.cs` | .NET Aspire ServiceDefaults 模板自带命名 |

**产品级改进点（P2 候选）**：落位规则是给应用项目定的（Helper→Utils、Extensions→Extensions），对**类库/框架项目**失配率高——库的入口扩展类天然放模块根。改进方向：规则加适用范围（app/library），或给 placement_rules 加 min_depth/排除根目录门面类的开关。

## 四、验证有效的设计

1. **exclusions 豁免机制**：5 条豁免全部正确命中（GlobMatcher 匹配相对路径），闭环收口干净。
2. **规则资产与引擎分离**：全程只改 guardai.yml，一行代码没动，220→0。
3. **闸口装配**：init 写入 4 闸口（GitHub Actions / Gitee Go 片段 / pre-commit / AGENTS.md），检测到已有 `.claude/` 后正确合并 hook 进 settings.json（不覆盖 settings.local.json）。
4. **【给 AI 的修复指令】**：文本格式稳定，可直接被 hook 消费。

## 五、结论

- **检出能力真实有效**：WitchC 仓库 235 个 .cs/.csproj，调优后既不漏报也不冤枉好人（0 误报 / 0 漏报达成）。
- **默认配置的首跑体验是短板**：对非标准骨架仓库误报洪流，违背"首次报警零误报"铁律——这是 P2 最优先改进项。
- 建议 P2 排期：① init 自动识别顶层目录；② 检查器感知 .gitignore；③ 落位规则区分 app/library 适用范围。

## 附件
- `report-witchc-raw.txt`：默认配置首检（220 违规，误报洪流现场）
- `report-witchc-tuned.txt`：调优 allowed_dirs 后（7 违规，真信号）
- `report-witchc-final.txt`：exclusions 豁免后（0 违规，闭环）
