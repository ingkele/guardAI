# guardAI 编码规范（本项目自律清单）

> 做规范产品的，自己的代码就是第一份质检报告。本仓库自身遵循以下规范，并由 guardAI 自举检查。

## 命名

- 公共成员 PascalCase；私有字段 `_camelCase`；参数 camelCase；接口 `I` 前缀（如 `IGuardCheck`）；异步方法 `Async` 后缀。
- 类型名表达职责（`StructureCheck` / `GateInstaller`），不用缩写。

## 注释

- 所有公共 API 必须有中文 XML 文档注释（Summary / param / returns / exceptions 四件套）——由 `GenerateDocumentationFile` + `TreatWarningsAsErrors` 强制（CS1591 即错误）。
- 私有方法给 `<summary>`；移植/借鉴的代码注明来源（如"移植自 guard-tool"）。

## 工程配置

- `Directory.Build.props`：Nullable enable、TreatWarningsAsErrors、LangVersion latest、TFM net10.0。
- `.editorconfig`：命名规则、UTF-8 BOM、4 空格缩进。
- 零外部依赖（Core/Checks/Init/Cli 均不引第三方包；测试项目除外）。

## 结构

- 依赖单向：`Cli/Init` → `Checks` → `Core`，Core 不依赖任何上层，零循环。
- 检查模块实现 `IGuardCheck` 插槽，一模块一命名空间。
- 规则放 YAML 配置，逻辑做薄——宿主可迁移（CLI → 全局工具 → Analyzer），规则资产原样沿用。

## 纪律

- 中文 conventional commits；测试全绿才提交；UI 输出中文。
- 自举：`guardAI check` 检查自身仓库必须 0 违规，改动后跑 `dotnet test` 全绿。
- 修改实现前先更新《guard 产品设计方案》，保持文档与实现一致。
