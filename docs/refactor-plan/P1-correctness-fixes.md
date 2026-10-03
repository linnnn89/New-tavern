# P1 — 正确性修复（低风险）

> 非 UI 校验修复可直接执行；只有主题改色需要先观察相关区域。改动量：小，按完整修复组织提交。
> 先读 [README.md](README.md) 第 1 节的通用规则。

## 1. 目标

1. 修复七项历史提示词迁移测试与本地化误报，建立可信回归信号。完整套件的失败如实处理，不用单独重跑结果替代。
2. 消除"主窗口自行组装聊天 ViewModel"的第二条路径，这条路径会悄悄丢掉语音功能。
3. 修复 P0 确认的主题漏网问题。

**字号梯度不在本阶段**，已移到 P4（改动面大，和主题一起做更合适）。

## 2. 工作项

### P1-1 修复本地化校验误报（必做）

**现象**：`scripts/Test-Localization.ps1` 第 88 行抛出 `en-US contains untranslated zh-CN values: Speech.ApiKey`。

**原因**：脚本把"en-US 的值和 zh-CN 完全相同"视为漏翻。`Speech.ApiKey` 的值在 zh-CN、zh-TW、en-US 里都是品牌名 `Fish Audio API Key`，属于合法的相同值。脚本里已经有允许列表 `$protocolLiteralKeys`（第 71–77 行），用于 `Characters.Role.System` 这类协议字面量。

**做法（推荐 A）**：
- A. 在脚本里加一个品牌名允许项，把 `'Speech.ApiKey'` 加入允许列表。可以新建一个 `$brandLiteralKeys` 数组并在第 81 行的过滤条件里一起排除，避免把品牌名混进"协议字面量"的语义。**不改任何界面文字。**
- B.（不推荐，会改变用户看到的中文）把 zh-CN 改成 `Fish Audio API 密钥`。

**验证**：`& .\scripts\Test-Localization.ps1` 退出码为 0。

### P1-2 修复测试换行导致的 7 项失败（必做）

**原因**：`tests/` 被 `.gitignore` 排除，不受 `.gitattributes` 的 checkout 换行规则约束，本机相关测试源文件为 CRLF。C# 原始字符串字面量会保留源文件换行，使历史提示词种子带 `\r\n`；当前迁移按旧默认值精确匹配，不触发这些测试种子的升级。当前产品源码为 LF，先修复测试种子；本次证据不支持扩展生产迁移的匹配范围，不改变保护自定义提示词的逻辑。

**做法**：只在相关迁移测试的历史默认提示词种子中明确采用 LF，例如将该固定种子的 `\r\n` 替换为 `\n` 后写入测试数据库。保留历史文本与精确匹配断言，不规范化实际结果或自定义提示词，不复制生产迁移逻辑生成预期值。这使测试不再依赖源文件换行；无须批量改写整个 `tests/`。这些测试仍只在本地，按 D1 维持原有可见性。

**验证**：先运行七项受影响迁移测试；工作包完成时运行一次完整套件（总数以实际为准）。

**复核实测（2026-10-03，在仓库内的临时副本上做的，本机 `tests/` 未改动）**：
- 只把副本转成 LF 后全量跑：348/349。原来 7 项换行失败**全部修复**。
- 多出的 1 项失败是 `MemoryAndGroupTests.DirectSaveAllowsSecondEditAndSave`（第 114 行，等待第二次保存完成）。LF 副本和原 CRLF 版本里单独各跑 3 次通过；后续本仓库全量复核中也通过。可以确认它不由换行修复解释，不能仅凭重跑通过认定保存逻辑没有问题。
- 源码存在一个明确的测试完成信号差异：`SaveBodyAsync` 先清除脏状态，`AsyncRelayCommand` 到外层 `finally` 才恢复可执行。现有测试只等待 `!IsBodyDirty`，随后立即执行第二次保存。调整这项现有测试：第二次保存前同时等到正文不脏且保存命令可执行，继续断言数据库最终为第二版。命令状态差异是源码证据，但尚未确定性复现为该偶发失败的根因；若调整后仍失败，依据实际错误继续诊断，不反复重跑刷绿，也不顺手重写保存流程。

### P1-3 统一聊天 ViewModel 的组装路径（必做）

**现状**：
- 正式启动路径：`App.xaml.cs:137-151` 用 `ChatViewModelFactory` 创建 `ChatViewModel`，传入语音服务 `Speech`、`SpeechSettings`、`ChatReplies`，再传给 `MainWindowViewModel`。
- 第二条路径：`MainWindowViewModel.cs:40-66` 在参数 `chat` 为 `null` 时自己 `new ChatViewModel(...)`，**没有传 `speech`、`speechSettings`、`chatReplies`**。任何走这条路径的调用方都会得到没有语音的聊天页。
- 目前唯一走第二条路径的是私有测试 `tests/TavernDesk.Tests/ProviderAndStreamingTests.cs:3238`（`ApplicationNavigationDoesNotCancelSharedGeneration`）。

**做法**：
1. `MainWindowViewModel` 构造函数的 `chat` 参数改为必填（`ChatViewModel chat`，去掉默认值），删除第 42–66 行的兜底创建；`personas` 改为直接取 `chat.Personas`。
2. 更新上面那个测试：用 `new ChatViewModelFactory(services, interaction, fileDialog).Create()` 创建 `chat` 后传入。注意 `ChatViewModelFactory` 构造时会创建 `SpeechPlaybackService` 和 `WindowsSpeechAudioOutput`；如果测试环境因此失败，记录原因并改为在测试里直接 `new ChatViewModel(...)`（与 `SpeechTests.cs:339` 的写法一致），**不要**为测试保留生产代码里的兜底分支。
3. `ChatViewModel` 内部的 `chatReplies ?? new …`、`personas ?? new …` 维持现状：4 个测试文件依赖它们，当前不是独立问题，不列为后续必改项。

**验证**：构建与相关导航/语音测试通过；隔离启动后检查聊天页语音入口，必要时保留一张截图。完整套件在工作包收尾统一运行。

### P1-4 主题漏网画刷（依 P0 结论决定是否做）

- 如果 P0 的 Q1 确认 `ComposerHintBrush` 在非浅色主题下显示不对：把 `ComposerHintBrush` 加入 `InterfaceSettingsRuntime.cs` 的四套调色板（第 30/82/134/186 行开始的四个字典），值取各主题强调色加约 8% 透明度（浅色保持现值 `#142563EB`）。`Light.xaml:43` 的定义保留作为启动前默认值。
- 如果 P0 的 Q2/Q3 确认 `CharactersView.xaml:336`、`DashboardView.xaml:84/103` 的硬编码颜色在深色下有问题：新增对应画刷键，加入四套调色板，XAML 改为 `{DynamicResource 新键}`。**没有确认的不要改。**
- 图片上的渐变遮罩（`CharactersView.xaml:303-304`）、阴影（`WorldbookView.xaml:69`）、首次启动对话框（`FirstRunLanguageDialog.xaml:28`）不改。

**验证**：检查目标主题与默认浅色的受影响区域，确认缺陷修复且原有显示正常；改了四套颜色时再覆盖四种主题，不重复采集全页面。

## 3. 不做的事

- 不改迁移逻辑、schema、设置键。
- 不拆分任何 ViewModel（P2）。
- 不做字号梯度（P4）。
- 不处理 D1/D2/D3（等用户决定）。

## 4. 验收标准

| 检查 | 期望 |
|---|---|
| `dotnet build TavernDesk.sln -c Release --no-restore` | 0 警告 0 错误 |
| 私有测试 | 0 失败 |
| `scripts/Test-Localization.ps1` | 退出码 0 |
| 隔离验证 | 装配改动执行一次实际聊天页检查；纯测试/本地化修复不额外启动 UI |
| 隔离启动后聊天页 | 语音按钮存在；若做了 P1-4，目标主题截图正确 |
| `git grep "new ChatViewModel(" src` | 只剩 `ChatViewModelFactory.cs` 一处 |

## 5. 风险与回滚

相关修复按完整改动提交，出问题可回滚该提交。P1-2 改动的是本机未跟踪测试文件，执行前保存原文件副本，不用转换换行充当回滚。

## 6. 交接

按 [README.md 第 4 节](README.md#4-交付说明) 说明修复及实际验证结果，继续已授权工作。
