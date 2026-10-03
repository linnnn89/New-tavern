# UI 与架构重构交付索引

状态日期：2026-10-04。本轮改动基于：`ca55ef0`。

P1–P5 已完成本次选定的职责拆分；P6 已交付两批界面改进，尚未完成所有候选项和视觉验收。本目录原有逐阶段施工文档已合并到本页；旧步骤、失效行号、实施前测试失败和对话协调记录从 Git 历史查询。

## 已交付的职责边界

| 范围 | 当前实现 | 维护约束 |
| --- | --- | --- |
| P1 校验与装配 | 本地化品牌名白名单；本机历史提示词测试使用固定 LF；主窗口接收统一构建的 `ChatViewModel` | 不改用户提示词，不因导航重建应用级聊天实例 |
| P2 聊天职责 | `ChatCharacterPromptViewModel`、`GroupMemoryCoordinator` | 提示词绑定直接使用子对象；保留记忆失效、草稿与自动更新语义 |
| P3 聊天视图 | `Views/Chat/` 的会话列表、上下文、角色、人设、记忆、会话面板 | 子视图沿用父级 DataContext；外壳保留消息流、输入区和响应式布局 |
| P4 字号与主题 | `FontSize*` 动态资源；字号 10–30；语音页页脚适配；四主题语义资源 | 极端字号与缩放需保留返回设置调小字号的路径 |
| P5 设置与跑团 | `DataAndDiagnosticsSettingsViewModel`、`CampaignContextPreviewViewModel`、`CampaignSettingsPanelViewModel` | 保留设置失败回退、当前局归属、预算门禁、StateVersion 与事件退订 |
| P6 视觉与交互 | 设置目录、仪表盘、搜索框、聊天右栏和六项确认布局 | 详见 [UI 当前状态](../ui-design-optimization-plan.md)，不视为全部 P6 完成 |

具体类位置、事务和异步边界以 [架构指南](../architecture.md) 为准。本次没有新增依赖框架、SQLite schema 或持久化设置键。

## 交付与验证记录

- [PR #29](https://github.com/linnnn89/New-tavern/pull/29)：职责拆分、主题基础、仪表盘、设置导航和聊天交互，合并提交 `0621cb3`。
- [PR #30](https://github.com/linnnn89/New-tavern/pull/30)：六项已确认布局及 Windows 发布目录，合并提交 `ca55ef0`。
- 关联交付记录报告 Release 构建成功、359/359 本机私有测试及四语言 1602 键检查通过。局部窗口验证和未覆盖范围集中记录在 UI 状态文档，避免把历史套件数量当作持续验收承诺。
- P5 原有本机证据位于 `work/refactor-qa/p5/`，P6 位于 `work/ui-refresh/` 与 `work/ui-layout-decisions/`；这些目录不随仓库分发。

## 保留的范围约束

- `tests/` 继续留在本机，仓库继续跟踪 `app/` 与根启动器；本次未改变分发方式。
- 聊天消息工具菜单已允许点击外部关闭；没有把这个行为扩展到所有 Popup。
- 不追加调色板存储方式迁移、语音页面 XAML 重写或新的通用状态管理框架。
- 后续改动按实际风险选择定向测试和窗口场景；不再执行已完成的旧施工步骤。
