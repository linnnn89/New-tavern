# 响应式布局与验证

日期：2026-10-13。基于 `跑团记忆升级版` 的 `b3bafbb`；只改变 UI 布局、验证脚本与文档，不改变数据库、生成流程或保存语义。

## 有限空间与坐标

`Presentation/ResponsiveViewport` 是小型 WPF `Decorator`。它在 `MeasureOverride` 中取得父布局提供的有限宽高，在测量子控件之前通知页面，并将自身 DesiredSize 限制在该约束内。主窗口、独立聊天窗口的缩放根和页面边界使用它。页面不能放在给予无限尺寸的外层 ScrollViewer/StackPanel 内；滚动应位于边界里面。

这些数值已经是应用 LayoutTransform 后的布局 DIP。系统 DPI 由 WPF 处理，应用缩放也由 WPF 的 LayoutTransform 处理，页面不再读取物理屏幕分辨率、扣固定窗口边框、识别 MainWindow，或重复除以 ScaleFactor。子控件 MinWidth、滚动 extent 和可能被内容撑大的 ActualWidth 都不是断点依据。

`ResponsiveFit` 只处理尺寸比较，恢复时增加 24 DIP 缓冲，首次正常显示按声明参考尺寸判断。字体因子 `max(1, InterfaceFontSize / 14)` 增大内容所需参考空间，小字号不会把布局压得更密。参考尺寸是内容需求，不是窗口尺寸。

`ResponsivePanes` 只负责两个已有控件的排列：改变行列、Visibility 和页签选择，不重新创建或搬迁控件、不替换 DataContext。页签隐藏的面板继续测量，保留编辑、选择和滚动容器；列表始终在有限高度内，未给列表添加外层 ScrollViewer。事件均在控件内部；没有静态订阅或 LayoutUpdated 轮询。接入商 ViewModel 的事件订阅随 Loaded/Unloaded 成对管理。

## 各页面规则

| 区域 | 参考条件（页面内部 DIP） | 空间不足时 |
| --- | --- | --- |
| 聊天检查器 | 用户会话列表宽度 + 12 + `460 × 字体因子` + 用户检查器宽度 | 自动收起；点击后原面板作为右侧浮层；关闭按钮/Esc 收起；空间恢复后自动折叠或打开的浮层重新并排；手动关闭保持关闭 |
| 聊天会话列表 | 用户列表宽度 + 6 + `460 × 字体因子` | 列表入口显示在聊天上方，点击临时覆盖；关闭按钮/Esc 返回聊天；扩大后恢复原宽度 |
| 聊天高度 | `420 × 字体因子` 高 | 压缩标题与输入区留白，状态保留提示，输入框单行高度并内部滚动，模型设置入口保留 |
| 检查器图标文字 | `1240 × 字体因子` 宽、`560 × 字体因子` 高 | 仅显示图标，原有提示与无障碍名称保留；标签显示时导航条至少 96 DIP；切换不修改用户检查器栏宽 |
| 接入商 | `780 × 字体因子` 宽；列表 220、详情占余量 | 列表／配置页签；表单滚动，保存固定在底部，清除密钥位于可滚动表单 |
| 模型目录 | `720 × 字体因子` 宽；详情 330 | 目录／本地限制页签；保存限制固定在详情底部，输入独立滚动；筛选与刷新工具栏换行 |
| 功能分配 | `760 × 字体因子` 宽 | 模型目录／分配概览页签；标签在输入框上方；概览、参数与目标选择共用滚动区，保存固定在底部，原命令不变 |
| 世界书 | `760 × 字体因子` 宽；词条列表 320 | 词条列表／正文页签；选择项和未保存标题、正文复用原绑定；选择器始终可见；高度低于 `620 × 字体因子` 时管理工具自动收起，用户可展开，工具滚动区最多占可用高度的 35% |
| 跑团库 | `860 × 字体因子` 宽 | 不足宽但至少 `660 × 字体因子` 高时上下排列；宽高均不足时已有跑团／剧本库页签；没有已有跑团时只显示剧本库 |
| 角色书架 | 沿用卡片 WrapPanel | 顶部搜索、密度、导入和批量操作工具栏换行，不改变书架与卡片布局 |

聊天两个分隔条只在用户完成拖动时更新记忆宽度。自动折叠、标签变化和重复尺寸通知不累计加减栏宽。页面切换不取消流式生成；原 ViewModel 与会话生命周期保持原有边界。`InterfaceSettingsRuntime` 仅在主题改变时重新应用主题，避免字号／缩放调整引发 WPF 模板重建。

## 可重复验证

```powershell
dotnet build TavernDesk.sln -c Release
./scripts/Test-Localization.ps1
dotnet run --project scripts/ResponsiveLayoutChecks -c Release
dotnet run --project scripts/GroupContextHistoryTdd -c Release
# Windows：
./scripts/Start-IsolatedTest.ps1 -StartupProbe
dotnet run --project scripts/ResponsiveLayoutWindowsChecks -c Release
```

Linux 交叉编译需 `-p:EnableWindowsTargeting=true`；它不能证明 WPF 运行时布局正确。仓库 `tests/` 是未跟踪的私有测试，此工作区没有该测试集。新的公共检查放在 `scripts/`，不声称重跑历史 359 项测试。

- 纯尺寸检查：84 项，含宽高断点、非法/无限空间、反复越界、100/125/150/200% 系统 DPI 的数学坐标约定、80/100/125/150% 应用缩放、10/14/20/30 字号。DPI 数学测试不等于真实显示器 DPI 验收。
- Windows WPF 检查：真实 Measure/Arrange、超大子控件与滚动 extent、LayoutTransform 单次换算、反复切换时控件身份/草稿/选择、列表虚拟化标志及实际滚动位置；使用隔离虚构资料创建实际 MainWindow 和 ConversationWindow，检查自动折叠、浮层关闭、用户栏宽与手动关闭状态；四语言标签边界与最小窗口发送按钮边界；接入商、模型目录、功能分配的宽窄切换。
- Windows 证据图：由实际窗口的 WPF 视觉树通过 RenderTargetBitmap 导出，记录代表性宽窄布局；不是整屏抓取，也不证明硬件 DPI、抗锯齿和多显示器移动效果。GitHub workflow 上传 `responsive-layout-evidence` artifact。
- 不连接真实 Provider，不测试付费请求。真实长对话流式上翻、人工鼠标来回拖动、实际多显示器 DPI 和全部长文本视觉组合仍需桌面人工验收。

## 本次执行结果

逐页 Linux Release 交叉构建通过，均为 0 警告、0 错误。纯尺寸 84 项通过；已有群聊离线上下文测试通过（8 阶段，API 禁用）。本地化检查四语言 1665 键通过；脚本的 bin/obj 排除规则已兼容 Windows 和 Linux 路径。

Windows CI [运行 38426890843](https://github.com/linnnn89/New-tavern/actions/runs/38426890843)（源码 `787bae6`）已通过完整构建、本地化、84 项纯尺寸、8 阶段离线上下文、隔离启动与 119 项真实 WPF 布局检查。证据图取自该运行。后续仅移除一处 XAML 行尾空白并补齐本文档及截图，PR 检查再次验证最终提交。历史发布快照 `app/` 未重新打包，本次交付为源码；构建安装包仍使用原有 Windows 发布脚本。

## 证据图说明

主窗口宽图为 1600 × 900、应用缩放 100%；窄图为 1080 × 620、应用缩放 125%，字号 14。独立聊天窗口为 1500 × 850 与 960 × 620，应用缩放 100%。另附 1080 × 500、150% 应用缩放、字号 30 的聊天图。

图中使用隔离测试数据；页面标签按被测语言创建，已初始化的导航/状态和虚构资料可能保留初始化语言。没有调用真实模型或写入用户资料。

| 页面 | 宽布局 | 窄布局 |
| --- | --- | --- |
| 主窗口聊天 | [宽](ui-evidence/responsive/main-chat-wide.png) | [窄](ui-evidence/responsive/main-chat-narrow.png)、[大字号](ui-evidence/responsive/main-chat-large-font.png) |
| 独立聊天窗口 | [宽](ui-evidence/responsive/independent-chat-wide.png) | [窄](ui-evidence/responsive/independent-chat-narrow.png) |
| 接入商 | [宽](ui-evidence/responsive/providers-wide.png) | [窄](ui-evidence/responsive/providers-narrow.png) |
| 模型目录 | [宽](ui-evidence/responsive/models-wide.png) | [窄](ui-evidence/responsive/models-narrow.png) |
| 功能分配 | [宽](ui-evidence/responsive/assignments-wide.png) | [窄](ui-evidence/responsive/assignments-narrow.png) |
| 世界书 | [宽](ui-evidence/responsive/worldbook-wide.png) | [窄](ui-evidence/responsive/worldbook-narrow.png) |
| 跑团库 | [宽](ui-evidence/responsive/campaigns-wide.png) | [窄](ui-evidence/responsive/campaigns-narrow.png) |
