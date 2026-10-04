# Career Training 的代码与资源边界

Career Training 从 `resource/hachimi/ura/manifest.json` 加载。manifest v2 的 `screens` 和 `execution` 是有序文件列表；`resourceCatalog` 指向共享视觉资源目录。旧的单文件 manifest 会报告迁移错误。

## 目录

`resource/hachimi/career/` 按 `entry`、`modes/normal`、`modes/independent`、`turn`、`training`、`event`、`skill`、`race`、`settlement` 分组。每组的 `profile.json` 声明画面与语义动作，`execution.json` 描述局部识别和操作，`templates/` 保存对应图片。

URA 的日历、赛事、目标和事件规则仍在 `resource/hachimi/ura/`。测试截图保存在 `ura/testdata/` 和历史 `ura/screens/captures/` 目录，均不会复制到发布包。被识别器实际使用的截图模板仍保留在运行资源中。

## 修改行为

- 策略、回合推进、恢复状态和流程分支由 C# 拥有。`CareerRuntimeLoop` 控制观察与执行循环；Normal、Independent 和各局部流程提供具体行为。
- JSON 声明画面分类 `flow`、识别优先级、原始顺序 `order`、模板、ROI、阈值、重试和局部动作。跨回合策略不写入 JSON。
- 调用动作时使用完整 `semanticId`，例如 `rest.confirm`。动作后缀不会自动匹配其他动作。
- 执行结果带有状态、失败类型与语义 `outcome`。业务判断使用这些值；任务名与日志文本只用于诊断。
- 动态图片、筛选布局和视觉匹配参数在 `catalog.json` 中声明，通过资源包解析。相对模板路径按所属片段目录解析。

## 编辑与迁移

开发工具展示合成后的任务列表，并记录每个任务的来源。保存时回写原来的 execution 片段；未修改的模板路径保留原文，不生成一个新的合并大文件。新增任务写入当前选择的 execution 片段。

迁移自定义包时，将单文件拆为片段，在 manifest 中列出这些片段，将模板路径改为片段相对路径或目录中声明的资源，并为每个画面声明 `flow`、`order` 和识别优先级。加载器会检查重复 ID、动作绑定、任务跳转、模板路径和动态资源声明，错误消息指向来源文件。

保留现有设置 ID、Independent checkpoint 和 Normal 技能缓存格式。Normal 的临时确认状态只保存在当前运行中。
