# TavernDesk

TavernDesk 是面向 Windows 10/11 x64 的本地优先角色聊天与独立跑团客户端，使用 .NET 10、C#、WPF 和 SQLite 构建。角色卡、普通聊天、群聊、世界书、记忆与跑团均保存在用户自己的数据目录中；普通聊天和跑团使用独立数据域，不会互相回写。

当前仓库包含可直接运行的 `win-x64` 自包含快照。普通用户不需要安装 .NET 10 Runtime，保留根目录 `TavernDesk.exe` 与完整 `app/` 目录后，双击根目录 EXE 即可启动。

## 当前状态

状态日期：2026-08-11。

- 应用闭环已完成：角色卡、单聊、群聊、长期记忆、世界书/RAG、Provider 管理和独立跑团均可在本地使用。
- 当前数据库 schema 为 v19；迁移在事务中执行，遇到未来版本数据库会拒绝降级打开。
- 主壳、仪表盘和聊天主工作区已采用 Appica 风格。聊天页是全高四栏结构：主导航、会话列表、对话正文和上下文检查器。
- 设置页支持“默认浅色 / 深炭黑”主题、80%–150% 应用内缩放，以及全局字体与 10–32 字号设置；主题与缩放均可即时预览并显式保存。
- 界面语言支持简体中文、台湾繁体中文、English 和日本語。新建个人数据时会先显示一次语言选择；已有数据库不会被强制弹窗，缺少语言设置时保持简体中文。
- 应用缩放或窗口宽度不足以安全显示四栏时，聊天会自动收起右侧上下文栏；只有自动收起的右栏会在空间恢复后自动展开，手动折叠状态保持不变。
- 根目录启动器指向 `app/TavernDesk.App.exe`。当前 `app/` 已同步本轮四语源码并通过根启动器探针，可直接从根目录 EXE 启动当前交付版本。
- 当前公开解决方案包含 App、Core、Infrastructure 和 AgentHost 四个项目，没有测试项目；因此当前分支的可信验证是 Release 构建、存储 smoke、启动探针和真实 WPF 界面检查，而不是空跑 `dotnet test`。

## 快速开始

### 直接运行

完整发布目录必须至少保持以下关系：

```text
TavernDesk.exe
app/
  TavernDesk.App.exe
  TavernDesk.App.dll
  TavernDesk.Core.dll
  TavernDesk.Infrastructure.dll
  *.runtimeconfig.json
  *.deps.json
  .NET / WPF 运行库
  runtimes/win-x64/native/e_sqlite3.dll
```

双击或从终端运行：

```powershell
.\TavernDesk.exe
```

`TavernDesk.exe` 是薄启动器，不包含完整应用。不要只复制根目录 EXE，也不要从发布目录中单独移动 DLL。

同一 Windows 桌面会话只允许一个 TavernDesk 主进程。独立聊天窗口由现有主进程创建，不是第二个应用实例。

### 用户数据目录

默认数据根为当前用户“文档”目录下的 `TavernDesk`。位置记录在：

```text
%LOCALAPPDATA%\TavernDesk\config.json
```

数据根包括：

```text
taverndesk.db
attachments/
character-cards/
campaign-scenario-cards/
exports/
grok-cli-runtime/
secrets/
```

可在设置 → 数据中迁移目录。迁移先写入目标同级暂存目录并使用 SQLite backup，全部成功后才切换；失败不会覆盖当前数据根。

开发或隔离验证也可覆盖数据根：

```powershell
dotnet run --project src\TavernDesk.App\TavernDesk.App.csproj -c Release --no-build -- --data-root "D:\TavernDeskData"

$env:TAVERNDESK_DATA_ROOT = "D:\TavernDeskData"
dotnet run --project src\TavernDesk.App\TavernDesk.App.csproj -c Release --no-build
```

优先级为 `--data-root` → `TAVERNDESK_DATA_ROOT` → 用户配置 → 默认目录。外部覆盖生效时，设置页不会改写持久化的数据根配置。

## 已实现能力

### 角色与书架

- 导入和同容器导出 PNG、JSON、CHARX 角色卡。
- 保留未知 JSON 节点、PNG 非角色卡数据块和 CHARX 附带资源，避免编辑时静默丢失原始数据。
- 支持头像、角色字段、备选开场白、system prompt、post-history instructions、元数据和内嵌世界书。
- 支持默认书架、自定义书架、搜索、排序、三档封面尺寸、归类和按需批量整理。
- 批量移出只删除书架成员关系，不删除角色卡或聊天。
- 角色编辑草稿与正式资料分离；切换角色、离开详情或迟到异步读取不会覆盖当前会话。

### 普通聊天与群聊

- 支持多会话、单聊、群聊、独立聊天窗口、流式正文、取消、继续生成和 `Ctrl+Enter` 发送。
- 支持消息原位编辑、多个候选、候选切换、重新生成、从指定消息建立独立分支和 JSONL round-trip。
- 会话加载批量读取候选，消息列表使用 Recycling 虚拟化；后台会话生成不会因页面切换或关闭独立窗口而中断。
- 同一会话拒绝重复生成，不同会话可并发；顶部停止入口统一管理聊天、记忆和跑团的已登记请求。
- 气泡模式中用户消息在右侧、角色消息在左侧；群聊按同一发送者规则显示。小说模式只改变展示，不改变消息、候选或上下文。
- 无头像会使用系统联系人图标兜底；Windows 10 自动回退到随系统提供的 Segoe MDL2 Assets，不要求额外安装 Windows 11 图标字体。
- 群聊支持成员顺序、自动接力、`@角色名`、`@USER`/Persona 暂停、自动轮次上限和独立群聊记忆。
- 删除消息或整个会话前会明确确认；确认后事务式物理删除，不提供回收箱或恢复入口。

### 上下文、记忆与世界书

- 上下文检查器显示 Token、实际发送分段、API 请求结构、本轮召回和组装诊断。
- USER Persona、角色卡、世界书、记忆、历史、检索、post-history 和当前输入按固定顺序组装；当前输入保持最后一条。
- 已知 OpenAI 模型使用随程序发布的 Tiktoken 词表进行本地估算，未知模型使用明确标注的启发式回退。上下文超限时阻止请求，不静默截断角色卡、世界资料或当前输入。
- 记忆更新、压缩和群聊记忆合并分别使用独立功能分配，每项只有一份全局职责模板。
- 自动阈值更新默认开启：达到设置的用户轮次数后保存记忆正文并推进检查点；手动更新、压缩和群聊合并仍先生成可编辑草稿。
- 记忆工作流默认每 20 个用户轮次触发，单次最多发送 20 个用户轮次，并默认只发送检查点后的新增对话；这些值可按 owner 保存。
- 世界书支持全局、角色和跑团剧本范围，constant/selective、递归、概率、互斥组、正则/整词和 at-depth 注入。
- 世界书检索使用 SQLite FTS5 与 Embedding 的确定性混合召回。预览不会触发远程 Embedding；索引重建由用户明确触发。

### 独立跑团

跑团不是“群聊加一个 GM 提示词”，而是独立剧本、战役、参与者快照、事件流和记忆域。

当前已落地：

- `1 GM + USER + 0–4 AI 玩家`，支持 AI GM、USER GM、USER 同时下场和纯观察模式。
- 协作圆桌、秘密同投、严格先攻三种流程预设，共用统一 `CampaignFlowEngine`，各自由小型策略决定可行动席位和推进方式。
- 开局冻结角色、Persona、可选普通记忆、世界规则、GM 指令、叙事权限和模型路由快照。
- 每个 GM 与 AI 席位可选择不同 Provider/模型，途中变更只影响后续请求并写入本地事件。
- 每条完整 USER/AI 行动在同一原子写入中附加可信 `1d20`；额外 `NdM±K` 骰子作为独立公开记录。
- 技术失败、取消、超限和输出异常保留为可重试终态，不自动重试、不自动跳过，也不把残缺结果送入 GM 上下文。
- AI GM 必须输出非空 `【下一轮评定参考】`，并提交隐藏的叙事变化声明；确定性 Validator 未通过时不能锁定候选、推进回合、更新场景状态或进入长期记忆。
- R2-A 第一阶段已提供每局独立的 GM/Public 记忆、检查点、低频增量更新、上下文注入和记忆 ON/OFF；普通聊天记忆不会被跑团自动改写。
- R2-D 已按三种流程分别编译新增 NPC、关系变化和独立剧情线的叙事权限契约。

R1 已形成可运行闭环。`campaign_facts`、Participant Memory、持久化并发 barrier、战役分支/回滚、属性、背包、地图和战斗棋盘仍需真实长局证据，不属于当前已实现范围。

详见 [独立跑团模式产品与架构设计](docs/campaign_mode_design.md) 和 [R2-B 上下文预算记录](docs/TavernDesk-R2-B-Campaign-Context-Budget.md)。

### Provider 与模型

默认预置：

| 接入商 | 认证方式 | 当前边界 |
| --- | --- | --- |
| Grok CLI | 本机 `grok login` 订阅登录 | 官方 ACP `agent stdio`；不在 TavernDesk 中保存 Key |
| OpenRouter | API Key | OpenAI-compatible；支持稳定 `session_id`、usage 与缓存命中字段 |
| 硅基流动 | API Key | OpenAI-compatible |
| DeepSeek 官方 API | API Key | OpenAI-compatible；读取官方缓存命中/未命中字段 |
| LM Studio | 本地服务 | 默认 `http://127.0.0.1:6543`，模型由用户切换后主动刷新 |
| 自定义接入商 | API Key（可选） | API 必须兼容 OpenAI Chat Completions；连接方式固定且不可切换为 Grok CLI |

- API 类基地址填写到服务根或 `/v1`、`/api/v1`，不要加入 `/chat` 或 `/chat/completions`。
- “添加自定义接入商”窗口会明确提示兼容性要求；Anthropic Messages、Gemini 原生格式及其他专用协议当前不能作为自定义接入商使用。
- 打开旧数据根时会幂等校正历史 ID/适配器错配：内置项恢复为各自固定连接方式；无法恢复 HTTP 地址的旧自定义 Grok 错配会保留原记录并停用；未实现的旧原生适配器同样保留并停用。
- 模型目录只在用户主动刷新时联网；也可手工加入任意模型 ID，不猜测能力。
- 聊天走 `/chat/completions`；Embedding 功能固定走 `/embeddings`。支持的专用目录可从 `/embeddings/models` 合并模型记录。
- OpenRouter DeepSeek 推理设置当前只提供明确 OFF/ON，不建设通用推理参数矩阵。
- reasoning 在 Infrastructure 边界归一化，只用临时状态提示；原始推理不进入消息、记忆、数据库或下一轮上下文。

## 页面结构

主导航：

```text
仪表盘 · 聊天 · 跑团 · 角色 · 世界书 · 设置
```

| 页面 | 主要工作区 | 次要入口 |
| --- | --- | --- |
| 仪表盘 | 数据概览、最近会话、主要入口 | 当前运行状态 |
| 聊天 | 四栏：导航、角色/会话、消息/Composer、上下文检查器 | 顶部 ⋯ JSONL 导入/导出；上下文、角色、玩家人设、记忆、会话五个检查器页签 |
| 跑团 | 剧本库、起始大厅、三栏游戏桌面 | 跑团设置、本地刷新、途中模型调整、额外骰子 |
| 角色 | 书架、角色主页、聊天入口 | 基础、对话、提示词、元数据四页编辑器 |
| 世界书 | 世界书与词条正文 | 导入范围、索引重建、角色/剧本挂载管理 |
| 设置 | AI 与模型、默认行为、玩家人设、AI 行为模板、界面、数据 | Provider 模型目录、功能分配、资料目录迁移 |

Appica 资源只由主壳、仪表盘和聊天主工作区显式引用。次级窗口和弹窗继续使用既有结构与共享控件模板；没有重写其 `PART_*`、Popup、命中层或模态套叠逻辑。

在“设置 → 界面”选择主题后会立即预览；点击“保存界面设置”后写入本地 `ui.theme`，下次启动继续使用。深炭黑主题只替换现有语义色令牌和 Windows 主题模式，不改变聊天四栏、次级窗口、弹窗、命令或数据流程。

### 界面语言

- 可选项使用各自语言显示为 `简体中文`、`繁體中文`、`English`、`日本語`。
- 对全新数据根，数据库首次创建后会显示一次强制语言选择，选择完成后才创建主窗口。若初始化期间异常退出，待选标记会保留到下次启动。
- 对已有数据库，若没有 `ui.language` 且没有待选标记，则静默使用简体中文，不打断原用户。
- 设置 → 界面可保存下一次启动使用的语言；为避免运行中重建窗口和 ViewModel，普通设置变更不会即时切换，重启 TavernDesk 后生效。
- UI 固定文字位于 `src/TavernDesk.App/Localization/Strings.*.xaml`。模型 Prompt、角色卡内容、聊天正文、协议字面量和用户资料不会因界面语言而翻译或改写。
- 高级页面若收到只以简体中文提供的内部诊断，会在其他语言下显示本地化摘要并把原文写入诊断跟踪，避免把简体中文直接混入界面。

## 数据、安全与兼容性

- SQLite、角色卡、剧本卡、导出和密钥均留在本机；应用不提供云同步。
- API Key 位于数据根 `secrets/`，使用 Windows DPAPI CurrentUser 保护；SQLite 只保存随机引用，不保存明文 Key。
- Provider 删除先提交数据库级联删除，再清理受保护密钥文件；数据库失败时不会提前删除仍被引用的 Key。
- DPAPI 主要防止数据库、备份或单独密钥文件泄漏，不抵御同一 Windows 用户上下文中的恶意程序、管理员或被控制的运行进程。
- 角色卡和剧本卡导入后使用数据根内工作副本，编辑不覆盖原始来源文件。
- schema v10 起消息删除为不可恢复的物理删除；`messages.is_deleted` 仅作为旧数据库兼容列保留。
- schema v19 是当前最高版本。软件不会把未来版本数据库强制降级，也不会在迁移失败后保留半套变更。

## 工程结构

```text
src/
  TavernDesk.App             WPF 页面、窗口、ViewModel、主题和 UI 状态
    Localization/            四语资源、语言归一化和运行时展示边界
  TavernDesk.Core            领域模型、稳定接口与上下文/跑团契约
  TavernDesk.Infrastructure SQLite、Provider、角色卡、世界书、记忆和本地服务
  TavernDesk.AgentHost       不连接 API 的本地存储 smoke 入口
docs/
  handoff.md                 当前交接摘要与验证边界
  architecture.md            模块、数据、请求顺序和生命周期约束
  campaign_mode_design.md    独立跑团规则、阶段和非目标
  TavernDesk-R2-B-Campaign-Context-Budget.md
                              跑团上下文预算实施记录
  codex_worklog.md           按时间追加的实施与验证日志
tools/
  TavernDesk.RootLauncher.cs 根目录薄启动器源码
scripts/
  Test-Localization.ps1      语言键、占位符、硬编码和 XAML 资源检查
app/                          win-x64 自包含运行快照
```

`src/` 是源码基准。`app/` 是发布产物，日常 `dotnet build` 不会自动同步它；不要反向编辑 `app/` 中的 EXE 或 DLL。

## 开发、构建与发布

### 要求

- Windows 10/11 x64。
- .NET SDK `10.0.302`，或满足 `global.json` 的同一 feature band 最新补丁版本。
- NuGet 包默认写入仓库内 `.packages/`；首次 restore 需要访问 NuGet.org，已有完整缓存时可离线构建。

### 构建并运行源码

```powershell
dotnet restore TavernDesk.sln
& .\scripts\Test-Localization.ps1
dotnet build TavernDesk.sln -c Release --no-restore
dotnet run --project src\TavernDesk.App\TavernDesk.App.csproj -c Release --no-build
```

### 存储 smoke

该检查使用新的隔离目录，不连接 Provider：

```powershell
$tavernQaRoot = Join-Path $PWD ("work\verification\storage-smoke-" + (Get-Date -Format "yyyyMMdd-HHmmss"))
dotnet run --project src\TavernDesk.AgentHost\TavernDesk.AgentHost.csproj -c Release --no-build -- --storage-smoke $tavernQaRoot
```

### 发布 `app/`

先关闭正在运行的 TavernDesk，再执行：

```powershell
dotnet restore src\TavernDesk.App\TavernDesk.App.csproj -r win-x64
dotnet publish src\TavernDesk.App\TavernDesk.App.csproj `
  -c Release `
  --no-restore `
  -r win-x64 `
  --self-contained true `
  -p:PublishSingleFile=false `
  -p:DebugType=None `
  -o app
```

发布目录必须保留完整 .NET/WPF 运行库和当前 RID 的原生 SQLite 依赖。生成后可检查根启动器目标是否存在：

```powershell
$tavernProbe = Start-Process -FilePath .\TavernDesk.exe -ArgumentList "--probe" -WindowStyle Hidden -Wait -PassThru
$tavernProbe.ExitCode
```

期望退出码为 `0`。探针只检查 `app/TavernDesk.App.exe` 是否存在，不启动 GUI，也不证明业务功能已经通过。

## 当前验证边界

本分支最近完成：

- 四种语言各 1400 个资源键一致；占位符、空值、未完成标记、App 层简体硬编码和 1553 个直接 XAML 资源声明/引用检查通过。
- Debug 与 Release 解决方案构建：0 个警告、0 个错误。
- AgentHost 隔离 `--storage-smoke`：11 项 PASS，包含旧 Provider 适配器契约迁移，API 请求数为 0。
- 全新隔离数据根通过首次语言选择进入 English 主界面；自动导航到 Characters 后只有一个 TavernDesk 顶层窗口，无异常弹窗，进程正常退出。
- 最终 Release 在同一 English 隔离数据根进入 InputIdle，取得非零主窗口句柄并以退出码 0 正常关闭。
- 用户从实际 EXE 手动检查 English 界面，未发现问题；此前的下拉框英文裁切、透明首次语言窗和 Characters 静态资源连续弹窗均已定点修复。
- 修改的 XAML 资源图检查和 `git diff --check` 通过。
- Windows 150% DPI 下真实启动 WPF，检查 Appica 四栏聊天、用户右/角色左消息、Persona 编辑、搜索框、`depth` 输入和发送模式下拉框；未出现运行时绑定对话框、裁切或溢出。
- 应用内缩放布局矩阵确认默认主窗口 150%、最小主窗口 110% 和独立聊天 150% 会自动收起右栏，收起后剩余两栏均满足最小宽度；本轮未替代用户进行新的肉眼界面验收。
- 本轮只使用隔离资料目录；未读取 API Key，也未发送真实模型请求。`app/` 已同步本轮多语言源码，三个业务 DLL 与 RID Release 输出的 SHA-256 一致，无 PDB，根启动器 `--probe` 退出码为 0。

仍需真实使用验证：

- OpenRouter、硅基流动和 DeepSeek 官方 API 的当前账号模型刷新、流式回复、usage、限流与取消。
- `http://127.0.0.1:6543` LM Studio 的当前模型刷新与生成链路。
- Grok CLI 当前安装、`grok login`、ACP 单轮聊天、取消和订阅并发限制。
- 真实多模型跑团短局与中等长度跑团，包括上下文预算、途中换模型、秘密同投和失败重试。
- 大型历史数据库迁移、长列表性能、键盘无障碍和强制结束进程后的流式恢复体验。
- 应用内 110%–150% 缩放下自动收起/恢复右侧上下文栏的最终视觉与点击体验。
- 台湾繁中和日语的完整页面级视觉、长文本换行与术语自然度仍需人工短验收；静态资源与构建检查不能替代母语使用反馈。

## 稳定边界与非目标

- 不恢复 Ollama；本地模型只通过 LM Studio 的 OpenAI-compatible 入口接入。
- 不恢复消息回收箱、软删除 UI 或恢复消息能力。
- 不把普通聊天和跑团合并成同一数据域，也不让跑团记忆自动覆盖角色普通记忆。
- 不把记忆模板重新拆成 System/User 两套可配置模板。
- 不自动刷新模型目录，不自动重试付费请求，不把 API Key 写进 SQLite、导出或日志。
- 当前没有 Anthropic/Gemini 原生协议、专用 xAI API 适配器、通用附件工作流、图片生成、TTS、MCP 工具执行、自动更新器或云同步。
- 没有真实需求证据时，不建设通用 Agent、多智能体编排、规则 DSL、属性/背包/地图、战斗棋盘、战役分支或通用向量知识库。

## 文档阅读顺序

接手或继续开发时按以下顺序阅读：

1. 本 README：产品入口、运行方法与当前边界。
2. [docs/handoff.md](docs/handoff.md)：最新可信验证、未验证项和不要倒退的方向。
3. [docs/architecture.md](docs/architecture.md)：数据、请求组装和生命周期约束。
4. 涉及跑团时阅读 [docs/campaign_mode_design.md](docs/campaign_mode_design.md) 和 [docs/TavernDesk-R2-B-Campaign-Context-Budget.md](docs/TavernDesk-R2-B-Campaign-Context-Budget.md)。
5. 需要历史证据时查询 [docs/codex_worklog.md](docs/codex_worklog.md)，不要把旧日志中的快照测试数当成当前分支事实。
