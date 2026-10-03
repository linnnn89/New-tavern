# TavernDesk UI / 架构改进计划（P0–P6）

状态日期：2026-10-04。源码基线提交 `bf5d33e`；本目录随本轮改进入库。

本目录是执行与交接说明。P0–P5 是工作项编号，不是必须逐级批准的流程。已按用户授权推进 P1–P5；P0 与视觉改版由其他 AI 负责，不在本次重构中重复验收。第 2、3 节保留实施前基线，当前实施证据见第 7 节。

| 阶段 | 文件 | 主题 | 风险 |
|---|---|---|---|
| P0 | [P0-ui-baseline.md](P0-ui-baseline.md) | 按改动点采集运行时 UI 基线 | 无源码改动 |
| P1 | [P1-correctness-fixes.md](P1-correctness-fixes.md) | 恢复可信校验、组装路径统一、确认的主题缺陷 | 低 |
| P2 | [P2-chatviewmodel-split.md](P2-chatviewmodel-split.md) | 拆分 `ChatViewModel` | 中 |
| P3 | [P3-chatview-split.md](P3-chatview-split.md) | 拆分 `ChatView.xaml` | 中 |
| P4 | [P4-theme-typography-speech.md](P4-theme-typography-speech.md) | 字号跟随设置、修复确认的主题问题 | 中 |
| P5 | [P5-campaigns-settings-split.md](P5-campaigns-settings-split.md) | 拆分 `CampaignsViewModel` / `ProviderSettingsViewModel` | 中 |
| P6 | [P6-visual-refresh.md](P6-visual-refresh.md) | 视觉与交互改版（依据 [../ui-design-optimization-plan.md](../ui-design-optimization-plan.md)） | 中 |

## 1. 执行方式与范围

1. **按完整职责推进。** 先跑定向检查，相关修改验证通过后继续已授权工作，不在每个子步骤结束时重复询问。提交按可回滚的完整改动组织，不要求每个子步骤各建提交或 PR。
2. **按证据检索。** 先读本仓库的代码、版本与已有模式。只有本地证据不足或遇到陌生、版本敏感行为时查官方文档或源码；不要求每阶段例行搜索社区实现。
3. **范围控制。** 不处理无关问题。直接阻塞本任务或同一根因的问题可以一起修；只有实质扩大范围、改变兼容性或涉及破坏性操作时才询问。
4. **不新增依赖或框架。** 不引入 DI 容器、MVVM 框架（CommunityToolkit/Prism）、全局 Store、事件总线、Behaviors 包，与 [architecture.md §6](../architecture.md) 一致。
5. **不改持久化。** 不改 SQLite schema（当前 v24）、设置键（`ui.*` 等）、导入导出格式。
6. **保护用户数据。** 只用隔离测试资料（`scripts/Start-IsolatedTest.ps1`），不打开个人资料目录，不调用真实或付费 Provider。
7. 必要截图和临时证据放在 `work/refactor-qa/`（已被 git 忽略）。不要求为每个阶段生成独立报告文件或同时保存截图与 UIA JSON。
8. 与用户沟通使用简体中文。

### 建议执行顺序

| 工作包 | 内容 | 依赖与验收 |
|---|---|---|
| A：可信基线 | P1 校验修复与主窗口聊天装配统一；P0 仅采集实际改动区域 | 非 UI 校验修复不等待完整截图；建立可解释的测试基线 |
| B：职责整理 | P2a 角色提示词与六个绑定一起修改；P3 聊天视图搬移；P5d 数据与诊断设置 | P3 角色面板使用 P2a 的最终绑定；其余视图和 P5d 不依赖群聊记忆或主题迁移 |
| C：字体与主题体验 | P4 字号资源化，以及实际确认的主题缺陷 | 可以独立于 B 的文件拆分执行；验证默认外观与受影响页面布局 |
| D：异步状态拆分 | P2b 群聊记忆协调；P5a 跑团预览；P5b 记忆与预算面板 | 依赖与状态归属明确后迁移；定向验证结果应用、门控和事件生命周期 |

### 这次删减与保留的判断

- **删除过度流程**：逐阶段审批、强制外部检索、每阶段全套测试/本地化/全页面截图、UIA 整树节点数量完全相等、强制报告文件、以行数作为验收指标。
- **简化实现**：P2a 直接改相关绑定，取消先加后删的六个转发成员。
- **移出当前范围的低收益工作**：仅为存储形式把调色板迁到 XAML、仅为代码风格把语音页重写成 XAML，以及没有当前需求的接力/大厅/模型目录进一步拆分。这些不是安全措施，但当前收益不足以支持追加改动。
- **保留有效措施**：补齐真实依赖；保留记忆、预算、事务和密钥现有语义；核对 WPF 绑定与资源作用域；验证改动后的真实交互；针对实际异步调用检查结果归属和释放边界。
- **不追加推测性基础设施**：不为单纯搬移代码新建通用快照、后台调度、任务跟踪或取消框架。异步流程已有串行约束时保持它；只有实际允许刷新重叠或复现迟到覆盖时才增加局部刷新版本校验。

### 验证预算

每个工作包先运行最接近改动的现有测试与实际操作。新增自动化测试通常 1 项，最多 3 项，覆盖具体行为；不为文件拆分本身加测试。本次用户明确要求不运行全局测试，收尾也只做定向验证。只改本地化脚本时运行本地化检查；纯 C#/XAML 搬移不重复运行无关检查。失败如实记录，单独重跑通过不等于全量通过。

## 2. 基线事实（2026-10-03 实测）

| 项目 | 结果 |
|---|---|
| `dotnet build TavernDesk.sln -c Release --no-restore` | 成功，0 警告 0 错误 |
| 私有测试 `dotnet test tests\TavernDesk.Tests\TavernDesk.Tests.csproj -c Release --no-restore` | **342/349 通过，7 项失败**。原因已查明：本地 `tests/` 文件是 CRLF 换行（未纳入 git，所以没有被 `.gitattributes` 规范化），种子数据里的旧版提示词因此带 `\r\n`，"按原文精确匹配才升级"的迁移不会触发。属于测试环境问题，不是产品缺陷。P1 修复。 |
| `scripts/Test-Localization.ps1` | **失败**：`en-US contains untranslated zh-CN values: Speech.ApiKey`。值是品牌名 "Fish Audio API Key"，中英文相同，属于误报。P1 修复。 |
| 四语资源键数量 | 均为 1596，键集合一致 |
| 隔离启动 | `Start-IsolatedTest.ps1` 返回 `window-shown` |
| 已知偶发失败 | `MemoryAndGroupTests.DirectSaveAllowsSecondEditAndSave`：全量并行运行时偶尔失败（等待第二次保存超时），单独运行稳定通过（复核时 LF/CRLF 两种情况各跑 3 次，全部通过）。与换行无关。处理方式见 P1-2 |

失败的 7 项测试（均在 `ProviderAndStreamingTests.cs`）：
`CampaignGmThirdPersonDefaultUpgradesExactPreviousDefault`、`CampaignActionRollDefaultsUpgradeFromExactPreviousDefaults`、`PlainGroupHistoryPromptMigrationReplacesTheOldBuiltInChatPrompt`、`CampaignSpeakerOwnershipDefaultUpgradesFromExactV5Prompt`、`CampaignGmNoReplayDefaultUpgradesFromExactV6Prompt`、`CampaignEventLifecycleDefaultUpgradesExactV8Prompts`、`ExactLegacyRoleplayDefaultsUpgradeWithoutOverwritingCustomPrompts`。

## 3. 代码体量（改动热点）

| 文件 | 行数 |
|---|---|
| `src/TavernDesk.App/ViewModels/ChatViewModel.cs` | 3121 |
| `src/TavernDesk.App/ViewModels/CampaignsViewModel.cs` | 2872 |
| `src/TavernDesk.App/ViewModels/ProviderSettingsViewModel.cs` | 1805 |
| `src/TavernDesk.App/Views/ChatView.xaml` / `.xaml.cs` | 1681 / 544 |
| `src/TavernDesk.App/Themes/Light.xaml` | 1698 |
| `src/TavernDesk.App/Services/InterfaceSettingsRuntime.cs`（四套调色板写在代码里） | 381 |

项目依赖图：App → Core、Infrastructure；Infrastructure → Core；AgentHost → Core、Infrastructure；Tests → App、Infrastructure。没有循环依赖。

## 4. 交付说明

工作包完成后在对话中说明实际改动、验证与限制即可；创建 PR 时写简洁描述，不额外生成报告文件。需要保存的截图给出路径。

```markdown
# <工作包> 交付说明

## Delivered
做了什么（面向用户的变化 + 内部变化）。

## Files
改动的重要文件及一句话说明。

## Verification
实际执行过的命令和结果（构建、测试数量、本地化脚本、截图路径）。未执行的不写。

## Decisions
重要取舍、检索到的参考链接、偏离计划之处及原因。

## Limitations
已知问题、跳过的验证、发现但未处理的相邻问题。

```

## 5. 尚待用户决定（不阻塞 P0–P2）

| # | 问题 | 默认处理 |
|---|---|---|
| D1 | `/tests/` 被 git 忽略，回归测试只存在于本机。是否入库或放进私有仓库？ | 维持现状；P1 在本地固定历史提示词测试种子的 LF 换行，不自动公开测试 |
| D2 | `app/` 发布快照（420 个文件）和根目录 `TavernDesk.exe` 被 git 跟踪，pack 已达 457 MiB。是否改为只通过 Releases 分发？ | 维持现状 |
| D3 | 弹窗不因外部点击关闭（#22）。是否至少支持 Esc 关闭？ | P0 先核实当前 Esc 行为，再请用户决定 |

## 6. 文档维护

P2、P3、P5 改动了代码位置后，同步更新 [architecture.md](../architecture.md) 的 §2 / §6 / §7（代码定位表）。只写实际执行过的验证结论。

## 7. 当前验证证据（2026-10-03）

实施前计划评审的构建、342/349 私有套件及本地化失败属于第 2 节的历史基线，不代表修改后的结果。

- P1：恢复本地化检查与历史提示词测试种子的 LF 换行；等待实际保存完成；统一主窗口聊天装配路径。
- P2：拆出角色提示词与群聊记忆协调，六个提示词绑定直接使用子对象。
- P3：聊天视图按完整面板搬入 `Views/Chat/`，同步架构定位表。
- P4：字号资源跟随设置，允许范围 10–30；修复语音页底部、导航和聊天发送区在空间不足时的恢复路径。四语本地化检查 1596 项通过，6 项相关定向测试分别通过；隔离窗口完成字号 30 调回 14、保存并读取 SQLite 确认。极端设置的验收边界是能回到设置调小字号。
- P5：拆出资料与诊断、跑团预览、跑团记忆及预算面板。新增 3 项私有测试和 8 项现有相关测试分别通过；新增跑团测试首次因测试种子缺少必填字段失败，补齐后通过。覆盖持久化失败回退、目录切换安排与取消、预算门禁、旧局迟到结果、StateVersion 冲突及进度订阅释放。没有运行完整套件。
- P5 隔离界面已确认跑团预览显示、设置对话框使用子对象、预算保存 24000 后未保存修改 28000 再关闭重开仍为 24000。真实 WPF Dispatcher 场景确认排队后切局或释放时丢弃旧进度。`--test-root` 下资料目录按设计只读，非覆盖模式的切换安排与取消由临时目录集成测试验证。
- P5 当前源码副本纯 Release 构建 0 警告、0 错误；构建前移除测试探针，源码文件哈希与工作区一致。Release 隔离初始化探针返回 `initialized` 并以 0 退出，真实测试窗口也已正常关闭。
- 数据页实际确认 API 测试模式开启、清空一个虚构输出文件后输出目录保留且目录外文件未删除；相关页面、跑团对话框和聊天会话检查未记录绑定警告或错误。关闭模式的键盘动作因 WinCode 未确认焦点而未执行，没有重复抢焦点；关闭顺序与失败回退由定向测试验证。记忆开关的持久化已由真实临时库测试验证，UIA 的 TogglePattern 只改变控件勾选，不触发其命令，因此不将这次 UIA 操作记作实机开关验收。在资源管理器打开输出目录的入口由集成测试确认调用，未另开资源管理器窗口。
- 消息菜单：仅将聊天消息工具 Popup 的 `StaysOpen` 改为 `False`；用户已实测确认点击外部后自动消失。未扩大到其他 Popup 或全局菜单样式。

本地测试和界面证据位于 `work/refactor-qa/p5/`，测试目录继续被 Git 忽略。构建和界面检查使用独立源码副本及虚构资料，不调用真实 Provider。副本曾因混入新聊天视图而缺少同步更新的头像转换器资源，已整体同步当前源码重新构建；这次测试副本错误不作为当前产品源码缺陷记录。

上述 P1–P5 验证限制是前序对话的执行边界。本轮接收交接后统一完成 Release 构建、359/359 本地私有测试、四语 1602 键检查、已确认设置与聊天布局的真实窗口交互及 Windows 重新打包。P6 的具体交付、截图来源和未覆盖范围见 [UI 优化计划 §16](../ui-design-optimization-plan.md#16-本轮推进与收尾2026-10-03-至-2026-10-04)，并不表示全部视觉规划已完成。
