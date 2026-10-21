# 真实项目试用反馈分析：moon-adorn-backend（2026-10-20，第二份）

> 反馈来源：用户邮件《guardAI 试用反馈》（2026-10-20 20:48，ingkele@witchc.cn → Agent Mail）。
> 试用项目：`F:\明月装饰公司小程序\moon-adorn-backend`（**.NET 8 Web API**，明月装饰小程序后端）——正是 M3 需要的 .NET 项目样本。
> 试用流程：init → check --report → ui，全流程无报错。扫描 14 文件、0 违规。

## 一、项目构成（本地取证）

- `MoonAdorn.csproj`：Microsoft.NET.Sdk.Web / net8.0 / EF Core + Pomelo MySQL（应用项目）；
- 代码文件 13 个 .cs + 1 个 .csproj = 14：Controllers/（Admin、Api）、Data/（AppDbContext）、Models/、Pages/Admin/（7 个 Razor 页面模型）、根目录 Program.cs、SeedData.cs；
- **Services/ 目录存在但是空的**（团队已建好、尚未填代码）；
- 无任何 `*Helper.cs / *Utils.cs / *Extensions.cs` 文件。

## 二、0 违规核实结论：真干净（非漏报）

逐文件核对：全部 13 个 .cs 都在白名单目录内（Controllers/Data/Models/Pages + 根目录 `*` 条目），
落位规则候选文件 0 个。**本次"通过"是真实结论**，与 witchc-website 的假绿有本质区别。

## 三、新发现 P1 缺口：既有空目录不进基线快照（实验证实）

**实验**（项目副本，不碰真实项目）：往既有空目录 `Services/` 写合法业务代码 `OrderService.cs`，check 报：

> x Services/OrderService.cs
> 规则: 目录白名单
> 说明: 文件不在项目骨架白名单的任何目录内（**疑似 AI 自由发挥新建了目录**）

**根因**：`ProjectScanner.ScanTopDirectories` 只收录"含代码文件"的顶层目录——
`Services/` 空目录被过滤，不进 KnownDirectories 基线快照，AI 后续填入代码即被误伤。
**这是首跑零误报铁律的真实反例**：目录是团队建的（init 时刻已存在），却被判"AI 新建"。
影响面：任何"先建目录骨架、后填代码"的开发流（DDD 分层项目极常见）都会踩中。

## 四、旧配置提示（用户侧处置）

用户项目的 guardai.yml 是**旧版 exe 生成的 M1 格式**（`Controllers/**` 递归通配 + 无逐层子目录条目），
init 正确跳过重生成（"guardai.yml 已存在"）。旧格式保留了 M2-2 修复前的嵌套野目录盲区。
**处置**：`init --force` 重新生成即可升级到精确快照格式（`Controllers/*` + 逐层子目录）。

## 五、M3（落位规则 scope: app|library）素材状态

- 本项目落位规则 **0 触发**（无 Helper/Utils/Extensions 文件）——收不到 app 侧落位反馈；
- 但 WitchC 试用（2026-10-19）已有类库侧实锤：**落位规则对类库项目失配（门面类/功能区内聚/Aspire 模板）**；
- 结合两份样本：M3 的 scope 设计依据已够（应用项目规则 0 误报、类库项目规则误报），可开工。

## 六、修复建议（合并批次）

1. **假绿修复**（witchc-website P0）：扫描 0 文件不再报"通过"；
2. **空目录基线缺口**（本项目 P1）：KnownDirectories 收录全部既有目录（空目录也算骨架），
   TopDirectories 的用途推断维持"含代码"过滤不变；
3. **生态感知 include_extensions**（witchc-website P1，方案 B）：识别 Node/Vue/TS 自动放扩展名；
4. **M3 落位规则 scope: app|library**：类库项目门面类/功能区内聚豁免；
5. 文案：backend/ purpose 误标"前端工程"修正。
