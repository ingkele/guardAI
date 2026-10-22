# AI 审查案例库 · 十一轮自审闭环（2026-10-22）

> 素材来源：guardAI v0.4.0 装配期间 11 轮 `review` 自审报告（`raw/review-20261022-*.json`，11 份，证据链完整）。
> 审查模型：mimo-v2.6-pro；审查对象：AI 层（review 命令、LLM 客户端、配置解析）自身的装配 diff。
> 共 43 条 findings（warn 19 / info 24），全部修复后收敛发版。本库按类别归档，每条给出根因、修复与**可沉淀规则**（"规则 = AI 判断的缓存"——后续 AI 规则发现员的输入素材）。

## 收敛轨迹

| 轮次 | 报告 | 发现数 | 级别构成 | 本轮抓到的代表性问题 |
|---|---|---|---|---|
| R1 | review-…-102949 | 5 | 4 warn + 1 info | git 管道死锁、配置键名不匹配致审查上下文丢失 |
| R2 | review-…-103431 | 3 | 2 warn + 1 info | 严重级别映射两处重复、魔法数字 |
| R3 | review-…-103907 | 3 | 3 info | 注释承诺与实现矛盾、缺省值双定义 |
| R4 | review-…-104645 | 5 | 2 warn + 3 info | --json 契约被污染、密钥异常冲破契约 |
| R5 | review-…-105447 | 5 | 1 warn + 4 info | 取消被误报为超时、Config 反依赖 Llm |
| R6 | review-…-112928 | 3 | 2 warn + 1 info | **敏感文件外发（真安全漏洞）** |
| R7 | review-…-114622 | 5 | 2 warn + 3 info | Ctrl+C 不贯通 git 收集、密钥策略自相矛盾 |
| R8 | review-…-115526 | 3 | 1 warn + 2 info | 配置格式错被误报为"缺少配置" |
| R9 | review-…-120126 | 4 | 1 warn + 3 info | 校验重复且异常契约不一致 |
| R10 | review-…-121222 | 4 | 3 warn + 1 info | 异常契约违约、NaN 溜过范围校验 |
| R11 | review-…-122039 | 3 | 1 warn + 2 info | 全跳过误报"无改动"、事件处理器不注销 |

收敛判定：error/warn 清零才走、info 记档、**收效递减即停**（R11 后收口发版，重构回声类发现不再开新一轮）。

## 统计

- 按级别：warn 19、info 24（无 error）。
- 按类别：异常契约与报错准确性 11、单一事实来源与复用 11、命名/文件/结构 8、并发取消与进程 5、注释与实现一致 5、性能与边界 2、安全 1。
- 按目标层（可沉淀规则去向）：本地检查（引擎可查）12、AI 审查清单 18、提示词规则 13。

---

## A. 安全（送审外发边界）— 1 条

#### C22 · warn · R6-1 ★ 本库最高价值案例
**位置**：`src/GuardAI.AI/Review/DiffCollector.cs:43`
**问题**：未跟踪文件的完整内容直接送入外部大模型接口，未过滤 `.env`、`*.pem`、`*.key`、`credentials` 等敏感文件——这些文件通常是新增且未被 gitignore 忽略的，恰好会被 `ls-files --others --exclude-standard` 收集并外发，存在密钥泄露到第三方模型服务的风险。
**根因**：收集逻辑只关心"有哪些改动"，没有"外发边界"意识；送审通道默认信任所有 diff 内容。
**修复**：DiffCollector 三道敏感过滤闸——①文件名模式（`.env*`/`credentials*`/`*.pem|key|p12|pfx|jks|keystore|ppk|kdbx`/`id_rsa*` 等，路径段边界+行尾锚定）②目录模式（`.aws/`、`.ssh/`、`.gnupg/`、`.kube/`）③内容特征（`BEGIN PRIVATE KEY`、`sk-` 长令牌、`AKIA` AWS Key）；命中只记文件名进跳过清单，**内容零外发**；宁可漏审不外发。
**可沉淀规则**：`任何把代码/文件内容送外部服务的动作，必须先过敏感过滤（文件名+目录+内容特征），命中只报文件名不发内容` → 【本地检查】+【提示词规则】

---

## B. 异常契约与报错准确性 — 11 条

#### C08 · info · R2-3
**位置**：`src/GuardAI.Cli/Program.cs:217`
**问题**：catch 过滤器 `ex is TaskCanceledException or TimeoutException or OperationCanceledException` 中 TaskCanceledException 是 OperationCanceledException 的派生类，前者冗余。
**根因**：异常继承层次不清导致的防御性堆叠。
**修复**：简化为 `ex is OperationCanceledException or TimeoutException`。
**可沉淀规则**：`catch 过滤器不要同时列基类与派生类` → 【AI 审查清单】

#### C12 · warn · R4-1
**位置**：`src/GuardAI.Cli/Program.cs:238`
**问题**：`--json` 模式下仍向 stdout 打印"审查报告已写入: …"人类可读提示，紧跟 JSON 之后，机器消费（管道/CI 解析 stdout）会解析失败。
**根因**：把"给人看的提示"和"给机器的输出"混在同一条流上。
**修复**：提示改写 `Console.Error`；`--json` 输出只保留纯 JSON。
**可沉淀规则**：`--json/机器可读模式下 stdout 只允许输出协议数据，人类提示一律走 stderr` → 【本地检查】+【提示词规则】

#### C13 · warn · R4-2
**位置**：`src/GuardAI.AI/Config/AIConfig.cs:72`
**问题**：ResolveApiKey 缺环境变量抛 InvalidOperationException，而 ILlmClient.ChatAsync 契约声明鉴权错误抛 LlmException；异常逃逸成未处理崩溃而非可行动中文报错。
**根因**：底层方法抛出的异常类型与上层声明的契约不一致。
**修复**：密钥解析移入 `LlmKey.Resolve`，缺环境变量抛 LlmException，契约单一。
**可沉淀规则**：`每个方法只能抛其 XML 注释声明的异常类型；跨层异常必须在边界转换为目标契约类型` → 【AI 审查清单】

#### C23 · warn · R6-2
**位置**：`src/GuardAI.Cli/Program.cs:205`
**问题**："没有可审查的改动"这一正常场景抛 InvalidOperationException 直冲堆栈，用户看到原始报错而非"先改代码再跑 review"的友好提示。
**根因**：可控业务分支用异常表达，但调用方没有把它纳入可控异常集合。
**修复**：RunReview 显式 `catch (InvalidOperationException)` 输出友好消息，exit 1。
**可沉淀规则**：`CLI 入口的 catch 必须覆盖所有可控异常分支，任何正常场景都不允许向用户暴露堆栈` → 【AI 审查清单】

#### C26 · warn · R7-2
**位置**：`src/GuardAI.AI/Config/AIConfig.cs:93`
**问题**：密钥策略两处不一致——解析器对 openai 强制要求 api_key_env，客户端却写了"key 为空只发匿名请求"的分支，该分支永远走不到，本地无鉴权端点无法配置。
**根因**：同一条策略在两个层各写一套且互不知情。
**修复**：统一为"anthropic 必填（API 强制 x-api-key）、openai/ollama 放行无鉴权端点"，解析器与客户端同改。
**可沉淀规则**：`一条业务策略只允许一个定义点；两个层各自实现同一策略视为缺陷` → 【AI 审查清单】+【本地检查】

#### C31 · warn · R8-2
**位置**：`src/GuardAI.AI/Config/AIConfig.cs:72`
**问题**：`ai:` 段存在但值不是映射（缩进写错）时报"缺少 ai: 配置段"，与真实原因不符，误导排查方向。
**根因**："缺失"与"格式错误"两个分支合并处理，报错文案丢失真因。
**修复**：Parse 区分两分支——缺段返回 null，格式错抛 FormatException"ai: 段必须是键值映射（检查缩进与字段格式）"。
**可沉淀规则**：`报错文案必须指向真实原因；解析器要区分"缺失/类型错/格式错"分别报` → 【AI 审查清单】

#### C32 · info · R8-3
**位置**：`src/GuardAI.AI/Review/DiffCollector.cs:59`
**问题**：mode 采用 `_ =>` 兜底，未知模式（拼写错误）静默按 changed 收集，public API 静默降级不易被发现。
**根因**：用 switch 兜底分支吞掉了非法输入。
**修复**：switch 前显式校验，未知值抛 FormatException 列出可用值。
**可沉淀规则**：`public API 对枚举式入参必须显式拒绝非法值，禁止兜底分支静默降级` → 【本地检查】

#### C33 · warn · R9-1
**位置**：`src/GuardAI.AI/Review/DiffCollector.cs:78`
**问题**：模式校验在 Program 与 DiffCollector 重复出现且异常类型不一致（FormatException vs ArgumentException），同一输入错误在不同入口表现不同契约。
**根因**：校验逻辑重复实现，各写各的异常类型。
**修复**：`DiffCollector.SupportedModes` 单一事实来源（IReadOnlyList），两处共用同一校验、同抛 FormatException。
**可沉淀规则**：`同一输入的校验只允许一个定义点，异常类型全入口一致` → 【本地检查】

#### C37 · warn · R10-1
**位置**：`src/GuardAI.AI/Review/ReviewParser.cs:19`
**问题**：Parse 注释声明抛 FormatException，实际 JsonDocument.Parse 抛 JsonException（非其子类），契约与实现不一致。
**根因**：注释写的是期望契约，实现没有包装第三方异常。
**修复**：ParseDocument 包 try/catch(JsonException) 重抛 FormatException 带截断预览。
**可沉淀规则**：`第三方库异常不得冲破本层异常契约，边界处必须转换` → 【AI 审查清单】

#### C38 · warn · R10-2
**位置**：`src/GuardAI.AI/Llm/OpenAICompatClient.cs:53`
**问题**：注释承诺"响应格式错误"统一抛 LlmException，实际 JsonDocument.Parse 对网关 HTML 错误页抛 JsonException 直冲堆栈。
**根因**：三个协议客户端各自裸调 JsonDocument.Parse，无统一包装。
**修复**：`LlmHttp.ParseJson(body, label)` 统一包装 JsonException→LlmException，三客户端共用。
**可沉淀规则**：`解析外部输入的异常转换要有唯一封装点，禁止各调用点裸调解析 API` → 【本地检查】

#### C42 · warn · R11-2
**位置**：`src/GuardAI.AI/Review/DiffCollector.cs:134`
**问题**：改动全部被敏感过滤跳过时（files==0 但 skipped>0）仍报"没有可审查的改动……先改代码"，提示与事实不符，跳过清单随异常丢弃。
**根因**：零结果的两种成因（真没改动 / 全被过滤）合并成一条报错。
**修复**：抛错前区分两分支——全跳过时报"N 个文件疑似含密钥已跳过（列文件名）"，仅真无改动才提示先改代码。
**可沉淀规则**：`报错/提示必须区分成因；有跳过/过滤动作时必须随报错输出清单` → 【AI 审查清单】

---

## C. 并发、取消与进程 — 5 条

#### C04 · warn · R1-4
**位置**：`src/GuardAI.AI/Review/DiffCollector.cs:96`
**问题**：RunGit 先同步读 stdout 再读 stderr，git 向 stderr 写满管道缓冲区时父子进程互等死锁（大仓库 diff/报错场景 stderr 非空是常态）。
**根因**：经典进程管道死锁——顺序读两个输出流。
**修复**：双流并发 ReadToEndAsync 后 WaitForExitAsync。
**可沉淀规则**：`读子进程输出必须 stdout/stderr 并发消费，禁止顺序 ReadToEnd` → 【本地检查】

#### C16 · info · R4-5
**位置**：`src/GuardAI.Cli/Program.cs:207`
**问题**：RunReview 用 `.GetAwaiter().GetResult()` sync-over-async 阻塞，占线程最长至超时上限，且不传 CancellationToken，Ctrl+C 无法取消。
**根因**：async 方法被同步调用，取消链路缺失。
**修复**：RunReview 全链 async，CancellationToken 贯通到模型调用。
**可沉淀规则**：`async 方法必须 await 调用并传递 CancellationToken，禁止 GetAwaiter().GetResult()` → 【本地检查】

#### C17 · warn · R5-1
**位置**：`src/GuardAI.Cli/Program.cs:214`
**问题**：catch 把 OperationCanceledException 与 TimeoutException 一并按"模型调用超时"提示，用户 Ctrl+C 取消被误报超时，误导调大 timeout；HttpClient 超时实际抛 TaskCanceledException 与用户取消混为一谈。
**根因**：取消与超时共享同一异常类型家族，靠异常类型区分不了，需看 CancellationToken 状态。
**修复**：按 `cts.IsCancellationRequested` 分流——用户取消报"已取消"（exit 2），真超时才提示调大 timeout_seconds。
**可沉淀规则**：`区分"用户取消"与"超时"必须查 CancellationToken 状态，不能只看异常类型` → 【AI 审查清单】

#### C25 · warn · R7-1
**位置**：`src/GuardAI.AI/Review/DiffCollector.cs:179`
**问题**：RunGit 同步阻塞且不接受 CancellationToken，"Ctrl+C 可中断"的承诺只覆盖模型调用，git 收集阶段按 Ctrl+C 无反应。
**根因**：取消链路在收集阶段断开。
**修复**：CollectAsync/RunGitAsync 全异步 + CancellationToken 贯通，取消时 `Kill(entireProcessTree: true)`。
**可沉淀规则**：`取消承诺必须端到端贯通；所有耗时外部调用（进程/HTTP）都要接收 CancellationToken` → 【本地检查】

#### C43 · info · R11-3
**位置**：`src/GuardAI.Cli/Program.cs:200`
**问题**：Console.CancelKeyPress 处理器注册后从不注销，闭包引用 using 的 cts；RunReview 返回后触发 Ctrl+C 会踩 ObjectDisposedException。
**根因**：事件订阅生命周期长于被订阅对象。
**修复**：处理器提为局部变量，try/finally 中 `-=` 注销后再释放 cts。
**可沉淀规则**：`事件订阅必须成对注销，且不得让处理器闭包持有短生命周期对象` → 【AI 审查清单】

---

## D. 单一事实来源与复用 — 11 条

#### C02 · warn · R1-2
**位置**：`src/GuardAI.AI/Review/ReviewPrompt.cs:87`
**问题**：BuildRuleSummary 提炼的键是 `placement`，配置实际段名是 `placement_rules`，项目的放置/分层规则永远不会送给模型，审查上下文静默缺失关键约定。
**根因**：配置段名字符串在两处各写一份，改名后漏同步。
**修复**：键列表改实际段名并补测试断言 placement_rules 能进摘要。
**可沉淀规则**：`配置键/段名必须单点定义引用，禁止魔法字符串散落；有"按名取配置"逻辑必须有测试锁` → 【本地检查】

#### C03 · warn · R1-3
**位置**：`src/GuardAI.AI/Review/ReviewPrompt.cs:103`
**问题**：FormatValue 对列表只调元素 ToString()，`allowed_dirs` 这类字典列表渲染成 `System.Collections.Generic.Dictionary`2[...]` 类型名（真实出现 17 行），对模型是无效噪音。
**根因**：格式化没有递归处理嵌套结构。
**修复**：列表/字典值递归 FormatValue，字典输出 `key=value`。
**可沉淀规则**：`序列化/格式化嵌套结构必须递归处理，禁止对未知类型直接 ToString()` → 【AI 审查清单】

#### C05 · info · R1-5
**位置**：`src/GuardAI.AI/Llm/OpenAiCompatClient.cs:23`
**问题**：JsonOptions 挂在 OpenAiCompatClient 上却被 Anthropic/Ollama 跨实现引用，Truncate 三客户端各复制一份。
**根因**：共享工具落在了某个具体实现类上。
**修复**：抽出 `LlmJson`（后拆 `LlmText`）共享类，删除各客户端副本。
**可沉淀规则**：`被两个以上类型共用的工具必须是独立静态类，禁止挂在某个实现类上` → 【本地检查】

#### C06 · warn · R2-1
**位置**：`src/GuardAI.AI/Review/ReviewReport.cs:105`
**问题**：私有 SeverityOrder 与 ReviewFinding.SeverityRank 的级别映射完全重复，新增级别时易漏改。
**根因**：同一语义映射两处维护。
**修复**：删 SeverityOrder，统一用 SeverityRank。
**可沉淀规则**：`同一语义映射（枚举排序/等级换算）只允许一个定义点` → 【本地检查】

#### C07 · warn · R2-2
**位置**：`src/GuardAI.Cli/Program.cs:258`
**问题**：HitsFailLevel 用字面量 0/1/2 表示严重级别阈值，与 SeverityRank 返回值重复定义，魔法数字。
**根因**：等级数值没有走单一来源。
**修复**：改用 SeverityRank 求阈值。
**可沉淀规则**：`禁止魔法数字表达枚举/等级语义，必须引用命名常量或单一换算函数` → 【本地检查】

#### C10 · info · R3-2
**位置**：`src/GuardAI.AI/Config/AiConfig.cs:107`
**问题**：解析器缺省值 300/8192/0.1 与 record 构造默认参数重复定义，不同步即"配置缺省"与"代码缺省"打架。
**根因**：默认值双定义。
**修复**：抽 `AIDefaults` 常量类，两处引用同一组。
**可沉淀规则**：`默认值只允许一处定义（常量类），配置解析与类型构造必须同源` → 【本地检查】

#### C18 · info · R5-2
**位置**：`src/GuardAI.AI/Review/ReviewReport.cs:95`
**问题**：WriteJson 每次 new JsonSerializerOptions，中文被转义成 `\uXXXX`，与"报告可读"约定不一致。
**根因**：序列化选项就地新建，约定散落。
**修复**：复用 `LlmJson.IndentedOptions`（WriteIndented + UnsafeRelaxedJsonEscaping）。
**可沉淀规则**：`JsonSerializerOptions 必须共享静态实例，禁止调用点 new` → 【本地检查】

#### C19 · info · R5-3
**位置**：`src/GuardAI.AI/Review/ReviewParser.cs:112`
**问题**：Preview 与 Truncate 同程序集重复实现"报错截断展示"，仅阈值不同。
**根因**：相似工具各写一份。
**修复**：统一 `LlmText.Truncate(text, maxLength)`，删 Preview。
**可沉淀规则**：`同类工具方法（截断/格式化/换算）合并为带参单一实现` → 【AI 审查清单】

#### C28 · info · R7-4
**位置**：`src/GuardAI.AI/Review/DiffCollector.cs:172`
**问题**：ExtractDiffPath 每段都走 `Regex.Match(静态方法新建)`，与同类正则"静态 readonly + Compiled"写法不一致。
**根因**：正则声明方式不统一，重复解析开销。
**修复**：提为 `private static readonly Regex DiffPathPattern`（Compiled）。
**可沉淀规则**：`Regex 必须静态 readonly + Compiled，禁止方法内新建` → 【本地检查】

#### C36 · info · R9-4
**位置**：`src/GuardAI.AI/Llm/AnthropicClient.cs:42`
**问题**：三客户端"构造请求+序列化+发送+读 body+非成功抛 LlmException"样板重复三次。
**根因**：HTTP 收发样板没有下沉公共层。
**修复**：抽 `LlmHttp.SendAsync/JsonContent/ParseJson`，客户端只留协议差异。
**可沉淀规则**：`三个以上实现重复的流程样板必须下沉公共层，实现类只留差异点` → 【AI 审查清单】

#### C40 · info · R10-4
**位置**：`src/GuardAI.AI/Review/DiffCollector.cs:44`
**问题**：SupportedModes 是可变数组（public static readonly string[]），外部可改写元素，破坏单一事实来源。
**根因**：只读意图没有用类型表达。
**修复**：改 `IReadOnlyList<string> { get; }` 属性。
**可沉淀规则**：`暴露为"常量集合"的 API 必须是 IReadOnlyList/IReadOnlyCollection，不能是可变数组` → 【本地检查】

---

## E. 命名、文件与结构 — 8 条

#### C01 · warn · R1-1
**位置**：`src/GuardAI.Cli/Program.cs:260`
**问题**：局部变量 `Rank` 用了 PascalCase，违背 camelCase 约定；且与 ReviewReport 排序逻辑同语义。
**根因**：命名手误 + 职责归属不清。
**修复**：改 `rank`，级别映射收敛到 ReviewFinding.SeverityRank。
**可沉淀规则**：`局部变量/参数一律 camelCase` → 【本地检查】（机械性，可 100% 查）

#### C11 · info · R3-3
**位置**：`src/GuardAI.AI/Config/AiConfig.cs:15`
**问题**：命名空间 GuardAI.AI（缩写全大写）与类型 AiConfig（缩写混写）风格不一致。
**根因**：两字母缩写风格无项目约定。
**修复**：统一全大写 AI（AIConfig/AIConfigParser/OpenAICompatClient）。
**可沉淀规则**：`两字母缩写全大写（AI/IO/HTTP），全库统一并在规范中写明` → 【提示词规则】

#### C14 · info · R4-3
**位置**：`src/GuardAI.AI/GuardAI.AI.csproj:4`
**问题**：GuardAI.AI 引用 GuardAI.Core 但新代码零使用，未使用的依赖徒增耦合。
**根因**：项目模板默认引用未按需清理。
**修复**：删除 ProjectReference，需要时再引。
**可沉淀规则**：`项目引用必须实际被使用，装配完成时清理未用引用` → 【AI 审查清单】

#### C15 · info · R4-4
**位置**：`src/GuardAI.AI/Llm/OpenAiCompatClient.cs:1`
**问题**：文件名 OpenAiCompatClient.cs 与类名 OpenAICompatClient 不一致，检索导航易漏。
**根因**：重命名漏了文件名。
**修复**：文件重命名对齐类型名。
**可沉淀规则**：`文件名 = 类型名（一文件一公共类型）` → 【本地检查】

#### C20 · info · R5-4
**位置**：`src/GuardAI.AI/Config/AIConfig.cs:40`
**问题**：常量 ProviderOpenAi 与类型 AIConfig/OpenAICompatClient 缩写大小写混用。
**根因**：同 C11，缩写约定缺失。
**修复**：ProviderOpenAI/DefaultOpenAIBaseUrl，品牌名 OpenAI 保持。
**可沉淀规则**：`缩写风格全库一致；品牌名按官方写法（OpenAI）` → 【提示词规则】

#### C21 · info · R5-5
**位置**：`src/GuardAI.AI/Config/AIConfig.cs:72`
**问题**：配置层 ResolveApiKey 抛传输层 LlmException，Config 反向依赖 Llm。
**根因**：异常类型归属错层。
**修复**：密钥解析整体移到 Llm 层（LlmKey），Config 零 Llm 依赖。
**可沉淀规则**：`依赖方向单向：底层/配置层不得引用上层/传输层类型（含异常类型）` → 【AI 审查清单】

#### C30 · info · R8-1
**位置**：`src/GuardAI.AI/Config/AiConfig.cs:61`
**问题**：一个文件含 AIDefaults/AIConfig/AIConfigParser 三个公共类型，按类型名定位文件困难。
**根因**：装配期贪图顺手合并。
**修复**：拆三文件，命名空间不变。
**可沉淀规则**：`一文件一公共类型，文件名=类型名` → 【本地检查】

#### C35 · info · R9-3
**位置**：`src/GuardAI.AI/Llm/LlmJson.cs:8`
**问题**：LlmJson 同时放 JSON 选项与文本截断 Truncate，按名检索工具易漏。
**根因**：类名与职责不匹配。
**修复**：Truncate 拆到 LlmText。
**可沉淀规则**：`类名必须覆盖其全部职责；名实不符时拆类或改名` → 【AI 审查清单】

---

## F. 注释与实现一致 — 5 条

#### C09 · info · R3-1
**位置**：`src/GuardAI.AI/Review/ReviewFinding.cs:33`
**问题**：注释称"未知级别按 warn（宁松勿严，避免误卡）"，但 warn 在 `--fail-on warn` 下会拦截，实为偏严，注释自相矛盾。
**根因**：注释描述的是愿望不是行为。
**修复**：注释改为与行为一致的描述。
**可沉淀规则**：`注释只描述实际行为；"宁松勿严"之类的方针语必须与代码行为对得上` → 【AI 审查清单】

#### C24 · info · R6-3
**位置**：`src/GuardAI.AI/Config/AIConfig.cs:13`
**问题**：MaxTokens 注释写"16384 起步"但常量是 8192，注释与取值不一致。
**根因**：注释先行、实现后改，忘了回改注释。
**修复**：注释改"默认 8192；推理模型建议配置 16384 起步"。
**可沉淀规则**：`注释中出现的数值必须与常量取值一致，改值必改注释` → 【AI 审查清单】

#### C29 · info · R7-5
**位置**：`src/GuardAI.AI/Review/DiffCollector.cs:67`
**问题**：`FileInfo.Length > MaxFileChars * 2` 字节数对比字符阈值，换算无说明，两套口径混用，读者无法判断多大文件被跳过。
**根因**：字节/字符口径混用且魔法换算。
**修复**：拆 `MaxFileBytes = 1_000_000`（字节预筛）与 `MaxFileChars = 20000`（字符截断）两常量，注释写明两阶段。
**可沉淀规则**：`字节与字符阈值分开命名并注释口径，禁止魔法换算系数` → 【AI 审查清单】

#### C34 · info · R9-2
**位置**：`src/GuardAI.AI/Config/AIConfig.cs:9`
**问题**：BaseUrl 参数注释写"为空时取默认"，但 record 参数非空必填，该行为实际在解析器；注释描述了解析侧行为而非 record 契约。
**根因**：注释写错归属层。
**修复**：参数注释改"已归一的基地址（不含尾斜杠）；缺省补齐见 AIConfigParser"。
**可沉淀规则**：`参数注释只描述该类型/方法的契约，跨层行为指向实际实现处` → 【AI 审查清单】

#### C41 · info · R11-1
**位置**：`src/GuardAI.AI/Llm/LlmJson.cs:7`
**问题**：LlmJson 类注释仍写"含报错截断"，但 Truncate 已拆去 LlmText，注释残留。
**根因**：重构后注释漏改。
**修复**：类注释改"JSON 序列化选项共用"。
**可沉淀规则**：`重构移动成员后必须同步更新两侧类注释` → 【AI 审查清单】

---

## G. 性能与边界 — 2 条

#### C27 · info · R7-3
**位置**：`src/GuardAI.AI/Review/DiffCollector.cs:151`
**问题**：SplitDiffSegments 对每段两次 `ToString().Trim()`，60k 级 diff 逐文件全量双份分配。
**根因**：热循环里无意的重复物化。
**修复**：只物化一次 `var text = current.ToString()`。
**可沉淀规则**：`循环内禁止重复 ToString/全量复制；热路径先物化再判断` → 【AI 审查清单】

#### C39 · warn · R10-3
**位置**：`src/GuardAI.AI/Config/AIConfigParser.cs:115`
**问题**：`value < 0 || value > 2` 拦不住 NaN/Infinity——NaN 与任何值比较均为 false，非法 temperature 直透请求体。
**根因**：浮点范围校验的隐性漏洞。
**修复**：范围判断前加 `double.IsFinite(value)`。
**可沉淀规则**：`浮点范围校验必须先 IsFinite 再比大小` → 【本地检查】+【提示词规则】

---

## 附：级别与类别速查

| 类别 | 条数 | warn | info | 案例号 |
|---|---|---|---|---|
| A 安全 | 1 | 1 | 0 | C22 |
| B 异常契约与报错准确性 | 11 | 7 | 4 | C08 C12 C13 C23 C26 C31 C32 C33 C37 C38 C42 |
| C 并发取消与进程 | 5 | 3 | 2 | C04 C16 C17 C25 C43 |
| D 单一事实来源与复用 | 11 | 4 | 7 | C02 C03 C05 C06 C07 C10 C18 C19 C28 C36 C40 |
| E 命名文件与结构 | 8 | 1 | 7 | C01 C11 C14 C15 C20 C21 C30 C35 |
| F 注释与实现一致 | 5 | 0 | 5 | C09 C24 C29 C34 C41 |
| G 性能与边界 | 2 | 1 | 1 | C27 C39 |
