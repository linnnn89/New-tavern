# TavernDesk

TavernDesk 是面向 Windows 10/11 的本地酒馆角色聊天客户端，使用 .NET 10、C#、WPF 和 SQLite 构建。当前名称是开发代号，发布前可整体替换。

项目采用 clean-room 实现：OMate Pro APK 的静态逆向报告只用于确认功能边界、兼容目标和风险，不复用、翻译或移植其 Dart AOT/汇编实现。

## 当前状态

当前已经形成角色卡导入、角色书架、多会话聊天、云端 OpenAI-compatible API、Grok CLI 订阅后端、长期记忆、群聊、本地检索和独立跑团的可运行闭环，但尚不等同于SillyTavern 的全部功能。

当前“跑团记忆升级版”已经完成 R2-B 跑团上下文预算、发送前分项估算、低频 GM/Public 长期记忆和每局独立 ON/OFF 开关。跑团预览与实际请求由同一个 Planner 生成，默认单次请求容量为 15,000 tokens（输入与输出预留合计）。

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
- 聊天列表和角色主页中的会话行支持右键“删除整个聊天记录”；删除会物理清理该会话的消息、候选回复、JSONL/检索缓存和会话级工作流数据，但不删除角色卡、角色共享长期记忆或其他会话；
- 支持消息原位编辑、候选回复、重新生成、从任意消息复制独立分支；
- 删除消息时可选择“仅当前”或“当前及后续”；最终确认后立即永久删除，不提供回收或恢复；
- 对话泡右键或气泡外右下角实心 `+` 打开按稳定消息 ID 锚定的悬浮工具条；
- 支持气泡与小说两种显示模式；
- 仪表盘最近会话可精确跳转到对应会话；
- 具体会话可从右键菜单打开独立聊天窗口；各窗口选择状态独立，但共享应用级后台生成；
- 同一 Windows 桌面会话只允许一个 TavernDesk 应用实例；重复运行根目录入口或直接运行应用 EXE 时，第二个应用进程在访问数据库和 Provider 前提示“已经在运行”并退出。主窗口内打开的独立聊天等子窗口仍属于首个应用进程，不受此限制；
- 只要程序未退出，切换页面、最小化、打开设置或关闭非主窗口都不会取消当前流；重开独立窗口可重新附着流；
- 主窗口、独立聊天窗口和消息编辑器分别保存最后使用尺寸。

### Provider 与上下文

- Provider 页面默认预置 Grok CLI（订阅登录）、OpenRouter、硅基流动、DeepSeek 官方 API 和本地 LM Studio 五项；“添加”入口可新建自定义 OpenAI-compatible API 提供商。升级数据根会补齐缺失的默认项，使用未实现适配器的旧记录会停用并保留在数据库中；
- API 类接入统一使用已实现的 OpenAI-compatible 适配器，另一可选适配器仅为 Grok CLI；不提供未实现的 Anthropic、Google、Ollama 等协议。自定义 API 基地址填写到 `/api/v1` 或 `/v1` 结束，不要加入 `/chat`，实际聊天网关会补全 `/chat/completions`；
- LM Studio 默认地址为 `http://127.0.0.1:6543`，不硬编码模型；本机切换模型后主动刷新模型目录并重新选择或分配即可；
- API Key 独立保存在数据根的 `secrets/`，使用 Windows DPAPI 当前用户范围保护；SQLite 只保存随机文件引用；
- 接入商可从列表右键删除；删除时同步清除其本地模型目录、功能分配和 TavernDesk 保存的 Key，已删除的默认项不会在下次启动时自动恢复；
- 模型目录只在用户主动刷新时请求；先选供应商，再搜索模型并设置功能分配；五项生成类功能分别保存并恢复自己的 Provider、模型、上下文上限、最大输出、temperature 和 top_p，即使使用同一模型也不会用模型目录默认值覆盖功能专属参数；另设平行的“Embedding 向量化”分配，只保存 Provider 与模型，不显示或使用生成参数。目录统一收录普通模型目录和可用的专用 Embedding 目录，目录元数据不决定请求接口；右侧可输入任意模型 ID 或名称保存到本地目录，不发起网络请求。实际执行 Embedding 功能时发送 `/v1/embeddings`，聊天类功能发送 `/v1/chat/completions`；
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
- 记忆更新、压缩和按对话次数触发都先生成可编辑草稿，自动更新默认开启；用户保存后才覆盖正文并推进检查点，绝不会自动覆盖正文；
- 每个群聊 ID 拥有独立记忆；群聊分支复制成员和接力设置，但不复制旧群聊记忆；
- 群聊支持手动、固定顺序、最后一句 `@角色名` 和随机接力；
- 个人聊天隐藏“群聊”标签，只有当前会话确为群聊时才显示群聊设置；
- 识别 `@USER` 或当前 Persona 名后暂停自动聊天，等待用户回复；
- 群聊记忆可与指定角色记忆生成合并草稿，遵循“角色本体记忆为主、群聊记忆为辅”；
- 记忆更新、记忆压缩和群聊记忆合并各自只有一份可编辑全局提示词，不再提供第二份 User 模板或角色/群聊局部覆盖；记忆更新与压缩提示词未自定义时自动跟随当前内置默认，恢复内置默认会取消显式覆盖；旧记忆、新消息、目标 tokens 等运行资料由程序构造固定数据载荷，群聊接力仍可追加当前群聊专属规则；
- 安全宏支持变量、日期时间、确定性 pick/random 和骰子表达式，未知宏原样保留；
- 世界书支持 constant/selective、关键词逻辑、大小写/整词/正则、递归、概率、互斥组、顺序和 at-depth；
- 世界书导入后形成独立本地工作副本，词条名可编辑保存；原始来源文件不改写，标题变更后需手动重建 Embedding 索引才会同步到 FTS/向量索引；
- 预设按 global → character → conversation 叠加并提供来源诊断；
- SQLite FTS5 trigram 支持中文消息召回及独立 Token 预算；
- SillyTavern 聊天 JSONL 可导入、编辑、导出和再次导入，并保留未知字段与候选回复。

### 独立跑团

- 左侧“跑团”是独立数据域，不复用普通聊天消息或记忆表；可从剧本库开新局、继续已保存跑团，或基于同一剧本另开互不影响的一局；
- 剧本卡使用独立结构化模板保存简介、世界观、公开规则、GM 主持指令、开场设置、开场旁白和原始档案；导入角色卡式剧本时，`first_mes` 不再作为独立剧本字段保存或显示，`mes_example` 只进入历史档案，均不会冒充当前跑团消息；
- 剧本库支持“新建剧本”问卷式编辑，可逐项填写结构说明并在保存时选择多个现有世界书；世界书页面也能在单独的“跑团剧本绑定”行中批量绑定或解绑剧本，双向操作保持一致；
- 起始大厅统一设置流程预设、AI/USER GM、USER Persona、上下文预算、世界与规则、0–4 名 AI 玩家、每席模型，以及是否一次性导入普通聊天记忆或原世界知识；角色勾选即增加席位、取消勾选即减少席位，`1 GM + USER + 0 AI` 可以直接开局；
- 点击“开始游戏”后保存角色、记忆、世界与模型路由快照；书架角色和普通记忆后续修改不会污染已开始跑团，跑团内容也不回写普通聊天；
- 支持协作圆桌、秘密同投、严格先攻；每个席位有独立的排队、流式、完成、失败或中止缓存，可单独重试，失败正文不会进入 GM 上下文；
- 支持 AI GM、USER GM 和“裁判下场踢球了”兼任模式；USER/AI 的每条完整玩家行动在同一事件内自动附带可信 `1d20`，额外 `NdM±K` 公开掷骰保留为独立工具；
- GM 与 AI 玩家职责使用可编辑的全局提示词，并在跑团大厅提供直达入口；GM 提示词未自定义时自动跟随当前内置默认，恢复内置默认会取消显式覆盖；玩家请求按事件生命周期区分已裁定共同历史、最新 GM 行动依据和本轮待裁定席位内容，公开台词与行动意图可以被同席玩家感知或回应，但成败、观察结论和世界影响必须等待 GM 裁定；AI GM 必须以 `【下一轮评定参考】` 收尾，否则留存失败原文但不推进回合；
- GM 和 AI 玩家请求统一通过 `ICampaignContextPlanner` 生成；跑团右侧预览与 `CampaignRunner` 实际发送使用同一份 `CampaignContextPlan.Messages`，Planner 本身不调用 Provider、不写数据库、不更新记忆；
- 每局默认 `ContextTokenBudget = 15000`，表示单个 GM 或 AI 玩家请求的“输入上下文 + 输出预留”容量，不是整轮所有请求的合计。有效容量取本局预算与模型 ContextLimit 的较小值；
- 上下文优先保留系统规则、身份与席位、角色/剧本/世界资料、最新 GM 场景和当前回合行动；GM/Public 长期记忆与较旧历史使用剩余预算。预算不足时只按事件整体省略较旧历史；固定资料或当前回合本身超限时阻止对应生成并显示原因；
- 跑团游玩页右侧提供默认折叠的“本轮上下文估算”，按当前阶段显示各 AI 席位或 GM 的输入、输出预留、容量、分区明细及历史裁剪/超限状态。预估不会调用 Provider、Embedding 或记忆模型；
- GM/Public 跑团记忆只在成功锁定 GM 裁定后检查。默认累计 3 个完整轮次，或上次 checkpoint 后已裁定事件达到约 4,000 tokens 时更新；玩家行动、单独掷骰、打开页面、重新载入和切换模型不会触发记忆模型；
- 记忆更新严格截止到最新成功的 `GmResolution`，不会吸入下一轮尚未裁定的玩家行动。旧跑团没有 bank/checkpoint 时，打开页面不会自动产生 API 调用，可由用户从最新已裁定历史手动建立；
- 每个跑团右上角有独立保存的“记忆 ON/OFF”胶囊开关。OFF 时不调用记忆总结模型，也不向 GM/AI 玩家请求注入 GM/Public 长期记忆或其结构包装；角色卡、规则、当前行动和近期原始历史仍保留。OFF 不删除既有 bank/checkpoint，重新 ON 后从原 checkpoint 继续，并不会因切换开关立即追溯调用模型；
- 已保存跑团列表支持对单局右键永久删除，二次确认后级联清除该局席位与事件，不删除剧本卡、角色卡或其他跑团；
- GM 请求把“已裁定历史”和“本轮待裁定行动”分区发送；本轮玩家已经提交的公开台词或表达不由 GM 重演，但行动成败、观察是否成立及其对 NPC、环境和世界的影响仍由 GM 在本轮首次确认；
- 游戏途中可单独更换任一 AI 玩家或 GM 的 Provider/模型；变更作为审计事件保存，不改写既有事件；
- 应用重启后可继续跑团；启动时遗留在排队或流式状态的旧生成会收尾为“已中断 / 进程退出”，保留已持久化内容并等待用户显式重试，不自动再次调用模型；公共、单席私有和 GM-only 事件按查询边界隔离，秘密同投的本轮行动及自动骰不会提前泄露，GM 裁定完成进入下一轮后再一起揭示。

### 本地数据可靠性

- SQLite 当前 schema 为 v17，按版本执行事务化顺序迁移；失败回滚，并拒绝以旧软件打开更高版本数据库。v15 增加跑团 GM/Public 记忆银行及事件检查点，v16 增加本局上下文预算、记忆轮次间隔和待处理 Token 阈值，v17 增加每局 `MemoryEnabled`；旧跑团迁移后默认 ON，不删除已有记忆或检查点；
- 普通聊天角色记忆银行自动更新默认开启：每新增 20 个用户轮次触发一次，单次最多发送 20 个用户轮次（含对应角色回复），并默认只发送自上次检查点后的新增对话；更新仍先生成草稿，必须人工保存才会覆盖正文并推进检查点；
- 消息使用会话内稳定 `sequence_no` 排序，删除后续消息、复制分支、候选回复和上下文组装不依赖 SQLite `rowid`；
- 角色卡和剧本卡导入都使用数据根内的工作副本，不修改原文件；损坏的聊天归档会在数据库写入前拒绝；
- 跑团核心使用 `campaign_scenarios`、`campaigns`、`campaign_participants` 和 `campaign_events`，GM/Public 长期记忆与边界分别保存在 `campaign_memory_banks` 和 `campaign_memory_checkpoints`；操作 ID、终态守卫和乐观版本共同防止重试、迟到分片或并发收尾重复生效。

## 工程结构

```text
app/                         明确发布时生成的 Windows 运行快照，不是源码
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
  TavernDesk-R2-B-Campaign-Context-Budget.md
                              R2-B 预算、低频记忆、ON/OFF 与验证记录
  codex_worklog.md            按时间追加的实施和验证记录
  handoff.md                  新对话优先读取的当前快照与接手顺序
tools/
  TavernDesk.RootLauncher.cs  根目录 EXE 的最小启动器源码
```

### 跑团 R2-B 核心链路

```text
CampaignEvent / CampaignMemory / CharacterSnapshot
                         │
                         ▼
              CampaignContextPlanner
                         │
             CampaignContextPlan
                 ┌───────┴────────┐
                 ▼                ▼
       跑团右侧 Token 预览   CampaignRunner 实际请求

成功锁定 GmResolution
           │
           ▼
CampaignMemoryUpdateService
           │
           ▼
GM/Public bank + checkpoint
```

`src/` 是唯一源码基准。`CampaignContextPlanner` 负责组装、预算分配、历史选择和 Token 估算；`CampaignRunner` 只消费计划并调用 Provider；`CampaignsViewModel` 展示同一计划的分区结果；`CampaignMemoryUpdateService` 只在权威 GM 裁定边界处理长期记忆。

## 构建与运行

要求：

- Windows 10/11 x64；
- .NET SDK `10.0.302` 或兼容的 .NET 10 补丁版本；
- NuGet 包缓存使用项目目录 `.packages/`。

```powershell
cd "<TavernDesk 仓库目录>"
dotnet restore TavernDesk.sln
dotnet build TavernDesk.sln -c Debug --no-restore
dotnet run --project src\TavernDesk.App\TavernDesk.App.csproj --no-build
```

当前根目录已经生成 `TavernDesk.exe`。它是面向用户的薄启动器，只启动确定性发布目录 `app` 中的自包含版本：

```powershell
.\TavernDesk.exe
```

该文件不包含应用代码和 DLL；它只启动
`app\TavernDesk.App.exe`。如果完整 `app` 发布目录不存在，启动器会显示可操作的提示。

仓库中的 `app\TavernDesk.App.exe` 是明确发布时生成的确定性运行快照，可直接运行：

```powershell
.\app\TavernDesk.App.exe
```

当前 `app/` 是 `win-x64` 自包含发布，不要求目标设备预先安装 .NET 10；日常构建只更新 `src/**/bin`，不会自动同步 `app/`。只有准备更新确定性发布快照时才重新执行下面的 `dotnet publish`。不要编辑 `app/` 内的 DLL，也不要把它反向同步回 `src/`。

更新确定性 `app/` 发布目录：

```powershell
dotnet publish src\TavernDesk.App\TavernDesk.App.csproj `
  -c Release --no-restore -r win-x64 --self-contained true `
  -p:PublishSingleFile=false `
  -p:DebugType=None `
  -o app
```

发布目录必须保留 EXE、DLL、`coreclr.dll`、`hostfxr.dll`、WPF 运行库和 `runtimes/` 等文件；不能只复制 `TavernDesk.App.exe`。

指定独立数据根：

```powershell
dotnet run --project src\TavernDesk.App\TavernDesk.App.csproj --no-build -- --data-root "D:\TavernDeskData"
```

也可设置环境变量 `TAVERNDESK_DATA_ROOT`；命令行参数优先。未指定时，数据根为当前用户“文档”目录下的 `TavernDesk`。数据库、附件、角色卡、导出、Grok CLI 隔离工作目录和 DPAPI 密钥文件都分目录放在该数据根内。

集中验证：

```powershell
dotnet test TavernDesk.sln -c Release --no-restore
dotnet build TavernDesk.sln -c Release --no-restore
```

项目内存储自检不会连接 API：

```powershell
dotnet run --project src\TavernDesk.AgentHost\TavernDesk.AgentHost.csproj --no-build -- --storage-smoke ".\user-data\verification-local"
```

最近完整基线（2026-08-06）：完整并行 Release 测试 `149/149` 通过，标准 Release 构建为 0 个警告、0 个错误，`git diff --check` 通过。覆盖 schema v17、R2-B Planner 与 Runner 消息一致性、固定上下文阻断、旧历史裁剪、GM/Public 可见性、低频记忆阈值、每局 ON/OFF、旧跑团手动建立、失败重试以及并行 SQLite 测试清理。本轮自动化验证未读取 API Key、刷新远端模型目录或调用真实 Provider。

## 稳定产品约束

- Windows 原生桌面软件，不要求全部装入单文件 EXE；
- 同一 Windows 桌面会话只运行一个 TavernDesk 应用进程；独立聊天等子窗口必须由现有主进程创建；
- 默认浅色皮肤；“界面设置”提供聊天自动滚动、系统字体和字号，保留与业务逻辑解耦的 Skin 扩展边界，暂不开发动态主题；
- 默认数据根位于“文档”目录，并允许用户整体修改；
- API Key 不进入应用导出的备份，模型列表不自动联网刷新；
- 普通聊天上下文超限时阻止发送；跑团只允许按预算省略较旧事件历史，固定资料、身份和当前回合内容不自动压缩或删除，仍然超限时阻止对应生成；
- 修改角色设定只影响后续上下文，不改写既有消息；
- 分支是完全独立的会话副本，不共享消息节点；
- 消息删除在明确范围和最终确认后立即永久生效，不提供回收、恢复或隐藏的软删除入口；
- 普通聊天记忆先生成草稿并由用户确认保存；跑团 GM/Public 记忆按权威 GM 裁定和本局阈值自动更新，并由每局 ON/OFF 开关控制；
- Agent/MCP 只服务酒馆聊天，不建设代码工程 Agent；
- 不内置自动更新器，不注册文件关联，不使用 Windows 系统通知。

## 当前边界

尚未完成或尚未充分验证：

- 非 Tiktoken 模型家族的本地 tokenizer 和自动上下文压缩；
- 世界书之外的通用向量知识库、后台增量更新和检索缓存；
- Anthropic 和 Gemini 原生协议；
- Grok CLI 的模型枚举目前使用“当前默认模型”占位；真实订阅登录、生成和取消需在用户桌面会话内验收；
- Grok CLI 自身会按官方行为把 ACP 会话保存在 `~/.grok/sessions`；TavernDesk 不自动删除用户的 Grok CLI 会话；
- 图片/普通文件附件、语音和 TTS；
- MCP、有限聊天工具及权限交互；
- 强制结束进程后的流式断点续传；当前只将遗留请求安全收尾为可重试终态，不续传、不自动重试；
- 大型真实数据库迁移、长时间压力、DPI/键盘无障碍和长列表性能基线；
- R2-B 已通过自动化回归和界面截图检查，但多人真实长局、不同 Provider 的实际 Token usage、三轮/4,000-token 自动更新触发和 ON/OFF 成本差异仍需用户桌面实测；
- 跑团的角色属性、背包、地图、战斗棋盘、规则 DSL、战役分支/回滚和跨设备同步；
- SillyTavern 插件私有扩展及所有第三方 Provider 变体。
- DeepSeek 推理目前只提供明确的 OFF/ON，不增加 `low`/`high`/`max` 强度档位；真实使用证明需要时再扩展。

未来原生 Provider 适配器必须在 Infrastructure 内转换为统一流事件，不能把服务商条件分支带入聊天 ViewModel。

## 后续候选

- OpenRouter、硅基流动、DeepSeek 官方 API、`127.0.0.1:6543` LM Studio 与 Grok CLI 的真实短链路、取消行为和更完整错误矩阵；
- 以真实长局验证跑团上下文预算、不同 Provider 的并发限制和异常恢复；只有出现实际需求时再考虑持久化回合 barrier；
- 按真实需求扩展非 Tiktoken 模型家族、可配置上下文压缩和结构化记忆；
- 世界书之外的通用 embedding 知识库；
- 群聊成员动态增删/排序和每角色模型覆盖；
- 图片与普通文件附件；
- 仅服务聊天的授权文件夹、网页/API、知识检索、有限命令和 MCP。

后续工作开始前请依次阅读：

1. [当前交接摘要](docs/handoff.md)；
2. 本 README 的状态与边界；
3. [架构约束](docs/architecture.md)；
4. 涉及跑团时再读 [跑团设计](docs/campaign_mode_design.md)；
5. 涉及 R2-B 时再读 [跑团上下文预算与低频记忆实施方案](docs/TavernDesk-R2-B-Campaign-Context-Budget.md)；
6. [工作日志](docs/codex_worklog.md) 的最新记录。
