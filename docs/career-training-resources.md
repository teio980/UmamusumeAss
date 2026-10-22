# Career Training 的代码与资源边界

Career Training 从 `resource/hachimi/ura/manifest.json` 加载。manifest v2 的 `screens` 和 `execution` 是有序文件列表；`resourceCatalog` 指向共享视觉资源目录。旧的单文件 manifest 会报告迁移错误。

## 目录

`resource/hachimi/career/` 按 `entry`、`modes/normal`、`modes/independent`、`turn`、`training`、`event`、`skill`、`race`、`settlement` 分组。每组的 `profile.json` 声明画面与语义动作，`execution.json` 描述局部识别和操作，`templates/` 保存对应图片。

URA 的日历、赛事、目标和事件规则仍在 `resource/hachimi/ura/`。测试截图保存在 `ura/testdata/` 和历史 `ura/screens/captures/` 目录，均不会复制到发布包。被识别器实际使用的截图模板仍保留在运行资源中。

## 修改行为

- 入口与模式配置由原生 `GameTasks` 拥有；`CareerFlowCoordinator` 控制运行交接，`CareerDomain` 拥有策略、回合与剧本规则。WPF 负责设置与日志，不再执行托管育成流程。
- `CareerTransitionPolicy` 统一声明动作后可到达的页面及事件返回阶段。普通运行只扫描当前动作链的候选，首次无上下文恢复可做全量识别；独立育成内部配置按检查点和动作图执行。
- JSON 声明画面分类 `flow`、识别优先级、原始顺序 `order`、模板、ROI、阈值、重试和局部动作。跨回合策略不写入 JSON。
- 调用动作时使用完整 `semanticId`，例如 `rest.confirm`。动作后缀不会自动匹配其他动作。
- 执行结果带有状态、失败类型与语义 `outcome`。业务判断使用这些值；任务名与日志文本只用于诊断。
- 动态图片、筛选布局和视觉匹配参数在 `catalog.json` 中声明，通过资源包解析。相对模板路径按所属片段目录解析。

剧本选择页先验证 `career.entry.scenario.ura_card`。未命中时，以 `scenario_select_right_arrow.png` 的透明箭头素材通过 `MatchTemplateColor` 匹配并点击右箭头，切换后仍只观察 `scenario_select`，利用轮播回绕查找 URA；命中后才执行 `scenario.next` 进入选马。`entry/profile.json` 的 `scenarioSelection.maxAdvanceAttempts` 限制向右切换次数（当前为 8）。箭头搜索范围覆盖左右摆动位置，素材不保留剧本背景；四个剧本的 ADB 实机回归截图保存在 `testdata/hachimi/ura/captures/scenario_select_20261022/`。

## 编辑与迁移

赛前属性与跑法画面统一使用 `race_runner`：只匹配右侧 Runners 的局部模板，ROI 为 `[700, 1060, 180, 140]`，阈值保持 `0.88`。保留原小模板，并从实际 URA Runner 截图提取局部字样作为兼容变体；重试成功检查复用这两个小模板。旧 `race_attributes` 页面、重复动作绑定和整图识别已删除；五张历史整图移到 `ura/testdata/captures/runtime_frames/`，只用于像素回归，不进入发布包。其中四张实际为 Race Day、赛前剧情或赛事列表，回归要求它们不能被识别成 Runner。赛事详情确认、连赛保护、跑法配置、重试及直接赛果续接都使用同一 Runner 检查点。

定点空点使用通用任务节点 `algorithm: JustReturn`、`action: ClickRect` 和 `specificRect`，不依赖 TAP 模板。`ClickRect`（以及 `ClickSpecificRect`）可设置 `repeat`（默认 1，范围 1–100）和 `repeat_delay`（两次点击间的等待，毫秒，默认 0，范围 0–10000）。重复点击要求明确的四项 `specificRect`；显式 `JustReturn` 定点点击直接通过设备坐标输入通道执行，第一次点击前及重复点击之间均不截图、不做输入前视觉检查；设备会校验当前分辨率及旋转方向。带识别证据的点击继续使用原有帧凭据。失败或取消会立即终止剩余点击。

View Results 使用这套通用能力：按钮点击后取消原来的 500 ms 等待，进入 `repeat: 2`、`repeat_delay: 150` 的空点节点；之后每轮先通过 `successTasks` 检查 Next 和已有重试弹窗，没有命中才补一次空点。Next 只用于退出空点阶段，下一步结果处理负责点击它。空点坐标与 Next 按钮分开，补点循环保留 `maxTimes` 上限。旧 TAP 识别节点与运行模板已删除。

开发工具展示合成后的任务列表，并记录每个任务的来源。保存时回写原来的 execution 片段；未修改的模板路径保留原文，不生成一个新的合并大文件。新增任务写入当前选择的 execution 片段。

迁移自定义包时，将单文件拆为片段，在 manifest 中列出这些片段，将模板路径改为片段相对路径或目录中声明的资源，并为每个画面声明 `flow`、`order` 和识别优先级。加载器会检查重复 ID、动作绑定、任务跳转、模板路径和动态资源声明，错误消息指向来源文件。

保留现有设置 ID、Independent checkpoint 和 Normal 技能缓存格式。智能训练在发送提交前持久化确认记录，恢复后以实际结果或回合证据消除记录，不能因重启重复提交。

当前页面、动作绑定及其与 main 的差异见 [Career 全操作清单](career-flow-action-inventory.md)。清单由 `CareerTests [CareerTransitionAudit]` 导出后用 `tools/GenerateCareerFlowAudit.py` 生成；只有已明确合并到 Runner 的旧页面允许删除，其余基线页面或动作缺失会使生成失败。
