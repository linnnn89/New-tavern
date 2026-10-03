# P5 — 拆分 `CampaignsViewModel` 与 `ProviderSettingsViewModel`

> 不依赖 P4。P5d 可独立实施；P5a/P5b 先明确下列依赖与状态边界。跑团与设置按各自完整改动组织提交。
> 先读 [README.md](README.md) 第 1 节的通用规则，以及 [campaign_mode_design.md](../campaign_mode_design.md)、[architecture.md §4.5、§6](../architecture.md)。

## 1. 目标

沿用现有子对象与回调模式，拆出有完整职责的跑团预览、记忆/预算面板及数据/诊断设置。能局部改绑定时直接绑定子对象。用户可见行为不变，不以减少行数作为验收指标。

## 2. 先例

- 剧本编辑已拆出 `CampaignScenarioEditorViewModel`（architecture.md §6"剧本编辑与事务边界"）：父级负责导航、选择和忙碌状态，通过属性转发保持 XAML 绑定兼容。
- 界面设置已拆出 `InterfaceSettingsViewModel`（architecture.md §6"界面设置的职责边界"）：父级持有子组件并转发属性、命令、变化通知。

## 3. P5-A `CampaignsViewModel`（2872 行，构造函数 18 个参数）

### 方法地图（基线行号，执行前重新核对）

| 职责块 | 主要成员 | 耦合 | 本阶段 |
|---|---|---|---|
| 上下文预览 | `RefreshContextPreviewAsync`（2163）、`SetContextPreviewBlock`（2300）、`AddContextPreviewItem`（2310）、`EffectiveInputBudget`、`ContextPlanStatusText`、`ContextBlockReason`、`ContextSectionTitle`、`ContextBlockedHelpText`（2402）、`ContextSectionStateText`（2337–2425，多为静态函数）；字段 `_contextPreviewBlocked`、`_contextPreviewBlockingReason`、`_contextBlockedSeatReasons`（80–82）；属性 `ContextPreviewSummary`（423）、`HasContextPreview`（424）、集合 `ContextPreviewItems` | **中**（复核上调）：预览**不是只读**。阻断状态参与游戏门控：329、344 行（回合推进是否可用）、361、379 行（帮助文字）、2578 行（每个席位能否行动） | **P5a** |
| 跑团记忆面板 | `PrepareCampaignMemorySettings`（1599）、`SaveCampaignMemorySettingsAsync`（1649）、`TryParseSetting`（1749）、`ToggleCampaignMemoryAsync`（1772）、`RetryCampaignMemoryAsync`（1795）、`RefreshCampaignMemoryStatusAsync`（1839）、`LatestCompletedGmResolution`（1919）、`SetCampaignMemoryStatus`（1931）、`OnCampaignMemoryProgressChanged`（2692）、`CompleteMemoryOperation`（2757）、`MemoryScopeName`（2772）；字段 `_activeMemoryOperations`、`_memoryTokensByOperation`；属性 `CampaignMemoryStatusText`…`CanRetryCampaignMemory`（405–425）、`IsMemoryUpdating`、`MemoryProgressText`、`MemoryReceivedTokenText`；命令 `RetryCampaignMemoryCommand`、`ToggleCampaignMemoryCommand`、`SaveCampaignMemorySettingsCommand` | 中：进度事件和忙碌状态与游戏桌面共用 | **P5b** |
| 准备大厅 | `SaveLobbyAsync`…`LoadDraftIntoLobbyAsync`（1123–1305）、`ApplyScenarioToLobby`…`ApplyRoute`（2446–2554） | 中 | 不做 |
| 剧本库 | `RefreshLibraryAsync`…`BackToLibraryAsync`（879–1122） | 中 | 不做 |
| 游戏桌面与回合执行 | `RefreshGameAsync`…`ApplyGmRouteAsync`（1306–1585）、`LoadGameAsync`（1941–2135）、`RefreshSeatActionStates`、`RaiseGameProperties`（2564–2643） | 高：直接驱动 `ICampaignRunner`，涉及唯一终态、取消和重试 | **不做** |

### P5a 新建 `CampaignContextPreviewViewModel`

1. 迁入上表"上下文预览"一行的成员，静态格式化函数一起迁入。
2. 依赖 `ICampaignContextPlanner?`、`ICampaignScenarioRepository`、`ICampaignMemoryRepository?` 和父级正在使用的同一个 `ICampaignFlowEngine`。每次刷新由父级传入本次 `CampaignAggregate`；父级提供 `Func<CampaignAggregate, bool> isCurrentGame`（如 `game => ReferenceEquals(_game, game)`）用于应用前确认。保持 planner 与 memory repository 的可空语义，不在跨 `await` 的流程里反复获取父级当前 `_game`。
3. **门控状态必须对父级可读**。子对象公开只读成员：`IsBlocked`（原 `_contextPreviewBlocked`）、`BlockedHelpText`（原 `ContextBlockedHelpText()`）、`TryGetSeatBlockReason(seatId, out reason)`（原 `_contextBlockedSeatReasons` 查询）。父级 329、344、361、379、2578 行改为读这些成员。
4. **预览刷新后父级要重新计算游戏属性**：现在 `RefreshContextPreviewAsync` 改完阻断状态后，父级依赖的属性（回合推进可用性、帮助文字、席位行动状态）要通知界面刷新。迁移后，子对象在刷新结束时调用父级传入的 `Action onPreviewChanged`，父级在其中调用 `RaiseGameProperties()` / `RefreshSeatActionStates()`。执行前先读一遍 2163–2310 行，看清现在是在哪里触发这些通知的，保持同样的时机。
5. `CampaignsView.xaml` 涉及 3 个绑定（已核对）：1024 行 `HasContextPreview`、1026 行 `ContextPreviewSummary`、1031 行 `ContextPreviewItems`。直接改为 `{Binding ContextPreview.HasContextPreview}` 等，**不保留转发属性**（改动小，适合直接改绑定）。
6. 跑团上下文预算的规则以 [TavernDesk-R2-B-Campaign-Context-Budget.md](../TavernDesk-R2-B-Campaign-Context-Budget.md) 为准，只搬代码不改逻辑。
7. 原方法在异步计划完成后、最终 campaign ID 检查之前就写入预览集合和阻断状态。迁移时先在局部构建结果，在应用前确认父级仍持有本次传入的 aggregate，再更新集合、阻断与通知；错误结果也遵守同一归属检查。保持现有调用链串行等待，不为拆分新增独立后台刷新、深拷贝快照或通用取消框架。只有实际允许同一 aggregate 的刷新重叠时，才增加局部刷新版本校验。
8. 验证重点：上下文超预算时回合推进禁用、帮助文字和席位提示不变；使用可控 planner 完成一次迟到结果场景，确认旧局结果不会修改当前局状态。优先复用现有测试替身。

### P5b 新建 `CampaignSettingsPanelViewModel`（记忆 + 上下文预算设置）

> 复核更正：初版只考虑了记忆。实际上记忆设置对话框同时编辑上下文预算，它的 DataContext 是整个 `CampaignsViewModel`。

1. 迁入上表"跑团记忆面板"一行的成员，**加上**对话框用到的全部预算字段：`CampaignContextTokenBudgetText`、`CampaignPlayerHistoryBudgetText`、`CampaignGmHistoryBudgetText`、`CampaignMemoryUpdateIntervalRoundsText`、`CampaignMemoryPendingTokenThresholdText`、`CampaignMemorySettingsStatusText`（字段约 73–78 行，属性约 430 行起），以及 `PrepareCampaignMemorySettings`（1599）。
2. 依赖与回调（按源码核实）：
   - `SaveCampaignMemorySettingsAsync` 通过 `_campaigns.UpdateContextSettingsAsync(…, _game.Campaign.StateVersion, …)` 保存（1695–1697 行），然后调用父级的 `LoadGameAsync(campaignId)`（1704 行）重新加载，外面包着父级的 `RunUiAsync`（1690 行，管理忙碌状态）。
   - 因此子对象需要：`ICampaignRepository`、`ICampaignMemoryRepository?`、`ICampaignMemoryUpdateService?`、`Func<CampaignAggregate?> currentGame`、`Func<Func<Task>, Task> runUiAsync`、`Func<string, Task> reloadGame`、读取页面/忙碌状态的回调、写父级 `StatusText` 与通知父级刷新状态的回调。
   - `CanRetryCampaignMemory` 等属性保持读取父级页面和忙碌状态。1719 行实际属于 `CanMoveGmCandidate`，不是记忆重试；GM 候选选择命令留在父级，不迁入设置面板。
   - 这些忙碌状态**留在父级**。子对象状态变化时通知父级刷新（父级 `RaiseGameProperties` 里与记忆相关的通知改为调用子对象）。
3. 保持记忆进度经 `RunOnUi` 更新。当前事件在排队前检查 campaign ID；迁移后在真正执行 UI 回调时也检查当前局和面板是否释放，避免排队期间切换局后写错状态。`LoadGameAsync:1956` 在切换局时清空记忆操作集合的逻辑同步迁入面板的切换入口。
   - 当前 `CampaignsViewModel` 没有释放方法。若面板接管事件订阅，应同时补上父级调用它解除订阅的实际应用退出入口（核对 `MainWindow.xaml.cs:79` 的关闭链）；仅定义无人调用的 `Dispose()` 不算完成。
   - 页面导航不释放应用持有的面板，也不取消应用级记忆任务。保留记忆后台更新不阻塞本地导航的现有语义，不增加另一套任务主管。
4. **对话框绑定**：
   - `src/TavernDesk.App/CampaignMemorySettingsDialog.xaml` 位于项目根目录，不在 `Views/`。它的 DataContext 在 `CampaignsView.xaml.cs:23-28` 设置：先调用 `viewModel.PrepareCampaignMemorySettings()`，再 `DataContext = viewModel`。改为 `viewModel.SettingsPanel.Prepare()` 和 `DataContext = viewModel.SettingsPanel`。
   - 对话框的全部绑定（已核实，16 个）：`CampaignContextTokenBudgetText`、`CampaignPlayerHistoryBudgetText`、`CampaignGmHistoryBudgetText`、`CampaignMemoryUpdateIntervalRoundsText`、`CampaignMemoryPendingTokenThresholdText`、`CampaignMemorySettingsStatusText`、`CampaignMemoryStatusText`、`CampaignMemoryActionText`、`CampaignMemoryToggleText`、`IsCampaignMemoryEnabled`、`CanToggleCampaignMemory`、`CanRetryCampaignMemory`、`ShowCampaignMemoryAction`、`ToggleCampaignMemoryCommand`、`RetryCampaignMemoryCommand`、`SaveCampaignMemorySettingsCommand`。这些都属于子对象，所以对话框内部的绑定路径**不用改**，只换 DataContext。其中 `CanToggleCampaignMemory`/`CanRetryCampaignMemory` 依赖父级忙碌状态，由第 2 条的 `Func<bool>` 回调计算。
   - `CampaignsView.xaml` 中记忆区域的绑定改为 `SettingsPanel.*`。用 `rg "CampaignMemory|MemoryProgress|MemoryReceived|IsMemoryUpdating|Budget" src tests` 找全引用。
5. 不变量（campaign_mode_design.md / architecture.md §3）：跑团记忆是事件日志的派生投影；每局独立；取消和显式重试的语义不变；保存预算时的 `StateVersion` 乐观并发检查不变。

## 4. P5-B `ProviderSettingsViewModel`（1805 行）

### 方法地图（基线行号）

| 职责块 | 主要成员 | 本阶段 |
|---|---|---|
| 资料目录与诊断 | `LoadDataRootSettings`（603）、`LoadDiagnosticsSettingsAsync`（631）、`SetApiTestModeAsync`（661）、`OpenApiTestOutputAsync`（719）、`ClearApiTestOutputAsync`（738）、`RefreshApiTestOutputSummaryAsync`（767）、`FormatFileSize`（785）、`PickDataRoot`（804）、`ChangeDataRootAsync`（814） | **P5d** |
| 模型目录与功能分配 | `RefreshModelsAsync`（1248）…`ToggleReasoningAsync`（1701）、`TryReadLimits`（1734） | 不做 |
| 接入商资料编辑与密钥 | `ReloadProfilesAsync`…`RefreshProfileReferencesAsync`（876–1247）、`KeyStatusFor` | 不做（涉及密钥轮换与删除顺序的安全约束） |

### P5d 新建 `DataAndDiagnosticsSettingsViewModel`

1. 迁入上表第一行成员及其字段、属性、命令。依赖与父级现在持有的类型一致：`IAppSettingsRepository?`（字段 `_appSettings`）、`AppDataLocationService?`、`ITavernDeskDiagnostics`、`IFileDialogService`、`IUserInteractionService`；设置键 `diagnostics.apiTestMode.enabled` 不变。
2. 子对象挂在 `ProviderSettingsViewModel` 下，数据页签直接绑定子对象；父级 `LoadAsync` 调用子对象加载。现有 `ProviderSettingsViewModel.ApiTestModeSettingKey` 可保留为常量兼容入口，不为其新增运行时转发层。
3. 安全约束（README / architecture.md §3、§5，必须保持）：
   - 更改资料目录只**记录**为下次启动执行，本次运行不切换；重新保存当前目录可取消待执行切换。
   - API 测试模式默认关闭；开启时界面明确提示会记录对话内容；清空只删除 `tests\output` 的直接子项并保留目录。
   - 启用模式后若设置持久化失败，关闭刚启用的诊断模式并显示失败；关闭时维持原有持久化与运行时关闭顺序。
4. 保持现有保存时机。`ConfirmCanLeaveAsync` 当前检查语音和 Provider 草稿，不因拆分新增数据页未保存拦截。

## 5. 不做的事

- 不动 `CampaignRunner`、回合流程策略、游戏桌面执行代码。
- 不动接入商密钥相关代码。
- 不改 schema、设置键、事件日志格式。
- 不新增依赖或框架。

## 6. 验证

| 检查 | 期望 |
|---|---|
| 构建与定向测试 | Release 构建通过；按用户要求只运行对应预算门控、记忆、数据与诊断的定向测试，不运行完整套件 |
| 运行时绑定与布局 | 打开实际修改的游戏桌面/记忆对话框/数据页，无新增绑定错误，必要时保存截图；不采集未修改页签 |
| 实机流程（隔离资料，不调用真实 Provider） | 跑团：打开已有跑团 → 上下文预览显示；记忆开关、记忆设置对话框保存与取消；离开页面时未保存提示。设置：切换 API 测试模式开/关、打开与清空测试输出目录、选择新资料目录后显示"下次启动生效"并可取消 |
| 状态边界 | 旧局预览与排队进度不更新当前局；预算保存保留 StateVersion 检查；面板事件有实际解除订阅入口 |

## 7. 风险与回滚

- 跑团记忆进度事件在后台线程触发，迁移后要确认仍经 `RunOnUi` 回到 UI 线程。
- 按完整职责组织提交，可回滚相关迁移。新增测试合计遵守当前工作包最多 3 项，不为每个搬出的方法各写一个测试。

## 8. 文档

更新 [architecture.md](../architecture.md) §6 两段职责边界说明和 §7 代码定位表。

## 9. 交接

按 [README.md 第 4 节](README.md#4-交付说明) 说明职责变化、实际验证和限制。只汇总本次授权范围，不附加下一轮拆分任务。
