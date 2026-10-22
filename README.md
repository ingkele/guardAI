# guardAI —— AI 时代的项目骨架守卫

> 确定性引擎裁判合规，AI 只做装配。让任何项目在 10 分钟内接入"结构级 + 资产级"规范门禁。

**当前版本**：v0.5.1 · **分发**：win-x64 / linux-x64 自包含单文件（零运行时依赖）· **开发**：.NET 10 · **单测**：274

## 它解决什么

AI 写代码的三类无声腐蚀：

1. **骨架漂移**——AI 长会话后自建 `utils2`/`common` 野目录，项目骨架失控；
2. **重复造轮子**——AI 忘记已有封装，一个上传方法重写 4~8 遍；
3. **规范衰减**——注入的规范随会话变长被稀释。

guardAI 管"结构级 + 资产级"规范门禁，并做接入自动化（CI / pre-commit / AI hook 一键装配）。

## 启动方式（三选一）

### 方式一：发行版（推荐，零依赖）

从 [GitHub Releases](https://github.com/ingkele/guardAI/releases) 下载 `guardAI-win-x64.zip` / `guardAI-linux-x64.zip`，解压即用（自包含 ~74MB，无需安装 .NET）：

```powershell
# Windows
.\GuardAI.Cli.exe init --repo "D:\你的项目"

# Linux
./GuardAI.Cli init --repo "/path/to/repo"
```

### 方式二：从源码运行（开发调试）

```bash
git clone https://github.com/ingkele/guardAI.git
cd guardAI
dotnet run --project src/GuardAI.Cli -- init --repo D:\repos\MyProject --yes
```

### 方式三：懒人测试脚本

双击 `test.cmd`（或 PowerShell 跑 `test.ps1`）：输入项目路径 → 勾选 `init / check / strict / review / learn / ui` → 自动按序跑完出汇总表。其中 `review` / `learn` 是 **AI 触发项**（需 `ai:` 段 + 环境变量 KEY，消耗 token），`init` 加 `-Ai` 开关附 AI 装配建议。也可一条命令直达：

```powershell
powershell -ExecutionPolicy Bypass -File test.ps1 -Repo "D:\你的项目" -Steps init,check
powershell -ExecutionPolicy Bypass -File test.ps1 -Repo "D:\你的项目" -Steps review,learn   # 只跑 AI 触发
```

## 用法速览

```powershell
guardAI init --repo "D:\你的项目"                  # 装配：扫描 → 生成配置 → 装三道闸口 → 自动首检
guardAI init --repo "D:\你的项目" --ai             # AI 装配增强：大模型产配置建议（需 KEY，见下）
guardAI check --repo "D:\你的项目" --mode changed  # 日常检查（改完代码跑，修到 0 违规）
guardAI review --repo "D:\你的项目"               # AI 语义审查：命名/注释/分层/复用（需 KEY）
guardAI review --repo "D:\散文件目录" --mode all   # 非 git 目录全量送审代码文件（案例/散文件场景）
guardAI learn --repo "D:\你的项目"                # AI 规则发现员：从审查素材提炼候选规则（需 KEY）
guardAI ui --repo "D:\你的项目"                   # 本地台账网页：审查记录 / 闭环率
```

## 命令一览与退出码

| 命令 | 作用 | 需要 KEY |
|---|---|---|
| `check` | 门禁检查（`--mode changed/all`、`--only`、`--strict` 严格审计、`--json`、`--html`、`--report`） | 否 |
| `init` | 装配：生成 `guardai.yml` + 补齐三道闸口 + 首检（`--ai` 为 AI 装配增强，`--force` 重新生成） | 仅 `--ai` |
| `review` | AI 语义审查（默认只报告，`--fail-on error/warn/info` 才按级别拦截） | 是 |
| `learn` | AI 规则发现员：审计审查素材，提炼候选规则（`--json` / `--report`） | 是 |
| `ui` | 本地台账网页：审查记录 / AI hook 结论 / 闭环率 | 否 |
| `doctor` | 环境与配置自检 | 否 |
| `mcp` | MCP server：AI 客户端直调 `scan_project` / `generate_config` / `get_report` / `search_capability` | 否 |
| `index` / `search` | 能力清单生成 / 写码前强制检索（防重复造轮子） | 否 |

**退出码（全命令统一）**：`0` 通过 / `1` 存在违规 / `2` 配置或环境错误——CI 与 hook 由此区分"拦下"与"跑挂了"。

## KEY 配置（AI 能力专用）

只有 `review`、`init --ai`、`learn` 三个 AI 命令需要 KEY，其余命令纯本地运行、不联网。密钥**只进环境变量，绝不进配置文件**——配置里只写环境变量的**名字**。

**第一步**：`guardai.yml` 追加 `ai:` 段（以 mimo 为例）：

```yaml
ai:
  provider: openai        # openai（OpenAI 协议，兼容 mimo/DeepSeek 等）/ anthropic / ollama
  model: mimo-v2.5-pro
  api_key_env: MIMO_API_KEY                 # ← 只写变量名，不写密钥值
  base_url: https://token-plan-cn.xiaomimimo.com/v1
  # max_tokens: 16384      # 推理模型（思考型）给足，16384 起步
  # timeout_seconds: 600   # 推理模型审查大改动建议 600
```

**第二步**：设置环境变量：

```powershell
$env:MIMO_API_KEY = "你的密钥"     # 当前窗口临时生效
setx MIMO_API_KEY "你的密钥"        # 永久生效（新开窗口才生效）
```

**本地 Ollama 零配置**：`ai: { provider: ollama, model: qwen3:8b }`——无需 `api_key_env`，默认 http://localhost:11434。

### 密钥安全

- `api_key_env` 只存环境变量**名**，密钥值全程不落盘、不进 git；
- 送审内容外发前自动过滤疑似密钥/凭据（`.env*` / `*.pem` / `*.key` / `id_rsa*` 等文件名、`BEGIN PRIVATE KEY` / `sk-` 令牌等内容特征），只报文件名清单，**密钥内容不出本机**；
- 本地优先：代码内容只发给 `ai:` 段配置的模型服务；用本地 Ollama 则完全不出本机。

## 三道闸口（`guardAI init` 自动装配）

| 闸口 | 管什么 | 模板 |
|---|---|---|
| AI hook（Claude Code / Codex） | 当轮拦截：AI 写完文件立即报、当场修 | `templates/hooks/` |
| git pre-commit | 本地提交 | `templates/hooks/pre-commit` |
| CI（GitHub Actions / Gitee Go） | 合入底线，与"谁写的代码"无关 | `templates/ci/` |

外加 `AGENTS.md` 注入片段（骨架红线 + 检索先行 + 改完复检），带版本戳自动升级。

## 项目结构

```
src/GuardAI.Core      规则引擎（YAML 解析、通配匹配、文件收集、报告）
src/GuardAI.Checks    检查模块（structure / duplicate 双通道 / 能力清单 / 多语言成员解析）
src/GuardAI.AI        AI 层（review 审查员 / init --ai 装配顾问 / learn 规则发现员 / LLM 三协议接入）
src/GuardAI.Init      装配器（扫描、配置生成、闸口安装）
src/GuardAI.Cli       单入口命令行
config/presets        语言预设（规则资产）
templates             闸口模板（CI / hook / AGENTS 片段）
tests/GuardAI.Tests   xUnit 单测（256 用例）
docs/cases            案例库（50 案例 / 39 规则）
```

多语言支持：C# / Java / Kotlin / TypeScript / JavaScript / Go / Python 成员解析，按扩展名自动分发，无需配置。

## 质量基准

| 场景 | 结果 |
|---|---|
| 全量单测 | ✅ 256/256 |
| 自举：guardAI 检查自身仓库 | ✅ structure 93 文件 0 违规 + duplicate 87 文件 0 违规 |
| 违规现场（DemoShop 实测） | ✅ 5 处全抓，退出码 1 |
| 多语言冒烟（Java 真重复） | ✅ 100% 相似 3 处全逮，存量债首跑收编不轰炸 |

## 设计原则

1. 引擎裁判，AI 装配；2. 门禁无头，台账有页面；3. 一条命令，永不改变；
4. 规则放配置（`guardai.yml`），逻辑做薄；5. 注入会衰减，门禁不衰减；6. 本地优先，代码不出本机。

## 路线图

P1 核心引擎 + `init` ✅ → P2 MCP + 误报治理 + 台账 ✅ → P3 重复检测 + 能力清单 ✅ → P4 多语言 + 三步 AI 层（`review` / `init --ai` / `learn`）✅（v0.5.0）→ help 体系 + 钩子根治 + AI 触发项（v0.5.1）→ P5 线上轻量版 + 前端规则
