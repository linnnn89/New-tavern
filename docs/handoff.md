# TavernDesk 当前交接摘要

状态日期：2026-08-03（北京时间）

用途：新对话接手时先读本文件，避免重新全库调查、重复已完成工作或恢复已经否决的设计。

## 1. 项目结论

TavernDesk 是 Windows 本地优先、非商用个人使用的角色聊天与独立跑团软件。当前已形成可运行闭环，架构总体合理；没有证据要求进行全应用重构。后续应以真实使用验收和局部修复为主，不主动制造新需求。

项目遵循：

- 第一性原理：先解决用户真正要完成的对话或跑团行为；
- 最小爆炸半径：允许必要的局部重构，不建设通用状态框架；
- 本地优先：数据、角色卡、剧本、聊天和密钥均留在本机；
- 证据门槛：未经真实使用证明，不提前实现 R2/R3 或服务商特例矩阵。

## 2. 用户已经明确的方向

- 当前不继续本地模型方向，尤其不使用 Ollama；不要主动恢复这条路线。
- Provider 优先级是 OpenRouter 与 Grok CLI。Grok CLI 走本机订阅登录和官方 ACP `agent stdio`，不等同于 xAI 按 Token 计费 API。
- xAI API 与其他 OpenAI-compatible 保持可用，但不是当前首要验收对象。
- 提示词要精炼、角色扮演职责清楚、固定内容靠前、动态内容靠后，优先利用 DeepSeek/OpenRouter 的相同前缀缓存。
- 记忆更新、记忆压缩和群聊记忆合并各只有一份全局模板，不恢复 User 模板或角色/群聊局部覆盖。
- 普通聊天和跑团记忆严格隔离；开跑团时只能显式选择是否一次性导入角色、Persona 或普通记忆快照。
- 导入角色卡、剧本卡时保留未知字段、原始内容和既有成人向设定，不进行擅自净化或改写。
- 消息不设回收箱。选择删除范围并最终确认后立即永久删除，不能恢复。
- 自定义书架的批量整理默认隐藏；只有点击小“批量”按钮后才显示复选框和整理栏。
- 不为小问题增加全局 Store、事件总线、通用状态机、模板 DSL、规则 DSL、第二套生成主管或新依赖注入体系。
- 不做无意义的全库扫描、文件上传、代码上传或重复验证。外部检索不得上传项目文件、用户数据或凭据。

## 3. 当前实现快照

### 角色书架

- 支持 PNG/JSON/CHARX 导入、同容器导出、未知字段和资源 round-trip。
- “进入”和“编辑”按被点击角色 ID 打开，不再依赖第一张或默认选中角色。
- 角色详情采用“长期书架状态 + 每次打开新建的短期详情会话”：
  - 唯一 `SessionId` 与 `CharacterId`；
  - 正式资料和未保存草稿分离；
  - `Overview/Edit/Classification` 单一互斥模式；
  - 旧读取、旧保存或旧收尾发布前校验会话与角色身份；
  - 切换或关闭时取消旧读取，迟到结果不能覆盖新角色。
- 自定义书架批量整理是临时模式；批量移出只删除书架成员关系，不删除角色卡或聊天。

### 普通聊天与提示词

- 多会话、消息编辑、候选、重新生成、独立分支、群聊、长期记忆、FTS5 检索和 JSONL round-trip 已接通。
- USER Persona、角色卡、世界书、记忆、历史和当前输入具有明确数据分区；单聊依赖原生 role，群聊使用单行 JSON `speaker` 信封避免正文伪造作者。
- 全局聊天提示词包含精炼 RP 合同：保持角色、与 USER 输入语言一致、不替 USER 或其他角色行动、只输出最终正文、不泄露思考过程。
- 角色局部入口直接修改角色卡既有 `system_prompt` 和 `post_history_instructions`，不创建第二份角色配置。
- reasoning 在 Infrastructure 边界归一化；原始推理不写入气泡、候选、记忆或数据库。
- 顶栏红色停止按钮管理 TavernDesk 当前登记的聊天、记忆和跑团生成。

### Provider 与密钥

- OpenRouter、xAI API、Grok CLI 和通用 OpenAI-compatible 已有统一配置入口。
- API Key 位于数据根 `secrets/`，使用 Windows DPAPI CurrentUser 保护；SQLite 仅保存随机引用。
- 删除接入商会同步删除其本地模型目录、功能分配和 TavernDesk 保存的密钥文件；默认接入商被用户删除后不会自动复活。
- DPAPI 可降低数据库、备份或单独文件泄漏导致的明文暴露，但不能抵御同一 Windows 用户上下文中的恶意程序、管理员、调试器或正在运行进程被控制。
- OpenRouter 的 DeepSeek 快捷设置当前只提供明确 OFF/ON；不提前增加 `low/high/max`。

### 独立跑团 R1

- 跑团拥有独立剧本模板、战役、参与者快照和事件流，不复用普通聊天消息或记忆表。
- 支持 `1 GM + USER + 0–4 AI`、AI/USER GM、“裁判下场踢球了”、三种流程预设、多 Provider/多模型席位、途中换模型、本地骰子、席位失败缓存和单独重试。
- 每条完整 USER/AI 玩家行动在同一原子写入中自动附加可信 `1d20`；GM 按角色能力、方法、局势和点数灵活裁定，`1`/`20` 不是绝对失败/成功。
- GM 可推进世界、NPC、环境与剧情，但不能替玩家补写新的台词、心理、决定、反应或下一步。AI GM 必须以 `【下一轮评定参考】` 收尾，缺失时原文留痕、回合不推进并要求显式重试。
- 公共、单席私有和 GM-only 查询边界已经隔离；开始游戏后冻结角色、可选记忆、世界规则和模型路由快照。
- 秘密同投的行动及自动骰在本轮裁定前互盲，裁定完成进入下一轮后一起揭示。
- R1 已闭环。R2/R3 仍需真实长局证据，详见 `campaign_mode_design.md`。

### 永久删除与 schema v10

- 聊天页已经移除回收箱按钮和回收箱窗口。
- 删除流程是“选择仅当前或当前及后续 → 最终确认不可恢复 → 事务内物理删除”。
- 范围按稳定 `sequence_no` 计算；候选和导入负载级联清理，FTS 索引同步删除。
- schema v10 会在下次启动时永久清理旧版本回收箱中的遗留消息。这是用户明确要求的不可逆迁移。
- `messages.is_deleted` 仅作为旧 schema 兼容列保留，不是可恢复产品状态。

## 4. 最新可信验证

2026-08-03 当前工作区：

- Release 自动化测试：`92/92` 通过；
- Release 解决方案构建：0 个警告、0 个错误；
- 根目录 `TavernDesk.exe --probe`：退出码 0；
- 隔离 WPF 视觉验证：自动行动骰、GM 收尾章节与“额外掷骰”布局正常，无启动/绑定错误；
- 未读取 API Key、未刷新模型目录、未发送 OpenRouter/xAI/Grok 真实请求；
- 验证临时数据与生成器已清理；当前没有 TavernDesk GUI 进程。

最新永久删除回归覆盖：

- 按后续范围物理删除；
- 消息候选外键级联；
- FTS5 检索清理；
- v9 → v10 旧软删除遗留清理；
- 未来数据库版本拒绝降级打开。

## 5. 尚未充分验证

- 最新“按需批量整理”与“永久删除确认”仍应由用户做一次真实视觉和点击验收；自动化与 XAML/Release 编译已通过。
- OpenRouter 真实账号下的模型刷新、流式回复、缓存 usage、reasoning、额度/限流错误和取消后计费行为。
- Grok CLI 的真实安装发现、`grok login`、ACP 普通聊天、会话取消和订阅侧并发限制；模型枚举当前仍使用默认模型占位。
- 真实多模型跑团短局及中等长度跑团：上下文预算、途中换模型、席位失败重试、秘密同投隔离和停止全部生成。
- 大型真实数据库迁移、长列表性能、DPI/键盘无障碍和强制结束进程后的流式恢复。
- 非 Tiktoken 模型家族的本地 tokenizer、自动上下文压缩、embedding、附件、TTS、MCP、Anthropic/Gemini 原生协议。

## 6. 下一对话推荐顺序

1. **先做两项人工冒烟**
   - 自定义书架：默认无复选框 → 点击“批量” → 选择 → 移出 → 角色与聊天仍在。
   - 消息删除：选择范围 → 最终确认 → 消息立即消失；聊天页没有回收箱入口。
2. **真实 OpenRouter 短链路**
   - 保存 Key → 主动刷新模型 → 分配角色聊天 → 发一条短消息 → 检查流式、usage、缓存和停止。
   - 不批量消耗 Token，不先做长局。
3. **真实 Grok CLI 短链路**
   - 确认本机 CLI 与登录状态 → ACP 单轮普通聊天 → 取消。
   - 不读取或创建 xAI API Key，不开放 CLI 工具权限。
4. **跑团真实短局**
   - 使用现有 Naruto 剧本和 1–2 张角色卡；
   - 可给不同 AI 席位分配不同 OpenRouter 模型；
   - 分别验证一个协作回合和一个技术失败/重试，不扩建 R2。
5. **只根据真实故障追加局部修复**
   - 若短局证明确有 barrier、并发上限、摘要压缩或超时策略需求，再讨论对应最小实现。

## 7. 不要倒退的边界

- 不恢复 Ollama/本地模型主路线。
- 不恢复消息回收箱、软删除 UI 或恢复消息能力。
- 不把记忆模板重新拆成 System/User 两套。
- 不让角色书架目录重新持有角色编辑草稿或详情会话状态。
- 不把跑团改回普通群聊加 GM 提示词。
- 不让 reasoning 原文进入数据库或模型下一轮上下文。
- 不自动刷新模型目录，不把 API Key 写进 SQLite、导出文件或日志。
- 不因功能“可能有用”就实现属性、背包、地图、规则 DSL、向量库或通用 Agent 平台。

## 8. 接手操作

优先使用项目内 SDK，因为某些终端的 `PATH` 不包含 `dotnet`：

```powershell
cd "D:\Documents\女主角搜索器\TavernDesk"
& .\.dotnet\dotnet.exe test TavernDesk.sln -c Release --no-build --no-restore
& .\.dotnet\dotnet.exe build TavernDesk.sln -c Release --no-restore
.\TavernDesk.exe
```

注意：

- 启动 GUI 前先确认用户是否希望打开；遇到 Release 文件锁时先用只读进程检查，不擅自结束用户窗口。
- TavernDesk 是独立 Git 项目，仓库根目录为 `D:\Documents\女主角搜索器\TavernDesk`，正式本地分支为 `main`。
- 父目录中的“女主角索引”仓库当前冻结；同级 APP 反编码目录也是独立项目。不要使用父仓库状态解释 TavernDesk，也不要把二者纳入 TavernDesk 提交。
- TavernDesk 当前未配置远端；未经用户明确授权，不创建远端、不推送。
- 工作记录继续追加到 `docs/codex_worklog.md`，不要静默改写已有历史。
- 开始任何新功能前先读 README 与架构；涉及跑团再读 `campaign_mode_design.md`。

## 9. 关键文件入口

- 产品现状与边界：`README.md`
- 架构与生命周期：`docs/architecture.md`
- 跑团规则：`docs/campaign_mode_design.md`
- 历史实施记录：`docs/codex_worklog.md`
- 角色书架：`src/TavernDesk.App/ViewModels/CharactersViewModel.cs`
- 普通聊天：`src/TavernDesk.App/ViewModels/ChatViewModel.cs`
- 删除确认：`src/TavernDesk.App/Services/UserInteractionService.cs`
- 会话仓储：`src/TavernDesk.Infrastructure/Storage/SqliteConversationRepository.cs`
- 数据库迁移：`src/TavernDesk.Infrastructure/Storage/SqliteDatabase.cs`
- Provider：`src/TavernDesk.Infrastructure/Providers/`
- 跑团：`src/TavernDesk.Infrastructure/Campaign/` 与 `src/TavernDesk.App/ViewModels/CampaignsViewModel.cs`
