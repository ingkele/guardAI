# guardAI 五件套修复回归报告（F1~F5）

> 修复时间：2026-10-20 | 触发来源：两份真实项目实测反馈（witchc-website / moon-adorn-backend）
> 验证方式：真实项目**副本**上复测（不动原仓库），逐条对照"修复前误报/假绿 → 修复后翻绿"

## 修复清单与验证结论

| 编号 | 问题 | 级别 | 修复 | 复测结论 |
|------|------|------|------|----------|
| F1 | all 模式扫 0 文件报"通过"（假绿） | P0 | 空扫描警告 + JSON `EmptyScan` 标记 + 退出码 2；changed 模式 0 文件维持正常；台账记 `empty` 不记 `pass` | ✅ 翻绿 |
| F2 | 既有空目录漏出基线快照被误报"AI 新建" | P1 | 无任何文件的空目录树也收进白名单；有文件但无代码的目录维持不收 | ✅ 翻绿 |
| F3 | include_extensions 写死 .cs/.csproj | P1 | 按仓库实测代码文件扩展名生成（CodeFileExtensions 补 .vue/.mjs/.cjs）；无代码文件回落默认 | ✅ 翻绿 |
| F4 | 落位规则对类库项目失配 | P1 | `project_type: app\|library` + placement_rules `scope` 字段，引擎按形态过滤；默认三条落位规则 `scope: app` | ✅ 翻绿（含反向验证） |
| F5 | backend/ 误标"前端工程" | P2 | 目录名语义优先（backend/server/api→后端工程，frontend/web/client→前端工程），内容兜底 | ✅ 翻绿 |

## 复测现场

### F1：witchc-website 假绿修复（对照：修复前"扫描 0 个文件 → 结果: 通过"）

- 旧配置（.cs/.csproj）跑 all 模式 → **"结果: 空扫描警告 - 扫描 0 个文件，本次结论不成立"** + 退出码 2
- JSON 端到端：`"FilesScanned": 0, "EmptyScan": true`
- 台账：all 空扫描记 `empty`；changed 模式 0 文件仍记 `pass`（有单测锁定）

### F2：moon-adorn 空目录误报修复（对照：修复前写 Services/OrderService.cs 报"疑似 AI 自由发挥新建了目录"）

- `init --force` 后 `Services/*` 进白名单，purpose 标注"空目录（既有骨架，预留）"
- 往 Services/ 写合法 OrderService.cs → **0 违规**（误报消失）

### F3：witchc-website 生态感知（对照：修复前识别 Unknown、写死 .cs/.csproj）

- `init --force` 实测出 `.js / .ts / .vue`，扫描 **36 个真实文件、0 违规**
- project_type 正确、backend/frontend 目录逐层进白名单

### F4：WitchC 类库回归（对照：修复前 5 处落位误报——门面类/功能区内聚/Aspire 模板）

- `init --force` 判定 **`project_type: library`**，233 个文件 **0 违规**，5 处落位误报全部消失
- **反向验证**（不矫枉过正）：moon-adorn（app）里写 Controllers/ImageHelper.cs → 落位违规**照抓**（"工具类必须放 Utils 目录"）

### F5：purpose 文案修复（对照：修复前 backend/ 标"前端工程"）

- witchc-website 复测：`backend/*` → "后端工程"、`frontend/*` → "前端工程"

## 回归中抓到并顺手修掉的新问题

**F4 判定启发式被模板/验证工程带偏**：WitchC 首轮回归被误判 `app`——`templates/**`（模板资产）与 `.verify/`（包验证现场）里的 Web/Exe 示例项目触发了 app 信号。
修复：形态判定排除 samples/examples/demos/tests/benchmarks/templates/.verify 等"演示性工程"路径（单测 `模板与验证目录里的应用项目不影响形态判定` 锁定）。
这正是"拿真实项目回归"的价值——纯单测想不到 WitchC 的 templates 目录里藏着 Web 模板工程。

## 其余回归

- 全量单测 **75/75 绿**（新增 19 个用例覆盖 F1~F5）
- 自举：guardAI 自身 `init --force` 后 32 文件 0 违规，`project_type: app`（CLI 程序判定正确）
- 双平台重发布：win-x64（73.7MB）/ linux-x64（73.8MB）自包含单文件，发布版冒烟通过
  （WitchC 233 文件 0 违规 + JSON EmptyScan 标记正确；Git Bash 中文乱码为终端显示问题，内容正确）

## 配置兼容性

- 旧 guardai.yml（无 project_type/scope 字段）**原样可用**：project_type 缺省 app、scope 缺省 all，行为与旧版一致
- 要享受 F2/F3/F4 修复效果需 `guardAI init --force` 重新生成基线（试用操作卡已更新说明）
