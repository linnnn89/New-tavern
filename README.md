# TavernDesk

TavernDesk 是面向 Windows 10/11 的本地酒馆客户端，使用 .NET 10、C#、WPF 和 SQLite 构建。它提供角色卡、普通聊天、群聊、世界书和独立跑团工作区；普通聊天与跑团使用不同的数据域，不互相污染。

## 当前版本

当前代码包含两条已落地的设计线：

- R2-B 跑团上下文与记忆：单次 GM/AI 玩家请求默认 15,000 tokens（输入上下文与输出预留合计），发送前预览与实际请求共用同一个 Planner；GM/Public 记忆按成功裁定后的低频阈值更新，并由每局独立的记忆 ON/OFF 控制。
- R2-C UI 信息架构整理：保留既有业务和数据绑定，减少高频页面的重复入口，把低频设置收进页面内的分类、折叠区或弹出菜单。

R2-C 的原则是“重排现有功能，不新增业务层”：不改 Provider 协议、数据库语义、记忆模型、角色卡导入格式或普通聊天记忆规则。

## 页面结构

左侧一级导航保持不变：

```text
仪表盘 · 聊天 · 跑团 · 角色 · 世界书 · 设置
```

| 页面 | 高频工作区 | 低频入口 |
| --- | --- | --- |
| 聊天 | 会话、消息、生成、候选回复 | 顶部 ⋯：JSONL 导入/导出；右侧五个页签：上下文、角色、玩家人设、记忆、会话 |
| 跑团 | 三栏游玩页：玩家席位、跑团记录、当前步骤 | 跑团设置；顶部 ⋯ 只放重新载入；大厅的高级设置和本局剧本覆盖 |
| 角色 | 书架、角色主页、聊天入口 | 角色编辑器四页：基础、对话、提示词、元数据 |
| 世界书 | 世界书选择、词条查看和编辑 | 导入范围弹出菜单；管理挂载…弹出角色/跑团剧本绑定 |
| 设置 | AI 与模型、默认行为、AI 行为模板、界面、数据 | 各页面只保留已有持久化能力，不创建空的“高级”页 |

### 聊天

右侧面板有五个可见页签：

1. 上下文：Token、请求结构、API 状态和本轮召回；召回开关直接可见，详细范围和诊断在折叠区。
2. 角色：当前角色的角色卡提示词和局部编辑入口。
3. 玩家人设：下拉选择已归档的人设，编辑区使用独立缓冲；只有保存才写回，取消会恢复已保存内容。
4. 记忆：普通聊天记忆银行和工作流；详细操作仍复用原有命令。
5. 会话：当前会话状态；群聊配置只在实际群聊中显示。

显示模式保持直接可用。JSONL 归档动作收进顶部 ⋯，不改变导入/导出命令。

### 跑团

剧本编辑器使用四个页签：

```text
基础 · 世界与规则 · 主持与开场 · 资料
```

资料页保留世界书绑定和旧示例/历史归档折叠区。起始大厅直接展示开局必需的剧本、GM、参与方式、USER 名称、AI 玩家、每席模型、记忆导入和开始/保存动作；历史预算与全局 GM/AI 玩家提示词集中在一个“高级设置”区，剧本正文默认以摘要显示，只有“本局覆盖剧本内容…”才展开编辑框。

### 角色

书架、搜索、排列、导入和批量管理保持原有行为。角色主页保留继续聊天、新建聊天、编辑、替换图片、删除和归类；编辑状态把原有字段按使用场景分为基础、对话、提示词和元数据，保存、导入和导出命令不变。

### 世界书

正文工作区占据主要页面。导入时在弹出菜单中选择作用范围：全局、指定角色或指定跑团剧本；挂载关系在“管理挂载…”弹出窗口中按角色/跑团剧本分组管理。现有 Scope、Binding Collections 和保存命令保持不变。

### 设置

设置页按现有功能重新分类：

- AI 与模型：接入商、模型目录、功能分配。
- 默认行为：现有聊天自动滚动设置；未新增全局“新聊天/新跑团复制默认”的设置框架。
- 玩家人设：在本机个人资料中归档多个玩家人设；设置页可新增、删除、保存和取消，新增档案默认为空白草稿，名称为空或使用程序保留值时不会保存；聊天页可直接选择和编辑当前档案。名称被拦截时会弹出安全命名提示，建议改用普通名称；旧资料被自动修正时会提示已改为“用户”。
- AI 行为模板：聊天、记忆银行、群聊、跑团四类模板在同一棵分类/功能列表中选择，右侧完整保留编辑器、恢复默认、导出和保存。
- 界面：字体、字号和主题说明。
- 数据：个人资料目录。

## 跑团上下文与记忆链路

```text
CampaignEvent / CampaignMemory / CharacterSnapshot
                         │
                         ▼
              CampaignContextPlanner
                         │
                         ▼
              CampaignContextPlan.Messages
                    ┌────┴────┐
                    ▼         ▼
              右侧 Token 预览  CampaignRunner
                                      │
                                      ▼
                                  Provider

成功锁定 GmResolution
          │
          ▼
CampaignMemoryUpdateService
          │
          ▼
       GM/Public bank + checkpoint
```

- Planner 只负责分区、预算、历史截取和估算，不调用 Provider、不写数据库。
- Runner 消费同一份 CampaignContextPlan.Messages，避免预览和实际发送不一致。
- 默认预算是单次请求上限，不是整轮所有席位请求的总和；有效容量取本局预算与模型 ContextLimit 的较小值。
- 固定资料、身份和当前回合内容不能被静默删除；预算不足时只省略较旧事件历史，固定区或当前回合自身超限则阻止对应生成。
- 记忆只在成功锁定 GM 裁定后检查阈值，默认累计 3 个完整轮次或约 4,000 个待处理 tokens；打开页面、切换模型、单独掷骰和 AI 玩家按钮不会触发记忆 API。
- 每局“记忆 ON/OFF”独立保存。OFF 时不调用记忆总结，也不向 GM/AI 玩家请求注入升级版长期记忆结构；既有 bank/checkpoint 保留，重新 ON 后从原 checkpoint 继续。

普通聊天记忆仍按普通聊天自己的会话/角色规则运行，不受跑团记忆开关影响。

## 工程结构

```text
src/
  TavernDesk.App             WPF 页面、窗口、ViewModel 和 UI 状态
  TavernDesk.Core            领域模型、稳定接口和上下文计划契约
  TavernDesk.Infrastructure SQLite、角色卡、Provider、世界书和本地服务
  TavernDesk.AgentHost       本地存储自检入口
tests/
  TavernDesk.Tests           自动化回归测试
docs/
  architecture.md            模块、数据和生命周期约束
  campaign_mode_design.md    独立跑团规则和边界
  TavernDesk-R2-B-Campaign-Context-Budget.md
                              R2-B 预算与低频记忆实施记录
  codex_worklog.md           实施和验证日志
  handoff.md                 当前接手摘要
tools/
  TavernDesk.RootLauncher.cs 根目录 EXE 启动器源码
app/                          明确发布时生成的 win-x64 自包含运行快照
```

src/ 是唯一源码基准。根目录 TavernDesk.exe 是薄启动器，只启动 app/TavernDesk.App.exe；日常构建不会自动同步 app/，不要反向编辑发布目录中的 DLL。

## 构建与运行

要求：Windows 10/11 x64、.NET SDK 10（仓库的 global.json 指定版本）和项目目录内的 NuGet 缓存 .packages/。

```powershell
cd "D:\CODEX PROJECT\TavernDesk"
dotnet restore TavernDesk.sln
dotnet build TavernDesk.sln -c Release --no-restore
dotnet run --project src\TavernDesk.App\TavernDesk.App.csproj --no-build
```

根目录启动器：

```powershell
.\TavernDesk.exe
```

确定性 app/ 发布快照：

```powershell
dotnet publish src\TavernDesk.App\TavernDesk.App.csproj -c Release --no-restore -r win-x64 --self-contained true -p:PublishSingleFile=false -p:DebugType=None -o app
```

发布目录必须保留 EXE、DLL、coreclr.dll、hostfxr.dll、WPF 运行库和 runtimes/ 等文件，不能只复制 EXE。

可用命令行或环境变量指定数据根：

```powershell
dotnet run --project src\TavernDesk.App\TavernDesk.App.csproj --no-build --data-root "D:\TavernDeskData"
$env:TAVERNDESK_DATA_ROOT = "D:\TavernDeskData"
```

命令行参数优先。默认数据根为当前用户“文档”目录下的 TavernDesk；数据库、附件、角色卡、导出、隔离工作目录和 DPAPI 密钥文件均位于数据根内。

## 验证与人工查验

代码侧快速检查：

```powershell
dotnet build TavernDesk.sln -c Release --no-restore
dotnet test TavernDesk.sln -c Release --no-restore
```

本地存储自检不会连接 API：

```powershell
dotnet run --project src\TavernDesk.AgentHost\TavernDesk.AgentHost.csproj --no-build --storage-smoke ".\user-data\verification-local"
```

本轮 UI 整理不做截图自动化；建议人工打开以下页面确认布局与入口存在：

1. 聊天：右侧五个页签、顶部 ⋯、玩家人设下拉编辑和召回高级折叠区；修改后验证取消不会写回。
2. 跑团剧本编辑、起始大厅和游玩页：四个剧本 Tab、高级设置、剧本覆盖 Expander、顶部 跑团设置/⋯。
3. 角色编辑：基础、对话、提示词、元数据四个 Tab，切换后字段仍可编辑并能保存。
4. 世界书：正文工作区、导入范围弹出菜单、管理挂载弹出窗口。
5. 设置：AI 与模型、默认行为、玩家人设、AI 行为模板、界面、数据；提示词树和人设档案选择后右侧编辑器随之切换。

## 稳定边界

- 普通聊天、群聊和跑团是不同数据域；跑团记忆开关不控制普通聊天记忆。
- API Key 不写入导出文件；Provider 模型目录只在用户主动刷新时联网。
- 超限时阻止请求，不静默截断角色卡、剧本固定资料或当前回合。
- 角色卡和剧本卡使用数据根内工作副本，不覆盖原始文件。
- 目前不提供 Anthropic/Gemini 原生协议、图片/语音/TTS、通用向量知识库、自动更新器或后台自动重试。
- 全局“新聊天/新跑团默认设置复制”尚未建立新的通用设置框架；只有现有可持久化的设置会在对应页面提供入口。

后续工作开始前，依次阅读 docs/handoff.md、本 README、docs/architecture.md，涉及跑团时再读 docs/campaign_mode_design.md 和 docs/TavernDesk-R2-B-Campaign-Context-Budget.md。
