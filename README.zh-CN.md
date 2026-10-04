<div align="center">
  <img src="./src/TavernDesk.App/Assets/Icons/app-icon.png" width="112" alt="TavernDesk 图标">
  <h1>TavernDesk</h1>
  <p>Windows 角色 AI 聊天与跑团客户端。</p>
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

TavernDesk 是 Windows 上的角色 AI 聊天与跑团客户端。导入角色卡，选好模型，就能开始单聊或群聊；需要延续剧情时，可以编辑世界书、整理长期记忆，或用剧本开启一局跑团。角色、对话和剧本保存在本机。

## 开始使用

从 [Releases](https://github.com/linnnn89/New-tavern/releases/latest) 下载 `TavernDesk-Setup-x64.exe`，选择安装目录后启动。安装包自带运行环境。卸载使用开始菜单的快捷方式，或安装目录中的 `Uninstall TavernDesk.cmd`。

1. 首次启动时选择界面语言。
2. 打开 **设置 → AI 与模型**，添加接入商，填写地址和密钥，刷新或添加模型。
3. 在功能分配页选择聊天、群聊接力等功能及模型，点击“保存到”对应功能。
4. 导入或新建角色，点击角色卡上的聊天按钮。首页的最近对话可以回到上次的聊天。

仓库也附带便携版本：完整下载并解压后，运行根目录的 `TavernDesk.exe`，保留旁边的 `app/` 文件夹。最新源码的运行方法见下方“从源码构建”。

## 聊天与群聊

聊天顶栏可以切换气泡和小说显示。消息支持编辑、重新生成、候选切换、建立分支和 JSONL 导入导出，也可以在独立窗口中打开会话。向上阅读历史时暂停自动跟随，点击回到底部即可继续跟随新消息。

输入区的“发送”按钮旁可以选择“发送并生成”或“仅保存消息”；生成中同一位置显示停止。尚未分配模型时，提示条中的“去分配模型”直接打开对应设置。输入区的预算和记忆摘要可以打开右侧检查器。

群聊成员条显示姓名，带外环的头像是下一位发言者。点击角色下方的“接话”可以指定角色；完全自动接力按成员顺序继续。

![群聊成员与发送模式](./docs/screenshots/group-turns.png)

## 核对上下文与记忆

检查器可以查看人设、角色卡、世界书、记忆、历史、检索结果和 API 请求结构。预算色条显示输入各段的比例，点击色段展开内容；预计总量包含输出预留，剩余预算另行列出。

![聊天与上下文检查器](./docs/screenshots/chat-inspector.png)

长期记忆按角色、群聊或跑团保存，可以编辑、压缩和设置检查点。聊天记忆草稿提供差异视图，核对新增和删除内容后再保存。

![记忆草稿差异](./docs/screenshots/memory-diff.png)

世界书可以挂载到全局、角色、对话、剧本或单局跑团。条目名称和正文可直接编辑，一起保存；切换条目保留当前编辑，离开时可选择保存、放弃或取消。保存后更新本地全文检索，向量索引通过重建操作更新。角色卡支持 PNG、JSON 和 CHARX 格式。

## 用剧本开启跑团

在剧本库中新建或导入剧本，选中后点击“用所选剧本开新局”。安排 GM 和玩家、选择回合流程，再为 AI 席位分配模型。每局保存自己的参与者、事件记录和公开／GM 记忆。

- 支持 AI 或真人 GM、真人玩家与最多四名 AI 玩家，也可旁观。
- 提供协作圆桌、秘密同投和严格先攻三种流程。
- GM 和每个 AI 玩家可以使用不同模型，行动骰子会记录在局内。
- 对局中可以停止生成，并从失败状态重试。

剧本编辑会在本机保留恢复草稿。剧本库显示“有未保存草稿”，可恢复编辑或丢弃；保存剧本后清除恢复草稿，返回剧本库会放弃当前编辑。原剧本已变化或删除时，恢复内容另存为新剧本。

![剧本库与草稿恢复](./docs/screenshots/campaign-recovery.png)

## 语音与界面设置

在 **设置 → 语音** 填写 Fish Audio 地址、密钥和默认音色；消息旁的齿轮可以指定角色音色。点击角色消息的小喇叭生成并播放，再次点击停止。高级合成参数默认折叠，详细参数见[语音设置说明](./docs/voice-settings.md)。朗读支持普通单聊和群聊。

界面设置提供浅色、深色、Cupertino 和 Material 主题，以及简体中文、繁体中文、英语和日语。语言更改后重启生效。修改缩放时，确认窗保留十秒；确认后保存，超时或关闭则恢复原比例。窄窗口会收起检查器，点击预算或记忆摘要可临时打开。

<details>
<summary>更多界面截图</summary>

窄窗口与右侧检查器：

![窄窗口聊天](./docs/screenshots/chat-narrow.png)
![窄窗口检查器](./docs/screenshots/chat-narrow-inspector.png)

角色库、世界书编辑与玩家人设：

![角色库](./docs/screenshots/character-shelf-current.png)
![世界书编辑](./docs/screenshots/worldbook-editor.png)
![已保存人设](./docs/screenshots/persona-list.png)

</details>

## 模型接入

| 接入方式 | 配置 |
| --- | --- |
| OpenRouter、硅基流动、DeepSeek | API Key 与模型 |
| LM Studio | 本地服务地址，默认 `http://127.0.0.1:6543` |
| Grok CLI | 在本机运行 `grok login` 完成订阅登录 |
| 自定义接入商 | OpenAI Chat Completions 兼容地址与可选密钥 |

自定义地址填到服务根、`/v1` 或 `/api/v1`，聊天路径由软件补齐。聊天、群聊接力和其他生成功能分别分配模型。

## 本地资料

默认资料目录为 `%USERPROFILE%\Documents\TavernDesk`。角色卡、聊天、记忆、剧本、附件和导出文件保存在这里；接入商密钥使用 Windows DPAPI 加密保存。资料目录可以在设置中更改，重启后迁移，原目录保留。

模型生成、Embedding 和语音请求发送到你配置的服务。错误日志位于 `%LOCALAPPDATA%\TavernDesk\logs`。设置中的 API 测试模式会把请求、回复、耗时和 Token 用量保存到软件目录的 `tests\output`，可在设置中打开或清空。

## 从源码构建

需要 Windows 10/11 x64 和 [global.json](./global.json) 指定的 .NET SDK。

```powershell
git clone --branch "跑团记忆升级版" --single-branch https://github.com/linnnn89/New-tavern.git
cd New-tavern
dotnet restore TavernDesk.sln
& .\scripts\Test-Localization.ps1
dotnet build TavernDesk.sln -c Release --no-restore
dotnet run --project src\TavernDesk.App\TavernDesk.App.csproj -c Release --no-build
```

普通构建输出到 `src/` 下的构建目录，仓库随附的 `app/` 是单独的发布版本。打包使用 `scripts/Build-WindowsInstaller.ps1`；隔离测试与维护说明见[架构与维护指南](./docs/architecture.md)。

## 文档与许可证

- [文档导航](./docs/README.md)
- [架构与维护指南](./docs/architecture.md)
- [跑团规则与实现](./docs/campaign_mode_design.md)
- [跑团上下文与记忆](./docs/TavernDesk-R2-B-Campaign-Context-Budget.md)

采用 [MIT License](./LICENSE)，允许商业使用、修改和再分发。
