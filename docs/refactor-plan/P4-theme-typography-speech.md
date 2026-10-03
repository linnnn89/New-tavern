# P4 — 字号跟随设置与主题体验

> 与 P3 文件拆分没有先后依赖。字体修改依据下列源码证据；主题改色先观察目标区域。按完整改动组织提交。
> 先读 [README.md](README.md) 第 1 节的通用规则。

## 1. 目标

1. **P4b**：让字体设置覆盖固定的说明文字、正文与小标题，包括 C# 构造的语音设置页。
2. 修复实际观察到的主题缺陷，沿用 P1-4 的处理方式。
3. P4a 调色板迁到 XAML、P4c 语音页重写成 XAML 移出当前范围：现有形式可用，前者仍保留重复默认值，后者只统一代码风格，不是本次字体修复的前提。

**默认 100%、字号 14、浅色主题下，界面外观必须与 P0 基线截图无可见差异**（P4b 只在用户改字号时才产生变化）。

## 2. 现状（已核对）

### 主题

- `src/TavernDesk.App/Services/InterfaceSettingsRuntime.cs` 里有四个 C# 字典：`LightThemeBrushes`（第 30 行起）、`DarkThemeBrushes`（82）、`CupertinoThemeBrushes`（134）、`MaterialThemeBrushes`（186），每个 47 个画刷键。
- `ApplyThemeResources`（353–380 行）：深色时设 `Application.ThemeMode = Dark`，其余设 `Light`（使用 .NET 9+ 的 WPF Fluent `ThemeMode`，带 `#pragma warning disable WPF0001`）；然后把所选字典的每个颜色创建为冻结的 `SolidColorBrush`，**直接写入 `Application.Resources[key]`**。直接写入的键优先于合并字典，所以能覆盖 XAML 默认值。
- `App.xaml` 合并 `Themes/Light.xaml`、`Themes/AppicaPrimary.xaml`、`Themes/AppicaShell.xaml`。这三个文件里共有 48 个画刷键，其中 47 个与代码调色板重复（作为启动前默认值），只有 `ComposerHintBrush` 是 XAML 独有、不随主题切换（P1-4 可能已处理）。
- 视图里没有用 `StaticResource` 引用这些调色板画刷（已核对为 0 处），都是 `DynamicResource`，所以运行时替换有效。
- 私有测试 `SpeechTests.cs:408`、`SpeechDialogCloseTests.cs:146` 会把 `Themes/Light.xaml`、`AppicaPrimary.xaml`、`AppicaShell.xaml` 合并进测试窗口。改动这三个文件的内容时要保证这些测试仍能拿到画刷。

### 字号

- `InterfaceSettingsRuntime.Apply` 把用户字号写入资源 `InterfaceFontSize`，并派生 `ChatFontSize = FontSize + 1`。默认 14，按用户要求范围调整为 10–30。字体设置**保存后**才生效；旧设置中超过 30 的值读取时按 30 应用。
- 页面 XAML 里约有 120 处写死的 `FontSize="数字"`（属性写法），分布：`ChatView.xaml` 36、`CampaignsView.xaml` 25、`DashboardView.xaml` 19、`ProviderSettingsView.xaml` 8、`CharactersView.xaml` 6、`MainWindow.xaml` 6、`WorldbookView.xaml` 5、`CampaignMemorySettingsDialog.xaml` 5、`FirstRunLanguageDialog.xaml` 3、`ShellCaptionButtons.xaml` 3、`Light.xaml` 2，其余对话框各 1。
- 数值分布：12px×38、11px×29、13px×10、17px×9、15px×7、16px×7、10px×6、18px×4，19px 及以上共 12 处（19、20、21、22×2、23、24、25、30、36）。
- **另有 25 处在共享样式的 `Setter` 里**（复核时补上，初版漏算）：`<Setter Property="FontSize" Value="N" />`，`AppicaPrimary.xaml` 8 处、`AppicaShell.xaml` 12 处、`Light.xaml` 5 处。数值：11×3、12×5、13×4、14×2、15×1、17×2、18×2、20、22、28×2、30、32。这些样式被很多页面共用，**改样式的收益比改单个元素大**，也更容易一次影响很多地方。
  - 两处 14px（`AppicaPrimary.xaml:78`、`AppicaShell.xaml:387`）等于默认字号，可以直接改成 `{DynamicResource InterfaceFontSize}`。
  - 列出位置的命令：`Select-String -Path src\TavernDesk.App\Themes\*.xaml -Pattern 'Property="FontSize"\s+Value="(\d+)"'`。
- **图标字形**：用 `AppicaShellIconFont`（Segoe Fluent Icons / MDL2）或 `Light.xaml:1578` 图标样式显示的字号属于图形尺寸，不算文字。确定无疑的有：`ShellCaptionButtons.xaml:12/23/31`（10px，占了 10px×6 里的 3 处）、`ChatView.xaml:662`（15）、`:670`（12）、`Light.xaml:1579`（11）。按"前后 4 行内出现图标字体"做的启发式扫描还找到这些**候选**（有误报，需逐个打开确认）：`AppicaShell.xaml:73`、`CharactersView.xaml:217`、`ChatView.xaml:70/252/376/494/673`、`DashboardView.xaml:90/131/172/213/354/384`、`MainWindow.xaml:207/283/314`。其中 `ChatView.xaml:673` 已确认是语音状态文字（误报）。
- `SpeechSettingsView.cs:211` 的 `Note()` 默认字号 12；调用处另有标题 22、小标题 16。只改 XAML 会漏掉这部分真实文字，需使用现有 C# `SetResourceReference` 接入字号资源。
- `SafeChoiceDialog.cs` 的字号**有意固定**，不受应用缩放影响（architecture.md §6），不改。

### 语音设置页

- `src/TavernDesk.App/Views/SpeechSettingsView.cs`（259 行）用 C# 构建控件树，有两个宿主：`ProviderSettingsView.xaml:1260`（设置 → 语音，`DataContext="{Binding Speech}"`）和 `SpeechSettingsDialog.cs:25`（消息旁齿轮打开的对话框）。
- 私有测试按 **AutomationId** 查找控件，这些 ID 是契约，必须保留：`SpeechApiUrl`、`SpeechApiKey`、`SpeechModelChoice`、`SpeechModel`、`SpeechSpeed`、`SpeechLatencyChoice`、`SpeechDefaultVoiceId`、`SpeechSave`、`SpeechCancelTest`、`SpeechStatus`，以及按 `"Speech" + 属性名` 和 `"Speech" + 属性名 + "Error"` 生成的输入框与错误提示 ID（`SpeechSettingsView.cs` 第 232、242 行）。
- 测试还断言：这些控件的 UIA 名称、`LabeledBy`、`HelpText` 非空；底部保存按钮固定在可见区域内；校验失败时焦点移到出错字段；模型下拉 6 项，选"自定义"后显示自定义输入框。

## 3. 步骤

### P4b 字号梯度

1. 在 `InterfaceSettingsRuntime.Apply` 中，仿照现有 `ChatFontSize = FontSize + 1`，按用户字号派生一组资源。**默认字号 14 时必须精确得到现在的像素值**：

   | 资源键 | 公式 | 默认值 | 替换的写死值 |
   |---|---|---|---|
   | `FontSizeCaption2` | base − 4 | 10 | 10px（6 处） |
   | `FontSizeCaption` | base − 3 | 11 | 11px（29 处） |
   | `FontSizeSmall` | base − 2 | 12 | 12px（38 处） |
   | `FontSizeBodySmall` | base − 1 | 13 | 13px（10 处） |
   | `FontSizeBodyLarge` | base + 1 | 15 | 15px（7 处） |
   | `FontSizeSubtitle` | base + 2 | 16 | 16px（7 处） |
   | `FontSizeSubtitleLarge` | base + 3 | 17 | 17px（9 处） |
   | `FontSizeTitle` | base + 4 | 18 | 18px（4 处） |

   派生值设下限（建议 9），防止用户把字号调到 10 时说明文字小到不可读。键名可以调整，交付时说明最终映射。表中"处数"只统计属性写法，样式 Setter 里的另算，图标字形先排除。
   **必须同时在 `Themes/Light.xaml` 第 5 行 `InterfaceFontSize` 旁边加上这些键的默认值**（`<system:Double x:Key="FontSizeSmall">12</system:Double>` 等），和现有的 `InterfaceFontSize` 做法一致。原因：`SpeechTests.cs:408`、`SpeechDialogCloseTests.cs:146` 的测试窗口只合并主题 XAML，不运行 `InterfaceSettingsRuntime.Apply`；启动早期也是一样。缺少默认值时，`DynamicResource` 找不到资源，字号会退回 WPF 默认的 12px，可能让"保存按钮固定在可见区域"这类布局断言失败。
2. **先改共享样式，再改页面**：
   - 第一步：共享样式中属于文字且≤18px 的 Setter 改为 `Value="{DynamicResource 对应键}"`，14px 两处改为 `{DynamicResource InterfaceFontSize}`；先排除图标样式。检查代表性受影响控件，无需额外采集全页面。
   - 第二步：页面 XAML 中把上表的文字字号替换为 `FontSize="{DynamicResource 对应键}"`。按控件用途排除图标与固定尺寸对话框；同一批相关文件完成后统一检查受影响页面，无需每个文件单独截图。
3. **19px 及以上保持不变**：本次先解决说明文字、正文与小标题的可读性，页面大标题、大数字与图标保持原尺寸，不追加比例字号方案。
4. **图标字形保持写死**：第 2 节的候选逐个确认用途，属于图标的不替换；确认为文字的照常替换，不要求另写完整排除报告。
5. 不改 `SafeChoiceDialog.cs`、`LocalizedMessageBox.cs` 的固定字号。
6. 语音页 `Note()` 的 12/16px 说明与小标题使用相同字号资源，可保留原方法参数并在创建 `TextBlock` 后调用 `SetResourceReference(TextBlock.FontSizeProperty, 对应键)`；22px 标题按本次“大标题保持”的范围维持原值。不要因此重写控件树、PasswordBox 或校验聚焦代码。

验证：字号 14 检查受影响区域默认外观；字号 10、20、最大 30 检查说明文字与小标题是否跟随。只对容易挤压的聊天侧栏和语音设置页加测 150% 缩放、最小窗口组合，不做全页面交叉矩阵。极端组合按用户要求验证：仍能滚动进入设置页面，把字号调小并保存，不为这种组合继续重排整个界面。语音页与语音对话框检查保存按钮可见、错误提示和聚焦正常。恢复隔离设置。

## 4. 不做的事

- 只调整 P1-4 确认有问题的主题颜色，不新增主题或整体调色。
- 不改主题/字号/缩放的设置键和保存时机（主题、缩放即时预览，字体保存后生效）。
- 不引入第三方主题库。
- 不处理 19px 以上的显示字号（除非用户要求）。

## 5. 验证汇总

| 检查 | 期望 |
|---|---|
| 构建与定向测试 | 隔离副本 Release 构建通过；运行受字体修改影响的语音布局/对话框及设置持久化测试。按用户要求不运行全局测试 |
| 默认设置 | 受影响区域保持原外观，保留代表性截图 |
| 主题修复（若有） | 实际修改的主题键在对应主题下显示正确 |
| 字号 10 / 20 / 30 | 目标文字跟随变化；极端组合仍能通过设置页面调小字号 |
| 运行时绑定错误 | 无新增 |

## 6. 参考

沿用项目现有 `InterfaceFontSize`、`ChatFontSize` 和 `SetResourceReference` 模式。资源优先级需要排查时参考 [WPF 合并资源字典官方说明](https://learn.microsoft.com/en-us/dotnet/desktop/wpf/systems/xaml-resources-merged-dictionaries)，不例行重复检索。

## 7. 交接

按 [README.md 第 4 节](README.md#4-交付说明) 说明最终字号映射、实际布局验证与必要截图，继续已授权工作。
