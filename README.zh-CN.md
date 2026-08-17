<div align="center">
  <img src="./src/TavernDesk.App/Assets/Icons/app-icon.png" width="112" alt="TavernDesk 图标">
  <h1>TavernDesk</h1>
  <p>面向 Windows 的本地优先角色 AI 对话、长期记忆、世界书与结构化跑团客户端。</p>
  <p>
    <img src="https://img.shields.io/badge/Windows-10%20%7C%2011-0078D4?logo=windows&logoColor=white" alt="Windows 10 和 11">
    <img src="https://img.shields.io/badge/.NET-10-512BD4?logo=dotnet&logoColor=white" alt=".NET 10">
    <a href="./LICENSE"><img src="https://img.shields.io/badge/License-MIT-F4C430" alt="MIT 许可证"></a>
  </p>
</div>

<p align="center">
  <a href="./README.md">English</a> ·
  <strong>简体中文</strong> ·
  <a href="./README.zh-TW.md">繁體中文</a> ·
  <a href="./README.ja-JP.md">日本語</a>
</p>

TavernDesk 希望把角色扮演所依赖的数据留在用户手中，并让它们始终看得见、改得动。角色卡、聊天、记忆、世界书和跑团保存在本地 SQLite 资料目录中；模型由用户选择，上下文可以在发送前检查，长期状态何时更新也由用户决定。

它更适合持续发展的角色关系，而不是一次性提示词：普通聊天和跑团各自维护状态，记忆有明确的草稿与检查点，跑团回合则由玩家、GM 和流程规则共同推进。

## TavernDesk 有什么不同

- **记忆不是黑箱。** 长期记忆按角色、群聊或跑团分别保存，可预览、编辑、压缩、设置检查点，再决定是否写入。
- **聊天与跑团互不污染。** 跑团拥有独立的剧本、参与者快照、事件流、GM 状态和记忆，不会暗中改写角色的普通聊天历史。
- **多角色协作有明确规则。** 跑团支持 AI 或真人 GM、真人与 AI 玩家、三种回合流程、按席位分配模型、骰子记录、结果校验、取消和失败重试。
- **上下文可以核对。** 检查器会展示 Token 估算、请求分段、世界书命中、检索诊断、排除项和实际 API 请求结构。
- **模型和数据由你选择。** 可以连接云端服务、本地 LM Studio 或 Grok CLI 订阅登录，角色库始终保存在自己的 Windows 资料目录。
- **兼容常用角色资产。** 支持导入和导出 SillyTavern 风格的 PNG、JSON、CHARX 角色卡，并尽量保留内嵌数据与附带资源。

## 主要功能

### 角色库与对话

- 角色书架、搜索、排序、封面尺寸、自定义归类、批量整理和完整资料编辑。
- 单聊、群聊、多会话、独立聊天窗口、流式输出、取消与继续生成。
- 群聊接力默认按成员固定顺序进行。完全自动接力会在上一条回复完成后继续；点击顶部角色头像的“立即接话”可以强制指定角色。模型回复中的 `@` 文本不会选择下一位角色，也不会暂停接力。
- 消息原位编辑、多个候选、重新生成、从指定消息建立分支，以及 JSONL 聊天导入导出。
- 气泡和小说两种显示方式。气泡模式中，用户消息固定在右侧，角色消息固定在左侧，群聊也遵循同一规则。
- 玩家人设、备选开场白、系统提示词、历史后指令，以及按角色分配模型。

### 记忆、上下文与世界书

- 角色、群聊和跑团各自拥有长期记忆，支持可编辑草稿、检查点、压缩和更新间隔。
- 玩家人设、角色卡、世界书、记忆、聊天历史、检索结果、历史后指令和当前输入按固定顺序组装，并可逐段查看。
- 对已知 OpenAI Tokenizer 进行本地 Token 估算；未知模型会明确使用回退估算，而不是假装精确。
- 世界书可挂载到全局、角色、对话、剧本或某一局跑团。
- 支持 SillyTavern 风格的确定性关键词规则，包括选择性匹配、递归、概率、互斥组、正则、整词匹配和 depth 注入。
- 使用 SQLite FTS5 检索，并可叠加 Embedding 混合排序；本地预览不会调用 Embedding 服务。

### 独立跑团

跑团是独立运行域，不是“群聊再加一段 GM 提示词”。

- `1 名 GM + USER + 0–4 名 AI 玩家`。
- 支持 AI GM、真人 GM、USER 同时担任玩家与 GM，以及纯观察模式。
- 协作圆桌、秘密同投、严格先攻三种回合流程。
- 开局冻结角色、玩家人设、世界规则、GM 指令、叙事权限和模型路由快照。
- 每个 AI 玩家与 GM 席位可使用不同的 Provider 和模型。
- 自动记录行动 `1d20`，也可单独投掷公开骰子表达式。
- GM 结果通过确定性校验后，才会推进回合或更新持久化跑团状态。
- 每局独立的公开/GM 记忆、上下文预算、取消和显式重试。

### Windows 桌面体验

- 原生 WPF 界面，支持 Windows 10/11 x64。
- 四栏聊天工作区，右侧上下文检查器可折叠。
- 默认浅色与深炭黑主题、界面缩放和全局字体设置。
- 界面语言：简体中文、繁体中文、English、日本語。
- 新资料目录首次启动时选择语言，之后可在设置中更改。

## 快速开始

普通玩家建议从 [Releases 页面](https://github.com/linnnn89/New-tavern/releases/latest) 下载最新的 `TavernDesk-Setup-x64.exe` 安装程序：

1. 运行安装程序并选择安装界面语言。
2. 自定义安装目录，并选择是否创建桌面和开始菜单快捷方式。
3. 启动 TavernDesk，首次运行时选择应用界面语言，再打开 **设置 → AI 与模型** 配置接入商并分配模型。

安装包内含私有 .NET 10 运行时和全部必要依赖，不创建注册表项，因此不会出现在 Windows“已安装的应用”列表中。可使用开始菜单的卸载快捷方式，或安装目录中的 `Uninstall TavernDesk.cmd` 卸载。升级和卸载都会删除安装程序管理的程序文件和 `tests\output`；用户后来放入安装目录的其他文件会保留。

仓库同时保留可直接运行的 `win-x64` 便携自包含版本，使用它也无需另外安装 .NET：

1. [下载仓库 ZIP](https://github.com/linnnn89/New-tavern/archive/refs/heads/%E8%B7%91%E5%9B%A2%E8%AE%B0%E5%BF%86%E5%8D%87%E7%BA%A7%E7%89%88.zip) 并完整解压，或使用 Git 克隆仓库。
2. 保持 `TavernDesk.exe` 与完整的 `app/` 目录位于同一层级。
3. 运行 `TavernDesk.exe`，选择界面语言。
4. 打开 **设置 → AI 与模型**，配置接入商并分配模型。

```powershell
git clone --branch "跑团记忆升级版" --single-branch https://github.com/linnnn89/New-tavern.git
cd New-tavern
.\TavernDesk.exe
```

`TavernDesk.exe` 是一个很小的启动器，完整运行环境位于 `app/`。只复制根目录 EXE 无法启动应用。

## 模型接入

| 接入方式 | 认证 | 说明 |
| --- | --- | --- |
| OpenRouter | API Key | OpenAI-compatible 聊天与模型目录 |
| 硅基流动 | API Key | OpenAI-compatible |
| DeepSeek 官方 API | API Key | OpenAI-compatible，并读取缓存使用字段 |
| LM Studio | 本地服务 | 默认地址：`http://127.0.0.1:6543` |
| Grok CLI | 本地订阅登录 | 使用本机 `grok login`；TavernDesk 不要求 Grok API Key |
| 自定义接入商 | API Key 可选 | 必须提供兼容 OpenAI Chat Completions 的 API |

自定义地址填写到服务根、`/v1` 或 `/api/v1` 即可，不要追加 `/chat` 或 `/chat/completions`。当前不支持 Anthropic Messages 和 Gemini 原生协议。TavernDesk 是客户端，不包含本地模型运行时或模型下载器。

## 本地数据与网络边界

默认资料目录为 `%USERPROFILE%\Documents\TavernDesk`，其中保存 SQLite 数据库、角色卡、剧本卡、导出、附件和受保护的接入商密钥。当前目录记录在 `%LOCALAPPDATA%\TavernDesk\config.json`，也可以从设置中迁移。

API Key 以 Windows DPAPI `CurrentUser` 保护文件保存，SQLite 只记录随机引用。TavernDesk 不提供内置云同步。“本地优先”不等于所有生成都离线：发起生成或 Embedding 请求时，提示词和必要的对话上下文会发送给你选择的服务。

隐私安全的滚动错误日志默认写入 `%LOCALAPPDATA%\TavernDesk\logs`，只包含错误类别、异常类型、脱敏后的调用位置和状态，不主动采集 API 请求/回复正文或授权头。设置中的 API 测试模式默认关闭；开启后会把请求正文、可见回复、耗时和 Token 用量写入软件根目录下的 `tests\output`，界面会明确提示其中含有对话内容，并可直接打开或清空目录。软件不主动记录授权头、Cookie、隐藏思考文本或完整 Embedding 向量，并会脱敏已知 Key 格式，但无法识别普通正文中的任意秘密；请勿把 Key 或个人信息写入提示词、名称、地址或错误文本。安装版升级和卸载时都会删除测试输出。

## 从源码构建

需要 Windows 10/11 x64，以及 [`global.json`](./global.json) 指定的 .NET SDK。

```powershell
dotnet restore TavernDesk.sln
& .\scripts\Test-Localization.ps1
dotnet build TavernDesk.sln -c Release --no-restore
dotnet run --project src\TavernDesk.App\TavernDesk.App.csproj -c Release --no-build
```

源码基准位于 `src/`。仓库中的 `app/` 是可运行发布快照，普通 `dotnet build` 不会自动更新它。

## 项目文档

- [架构基线](./docs/architecture.md)
- [独立跑团模式设计](./docs/campaign_mode_design.md)
- [跑团上下文预算](./docs/TavernDesk-R2-B-Campaign-Context-Budget.md)

## 许可证

TavernDesk 使用 [MIT License](./LICENSE)。在遵守许可证的前提下，允许商业使用、修改与再分发。
