# P0 — 运行时 UI 基线（只读）

> 按相关 UI 改动需要执行，不是其他工作的统一前置。改动：不改源码；仅在隔离资料中操作界面与测试设置。
> 先读 [README.md](README.md) 第 1 节的通用规则。

## 1. 目标

用真实界面核实会影响本次修改的疑点，并采集受影响区域的改动前截图。只有排查控件名称、状态或定位时才保留 UIA 片段，不采集全应用的截图与整树矩阵。

## 2. 为什么需要这一步

上次评审只读了 XAML 和 C#，没能截图：评审环境的终端运行在隔离桌面，启动的窗口对用户桌面和 WinCode UIA 都不可见。执行方必须在**能访问交互桌面**的环境里运行。

## 3. 准备

1. 查看现有实例（单实例门闩会让第二个实例失败退出；不自行结束用户正在使用的实例）：
   ```powershell
   Get-Process TavernDesk* -ErrorAction SilentlyContinue
   ```
2. 构建并启动隔离实例（复用虚构测试资料 `work/TAVERN-TEST/profile/`，不碰个人数据）：
   ```powershell
   & .\scripts\Start-IsolatedTest.ps1
   ```
   成功时输出 `Status : window-shown` 和 `ProcessId`，窗口标题带 `[TEST]`。
   如果脚本结束时窗口跟着消失（执行环境把子进程放在作业对象里），改为在交互桌面直接启动：
   ```powershell
   & "src\TavernDesk.App\bin\Release\net10.0-windows\TavernDesk.App.exe" --test-root "<仓库绝对路径>\work\TAVERN-TEST\profile" --test-reuse
   ```
   注意：根目录的 `TavernDesk.exe` 启动器**不转发参数**，不能用于测试。
3. WinCode MCP 连接必须固定到本仓库。如果现有连接报 `WORKSPACE_MISMATCH`，按返回的 `connectionGuide` 新建 STDIO 连接，本机配置为：
   ```text
   command: C:\Program Files\nodejs\node.exe
   args:    D:\CODEX PROJECT\WinCode MCP\dist\index.js --workspace "d:\CODEX PROJECT\New-tavern"
   ```
   连接后用 `wincode_hello_world` 核对工作区。UI 工具（`wincode_ui_*`）本身不依赖工作区，但 `wincode_ui_review` 的源码候选需要。

## 4. 取证方法

- 用 `wincode_ui_list_windows({processName:"TavernDesk.App"})` 拿 `pid` 和 `hwnd`。
- 用 `wincode_ui_inspect`，参数 `backgroundOnly:true, capture:"original", responseFormat:"compact", maxDepth:8, maxNodes:400`。需要源码对应时改用 `wincode_ui_review`，并传 `candidateFiles`（如 `src/TavernDesk.App/Views/ChatView.xaml`）。
- 切换页面只用 `wincode_ui_click`（后台语义点击）。不使用 `wincode_ui_type` 的 `mode:"type"`（会抢键盘焦点）。
- 必要截图放在 `work/refactor-qa/P0/`，命名为 `<页面>-<主题>-<缩放>.png`；UIA 用于定位和核对操作结果，不要求每张图同时保存 JSON。

## 5. 按改动选择的状态

### 5.1 页面定位参考（只选择受影响页面，默认浅色、100%）

| 页面 | 导航按钮的 AutomationProperties.Name 资源 | 额外状态 |
|---|---|---|
| 首页 | `Shell.Dashboard.Label` | — |
| 角色 | `Shell.Characters.Label` | 书架；打开一个角色详情 |
| 聊天 | `Shell.Chat.Label` | 选中一个会话；右侧面板依次打开 上下文 / 角色 / 人设 / 记忆 / 会话 五个页签；右侧面板折叠状态 |
| 跑团 | `Shell.Campaign.Label` | 剧本库；如有测试数据，打开准备大厅 |
| 世界书 | `Shell.Worldbook.Label` | — |
| 设置 | `Shell.Settings.Label` | 依次打开 AI 与模型 / 界面 / 语音 / 数据 页签 |

### 5.2 相关变体（主题改动看目标主题；布局改动看缩放与最小窗口）

1. **深色主题**：设置 → 界面 → 主题选"深色"。主题会即时预览，**不要点保存**。
   - 未保存的界面设置**不会阻止离开设置页**（`ProviderSettingsViewModel.ConfirmCanLeaveAsync` 只检查语音和接入商），所以切到首页、聊天页时深色预览会保留，可以直接采集。
   - 采集完重新进入设置页即可还原：进入设置页会调用 `Settings.LoadAsync`（`MainWindowViewModel.cs:387/400`），重新加载已保存的主题；检查已还原即可。
2. **150% 缩放**：设置 → 界面 → 缩放 150%。会弹出 10 秒确认框 `SafeChoiceDialog`。
   - 它是**模态窗口**（`_confirmScale` 同步等待结果），打开期间无法切换页面，"超时前截图"的办法只能截到设置页。
   - 正确步骤：用 `wincode_ui_list_windows` 单独找到确认框的 hwnd → `wincode_ui_click` 点"保留此缩放"（资源键 `ScaleConfirm.Accept`）→ 检查受影响页面 → 回到设置页把缩放改回 100% 并再次确认。
   - 缩放确认后会写入设置。采集结束必须核实已经改回 100%。
3. **最小窗口**：主窗口最小尺寸是 1080×500。记录在此尺寸下聊天页是否自动折叠右侧面板（`ChatView.xaml.cs` 的 `UpdateResponsiveLayout`）。

### 5.3 疑点清单（仅核实本工作包相关项，未检查的保持未知）

| # | 疑点 | 怎么看 | 来源证据 |
|---|---|---|---|
| Q1 | 输入框提示背景 `ComposerHintBrush` 不随主题切换 | 深色、Cupertino、Material 三种主题下的聊天页输入框区域 | 该画刷只在 `Themes/Light.xaml:43` 定义（`ComposerHintColor` = `#142563EB`，即 8% 透明度的浅色主题强调蓝），`InterfaceSettingsRuntime.cs` 的四套调色板里都没有，切换主题时不会更新；使用处 `Themes/AppicaPrimary.xaml:219`。透明度低，可能看不出问题，以截图为准 |
| Q2 | 深色主题下角色卡片上的白色半透明块是否刺眼 | 深色 + 角色书架 | `Views/CharactersView.xaml:336` `Background="#EEFFFFFF"` |
| Q3 | 深色主题下首页主卡片颜色是否协调 | 深色 + 首页 | `Views/DashboardView.xaml:84` `#2A7BF0`、`:103` `#DCEAFF` |
| Q4 | "字体大小"设置覆盖不到的文字 | 设置 → 界面，把字号改为 20 并**保存**（字体设置保存后才生效，见 architecture.md §6），截首页、聊天页、设置页；采集后改回 14 并保存 | 页面里有约 120 处写死的 `FontSize="数字"`，不跟随 `InterfaceFontSize` |
| Q5 | 弹出面板能否用 Esc 关闭 | 依次打开：聊天页"聊天存档"菜单（`ChatArchiveMenuButton`）、消息工具条（点消息气泡）、世界书导入选项，分别按 Esc。群聊成员菜单作为对照。这一步需要键盘焦点，**先征得用户同意**；不同意就跳过并标"未验证" | 要区分两类弹窗。**`Popup`**：`ChatView.xaml:295` `ChatArchiveMenuPopup`、`:678` 消息工具条（`IsToolbarOpen`）、`WorldbookView.xaml:54` `ImportOptionsPopup`，都设了 `StaysOpen="True"`，代码里没有找到 Esc 处理，这三个是真正的疑点。**`ContextMenu`**：`ChatView.xaml:163`、`:392` 群聊成员菜单，WPF 原生支持 Esc，预期正常。普通对话框有 `IsCancel` |
| Q6 | 只有图标的按钮缺少可访问名称 | UIA 树里统计 `controlType=Button` 且 `name` 为空的节点，列出它们的位置 | 所有 Views 合计只有 52 处 `AutomationProperties` |
| Q7 | 150% 缩放和最小窗口下是否有截断、重叠、出现横向滚动条 | 5.2 的截图 | — |

## 6. 不做的事

- 不改源码。需要验证缩放或字体时可以保存隔离设置，采集后恢复原值。
- 不配置真实 Provider，不发送聊天，不生成语音。
- 不追求像素级视觉评审。只记录明确的缺陷：截断、重叠、不可读、缺失、主题没有切换。

## 7. 验收标准

- 受影响区域有必要的基线截图，实际检查的疑点有结论。非 UI 校验修复不等待本项。
- 主题缺陷修复由实际可见问题决定（例如 Q1 确认后才修 `ComposerHintBrush`）；没有确认的问题不据静态疑点直接改色。
- 测试设置已恢复；若后续验证还要使用本次隔离实例，可继续复用，工作结束再关闭自建实例。

## 8. 交接

按 [README.md 第 4 节](README.md#4-交付说明) 简要说明实际观察及截图路径，继续已授权的相关工作。
