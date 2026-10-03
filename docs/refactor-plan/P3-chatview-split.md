# P3 — 拆分 `ChatView.xaml`

> 角色面板使用 P2a 完成的绑定；其余区域不依赖 P2b。改动以搬移 XAML 为主，按完整改动组织提交。
> 先读 [README.md](README.md) 第 1 节的通用规则。

## 1. 目标

把 `src/TavernDesk.App/Views/ChatView.xaml` 中边界清晰的区域拆成独立 `UserControl`，降低定位与修改成本。界面外观和行为不变；P2a 已完成相关绑定，不再新建或删除临时转发层。

## 2. 区域地图（基线行号，执行前请重新核对）

`ChatLayoutRoot` 是五列 Grid：列 0 会话列表（`ConversationListColumn`，MinWidth 220）、列 2 会话正文（`ConversationBodyColumn`，MinWidth 360）、列 4 右侧面板（`RightPanelColumn`，MinWidth 260），列 1/3 是 6px 分隔。

| 区域 | 行号 | 依赖的 code-behind | 本阶段 |
|---|---|---|---|
| 会话列表 | 25–223 | 无事件处理；第 159 行用 `RelativeSource AncestorType=UserControl` 取 `DataContext.SelectConversationCommand` | **P3b** |
| 会话头部（存档菜单、群聊成员条） | 224–423 | `ChatArchiveMenuButton_OnClick`、`GroupMember_*` 一组处理器和 `_groupMemberMenuTimer` | 不动 |
| 消息列表与消息模板 | 424–914 | `MessageHost_OnMouseEnter/Leave`、`MessageBubble_OnPreviewMouseRightButtonUp`、`MessagePlus_*`、自动滚动、消息订阅 | **不动**（虚拟化、悬停和右键逻辑耦合紧，风险高） |
| 输入区 | 约 915–945 | — | 不动 |
| 右侧面板外壳与折叠按钮 | 946–956、1635–1636（`TabControl`/`Border` 结束）、1638–1680（折叠按钮覆盖层） | `RightPanelToggleButton_OnClick`、响应式折叠（code-behind 68–240 行） | 不动 |
| 右侧"上下文"页签 | 957–1155 | 第 1121 行 `RelativeSource AncestorType=UserControl` 取 `DataContext.Retrieval.ExcludeCommand` | **P3a** |
| 右侧"角色"页签 | 1156–1219 | — | **P3a** |
| 右侧"人设"页签 | 1220–1277 | 第 1236、1244 行使用 `PersonaEditorTextBox_OnPreviewMouseLeftButtonDown`（code-behind 51–66 行） | **P3a** |
| 右侧"记忆"页签（含 5 个子页签） | 1278–1462 | — | **P3a** |
| 右侧"会话"页签 | 1463–1634（`TabItem` 结束于 1634） | 第 1483 行使用 `ChatCompactOptionTemplate`（见 P3a 第 5 步） | **P3a** |

## 3. 步骤

### P3a 右侧五个页签

1. 新建目录 `src/TavernDesk.App/Views/Chat/`，为每个页签内容新建 `UserControl`：`ChatContextPanel`、`ChatCharacterPanel`、`ChatPersonaPanel`、`ChatMemoryPanel`、`ChatSessionPanel`。`TabItem` 本身（含 `Header`）留在 `ChatView.xaml`，只把内容搬走，这样 P0 记录的选中样式和页签宽度（见根目录 `design-qa.md` 的修复历史）不受影响。
2. DataContext 规则：**五个面板都不设 DataContext**，全部继承 `ChatViewModel`。
   - 上下文、人设、记忆、会话四个面板：内部绑定路径原样保留。人设页签本来就直接绑定 `Personas.*`（`PlayerPersonaManagerViewModel`）和父级的 `SavePersonaCommand/CancelPersonaCommand/OpenGlobalPromptCommand`，P2 没有为它新建子 VM。
   - 角色面板：保留 P2a 已改好的 `CharacterPrompt.*` 六个成员路径。`OpenGlobalPromptCommand`（1210 行）和 `IsSingleCharacterConversation` 属于父级，保持原路径。
   - 为什么不给角色面板设 `DataContext="{Binding CharacterPrompt}"`：面板里还有父级成员，设了就要用 `RelativeSource` 绕回父级，反而更脆弱。
   - 原写法带 `Mode=OneWay`、`StringFormat` 的绑定，改路径时保留这些参数。
3. **`RelativeSource AncestorType=UserControl` 陷阱**：内容搬进新的 `UserControl` 后，"最近的 UserControl 祖先"变成新控件本身。
   - 第 1121 行位于检索结果的条目模板内（条目自己的 DataContext 是检索项），靠 `AncestorType=UserControl` 拿到 `ChatViewModel`。搬进 `ChatContextPanel` 后，最近的 UserControl 变成该面板，而面板继承的 DataContext 仍是 `ChatViewModel`，所以**保持原写法不改**，只做实机验证（点击排除按钮生效）。
   - 由于五个面板都不设 DataContext，这类绑定的结果不变；仍需逐个确认。
   - 搬移时检查新控件里的 `AncestorType` 绑定，确认仍指向预期 DataContext；交付只说明实际修正，不逐条生成报告。
4. `PersonaEditorTextBox_OnPreviewMouseLeftButtonDown`（点击文本框空白处时把光标放到末尾）移到 `ChatPersonaPanel.xaml.cs`，从 `ChatView.xaml.cs` 删除。
5. **资源解析陷阱（必须处理）**：`ChatView.xaml` 第 7 行在 `UserControl.Resources` 里定义 `ChatCompactOptionTemplate`，用于 320 行（会话头部）、923 行（输入区）和 **1483 行（会话页签）**。拆出的 `ChatSessionPanel` 是独立编译的 BAML，`StaticResource` 在加载时向上查找，此时它还没有挂到 `ChatView` 的逻辑树上，会抛 `XamlParseException`（找不到资源）。处理方式（二选一，推荐 A）：
   - A. 把 `ChatCompactOptionTemplate` 移到 App 级字典 `Themes/AppicaPrimary.xaml`（三处使用者都能解析，模板内容不变）。移动前先用 `rg "ChatCompactOptionTemplate" src` 确认没有重名键。
   - B. 在 `ChatSessionPanel.Resources` 里复制一份（会产生重复定义，不推荐）。
   - 其他 `StaticResource`（样式、字符串）都在 `App.xaml` 合并字典里，搬移后可以解析。搬移前用 `rg -n "x:Key" src/TavernDesk.App/Views/ChatView.xaml` 列出 `ChatView` 本地的全部资源键，逐个确认搬出去的面板有没有用到。

### P3b 会话列表

1. 新建 `Views/Chat/ChatConversationList.xaml`，搬入 25–223 行的内容，`Grid.Column="0"` 留在 `ChatView.xaml` 的宿主元素上。
2. 第 159 行的 `RelativeSource AncestorType=UserControl` 会改为指向新控件，新控件继承 `ChatViewModel`，所以结果不变；验证会话点击仍能选中。
3. 不设子 DataContext（`ConversationBrowserViewModel` 目前通过 `ChatViewModel` 转发，改绑定不在本阶段范围）。

### P3c 清理

1. 检查搬移后的资源、事件和绑定调用点，不清理与本次搬移无关的父级转发。
2. `ChatView.xaml.cs` 只保留：右侧面板折叠/响应式布局、会话头部菜单、消息列表行为、自动滚动。

## 4. 不做的事

- 不拆消息模板和消息列表（风险高，收益与风险不成比例）。
- 不把响应式折叠逻辑改写成附加行为（项目没有 Behaviors 依赖，不新增）。
- 不改样式、字号、颜色、文案（P4 处理字号与主题）。
- 保留现有 `AutomationProperties` 值。若本次操作的图标按钮缺少可访问名称，可用已有本地化文案补充，无需为这个局部可逆修正单独审批；不扩展为全应用无障碍重构。

## 5. 验证

| 检查 | 期望 |
|---|---|
| 构建与定向测试 | Release 构建通过；运行实际受影响的现有测试。完整套件在工作包收尾统一运行 |
| 运行时绑定错误 | Debug 构建运行，切换全部页签和子页签，输出中无新增 `System.Windows.Data Error`，无绑定错误弹窗 |
| 界面检查 | 打开实际搬移的页签/列表，内容、操作和默认布局正常；聊天外壳涉及布局时再检查 150% 与最小窗口，保留必要截图即可，不比较 UIA 整树数量 |
| 实机流程 | 点击会话切换；上下文页签的排除按钮；人设页签点击文本框空白处光标到末尾；记忆子页签切换；会话页签操作；折叠/展开右侧面板 |
| 结构收益 | 父视图只保留外壳与未搬移区域；子控件资源和事件可直接定位，没有新增重复模板 |

## 6. 风险与回滚

- 最大风险是 `RelativeSource` 和 DataContext 变化导致的静默绑定失效（界面不报错，只是按钮无反应或内容为空）。逐个页签做实机点击验证，不能只靠编译。
- 按完整改动组织提交，可回滚相关控件迁移。

## 7. 文档

更新 [architecture.md §7](../architecture.md) 代码定位表，增加 `Views/Chat/` 目录说明。

## 8. 交接

按 [README.md 第 4 节](README.md#4-交付说明) 说明实际搬移与运行验证，继续已授权工作。
