# TavernDesk 文档导航

安装、功能和模型配置从根目录 [简体中文 README](../README.zh-CN.md) 开始；源码维护从本页进入。

| 文档 | 用途 |
| --- | --- |
| [语音设置与行为](voice-settings.md) | 合成参数、保存冲突、播放、缓存和诊断 |
| [架构与维护指南](architecture.md) | 模块、数据与生命周期边界、代码定位、隔离测试和发布 |
| [跑团规则与实现](campaign_mode_design.md) | 参与者权限、三种流程、事件状态、GM 输出与失败处理 |
| [跑团上下文与记忆](TavernDesk-R2-B-Campaign-Context-Budget.md) | Token 预算、GM/Public 记忆、预览和长局验收 |
| [UI 当前状态与剩余范围](ui-design-optimization-plan.md) | 已确认布局、已实现行为、已知问题及未验收项 |
| [重构交付索引](refactor-plan/README.md) | P1–P5 的结果与代码入口，P6 的交付范围 |

## 维护约定

- 当前行为写在对应主题文档；跨主题内容用链接，避免重复保存版本不同的规则。
- 已完成的逐文件施工计划和旧工作日志不再维护，历史变更从 Git 提交及 PR 查询。
- README 使用的界面截图放在 [screenshots/](screenshots/)；早期界面记录保留在 `ui-evidence/`。
- `tests/` 与 `work/` 用于本机测试，按仓库规则不公开。
