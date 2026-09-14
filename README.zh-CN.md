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
- **上下文可以核对。** 检查器会展示 Token 估算、请求分段、世界书命中、检索诊断、排除项和实际 API 请求结构。普通聊天预览跟随当前输入和会话，迟到的结果不会覆盖新预览；请求组装完成后优先展示发送预算，直到你修改输入或切换会话。
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

### 手动角色语音（Fish Audio）

- 普通单聊和群聊的每条角色消息下方都有小喇叭。点击后才生成并播放；再次点击停止。收到回复、打开聊天或切换候选都不会自动生成语音。用户消息和系统消息不提供角色朗读按钮。
- 在“设置 → 语音”中填写 API 地址、Fish Audio API Key 和默认音色的 `reference_id`。点击消息旁的齿轮也可修改这些设置，并为当前角色指定专用音色。角色绑定使用角色 ID，重名角色不会混用音色。密钥通过 Windows DPAPI 加密保存，留空保存时保留原密钥，也可勾选清除密钥。
- 多个设置窗口保存时只合并各自实际修改的字段；只修改角色音色不会覆盖其他窗口新保存的模型或全局参数。输入无效时会提示具体字段及范围；存储失败单独提示。新配置提交后即生效，旧密钥清理失败会单独提示并记录错误日志，不会把已成功的保存报告为失败。
- 默认模型为 `s2.1-pro-free`，适合免费账户测试；下拉包含 `s1`、`s2-pro`、`s2.1-pro`、`s2.1-pro-free`、`drama-3-preview` 和“自定义”。自定义模型 ID 原样发送。Fish 官方接口可能把未知 ID 回退到默认模型，因此免费账户应明确选择 `s2.1-pro-free`。语音模型与音色 `reference_id` 是两个不同参数，语音模型与聊天模型也独立配置。
- 默认 API 地址为 `https://api.fish.audio/v1/tts`，可改为 Fish 兼容的 HTTPS 地址；本机服务允许 HTTP。密钥会发送到指定地址，不跟随 HTTP 重定向。配置保存、重载及恢复推荐值均不会调用 TTS。
- 推荐值参考 [Fish TTS 文档](https://docs.fish.audio/api-reference/endpoint/openapi-v1/text-to-speech)：语速 `1`、音量 `0 dB`、`temperature/top_p = 0.7`、`chunk_length = 300`、`min_chunk_length = 50`、`latency = normal`、`max_new_tokens = 1024`、`repetition_penalty = 1.2`、`early_stop_threshold = 1`。默认开启文本规范化、响度规范化和前段音频条件，默认关闭可选的 `quality-guard`。这些参数均可修改；“恢复推荐值”保留密钥和音色，点击保存后生效。
- 当前播放器固定使用 PCM、44100 Hz、16 位单声道；不提供无法播放的 MP3/Opus 参数。每条角色消息单独合成，不上传参考音频，也不需要下载模型。
- 全应用一次播放一条语音；点击另一条会停止前一条。当前会话开始新回复、切换或删除会话，或编辑、删除、重新生成、切换正在朗读消息的候选时会停止播放。生成或加载期间喇叭禁用并显示等待提示；已在播放时仍可停止。
- 只朗读当前显示的正文副本，去除代码块、图片和成对的加粗/行内代码标记，保留普通星号、未闭合标记、尖括号和大小比较表达式，不修改聊天记录。最近一条完整播放的音频暂存内存（最多 32 MiB）；正文、音色、模型、API 地址、合成参数及密钥配置相同可直接重播。中断或失败的音频不缓存，关闭应用后缓存消失。
- 生成失败会在消息下方显示原因，不自动重试；再次点击可能产生新的 Fish API 用量。本功能目前接入普通对话页面，独立跑团页面未接入。

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
- 剧本内容与世界书挂载在同一事务中保存。编辑每秒保留到本机，正常退出前补写；再次进入跑团时可恢复或明确丢弃，关闭恢复提示则保留到下次。保存成功会在同一事务内清除恢复草稿，返回剧本库则放弃当前修改。原剧本已变化或被删除时，恢复内容另存为新剧本。异常终止仍可能丢失最后一次成功写入之后的编辑。

### Windows 桌面体验

- 弹出面板和右键菜单不再因点击外部区域而关闭；窗口原有的 X、关闭或取消操作保持可用。

- 原生 WPF 界面，支持 Windows 10/11 x64。
- 四栏聊天工作区，右侧上下文检查器可折叠。
- 新资料库默认使用 100% 应用内缩放。切换比例时显示独立于应用缩放的置顶确认窗：10 秒内确认即保存该比例，超时或关闭窗口自动恢复之前的比例。其他界面偏好及恢复默认后的设置仍需点击保存，语言更改需重启；已有缩放设置继续保留。
- 优先保障应用内 100%：主窗口最小高度降为 500 个逻辑单位，界面设置的标题与选项上下排列，关键剧本输入项、聊天输入框和缩放控件提供可访问名称。长历史读取移到后台，消息分批应用到界面，保留取消和会话切换保护。Windows 系统缩放与应用内缩放分别处理。
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

在设置中更改资料目录，会安排在下次启动时切换。本次运行继续使用当前目录；下次取得单实例门闩后、业务服务打开数据库前才开始复制，因此安排迁移后继续产生的数据也会包含在内。复制或配置提交失败时仍使用原目录，成功复制后也保留原目录。重新保存当前目录可取消待执行切换。重试中断的复制时，已有副本可能保留为目标同级的 `.name.incomplete-<id>` 目录。

角色卡导入先暂存文件，再将角色、内嵌世界书和挂载关系放在同一 SQLite 事务中提交。保存失败会回滚新记录并清理本次创建的文件，复用的世界书保持原状。导入上限为 JSON 16 MiB、PNG/CHARX 256 MiB、每本世界书 20,000 条目。流式回复按 120 毫秒合并普通显示更新，按需生成全文快照，并在回复增长时复用未变化的正文和代码控件；首段及完成状态及时显示，保留完整正文和现有 Markdown 语法。空首页提供配置模型和导入角色入口；接入商数量表示已启用连接，不代表聊天模型已配置。

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
