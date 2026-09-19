# Normal Training 结构化养成架构

## 1. 目标与边界

Normal Training 保留现有的 Home → 剧本 → 马娘 → 继承 → 支援卡 → Strategy → Start 流程。本方案只整理 Start 之后的 Normal 启动配置和普通养成引擎，不改变 Independent Training 的行为。

整体分为三个阶段：

```text
共享进入流程
  → Normal 启动配置流程
  → 通用养成运行引擎
```

完整启动链：

```text
Home
→ 选择剧本、马娘、继承、支援卡
→ Normal Mode
→ Strategy
→ Start
→ 确认开始弹窗
→ Skip Opening Intro
→ Quick Mode
   → Shorten all events
   → Skip 调到双箭头最快速度
   → Confirm
→ 等待 career_main
→ 启动通用养成引擎
```

只有稳定识别到 `career_main` 后，才把控制权交给养成运行引擎。Skip Intro 和 Quick Mode 属于 Normal 启动配置，不属于回合养成逻辑，也不进入 URA 剧本模块。

## 2. C# 与 JSON 职责

### C# 负责

- Normal 启动状态机和中断恢复。
- 回合、目标赛、事件、结算等流程编排。
- URA 目标链、Finale 和比赛结果推进。
- 训练策略、checkpoint 和未知结果处理。
- 根据当前状态调用语义动作。

### JSON 负责

- 页面和控件模板、ROI、阈值。
- 单个语义动作的点击、滑动、等待和局部重试。
- 点击前 probe、点击后验证和已完成状态检测。

C# 中不保存点击坐标。JSON 不通过长 `next` 链执行完整养成流程，只实现可独立验证的原子动作。

## 3. C# 目录结构

```text
src/UmamusumeWpfGui/Services/Training/
├── Runtime/
│   ├── CareerRuntimeContracts.cs
│   ├── CareerTrainingEngine.cs
│   ├── CareerScreenObserver.cs
│   ├── CareerFlowDispatcher.cs
│   ├── CareerCheckpointStore.cs
│   └── Flows/
│       ├── CareerTurnFlow.cs
│       ├── CareerRaceFlow.cs
│       └── CareerSettlementFlow.cs
├── Normal/
│   ├── AdbNormalCareerTrainingPipeline.cs
│   ├── NormalCareerStartupFlow.cs
│   └── SimpleNormalTrainingStrategy.cs
└── Scenarios/Ura/
    ├── UraScenarioModule.cs
    ├── UraScenarioState.cs
    └── UraScenarioModels.cs
```

### `AdbNormalCareerTrainingPipeline`

- 负责运行锁、设置校验、加载 URA 包和调用共享进入流程。
- Final Confirmation 后把控制权交给 `NormalCareerStartupFlow`。
- 启动配置完成并识别到 `career_main` 后，创建并运行 `CareerTrainingEngine`。
- 不再直接持有完整页面 switch、模板缓存或重复的 JSON action 执行代码。

### `NormalCareerStartupFlow`

- 负责 Normal Mode、Strategy、Start、开始确认、Skip Intro 和 Quick Mode。
- 每完成一个步骤立即保存 checkpoint。
- 恢复时从已保存步骤继续，不能重复点击 Start。
- 复用现有 `CareerJsonActionExecutor` 执行语义动作。

### `CareerTrainingEngine`

执行通用循环：

```text
截图并识别页面
→ 更新通用状态和剧本状态
→ 剧本模块提供约束
→ 策略选择领域动作
→ JSON 执行语义动作
→ 验证结果
→ 保存 checkpoint
→ 下一轮
```

未识别页面、动作后状态不确定或超过安全动作数时，保存 checkpoint 并安全暂停。

### 流程处理器

- `CareerTurnFlow`：`career_main`、训练选择、训练结果、休息和事件。
- `CareerRaceFlow`：目标赛入口、比赛列表、比赛播放、结果和奖励。
- `CareerSettlementFlow`：育成完成、因子、奖励和返回 Home。

不为每个 screen 单独建立一个 class。

### `UraScenarioModule`

- 只负责 URA 目标链、目标比赛、Finale 阶段、重试规则和结果推进。
- 不读取坐标、不点击页面、不处理 Skip Intro 或 Quick Mode。
- 通过通用剧本接口接入运行引擎，为后续其他剧本保留扩展点。

## 4. 状态与公共契约

核心契约：

```text
CareerObservation
CareerDecision
CareerRuntimeState
CareerSessionState<TScenarioState>
CareerCheckpoint<TScenarioState>

ICareerScenarioModule<TScenarioState>
ICareerTrainingStrategy<TScenarioState>
```

通用状态保存：

- `ScenarioId`
- `PhaseId`
- `TurnIndex`
- `Energy`
- `LastScreenId`
- `LastAction`
- `CareerStarted`

URA 状态保存：

- `CurrentObjectiveId`
- `CurrentRaceId`
- `FinaleStageIndex`
- `RacePlacements`
- `RetryCount`

Independent Training 继续使用自己的 session 和 checkpoint，不与 Normal 共用运行状态。

## 5. Normal 启动状态与 checkpoint 兼容

`NormalCareerSetupStage` 包含：

```text
EnterCareer
ConfigureMode
ConfigureStrategy
StartCareer
ConfirmStart
SkipIntro
ConfigureQuickMode
SetQuickMode
ConfirmQuickMode
AwaitCareerMain
InCareer
```

现有 checkpoint 默认把枚举序列化为数字，因此必须保留旧值：

```csharp
EnterCareer = 0,
ConfigureMode = 1,
ConfigureStrategy = 2,
StartCareer = 3,
ConfirmStart = 4,
AwaitCareerMain = 5,
InCareer = 6,
SkipIntro = 7,
ConfigureQuickMode = 8,
SetQuickMode = 9,
ConfirmQuickMode = 10,
```

不能把 `SkipIntro` 插入为 `5` 后顺延旧状态，否则旧 checkpoint 中的 `AwaitCareerMain = 5` 会被解释成 Skip Intro。待执行状态必须使用显式匹配，不能再依赖枚举数值范围。

新的 checkpoint 使用版本号和 `mode = normal`。加载旧的无版本 `UraCareerSessionState` 时进行兼容迁移；无法确认的状态安全回到可观察入口，不能盲目重复不可逆操作。

## 6. JSON 与模板布局

场景资源按职责拆分：

```text
resource/hachimi/ura/screens/
├── profile.entry.json
├── profile.mode.normal.json
├── profile.mode.independent.json
├── profile.career.json
├── profile.ura.json
├── execution.entry.json
├── execution.mode.normal.json
├── execution.mode.independent.json
├── execution.career.json
├── execution.ura.json
└── templates/
    ├── entry/
    ├── normal/
    │   ├── quick_mode_header.png
    │   ├── quick_mode_shorten_selected.png
    │   ├── quick_mode_shorten_unselected.png
    │   ├── quick_mode_skip_off.png
    │   ├── quick_mode_skip_one.png
    │   ├── quick_mode_skip_two.png
    │   └── quick_mode_confirm.png
    ├── independent/
    └── career/
        ├── turn/
        ├── training/
        ├── event/
        ├── race/
        └── settlement/
```

Manifest 的 `screens` 和 `execution` 支持单个字符串或字符串数组，以兼容旧资源包。加载器按顺序合并分片；重复 task、冲突的 screen recognition、重复 semantic action 或缺失模板必须 fail closed。

Normal 启动流程保留以下语义动作：

```text
normal.post_start.ok
normal.post_start.skip
normal.quick_mode.shorten
normal.quick_mode.skip
normal.quick_mode.confirm
```

安排规则：

- `normal.post_start.skip` 绑定到实际显示 Skip 按钮的 Intro 页面，不挂在 `career_final_confirmation` 下。
- `normal_quick_mode_settings` 使用 `quick_mode_header.png` 作为稳定页面识别。
- Quick Mode 的 Shorten 和 Skip 必须先检测当前状态，避免重复执行时切换回错误状态。
- Confirm 只能在 Shorten 和双箭头 Skip 均验证成功后执行。

### Shorten 动作

```text
检测 quick_mode_shorten_selected
  → 已选中：成功返回
  → 未选中：匹配 quick_mode_shorten_unselected 并点击
             → 再次验证 selected
```

### Skip 速度动作

```text
检测 quick_mode_skip_two
  → 已是双箭头：成功返回
  → 否则检测 quick_mode_skip_one
       → 单箭头：点击一次并验证双箭头
       → Off：点击到单箭头，再点击到双箭头并验证
```

## 7. 首版简单策略

首版不实现训练收益 OCR，使用可替换的简单规则：

1. 强制目标赛优先。
2. 待处理事件优先。
3. 体力小于或等于 35 时休息。
4. 其他情况默认选择 Speed。
5. 事件默认选择第一项。
6. 首版不参加可选比赛，不自动购买技能。

后续高级策略通过 `ICareerTrainingStrategy<TScenarioState>` 替换，不修改页面执行层。

## 8. 日志语义

任务日志应覆盖：

- 跳过 Opening Career Introduction。
- 选择 Shorten all events。
- 检测和推进 Skip 速度。
- 确认最快 Skip 速度。
- 确认 Quick Mode 设置。
- 进入 `career_main` 并启动回合引擎。

日志记录语义动作和结果，不直接向用户暴露 JSON task 名或坐标。

## 9. 测试与验收

### 启动流程

- Intro Skip semantic action 能解析到对应 JSON task。
- Quick Mode 页面模板、ROI 和阈值可以正确加载。
- Shorten 已选中时不会点击并取消选择。
- Shorten 未选中时点击后必须验证 selected。
- Skip Off 能推进为单箭头，再推进为双箭头。
- Skip 单箭头只点击一次并到达双箭头。
- Skip 已为双箭头时不再点击。
- Shorten 或 Skip 未确认时不能执行 Confirm。
- Intro 和 Quick Mode 完成前不会启动 `CareerTrainingEngine`。

### checkpoint

- 在每个新增 setup stage 中断后均能从该阶段恢复。
- 恢复不会重复点击 Start。
- 旧 checkpoint 的 `AwaitCareerMain = 5` 和 `InCareer = 6` 保持原含义。
- 无法迁移的 checkpoint 不执行高风险动作并安全暂停。

### 养成引擎

- 使用 fake runtime 跑通 `career_main → training_selection → training_result → career_main`。
- 覆盖目标赛、事件、低体力休息、默认 Speed 和完整结算路径。
- 动作成功并确认新页面后才推进回合或目标。
- 未知页面和未知比赛结果不会错误推进状态。

### 回归与实机验证

- 现有 Independent Training、共享进入流程和任务路由测试全部继续通过。
- 所有 semantic action 都能解析到 execution task，所有模板引用存在。
- 模拟器 smoke test 至少覆盖 Skip Intro、Quick Mode、一次训练、一次休息、一次目标赛和中断恢复。

## 10. 实施默认值

- 首个目标剧本为 URA，运行引擎保持剧本可扩展。
- 默认训练为 Speed。
- 默认休息阈值为 35。
- 默认事件选择第一项。
- `pauseOnUnknownOutcome` 保持开启。
- 本方案不改变 Independent Training 的配置、执行和 checkpoint。
