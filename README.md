# TavernDesk

TavernDesk 是面向 Windows 10/11 的本地酒馆角色聊天客户端，使用 .NET 10、C#、WPF 和 SQLite 构建。当前名称是开发代号，发布前可整体替换。

项目采用 clean-room 实现：OMate Pro APK 的静态逆向报告只用于确认功能边界、兼容目标和风险，不复用、翻译或移植其 Dart AOT/汇编实现。

## 当前状态

当前已经形成角色卡导入、角色书架、多会话聊天、云端 OpenAI-compatible API、Grok CLI 订阅后端、长期记忆、群聊、本地检索和独立跑团的可运行闭环，但尚不等同于SillyTavern 的全部功能。

### 角色卡与书架

- 导入和同容器导出 PNG `ccv3`/`chara`、JSON V1/V2/V3、CHARX；
- round-trip 保留未知 JSON 字段、PNG 非角色 chunk 和 CHARX 非 `card.json` 资源；
- 提取 PNG/CHARX 竖向封面，支持密集、中等、大图三档排列、过滤和自定义书架；自定义书架点击小“批量”按钮后才显示多选入口，可批量移出角色但不会删除角色卡和聊天记录；
- 卡片悬停提供“聊天/编辑”快捷入口；“进入”按被点击的角色 ID 打开角色主页，“编辑”则直接进入同一二级页的编辑态，不再依赖默认选中角色；
- 角色主页集中提供编辑、归类、删除和替换图片，并按更新时间列出该角色的全部聊天；末次发言保留完整文本，由界面随窗口宽度动态省略；
- 书架目录与角色主页分开管理：每次进入都会创建只属于该角色的短期详情会话，独立持有正式资料、编辑草稿、聊天列表和读取任务；返回书架时取消旧读取并销毁该会话，迟到结果不能回写后来打开的角色；
- 编辑角色描述、性格、场景、首次发言、示例、System/Post-history、depth prompt、作者信息、标签和内嵌世界书；
- 备选开场白按数组元素独立编辑，可删除或追加；保存角色设定不会修改既有聊天。

### 聊天与窗口

- 聊天导航采用“角色 → 多次独立会话”两级结构，并按最后聊天时间倒序；
- 支持消息原位编辑、候选回复、重新生成、从任意消息复制独立分支；
- 删除消息时可选择“仅当前”或“当前及后续”；最终确认后立即永久删除，不提供回收或恢复；
- 对话泡右键或气泡外右下角实心 `+` 打开按稳定消息 ID 锚定的悬浮工具条；
- 支持气泡与小说两种显示模式；
- 仪表盘最近会话可精确跳转到对应会话；
- 具体会话可从右键菜单打开独立聊天窗口；各窗口选择状态独立，但共享应用级后台生成；
- 只要程序未退出，切换页面、最小化、打开设置或关闭非主窗口都不会取消当前流；重开独立窗口可重新附着流；
- 主窗口、独立聊天窗口和消息编辑器分别保存最后使用尺寸。

### Provider 与上下文

- Provider 页面只保留 Grok CLI（订阅登录）、OpenRouter、硅基流动、DeepSeek 官方 API 和本地 LM Studio 五项；升级数据根会补齐缺失项，旧/自定义记录为避免静默销毁密钥和模型分配而停用并保留在数据库中，但不再显示；
- API 类接入统一使用已实现的 OpenAI-compatible 适配器，另一可选适配器仅为 Grok CLI；不再提供未实现的 OpenAI、Anthropic、Google、Ollama、Custom 枚举或新增自定义接入商入口；
- LM Studio 默认地址为 `http://127.0.0.1:6543`，不硬编码模型；本机切换模型后主动刷新模型目录并重新选择或分配即可；
- API Key 独立保存在数据根的 `secrets/`，使用 Windows DPAPI 当前用户范围保护；SQLite 只保存随机文件引用；
- 接入商可从列表右键删除；删除时同步清除其本地模型目录、功能分配和 TavernDesk 保存的 Key，已删除的默认项不会在下次启动时自动恢复；
- 模型目录只在用户主动刷新时请求；先选供应商，再搜索模型并设置上下文/输出上限和功能分配；
- 功能分配总览中的 OpenRouter DeepSeek 模型可展开快速设置，以 OFF/ON 胶囊显式关闭或开启推理；模型 ID 必须包含完整 `deepseek` 词段，其他模型和接入商不会收到该参数；
- OpenAI Chat Completions 兼容适配器支持 SSE 和非流式 JSON，裸服务根地址自动补全 `/v1`；
- OpenRouter、硅基流动、DeepSeek 和 LM Studio 模型目录中的上下文与最大输出元数据会在存在时写入本地目录；常见鉴权、额度、限流和上游错误会转换为可操作提示；
- Grok CLI 通过官方 ACP `agent stdio` 接入，使用本机 `grok login` 的订阅凭据，不读取 TavernDesk API Key；每次生成使用新会话和独立工作目录，不向 CLI 开放终端、文件、MCP、网页、子代理或跨会话记忆；
- 上下文按缓存友好的顺序统一组装：精炼全局规则、USER Persona、角色卡、世界资料和长期记忆在前，原生 role 历史按序追加，检索结果、post-history 与群聊接力等当轮内容靠后，当前用户原文最后；不再插入会随轮次移动的历史起止或当前输入标记，世界书仍尊重既有 before/after/depth 语义；
- USER Persona 以独立 system 分区说明“USER 正在扮演谁”；单聊作者由原生 `role` 区分，群聊历史使用单行 JSON 的 `speaker.kind/name` 与 `content` 分离作者和正文，正文中的姓名、冒号或伪标题不能改变记录作者；
- 发送前显示与真实 Provider 内容一致的 Token 估算和 API 消息结构；GPT-5/4.1/4o/o 系列及 GPT-4/3.5 使用随发布产物内置的本地 Tiktoken 词表，其他模型保持启发式回退；消息封装仍可能因服务实现而异，因此统一标记为非精确值；超限时阻止发送，不自动截断；
- OpenRouter 聊天与跑团席位使用稳定的会话级 `session_id`；服务返回后显示输入缓存命中/未命中 Token，便于用真实 usage 判断缓存是否生效；
- reasoning/thinking 在 Provider 边界归一化为 `Reasoning`、`Content`、`Completed` 事件，原始思考不进入气泡、候选、记忆或数据库；
- thinking 识别采用结构化语义字段、受控的 `reasoning*`/`thinking*` 字段变体，以及响应开头的 `<think>`、`<thinking>`、`<analysis>` 成对标签兜底；
- 思考阶段只显示临时“正在思考中”波浪提示，正文首片段到达即隐藏；
- 不同会话可并行流式生成，同一会话拒绝重叠生成；停止操作可只影响当前会话，顶栏红色按钮可中止 TavernDesk 当前登记的全部聊天、记忆与跑团请求；
- 生成后单独显示服务返回的 usage、reasoning tokens 和完成原因。
- “设置 → 提示词管理”集中编辑聊天、记忆银行、群聊、跑团 GM 与 AI 玩家职责提示词；每项可恢复内置默认，整套配置可另存为 JSON，各业务模块提供直达入口并显示配置生效范围；
- 全局聊天提示词内置精炼 RP 合同：明确资料用途、当前角色与 USER Persona 的区别、同语言回复、不替 USER/其他角色行动，以及只输出最终正文、不泄露思考过程；具体人物资料继续由角色卡、世界书、记忆和历史承担，个人聊天显示“角色提示词”标签，可直接修改角色卡原有字段，不会另存第二份局部配置；

### 记忆、群聊与兼容语义

- 每个角色有一份跨会话共享、可直接编辑的记忆银行；
- 记忆更新、压缩和按对话次数触发都先生成可编辑草稿，用户保存后才覆盖正文并推进检查点；
- 每个群聊 ID 拥有独立记忆；群聊分支复制成员和接力设置，但不复制旧群聊记忆；
- 群聊支持手动、固定顺序、最后一句 `@角色名` 和随机接力；
- 个人聊天隐藏“群聊”标签，只有当前会话确为群聊时才显示群聊设置；
- 识别 `@USER` 或当前 Persona 名后暂停自动聊天，等待用户回复；
- 群聊记忆可与指定角色记忆生成合并草稿，遵循“角色本体记忆为主、群聊记忆为辅”；
- 记忆更新、记忆压缩和群聊记忆合并各自只有一份可编辑全局提示词，不再提供第二份 User 模板或角色/群聊局部覆盖；旧记忆、新消息、目标 tokens 等运行资料由程序构造固定数据载荷，群聊接力仍可追加当前群聊专属规则；
- 安全宏支持变量、日期时间、确定性 pick/random 和骰子表达式，未知宏原样保留；
- 世界书支持 constant/selective、关键词逻辑、大小写/整词/正则、递归、概率、互斥组、顺序和 at-depth；
- 预设按 global → character → conversation 叠加并提供来源诊断；
- SQLite FTS5 trigram 支持中文消息召回及独立 Token 预算；
- SillyTavern 聊天 JSONL 可导入、编辑、导出和再次导入，并保留未知字段与候选回复。

### 独立跑团

- 左侧“跑团”是独立数据域，不复用普通聊天消息或记忆表；可从剧本库开新局、继续已保存跑团，或基于同一剧本另开互不影响的一局；
- 剧本卡使用独立结构化模板保存世界观、公开规则、大厅说明、GM 开场和原始档案；导入角色卡式剧本时，`first_mes` 只进入大厅说明，`mes_example` 只进入历史档案，均不会冒充当前跑团消息；
- 起始大厅统一设置流程预设、AI/USER GM、USER Persona、上下文预算、世界与规则、0–4 名 AI 玩家、每席模型，以及是否一次性导入普通聊天记忆或原世界知识；角色勾选即增加席位、取消勾选即减少席位，`1 GM + USER + 0 AI` 可以直接开局；
- 点击“开始游戏”后保存角色、记忆、世界与模型路由快照；书架角色和普通记忆后续修改不会污染已开始跑团，跑团内容也不回写普通聊天；
- 支持协作圆桌、秘密同投、严格先攻；每个席位有独立的排队、流式、完成、失败或中止缓存，可单独重试，失败正文不会进入 GM 上下文；
- 支持 AI GM、USER GM 和“裁判下场踢球了”兼任模式；USER/AI 的每条完整玩家行动在同一事件内自动附带可信 `1d20`，额外 `NdM±K` 公开掷骰保留为独立工具；
- GM 与 AI 玩家职责使用可编辑的全局提示词，并在跑团大厅提供直达入口；运行时强制补入玩家自主权、灵活骰点解释和席位所有权协议，AI GM 必须以 `【下一轮评定参考】` 收尾，否则留存失败原文但不推进回合；
- 游戏途中可单独更换任一 AI 玩家或 GM 的 Provider/模型；变更作为审计事件保存，不改写既有事件；
- 应用重启后可继续跑团；公共、单席私有和 GM-only 事件按查询边界隔离，秘密同投的本轮行动及自动骰不会提前泄露，GM 裁定完成进入下一轮后再一起揭示。

### 本地数据可靠性

- SQLite 当前 schema 为 v10，按版本执行事务化顺序迁移；失败回滚，并拒绝以旧软件打开更高版本数据库；v10 会永久清理旧版本回收箱遗留消息；
- 消息使用会话内稳定 `sequence_no` 排序，删除后续消息、复制分支、候选回复和上下文组装不依赖 SQLite `rowid`；
- 角色卡和剧本卡导入都使用数据根内的工作副本，不修改原文件；损坏的聊天归档会在数据库写入前拒绝；
- 跑团使用 `campaign_scenarios`、`campaigns`、`campaign_participants` 和 `campaign_events` 四张独立表；操作 ID、终态守卫和乐观版本共同防止重试、迟到分片或并发收尾重复生效。

## 工程结构

```text
src/
  TavernDesk.App/             WPF 界面、窗口和 ViewModel
  TavernDesk.Core/            领域模型与稳定接口
  TavernDesk.Infrastructure/  SQLite、角色卡、Provider、检索和本地服务
  TavernDesk.AgentHost/       隔离的命令行自检入口
tests/
  TavernDesk.Tests/           集中自动化测试
docs/
  architecture.md             模块、数据与生命周期约束
  campaign_mode_design.md     独立跑团的已确认规则、R1 边界与后续证据门槛
  codex_worklog.md            按时间追加的实施和验证记录
  handoff.md                  新对话优先读取的当前快照与接手顺序
tools/
  TavernDesk.RootLauncher.cs  根目录 EXE 的最小启动器源码
```

## 构建与运行

要求：

- Windows 10/11 x64；
- .NET SDK `10.0.302` 或兼容的 .NET 10 补丁版本；
- NuGet 包缓存使用项目目录 `.packages/`。

```powershell
cd "D:\Documents\女主角搜索器\TavernDesk"
dotnet restore TavernDesk.sln
dotnet build TavernDesk.sln -c Debug --no-restore
dotnet run --project src\TavernDesk.App\TavernDesk.App.csproj --no-build
```

当前根目录已经生成 `TavernDesk.exe`。完成 Release 构建后，可以直接双击它，或运行：

```powershell
.\TavernDesk.exe
```

该文件只是 5 KB 的入口，不复制应用代码或 DLL；它启动
`src\TavernDesk.App\bin\Release\net10.0-windows\TavernDesk.App.exe`。
如果 Release 输出尚不存在，启动器会显示需要执行的构建命令。

指定独立数据根：

```powershell
dotnet run --project src\TavernDesk.App\TavernDesk.App.csproj --no-build -- --data-root "D:\TavernDeskData"
```

也可设置环境变量 `TAVERNDESK_DATA_ROOT`；命令行参数优先。未指定时，数据根为当前用户“文档”目录下的 `TavernDesk`。数据库、附件、角色卡、导出、Grok CLI 隔离工作目录和 DPAPI 密钥文件都分目录放在该数据根内。

集中验证：

```powershell
dotnet test TavernDesk.sln -c Debug --no-restore
dotnet build TavernDesk.sln -c Release --no-restore
```

项目内存储自检不会连接 API：

```powershell
dotnet run --project src\TavernDesk.AgentHost\TavernDesk.AgentHost.csproj --no-build -- --storage-smoke ".\user-data\verification-local"
```

最近完整基线（2026-08-03）：当前 Release 自动化测试 `93/93` 通过；回收箱孤立源码清理后的隔离 Release 干净构建为 0 个警告、0 个错误。根目录 `TavernDesk.exe --probe` 退出码 0 和隔离 WPF 视觉验证沿用同日上一基线，本轮未重复启动 GUI。验证没有读取 API Key、刷新模型目录或调用真实 Provider。

## 稳定产品约束

- Windows 原生桌面软件，不要求全部装入单文件 EXE；
- 默认浅色皮肤；保留与业务逻辑解耦的 Skin 扩展边界，暂不开发动态主题；
- 默认数据根位于“文档”目录，并允许用户整体修改；
- API Key 不进入应用导出的备份，模型列表不自动联网刷新；
- 上下文超限时阻止发送，不自动裁剪或截断；
- 修改角色设定只影响后续上下文，不改写既有消息；
- 分支是完全独立的会话副本，不共享消息节点；
- 消息删除在明确范围和最终确认后立即永久生效，不提供回收、恢复或隐藏的软删除入口；
- 记忆正文由用户确认保存，不由模型直接覆盖；
- Agent/MCP 只服务酒馆聊天，不建设代码工程 Agent；
- 不内置自动更新器，不注册文件关联，不使用 Windows 系统通知。

## 当前边界

尚未完成或尚未充分验证：

- 非 Tiktoken 模型家族的本地 tokenizer 和自动上下文压缩；
- 向量知识库与 embedding 召回；
- Anthropic 和 Gemini 原生协议；
- Grok CLI 的模型枚举目前使用“当前默认模型”占位；真实订阅登录、生成和取消需在用户桌面会话内验收；
- Grok CLI 自身会按官方行为把 ACP 会话保存在 `~/.grok/sessions`；TavernDesk 不自动删除用户的 Grok CLI 会话；
- 图片/普通文件附件、语音和 TTS；
- MCP、有限聊天工具及权限交互；
- 强制结束进程后的流式断点续传；
- 大型真实数据库迁移、长时间压力、DPI/键盘无障碍和长列表性能基线；
- 跑团的角色属性、背包、地图、战斗棋盘、规则 DSL、战役分支/回滚和跨设备同步；
- SillyTavern 插件私有扩展及所有第三方 Provider 变体。
- DeepSeek 推理目前只提供明确的 OFF/ON，不增加 `low`/`high`/`max` 强度档位；真实使用证明需要时再扩展。

未来原生 Provider 适配器必须在 Infrastructure 内转换为统一流事件，不能把服务商条件分支带入聊天 ViewModel。

## 后续候选

- OpenRouter、硅基流动、DeepSeek 官方 API、`127.0.0.1:6543` LM Studio 与 Grok CLI 的真实短链路、取消行为和更完整错误矩阵；
- 以真实长局验证跑团上下文预算、不同 Provider 的并发限制和异常恢复；只有出现实际需求时再考虑持久化回合 barrier；
- 按真实需求扩展非 Tiktoken 模型家族、可配置上下文压缩和结构化记忆；
- 可选 embedding 知识库；
- 群聊成员动态增删/排序和每角色模型覆盖；
- 图片与普通文件附件；
- 仅服务聊天的授权文件夹、网页/API、知识检索、有限命令和 MCP。

后续工作开始前请依次阅读：

1. [当前交接摘要](docs/handoff.md)；
2. 本 README 的状态与边界；
3. [架构约束](docs/architecture.md)；
4. 涉及跑团时再读 [跑团设计](docs/campaign_mode_design.md)；
5. [工作日志](docs/codex_worklog.md) 的最新记录。
