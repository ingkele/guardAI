# guardAI —— AI 时代的项目骨架守卫

> 确定性引擎裁判合规，AI 只做装配。让任何项目在 10 分钟内接入"结构级 + 资产级"规范门禁。

**当前版本**：v0.1.0（P1）· **运行时**：.NET 10 · **依赖**：零外部依赖

## 它解决什么

AI 写代码的三类无声腐蚀：

1. **骨架漂移**——AI 长会话后自建 `utils2`/`common` 野目录，项目骨架失控；
2. **重复造轮子**——AI 忘记已有封装，一个上传方法重写 4~8 遍；
3. **规范衰减**——注入的规范随会话变长被稀释。

ESLint/SonarQube 管"代码级"，guardAI 管"结构级 + 资产级"，并做接入自动化。

## 快速开始

```bash
# 1. 装配：扫描项目 → 生成规则配置 → 补齐 CI/pre-commit/AI hook → 自动首检
dotnet run --project src/GuardAI.Cli -- init --repo D:\repos\MyProject --yes

# 2. 日常检查（挂进 hook / CI 的就是这条命令）
dotnet run --project src/GuardAI.Cli -- check --repo D:\repos\MyProject --mode changed

# 3. 环境自检
dotnet run --project src/GuardAI.Cli -- doctor --repo D:\repos\MyProject
```

发布为 .NET 全局工具后等价于 `guardAI init` / `guardAI check` / `guardAI doctor`。

## 命令与退出码

| 命令 | 作用 |
|---|---|
| `guardAI check` | 执行检查（`--mode changed/all`、`--only structure`、`--json`、`--report <文件>`） |
| `guardAI init` | 装配：生成 `guardai.yml` + 补齐三道闸口 + 首检 |
| `guardAI doctor` | 环境与配置自检 |

**退出码（全命令统一）**：`0` 通过 / `1` 存在违规 / `2` 配置或环境错误——CI 与 hook 由此区分"拦下"与"跑挂了"。

## 三道闸口（`guardAI init` 自动装配）

| 闸口 | 管什么 | 模板 |
|---|---|---|
| AI hook（Claude Code / Codex） | 当轮拦截：AI 写完文件立即报、当场修 | `templates/hooks/` |
| git pre-commit | 本地提交 | `templates/hooks/pre-commit` |
| CI（GitHub Actions / Gitee Go） | 合入底线，与"谁写的代码"无关 | `templates/ci/` |

外加 `AGENTS.md` 注入片段（骨架红线 + 检索先行 + 改完复检）。

## 项目结构

```
src/GuardAI.Core      规则引擎（YAML 解析、通配匹配、文件收集、报告）
src/GuardAI.Checks    检查模块插槽（structure 三规则；duplicate 等待加入）
src/GuardAI.Init      装配器（扫描、配置生成、闸口安装）
src/GuardAI.Cli       单入口命令行
config/presets        语言预设（规则资产）
templates             闸口模板（CI / hook / AGENTS 片段）
tests/GuardAI.Tests   xUnit 单测（19 用例）
docs/regression       P0 回归报告
```

## 回归基准（与 guard-tool P0 参考实现逐条对齐）

| 场景 | 结果 |
|---|---|
| 合规骨架 6 文件（单测） | ✅ 0 违规 |
| 违规现场 3 文件（DemoShop 实测） | ✅ 5 处全抓，规则归属一致，退出码 1 |
| 自举：guardAI 检查自身仓库 | ✅ 21 文件 0 违规 |

## 设计原则

1. 引擎裁判，AI 装配；2. 门禁无头，台账有页面；3. 一条命令，永不改变；
4. 规则放配置（`guardai.yml`），逻辑做薄；5. 注入会衰减，门禁不衰减；6. 本地优先，代码不出本机。

## 路线图

P1（当前）核心引擎 + `guardAI init` → P2 MCP + HTML 报告 → P3 重复检测 + `guardAI ui` 台账页 → P4 多语言预设 + 发布矩阵 → P5 注释检查 + 前端规则 + 线上轻量版
