# M1 验收报告：init 自动识别仓库真实顶层目录

> 2026-10-19 | 验收靶子：WitchC 真实仓库（E:\WitchC，235 个受检文件）

## 验收结果：全部通过 ✅

| 验收项 | 标准 | 实测 | 结论 |
|---|---|---|---|
| WitchC init 后首检 | ≤ 5 处违规、无需手工豁免 | **5 处**（全为落位规则，即 M3 范畴） | ✅ |
| 单测 | 存量 19 无劣化 + 新增用例全绿 | **27/27 全绿**（新增 8） | ✅ |
| DemoShop 回归 | 5 处违规逐条同 P0 基线、exit 1 | 5 处一致、exit 1 | ✅ |
| 自举回归 | 0 违规 | 22 文件 0 违规、exit 0 | ✅ |

**核心对比：同一仓库同一引擎，默认配置首检 220 处违规 → 5 处，215 处误报由代码修复消除（非人工调配置）。**

## 改动内容

### ProjectScanner.cs
- `ProjectScanResult` 新增 `TopDirectories`（顶层目录 + 推断用途）与 `HasRootCodeFiles` 两个字段
- 新增 `ScanTopDirectories`：扫描仓库真实顶层目录，**噪声目录排除**（.git/.claude/bin/obj/node_modules/dist 等 17 类）
- 只收录"装代码"的目录（含 .cs/.csproj/.ts/.js 等扩展名的才进白名单）——纯文档/资产目录不需要
- 新增 `InferPurpose`：按目录内容推断用途（.csproj/.sln → 源码模块；.cs → 代码；package.json → 前端工程）

### ConfigGenerator.cs
- 白名单生成改为 **"实测基线 + 通用兜底"** 两段式：
  1. init 时刻仓库真实顶层目录（快照语义：既有目录全部合法，init 之后 AI 新建的才拦）
  2. 通用/DDL 兜底项（与基线首段重复的自动去重）
- 根目录有代码文件时生成 `*` 条目（根级 .cs 不误报）

## WitchC 自动生成的白名单（零手工干预）

```yaml
allowed_dirs:
  - path: .verify/**      purpose: 源码模块（init 实测既有目录）
  - path: framework/**    purpose: 源码模块（init 实测既有目录）
  - path: samples/**      purpose: 源码模块（init 实测既有目录）
  - path: templates/**    purpose: 源码模块（init 实测既有目录）
  - path: tests/**        purpose: 源码模块（init 实测既有目录）
  - path: src/**          purpose: 源代码      # 通用兜底
  - path: config/**       purpose: 配置
  - path: scripts/**      purpose: 脚本
  - path: docs/**         purpose: 文档
```

对照 WitchC 真实结构（framework/module/samples/DOCS/local-feed/nupkgs/src/templates/tests）：
- `module/`、`local-feed/`、`nupkgs/` 未进白名单——**正确**，它们不含受检代码文件（.cs/.csproj）
- `DOCS/` 未进白名单——正确，纯文档目录

## 剩余 5 处（M3 范畴，符合预期）

全部是 `*Extensions.cs → **/Extensions/**` 落位规则命中，即 WitchC 试用时逐一核实过的库门面类/功能区内聚类/Aspire 模板命名——M3（落位规则 scope: app|library）解决，本次不动。

## 设计语义（重要）

**"首跑零误报" ≠ "永不报警"**：基线快照语义 = init 时刻的既有骨架全部合法（不冤枉存量代码），门禁管的是 **init 之后** AI 新建的野目录。这正是四层防线里"门禁不衰减"的落点。
