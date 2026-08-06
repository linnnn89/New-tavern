# TavernDesk R2-B：跑团上下文预算与低频记忆更新实施方案

> 来源：ChatGPT 会话“跑团记忆银行设计”（conversation ID：`6a73fed1-8164-83e8-a2e3-8d99189f2584`）的最后一条 AI 方案回复。
>
> 本文件是当前项目的执行基准。每进入下一个工作包前，必须重新阅读本文件中对应的范围、约束、验收标准和当前执行状态；不得只凭上一次对话摘要继续实现。

## 一、版本定位

版本名称：`R2-B Campaign Context Budget`

核心目标：

1. GM 和 AI 玩家请求使用统一上下文预算。
2. 默认单次模型请求上限为 15,000 tokens。
3. 发送前显示分项 Token 估算。
4. 保证当前回合、最新 GM 场景和角色身份优先。
5. 历史根据剩余预算动态截取。
6. 固定资料超限时明确阻止生成，不静默删改角色卡。
7. 跑团记忆不再每轮必更新。
8. 记忆只处理已经完成 GM 裁定的事实区间。

## 二、范围定义

### 2.1 15,000 Token 的含义

15,000 tokens 定义为单次 AI GM 或单个 AI 玩家生成请求的“输入上下文 + 预留输出”容量上限，不是整个回合所有模型调用的合计上限。界面可以显示本阶段总输入估算和输出上限合计，但合计只用于成本提示，不作为阻止生成的统一限额。

### 2.2 本版本明确不实现

- `CampaignFact`；
- Participant Memory；
- 向量检索；
- 自动压缩角色卡；
- 自动改写剧本或世界书；
- 通用工作流引擎；
- 新 Agent 或子 Agent；
- 持久化后台队列；
- Provider 专用 Token 规则矩阵；
- 每个上下文分区几十项可配置参数。

## 三、架构方案

```text
CampaignEvent / CampaignMemory / CharacterSnapshot
                         │
                         ▼
              CampaignContextPlanner
                         │
        ┌────────────────┴────────────────┐
        │                                 │
GM Context Plan                   Player Context Plan
        │                                 │
        └────────────────┬────────────────┘
                         ▼
                  ITokenEstimator
                         │
          ┌──────────────┴──────────────┐
          │                             │
ProviderChatMessages             Token Breakdown
          │                             │
          ▼                             ▼
    实际模型请求                    跑团界面预览
```

关键原则：预览和实际发送必须使用同一个 Context Planner。Planner 不得调用 Provider，只负责组装、预算分配、历史筛选、Token 估算和诊断返回。

### 3.1 核心接口

```csharp
public interface ICampaignContextPlanner
{
    Task<CampaignContextPlan> BuildPlayerPlanAsync(
        CampaignAggregate aggregate,
        CampaignParticipant participant,
        CampaignMemoryBank? publicMemory,
        CancellationToken cancellationToken = default);

    Task<CampaignContextPlan> BuildGmPlanAsync(
        CampaignAggregate aggregate,
        CampaignScenario? scenario,
        CampaignMemoryBank? gmMemory,
        CancellationToken cancellationToken = default);
}
```

```csharp
public sealed record CampaignContextPlan(
    IReadOnlyList<ProviderChatMessage> Messages,
    IReadOnlyList<CampaignContextSectionEstimate> Sections,
    TokenEstimate Estimate,
    CampaignContextPlanStatus Status,
    string? BlockingReason = null);
```

```csharp
public sealed record CampaignContextSectionEstimate(
    string Id,
    string Title,
    ContextSegmentKind Kind,
    int EstimatedTokens,
    bool IsMandatory,
    bool WasIncluded,
    bool WasTruncated);
```

```csharp
public enum CampaignContextPlanStatus
{
    Ready,
    HistoryTrimmed,
    BlockedMandatoryContextTooLarge
}
```

## 四、持久化设置

### 4.1 Campaign 新增字段

```csharp
public int ContextTokenBudget { get; set; } = 15000;
public int MemoryUpdateIntervalRounds { get; set; } = 3;
public int MemoryUpdatePendingTokenThreshold { get; set; } = 4000;
```

| 字段 | 默认值 | 用途 |
|---|---:|---|
| `ContextTokenBudget` | 15000 | 单次 GM/玩家请求容量上限 |
| `MemoryUpdateIntervalRounds` | 3 | 每累计多少完整轮次允许触发一次记忆更新 |
| `MemoryUpdatePendingTokenThreshold` | 4000 | 未进入记忆的已裁定事件达到此规模时提前更新 |

最终容量为 `min(模型 ContextLimit, campaign.ContextTokenBudget)`。保留已有 `GmContextLimit`、`Participant.ContextLimit`、`GmMaxOutputTokens`、`Participant.MaxOutputTokens`、`PlayerHistoryBudget` 和 `GmHistoryBudget`。

### 4.2 数据库迁移

新增 schema v16：

```sql
ALTER TABLE campaigns ADD COLUMN context_token_budget INTEGER NOT NULL DEFAULT 15000;
ALTER TABLE campaigns ADD COLUMN memory_update_interval_rounds INTEGER NOT NULL DEFAULT 3;
ALTER TABLE campaigns ADD COLUMN memory_update_pending_token_threshold INTEGER NOT NULL DEFAULT 4000;
```

应用边界读取限制：`ContextTokenBudget` 8000–200000；`MemoryUpdateIntervalRounds` 1–50；`MemoryUpdatePendingTokenThreshold` 1000–50000。迁移不得修改旧跑团记忆或自动调用模型。

## 五、上下文预算算法

```text
有效总预算 = min(跑团设置预算, 模型 ContextLimit)
输入预算 = 有效总预算 - 预留输出 MaxOutputTokens
```

如果 `MaxOutputTokens >= 有效总预算`，直接阻止生成并提示模型配置不合理。

### 5.1 必须保留的固定上下文

AI 玩家：全局玩家 Prompt、运行协议、actor 身份、玩家席位名单、世界设定和公开规则、冻结角色卡、导入的初始角色记忆、原世界知识。

GM：全局 GM Prompt、GM 协议、世界设定和公开规则、GM 专用剧本说明、开场设置、玩家席位和角色能力资料。

固定内容不自动压缩、不静默删除。固定内容超过输入预算时必须阻止生成并显示具体超限分区。

### 5.2 必须保留的当前状态

AI 玩家：最新 GM 开场或裁定、对该角色可见的本轮待裁定行动、当前行动任务。GM：本轮所有已锁定 `PlayerIntent`、本轮行动骰、当前 GM 裁定任务。当前状态超过预算时也必须阻止生成。

### 5.3 弹性上下文和顺序

弹性上下文为 GM/Public 长期记忆和过去已完成裁定的事件历史。预算顺序为：预留输出 → 固定资料 → 当前回合 → 长期记忆 → 旧历史。长期记忆动态上限：

```csharp
memoryBudget = Math.Min(
    memory.TargetTokens,
    3000,
    remainingAfterMandatory * 40 / 100);
```

历史从最新向旧事件回溯，每条事件整体保留；最新 GM 裁定属于当前状态区；跳过被替换的失败尝试并遵守角色可见性。历史耗尽时返回 `HistoryTrimmed`，不是错误。

## 六、上下文分区建议

AI 玩家：系统规则、角色与席位身份、世界和规则、角色卡快照、初始角色记忆、原世界知识、公共长期记忆、旧历史、最新 GM 场景、本轮其他玩家公开提交、当前角色任务。

GM：系统规则、GM 专用说明、世界和规则、玩家席位及角色资料、GM 长期记忆、旧历史、本轮全部 `PlayerIntent`、当前 GM 裁定任务。

稳定资料在前，动态资料在后，以利用 Provider 前缀缓存。

## 七、跑团 Token 预估界面

在跑团游玩页右侧“当前步骤”区域新增默认折叠的“本轮上下文估算” Expander。

AwaitingActions 阶段逐个显示 AI 席位的输入估算、预留输出、容量和状态；秘密同投显示请求数、输入合计和输出合计（只作成本提示）。ReadyForResolution 阶段显示 GM 估算和分区明细。

状态文案：`上下文在预算内`、`较旧历史已按预算省略`、`固定资料与当前回合内容已超过预算`、`当前模型使用启发式 Token 估算`。

仅在打开/重载、行动完成、GM 裁定完成、模型切换、预算修改或记忆更新完成后刷新。预估不得调用 Provider、Embedding 或记忆模型。

## 八、记忆更新策略

### 8.1 权威边界

只处理“上次 checkpoint 后到最新成功锁定的 `GmResolution`”，不得吸入最新 GM 裁定之后的新一轮 `PlayerIntent`。

```csharp
Task<CampaignMemoryUpdateResult> UpdateAsync(
    string campaignId,
    long throughEventSequence,
    bool force = false,
    CancellationToken cancellationToken = default);
```

`throughEventSequence` 必须来自刚刚成功保存的 GM 裁定。

### 8.2 自动触发

只在成功完成 GM 裁定后检查，满足任一条件时更新：checkpoint 后至少 3 个完整轮次，或 checkpoint 后到最新 GM 裁定的已裁定事件估算达到 4000 tokens。不因玩家提交、AI 玩家提交/重试、单独掷骰、打开/重载或切换模型触发。

### 8.3 严格先攻、旧跑团和失败恢复

严格先攻使用 `checkpoint.ProcessedRound` 和最新已完成完整 `RoundNo`，不能统计 `GmResolution` 数量。没有 bank/checkpoint 的旧跑团打开时显示“尚未建立”和“从已裁定历史建立”按钮，不自动调用模型；用户点击时使用 `force=true` 和最新 GM Resolution sequence。初始化失败只显示“更新失败，可重试”，打开页面不自动重复调用。

## 九、记忆正确性边界

公共记忆不能只判断 `Visibility == Public`，应复用：

```csharp
bool IsEventVisibleToPublicMemory(
    Campaign campaign,
    CampaignEvent campaignEvent,
    long throughResolutionSequence);
```

GM 记忆事件载荷补充 `recipient_id`、`recipient_name`、`structured_data`；Public 记忆只接收经过可见性过滤的安全字段。

## 十、工作包

每个工作包单独完成、单独验证，禁止一次性全改。

### 工作包 A：数据模型和迁移

目标：新增三个 Campaign 设置字段和 schema v16。

主要文件：`Campaign.cs`、`SqliteDatabase.cs`、`SqliteCampaignRepository.cs`、`DatabaseAndRepositoryTests.cs`、`CampaignTests.cs`。

必须测试：新数据库默认值；v15→v16 默认值；保存/重读一致；重复初始化幂等；非法值在应用边界限制。完成标准：定向测试通过、Release 构建 0 error、不修改 Runner 和 UI。

### 工作包 B：纯上下文 Planner

目标：实现 `ICampaignContextPlanner` 和结果模型，不接入 Runner。

主要文件：`ICampaignContextPorts.cs`、`CampaignContextPlan.cs`、`CampaignContextPlanner.cs`、`InfrastructureServices.cs`、`CampaignContextPlannerTests.cs`。

实现要求：复用 `ITokenEstimator` 和现有上下文语义；返回最终 `ProviderChatMessage` 和分项估算；不调用 Provider、不改数据库、不更新记忆。

必须测试：默认 15,000；采用较小模型上限；最新 GM 场景保留；GM 本轮全部玩家行动保留；AI 玩家隔离 GM-only；只裁剪旧历史；固定角色卡超限返回 Blocked；不静默删除固定资料/当前行动；长期记忆动态上限；角色卡分项差异；GPT tokenizer 与非 GPT 回退估算。

完成标准：Planner 定向测试通过；不修改 `CampaignRunner`；不添加 NuGet 依赖。

### 工作包 C：接入 CampaignRunner

替换 `BuildPlayerMessages`、`BuildGmMessages` 和本地 `ApproximateTokens` 判断。流程为 Build Plan → 检查状态 → 使用 `Plan.Messages` 创建请求；实际发送前重新 Build Plan。必须验证 Planner/Runner 消息完全一致、Blocked 不调用 Provider、HistoryTrimmed 仍生成、行动/模型切换重算，以及现有身份隔离/秘密同投/GM 尾部协议测试不退化。

### 工作包 D：跑团 Token 预估 UI

页面加载调用 Planner 但不调用 Provider 或自动更新记忆；按 AI 席位和 GM 阶段显示估算；异常不崩溃；超限禁用对应生成按钮并显示原因。必须做小窗口、多 AI 滚动、默认折叠和不挤压记录区的人工截图检查。

### 工作包 E：低频记忆更新和边界修复

修改 `CampaignMemoryUpdateService`、`CampaignRunner`、`CampaignsViewModel` 和对应测试；可新增纯策略类 `CampaignMemoryUpdatePolicy`。覆盖行动/骰点不更新、3 完整轮次或 4000 Token 阈值、只到最新 GM Resolution、严格先攻、秘密同投公开生命周期、PrivateDelivery recipient、非法输出 checkpoint、并发去重、旧跑团无自动模型调用、手动 force 建立。

### 工作包 F：最终回归

回归命令：三个按 `CampaignContext`、`CampaignMemory`、`CampaignRunner` 过滤的 Release 测试；完整 `dotnet test TavernDesk.sln -c Release --no-restore`；`dotnet build TavernDesk.sln -c Release --no-restore`；`git diff --check`。

人工短局：1 USER + 1 AI + AI GM 协作圆桌 4 轮；2 AI + AI GM 秘密同投 2 轮；1 USER + 2 AI + AI GM 严格先攻完整 1 轮；大角色卡超限；模型上限小于 15,000。检查预览、消息一致性、超限不发请求、3 轮前不更新记忆、阈值后更新、下一轮未裁定内容不入记忆和 GM-only 隔离。

### 工作包 G：每局升级版记忆 ON/OFF

目标：在跑团游玩页右侧提供每个跑团独立保存的胶囊式 `ON/OFF` 开关。

ON 时保持当前 R2-B 记忆更新、长期记忆上下文注入和 Token 预估行为；OFF 时不调用记忆总结模型，也不向 GM/AI 玩家请求注入 GM/Public 长期记忆分区或其结构化包装，但仍保留角色卡、规则、当前行动和近期原始历史。

主要文件：`Campaign.cs`、`SqliteDatabase.cs`、`SqliteCampaignRepository.cs`、`CampaignMemoryUpdateService.cs`、`CampaignRunner.cs`、`CampaignContextPlanner.cs`、`CampaignsViewModel.cs`、`CampaignsView.xaml` 及对应测试。

实现约束：

- 新增 `MemoryEnabled`，默认值为 `true`；schema v17 迁移旧跑团为 ON，不删除已有 bank/checkpoint。
- UI、Runner、Planner 和 MemoryUpdateService 都要检查开关，不能只依赖按钮状态阻止 Provider 调用。
- OFF 后的新 GM 裁定、玩家行动、骰点、页面打开、重载和模型切换均不得触发记忆模型；已在进行中的 Provider 请求不承诺可撤回。
- 重新 ON 后从原 checkpoint 继续；不因切换 ON 自动追溯调用模型，下一次成功 GM Resolution 才按既有阈值检查；用户可使用手动 force 建立/补齐。
- OFF 不清除已有记忆数据；上下文预估仍显示，但长期记忆分区为禁用/0，不把 OFF 误报成固定资料超限。

必须测试：v16→v17 默认 ON、保存/重读开关、克隆继承开关；OFF 不调用 Provider 且不出现长期记忆结构；OFF 仍保留近期事件和身份隔离；ON 恢复后从 checkpoint 继续；Runner 自动触发和 UI 手动按钮均遵守开关。

## 十一、执行约束

```text
只完成当前工作包，不扩大范围。
禁止引入 Agent、多智能体、消息总线、通用状态框架、向量数据库、
新缓存系统、新 Provider 适配器和新 NuGet 依赖。
优先复用 ITokenEstimator、ContextSegment、TokenEstimate、
CampaignEvent 可见性和 ProviderChatMessage。
不要顺带重构无关聊天、世界书、Provider 或角色书架代码。
固定资料超限时返回明确错误，不自动摘要或删除角色卡、剧本规则、当前回合行动。
每个工作包先跑定向测试，再跑相关回归；不得为通过测试而弱化断言。
需求与现有代码冲突时停止该部分修改，报告文件、现有行为、冲突和最小备选方案。
不要下载 Hugging Face 模型，不进行系统级安装。
```

## 十二、验收定义

- 单次 GM 和 AI 玩家请求默认受 15,000 Token 预算控制；
- 实际请求和界面预览由同一个 Planner 生成；
- 分项包含角色卡、世界、Prompt、记忆、历史、当前回合和输出预留；
- 固定资料或当前回合超限时明确阻止；历史超限只裁剪旧历史；
- 不再每个 GM 裁定都必然更新记忆；默认每 3 个完整轮次或 4000 Token 已裁定内容更新；
- 记忆只处理到最新 GM Resolution；旧跑团打开不产生 API 成本；
- 身份、可见性、失败重试和事件锁定语义不退化；
- Release 构建、定向测试和完整回归通过；真实 UI 截图无布局溢出或按钮状态错误。

## 十三、推荐提交顺序

```text
1 feat: persist campaign context budget settings
2 feat: add campaign context planner and token breakdown
3 refactor: route campaign generation through context planner
4 feat: show campaign token preview before generation
5 fix: update campaign memory only at authoritative thresholds
6 test: cover campaign budget and memory update boundaries
```

每个提交必须保持可编译，避免把数据库、Runner、UI 和记忆服务合成一个巨大提交。

## 当前执行状态

- [x] 已读取会话最后一条 AI 方案。
- [x] 已将方案写入本文件。
- [x] 工作包 A：数据模型和 schema v16（已复核并通过迁移、持久化、边界限制和幂等相关定向测试）。
- [x] 工作包 B：纯上下文 Planner（已复核并通过预算、模型上限、历史裁剪、固定资料阻止、可见性和动态记忆上限定向测试）。
- [x] 工作包 C：Runner 接入（已通过既有 Runner 身份隔离、秘密同投、GM 尾部协议及 Planner 定向回归）。
- [x] 工作包 D：Token 预估 UI（代码、构建、定向回归及用户实际截图确认已完成）。
- [x] 工作包 E：低频记忆更新和边界修复（已完成并通过定向回归）。
- [ ] 工作包 F：最终回归和人工短局。
- [x] 工作包 G：每局升级版记忆 ON/OFF（已实现；待用户实际打开 EXE 验证交互）。

### A+B 本次验证记录

- 定向测试：18 个通过，0 失败，0 跳过。
- Release 构建：0 warning，0 error。
- `git diff --check`：通过（仅有 Git 的 LF/CRLF 提示）。
- A+B 阶段未修改 Runner、跑团 UI 或记忆更新触发链；C 阶段已将应用服务的 CampaignRunner 接入同一个 Planner，UI 和记忆触发链仍未改。

### C 本次验证记录

- `CampaignRunnerTests` 与 `CampaignR2BDataAndPlannerTests`：16 个通过，0 失败，0 跳过。
- 加上 `DatabaseAndRepositoryTests` 与 `CampaignMemoryTests` 的相关回归：26 个通过，0 失败，0 跳过。
- Planner 生成的实际消息保留既有两条 system/user 结构、身份隔离、秘密同投可见性和 GM 尾部协议；HistoryTrimmed 仍允许生成，固定资料阻断时不调用 Provider。
- 通过后续 Release 构建：0 warning，0 error。

### D 当前验证记录

- 源码 Release 构建：0 warning，0 error；项目引用从 `src` 项目文件解析正常。
- Campaign/Planner/Runner/数据库/UI 相关回归：32 个通过，0 失败，0 跳过。
- 已实现默认折叠的“本轮上下文估算”面板、AI 席位/AI GM 分区明细、秘密同投成本汇总、启发式估算文案，以及固定上下文超限时禁用对应生成按钮并显示原因。
- 用户提供实际游玩页截图，确认默认折叠、展开明细、多 AI 席位滚动、超预算原因文案和记录区布局；截图未触发 Provider。
- 全量测试仍有 4 个与本次 R2-B 文件无改动的既有失败：记忆提示词主体标签、候选导航前缀文案、Campaign GM 默认提示词迁移断言；未在本工作包扩大范围修复。

### E 当前验证记录

- `CampaignMemoryUpdateService` 只接受已成功锁定的 `GmResolution` 作为自动更新边界；行动、骰点、页面打开、重载和模型切换不会直接触发记忆模型。
- 默认按 checkpoint 后完整轮次达到 3 轮，或已裁定事件启发式估算达到 4000 tokens 触发；严格先攻按完整轮次而非 `GmResolution` 数量计算。
- 公共记忆复用秘密同投结算后的可见性生命周期；GM 输入包含 `recipient_id`、`recipient_name`、`structured_data`，公共输入只保留安全字段。
- 页面打开不再自动恢复；旧跑团显示“尚未建立”，手动按钮使用最新 GM Resolution 和 `force=true`。非法模型输出或非法边界均不推进 checkpoint，并保留并发请求去重。
- `CampaignMemoryTests`、`CampaignRunnerTests`、`CampaignR2BDataAndPlannerTests`、`DatabaseAndRepositoryTests`、`CampaignTests`：38 个通过，0 失败，0 跳过。
- 源码 Release 构建：0 warning，0 error；`git diff --check` 通过（仅有 Git 的 LF/CRLF 提示）。

### F 当前验证记录（未完成）

- 三组定向 Release 测试：CampaignR2BDataAndPlannerTests 8 个、CampaignMemoryTests 6 个、CampaignRunnerTests 8 个，合计 22 个通过，0 失败，0 跳过。
- 源码 Release 构建：0 warning，0 error；`git diff --check` 通过（仅有 Git 的 LF/CRLF 提示）。
- 已修复此前记录的 4 个基线断言：记忆提示词测试统一换行后匹配结构；候选导航测试采用当前 UI 的 `2/2` 文案；GM 迁移测试验证当前版本的防复述语义。4 个修复后的测试定向运行通过。
- 修复 `ChatApiMessagesLabelRoleCardPersonaHistoryAndCurrentInput` 测试在释放临时数据库前等待界面后台刷新完成，消除并行 SQLite 释放锁。
- 完整并行 `dotnet test TavernDesk.sln -c Release --no-restore`：149 个通过、0 个失败、0 个跳过。
- 根目录发布已重新执行到 `D:\CODEX PROJECT\TavernDesk\app`；没有执行人工短局，保留给用户实际打开 EXE 验证。

### G 当前验证记录（已实施）

- 已确认需求：开关按跑团独立保存；OFF 不进行记忆总结，也不向 API 注入长期记忆结构；保留必要的近期原始历史和当前上下文。
- G 本次实现：schema v17、每局 `MemoryEnabled`、Planner/Runner/MemoryUpdateService 闭环与右侧 ON/OFF 胶囊开关已完成；定向测试通过。
- G 定向验证：数据/Planner/记忆服务 17 个通过，CampaignTests 合并回归 25 个通过，CampaignRunnerTests 8 个通过；此前 4 个非本工作包断言及并行 SQLite 释放锁均已修复。Release 完整并行回归为 149 个中 149 个通过，发布目录为 `D:\CODEX PROJECT\TavernDesk\app`。
