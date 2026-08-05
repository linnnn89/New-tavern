# TavernDesk

TavernDesk 是一个 Windows 原生桌面 AI 角色扮演工具，支持角色卡、聊天、群聊、长期记忆、世界书和独立跑团。

这个公开副本不包含任何私人数据库、聊天记录、角色卡、剧本卡、API Key 或本地缓存。首次运行时，程序会在当前用户的“文档\TavernDesk”目录创建全新的本地数据，并自动初始化默认提示词、默认设置和内置 Provider 配置。

## 直接运行

系统要求：Windows 10/11 x64，以及 `.NET 10 Desktop Runtime`。

推荐双击根目录的带图标薄入口：

```text
TavernDesk.exe
```

也可以双击启动脚本：

```text
启动 TavernDesk.cmd
```

或者直接运行实际程序：

```text
app\TavernDesk.App.exe
```

`app/` 是已经构建好的 win-x64 发布目录，不包含调试符号。程序使用本地 SQLite 保存用户自己的数据；API Key 由 Windows DPAPI 保护，不会写入本仓库。

## 从源码构建

源码构建需要 `.NET SDK 10.0.302` 或兼容的 .NET 10 补丁版本：

```powershell
dotnet restore TavernDesk.sln
dotnet build TavernDesk.sln -c Release --no-restore
dotnet test TavernDesk.sln -c Release --no-restore
dotnet run --project src\TavernDesk.App\TavernDesk.App.csproj --no-build
```

默认数据根目录为：

```text
%USERPROFILE%\Documents\TavernDesk
```

如需隔离测试数据，可以设置：

```powershell
$env:TAVERNDESK_DATA_ROOT = "D:\TavernDesk-TestData"
```

## 项目结构

```text
src/TavernDesk.App/             WPF 界面与 ViewModel
src/TavernDesk.Core/            领域模型与接口
src/TavernDesk.Infrastructure/ SQLite、Provider、世界书与本地服务
tests/TavernDesk.Tests/         自动化测试
docs/architecture.md            架构说明
docs/campaign_mode_design.md    跑团设计边界
app/                            可直接运行的 win-x64 发布目录
```

## 隐私边界

- 本仓库不携带 `user-data/`、数据库、导出记录、聊天记录、角色卡或剧本卡。
- 本仓库不携带 `secrets/`、API Key、Provider 私密配置或 Grok 登录状态。
- 用户导入的文件会复制到用户自己的数据根目录，原始文件不会被公开副本修改。
- 默认提示词和默认设置来自源码中的初始化逻辑，用户首次启动即可获得完整的空白工作区。
