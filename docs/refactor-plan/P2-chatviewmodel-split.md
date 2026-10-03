# P2 — 拆分 `ChatViewModel`

> 前置：相关测试基线可解释。P2a 与角色页签绑定一起修改；P2b 可独立执行，不阻塞 P3。按完整改动组织提交。
> 先读 [README.md](README.md) 第 1 节的通用规则，以及 [architecture.md §4.2–4.4、§6](../architecture.md)。

## 1. 目标

把 `src/TavernDesk.App/ViewModels/ChatViewModel.cs` 中角色提示词编辑、群聊记忆协调两块职责拆出，明确它们的输入、状态与调用边界。用户可见行为不变；不以文件缩短或构造参数数量作为完成指标。

## 2. 沿用的项目内先例

本项目已经用同一方式拆过三次，照着做，不发明新模式：

| 先例 | 做法 |
|---|---|
| `ConversationBrowserViewModel`（会话列表） | `ChatViewModel` 构造时创建子对象，通过委托回调父级（如 `DeleteConversationAsync`），父级用只读属性转发（`ConversationGroups => _conversationBrowser.Groups`） |
| `ChatContextPreviewViewModel`（上下文预览） | 父级订阅子对象的 `PropertyChanged` 再转发通知（`OnContextPreviewPropertyChanged`，约第 2702 行） |
| `InterfaceSettingsViewModel`（界面设置） | 父级持有并转发原有属性、命令和变化通知，保持 XAML 绑定兼容（architecture.md §6） |

**绑定方式**：P2a 直接把角色页签的六个相关成员改为 `CharacterPrompt.*`，测试同步使用子对象，不增加待 P3 删除的转发属性。页签仍继承父级 DataContext，因为它还使用父级全局提示词入口和单聊状态。已有其他组件的转发不在本次清理范围。

## 3. 方法地图（基线 `bf5d33e` 的行号，执行前请重新核对）

| 职责块 | 主要成员与行号 | 与生成主流程的耦合 | 本阶段 |
|---|---|---|---|
| 角色提示词显示与编辑 | 字段 `_characterPromptCharacterId/_characterPromptCharacterName/_characterSystemPrompt/_characterPostHistoryInstructions/_characterPromptStatus`（约 62–66）；属性 `CharacterPromptCharacterName/CharacterSystemPrompt/CharacterPostHistoryInstructions/CharacterPromptStatus`（297–319）；`CanEditCharacterPrompt/EditCharacterSystemPromptAsync/EditCharacterPostHistoryAsync/EditCharacterPromptAsync/ApplyCharacterPrompts`（2488–2601）；命令 `EditCharacterSystemPromptCommand/EditCharacterPostHistoryCommand`（构造 183–188，声明 214–215） | 低：只用于显示，生成时从仓储读取角色卡，不读这些属性 | **P2a** |
| 人设镜像（**留在父级**） | `PersonaName/PersonaDescription/GlobalPreset/PersonaStatus`（414–456）、`OnPersonaManagerPropertyChanged/ApplyActivePersona`（458–476）、`RefreshPersonaPresentation/EffectivePersonaLabel/EffectiveCharacterMacroName`（1964–2003）、`LoadPersonaAsync/SavePersonaAsync/CancelPersonaEdits`（2445–2486） | **高**：`PersonaName` 是生成输入（912、1626、1803、2784 行），`RefreshPersonaPresentation` 遍历 `Messages`、`EffectiveCharacterMacroName` 读 `_conversationBrowser.Characters` 和 `SelectedConversation`。人设**编辑器**本身已经在 `PlayerPersonaManagerViewModel`，人设页签直接绑定 `Personas.EditorName/EditorDescription/Profiles/SelectedProfile/Status`（`ChatView.xaml:1231–1251`） | 不做 |
| 全局提示词入口（**留在父级**） | `OpenGlobalPromptCommand/OpenGlobalPromptAsync`（189、2603–2614） | 被 ChatView 6 处、CampaignsView 2 处绑定，失败时写 `Status` | 不做 |
| 群聊记忆协调 | 字段 `_invalidGroupMemoryScopes`、`_unsavedGroupMemoryBodies`（40–42）；`GenerateGroupMergeAsync`（1662）、`OnMemoryBodySaved/OnMemoryBodyChanged/ForgetUnsavedGroupMemoryBody`（1690–1747）、`TriggerGroupAutoMemory/TriggerGroupAutoMemoryCoreAsync/UpdateGroupMemoryAsync`（1748–1826）、`GetInvalidGroupMemoryScopes/MarkGroupMemoryInvalid/ClearGroupMemoryInvalid`（1827–1869） | 中低：接力结束后触发，有自己的失效标记；但外部调用点较多（见 P2b） | **P2b** |
| 群聊接力与倒计时 | `StartGroupContinueAsync`（1020–1166）、`ContinueGroupRelayAsync`…`SaveGroupStateAsync`（1346–1650），倒计时字段 73–75 | 高：直接使用生成会话、上下文组装、`Status` | 不做 |
| 消息操作 | `EditMessageAsync`…`CopyMessage`（2029–2428），其中 `RegenerateMessageAsync` 约 230 行 | 高 | 不做 |
| 发送、选择加载、流式会话应用 | `SendAsync`、`LoadSelectionAsync`、`ApplyGenerationSession` 等 | 核心 | 不做 |
| 语音 | `SpeechKey`…`ConfigureSpeechAsync`（1903–1941），约 40 行 | 低，但体量太小 | 不做（拆了收益小于转发成本） |

> 上次评审曾提议拆"消息编辑/候选/分支"和"语音绑定"。细看后调整：消息操作与生成会话耦合太深，语音太小，都不适合作为第一刀。

## 4. 步骤

### P2a 新建 `ChatCharacterPromptViewModel`

位置：`src/TavernDesk.App/ViewModels/ChatCharacterPromptViewModel.cs`。

> 复核更正：初版计划叫 `ChatPersonaPromptViewModel`，想把人设也一起搬走。核实后发现人设**编辑器**已经独立在 `PlayerPersonaManagerViewModel`；`ChatViewModel` 里剩下的 `PersonaName` 等是生成输入的镜像，和消息列表、会话浏览器纠缠，搬走只会增加转发。所以本步只拆"角色提示词"一块。

1. 迁入第 3 节"角色提示词显示与编辑"一行列出的字段、属性、两个命令和方法。**不迁** `OpenGlobalPromptCommand`、任何 `Persona*` 成员、`LoadPersonaAsync`（它同时加载 `chat.displayMode`）、`SaveDisplayModeAsync`。
2. 依赖通过构造函数传入：`ICharacterRepository`、`IUserInteractionService`，以及以下回调（与 `GroupChatViewModel` 接收委托的方式一致）：
   - `Func<bool> isSingleCharacterConversation`：对应 `CanEditCharacterPrompt` 里的 `IsSingleCharacterConversation`；
   - `Func<string?> selectedConversationId`：对应 `SelectedConversation?.Id`（2501、2555 行）；
   - `Action<Character> onCharacterSaved`：对应 `_conversationBrowser.UpdateCharacter(character)`（2553 行）；
   - `Func<Task> refreshContextNow`：对应 `RefreshContextEstimateAsync(immediate: true)`（2561 行）。
3. 子对象公开 `Apply(Character?)`（原 `ApplyCharacterPrompts`）和 `RaiseCanExecuteChanged()`。父级调用点：`ApplyCharacterPrompts(...)` 在 255、782、804、905 行；`EditCharacter*Command.RaiseCanExecuteChanged()` 在 272–273 行。
4. `ChatViewModel` 暴露 `public ChatCharacterPromptViewModel CharacterPrompt { get; }`。角色页签四个属性和两个命令改为 `CharacterPrompt.*`；保留原来的 `Mode=OneWay`、`StringFormat` 等参数。父级 `OpenGlobalPromptCommand`、`IsSingleCharacterConversation` 的路径不变。这样不需要父级转发六个成员及其属性通知。
5. `ProviderAndStreamingTests.cs:2469/2476/2489` 的相关读取同步改为 `viewModel.CharacterPrompt.CharacterSystemPrompt`。
6. 验证编辑、保存、显示、上下文刷新以及单聊/群聊切换；仅删除已经迁入子对象且调用方已更新的旧成员。

### P2b 新建 `GroupMemoryCoordinator`

位置：`src/TavernDesk.App/ViewModels/GroupMemoryCoordinator.cs`。它不是绑定用的 ViewModel，不继承 `ViewModelBase`。

1. 迁入第 3 节"群聊记忆协调"一行的字段和方法。
2. 依赖（已按源码核实）：`IGroupMemoryUpdateService`（字段 `_groupMemory`）、`MemoryWorkflowViewModel`（父级的 `Memory`，代码里用了 8 次），以及以下回调：
   - `Func<GroupChatViewModel> group`：代码里用了 8 次 `Group`，必须**延迟读取**，原因见第 4 条；
   - `Func<ConversationListItemViewModel?> selectedConversation`；
   - `Func<string?, bool> isSelectionReady`：保持原 `IsSelectionReady(selected.Id)` 的会话身份校验；
   - `Func<string> personaName`：用于更新后 `Memory.LoadAsync(..., userIdentity: PersonaName)`，保持原有取值时机；
   - `Action scheduleContextRefresh`（用了 5 次）；
   - `Action<string> setStatus`（写父级 `Status`）。
   - **不需要** `IGroupChatRepository`（初版计划写错了）。
3. 协调器公开下列方法，供父级调用：
   - `ForgetUnsavedBody()`：父级 777、799 行；
   - `TriggerAutoMemory(...)`：父级 1158、1308、1954、2049、2075、2280 行；
   - `GetInvalidScopes(conversationId)` 和 `HasUnsavedBody(conversationId)`：父级 `CreateContextRequest` 的 2739–2740 行，替代直接读 `_unsavedGroupMemoryBodies`；
   - `GenerateMergeAsync`、`UpdateAsync`：交给 `GroupChatViewModel`。
4. **构造顺序**：先在 `Memory` 之后创建协调器，`Group` 用 `() => Group` 延迟读取；再构造 `Group`，传入协调器方法组。构造函数不执行这些回调；不为构造依赖引入两段式初始化或 `Initialize()`。
5. `Memory.BodyChanged` / `Memory.BodySaved` 的订阅（160–161 行）移到协调器构造函数；`BeginDispose`（3080 行，解除订阅在 3095–3096 行）改为调用协调器的 `Dispose()`/`Detach()`。
6. **不变量**（来自 architecture.md §4.4，必须保持）：群聊记忆更新按会话串行；更新期间消息增删改或人工保存记忆会让旧快照失效并重算；自动保存要同时校验记忆版本和来源指纹；单次最多处理三批。迁移时只搬代码，不改这些逻辑。
7. 协调器随窗口释放时解除自身订阅；已经启动的应用级记忆更新继续遵守原服务生命周期，不因文件拆分新增取消主管。异步返回后保留原会话归属检查，释放后不再调用该窗口的 UI 回调。

## 5. 不做的事

- 除 P2a 的六个相关绑定外，不改变其他聊天绑定。
- 不动 `ChatViewModel` 构造函数里 `chatReplies ?? new`、`personas ?? new` 的兜底（4 个测试文件依赖），除非拆分自然消除了它。
- 不引入接口抽象、DI 容器或消息总线。
- 不改任何文案、设置键、持久化。

## 6. 验证

| 检查 | 期望 |
|---|---|
| Release 构建 | 0 警告 0 错误 |
| 定向测试 | P2a 运行角色提示词编辑现有测试；P2b 运行相关群聊记忆与选择隔离测试。完整套件在工作包收尾统一运行 |
| 绑定错误 | 用 Debug 构建运行，确认输出中没有新增的 `System.Windows.Data Error`。以往验证中"绑定与 Dispatcher 错误均为零"的做法可在 `docs/codex_worklog.md` 中搜索"绑定"找到，沿用同一方法 |
| 实机流程（隔离实例，不调用真实 Provider） | 角色页签：编辑系统提示词和历史后指令 → 保存后显示更新、上下文预览刷新；切换到群聊时两个编辑按钮变为不可用；切回单聊显示正确角色。人设页签（未改动，作回归）：修改并保存后上下文预览更新。群聊：打开群聊记忆页签，手动更新按钮的可用状态正确；编辑记忆正文但不保存时，上下文预览提示未保存 |
| 结构收益 | 子对象承担完整职责，依赖清单与调用点完整，没有为本次迁移增加待删除的转发层 |

## 7. 风险与回滚

- 主要风险是改绑定后漏通知、命令可用状态不更新，以及记忆加载丢失会话/人设输入。用对应交互验证，不比较整棵 UIA 树的节点数量。
- **已知坑**：项目曾出现只读属性被默认双向绑定导致运行时错误。P2a 改路径时保留显示属性的 `Mode=OneWay`，不为满足绑定而添加无意义 setter；真实打开角色页签验证。只检查本次修改的绑定。
- 相关职责可独立回滚，按完整改动组织提交。

## 8. 文档

更新 [architecture.md](../architecture.md)：§4.2 末尾补一句职责变化（参照 2026-09-11 那段的写法），§7 代码定位表增加两个新文件。

## 9. 交接

按 [README.md 第 4 节](README.md#4-交付说明) 说明实际职责变化和验证，继续已授权工作。
