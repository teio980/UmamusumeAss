# Normal Training 结构化养成架构

## 1. 目标与边界

Normal Training 保留现有的 Home → 剧本 → 马娘 → 继承 → 支援卡 → Strategy → Start 流程。本方案只整理 Start 之后的 Normal 启动配置和普通养成引擎，不改变 Independent Training 的行为。

整体分为四个阶段：

```text
启动页面探测与进入
  → 共享进入流程
  → Normal 启动配置流程
  → 通用养成运行引擎
```

用户点击开始训练后，不假设游戏一定处于 Home。运行器先截取一帧并识别当前入口页面；如果已进入剧本、马娘、继承、支援卡、Final Confirmation 或 Quick Mode 页面，则从对应步骤继续，跳过已经完成的入口操作。没有识别到受支持页面时，才从现有 Home → Career 流程开始。

完整启动链：

```text
Home
→ 选择剧本、马娘、继承、支援卡
→ Normal Mode
→ Strategy
→ Start
→ 识别 Start 后的实际页面
→ Skip Opening Intro
   → 确认弹窗中的 Skip（若弹窗出现）
→ Quick Mode
   → Shorten all events
   → Skip 调到双箭头最快速度
   → Confirm
→ 等待 career_main
→ 启动通用养成引擎
```

只有稳定识别到 `career_main` 后，才把控制权交给养成运行引擎。Skip Intro 和 Quick Mode 属于 Normal 启动配置，不属于回合养成逻辑，也不进入 URA 剧本模块。

任务启动时允许从以下页面恢复：

```text
Scenario Select      → 从 Scenario 步骤继续
Trainee Select       → 从 Trainee 步骤继续
Legacy Select        → 从 Legacy 步骤继续
Support Select       → 从 Support 步骤继续
Final Confirmation   → 从 Normal Mode 配置继续
Quick Mode           → 从 Quick Mode 配置继续
```

## 2. C# 与 JSON 职责

### C# 负责

- Normal 启动状态机和当前页面探测。
- 启动时当前页面探测，以及识别结果到入口步骤或 Normal setup stage 的映射。
- 回合、目标赛、事件、结算等流程编排。
- URA 目标链、Finale 和比赛结果推进。
- 训练策略和未知结果处理。
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
│   ├── CareerStartupRecoveryDetector.cs
│   ├── CareerFlowDispatcher.cs
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
- 调用 `CareerStartupRecoveryDetector`，根据当前真实页面决定入口起点；不盲目重新点击 Home 或 Career。
- Final Confirmation 后把控制权交给 `NormalCareerStartupFlow`。
- 启动配置完成并识别到 `career_main` 后，创建并运行 `CareerTrainingEngine`。
- 不再直接持有完整页面 switch、模板缓存或重复的 JSON action 执行代码。

### `NormalCareerStartupFlow`

- 负责 Normal Mode、Strategy、Start、Start 后页面识别、Skip Intro 和 Quick Mode。
- 每完成一个步骤只更新本次运行的内存状态。
- 重新启动时以当前游戏画面为事实来源，不保存或读取 Normal 运行进度。
- 复用现有 `CareerJsonActionExecutor` 执行语义动作。

### `CareerStartupRecoveryDetector`

- 在共享进入流程之前只截取一次当前画面，并用同一帧依次匹配所有可恢复页面。
- 匹配规则由调用方提供，包含识别用 screen ID、恢复后的 screen ID、入口步骤和可选的 Normal setup stage。
- 页面识别器只返回当前页面对应的进入目标，不执行点击、不修改运行状态。
- Normal pipeline 根据结果初始化 `CareerEntryNavigationState.Step` 和 `LastScreenId`；如果已到 Final Confirmation 或 Quick Mode，则直接初始化对应 setup stage。
- 未匹配到任何页面时返回空结果，由 pipeline 使用原有 Home 起点。
- 匹配顺序固定且页面模板必须互斥；一帧出现多个匹配时按明确优先级选择最靠后的安全步骤，不能依赖字典遍历顺序。
- Final Confirmation 的稳定识别可以由 Independent Training 复用，但 Normal 与 Independent 仍各自维护 setup session；Independent Training 不在本次改造范围内。

### `CareerTrainingEngine`

执行通用循环：

```text
截图并识别页面
→ 更新通用状态和剧本状态
→ 剧本模块提供约束
→ 策略选择领域动作
→ JSON 执行语义动作
→ 验证结果
→ 更新本次运行状态
→ 下一轮
```

未识别页面、动作后状态不确定或超过安全动作数时，安全暂停并返回结果；重新启动时重新探测当前画面。

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

Independent Training 继续使用自己的 session，不与 Normal 共用运行状态；本次 Normal 改造不修改 Independent Training 的既有实现。

启动恢复使用独立映射，不把视觉探测 screen ID 写成领域状态。当前页面与恢复目标的映射为：

| 识别 screen ID | 恢复 screen ID | 入口步骤 / setup stage |
|---|---|---|
| `normal_scenario_select_startup` | `scenario_select` | `CareerEntryNavigationStep.Scenario` |
| `normal_trainee_select_startup` | `trainee_select` | `CareerEntryNavigationStep.Trainee` |
| `normal_legacy_select_startup` | `legacy_select` | `CareerEntryNavigationStep.Legacy` |
| `normal_support_select_startup` | `support_select` | `CareerEntryNavigationStep.Support` |
| `normal_final_confirmation_startup` | `career_final_confirmation` | `ConfigureMode` |
| `normal_quick_mode_settings` | `normal_quick_mode_settings` | `ConfigureQuickMode` |

识别成功后必须先在本次运行的内存状态中设置 `LastScreenId`、入口步骤或 setup stage，再执行下一动作。游戏画面是当前运行的事实来源；不能把已经进入 `career_main` 的会话倒退到入口流程。

## 5. Normal 启动状态

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

Normal setup stage 只在当前运行内存中使用，不要求序列化兼容；待执行状态必须使用显式匹配，不能依赖枚举数值范围。无法确认当前页面时安全暂停，重新启动后从当前画面重新进入。

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
    │   ├── scenario_record_header.png
    │   ├── trainee_select_career_info.png
    │   ├── legacy1_title.png
    │   ├── deck_title.png
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

Final Confirmation 的启动探测模板 `career_final_confirmation_title.png` 属于共享 entry 模板；它可被 Normal 和 Independent 的启动恢复使用。`legacy1_title.png` 同时作为正常 Legacy 页面识别与启动恢复识别来源，避免两个模板描述同一稳定标题。

启动恢复 screen 只包含稳定 recognition，不包含 actions：

```text
normal_scenario_select_startup
  template: templates/normal/scenario_record_header.png
  roi: [620, 165, 205, 29]
  threshold: 0.88

normal_trainee_select_startup
  template: templates/normal/trainee_select_career_info.png
  roi: [576, 510, 145, 32]
  threshold: 0.88

normal_legacy_select_startup
  template: templates/normal/legacy1_title.png
  roi: [48, 821, 96, 32]
  threshold: 0.88

normal_support_select_startup
  template: templates/normal/deck_title.png
  roi: [66, 286, 73, 38]
  threshold: 0.88

normal_final_confirmation_startup
  template: templates/career_final_confirmation_title.png
  roi: [304, 59, 304, 42]
  threshold: 0.88

normal_quick_mode_settings
  template: templates/normal/quick_mode_header.png
  roi: [280, 390, 340, 50]
  threshold: 0.88
```

Manifest 的 `screens` 和 `execution` 支持单个字符串或字符串数组，以兼容旧资源包。加载器按顺序合并分片；重复 task、冲突的 screen recognition、重复 semantic action 或缺失模板必须 fail closed。

Normal 启动流程保留以下语义动作：

```text
normal.post_start.skip
normal.quick_mode.shorten
normal.quick_mode.skip
normal.quick_mode.confirm
```

安排规则：

- `normal.post_start.skip` 绑定到实际显示 Skip 按钮的 Intro 页面，不挂在 `career_final_confirmation` 下。
- `normal.start` 完成后先识别 `career_intro_event`、`career_main` 或 `career_races_ready`；不得把支援卡自动填充确认模板当作 Normal Career 的必经步骤。
- Opening Intro 的 Skip 可能需要先点击右下角白色 Skip，再点击中部确认弹窗中的绿色 Skip；确认步骤必须允许已关闭弹窗的设备直接继续。
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
2. 已识别的待处理事件优先；事件预期状态不能强制进入事件流程。
3. 在稳定识别到 `career_main` 后，通过 ADB 原始彩色帧测量顶部 Energy 条的填充比例；体力低于 50% 时休息。
4. 其他情况默认选择 Speed。
5. 事件默认选择第一项。
6. 首版不参加可选比赛，不自动购买技能。

后续高级策略通过 `ICareerTrainingStrategy<TScenarioState>` 替换，不修改页面执行层。

## 8. 养成阶段完整步骤

启动配置完成并稳定识别到 `career_main` 后，进入以下通用养成阶段。养成阶段不再依赖保存的进度文件；每个动作完成后都必须重新识别页面，再决定下一步。

### 8.1 每回合主循环

```text
1. 识别当前页面
   → 必须先确认是 career_main、目标赛页面、事件页面或结算页面

2. 在 career_main 更新本回合状态
   → TurnIndex
   → Energy
   → 当前目标 CurrentObjectiveId
   → 当前目标赛 CurrentRaceId
   → 更新事件预期状态 HasScenarioEvent（仅作提示，不作为强制动作依据）

3. 处理高优先级状态
   → 当前画面已识别为事件页面：进入事件处理
   → 有目标赛：进入目标赛流程
   → 已完成所有普通目标：进入 URA Finale

4. 没有比赛或事件时选择普通动作
   → Energy < 50%：选择休息
   → 其他情况：选择 Speed 训练

5. 执行领域动作
   → 休息：rest_confirmation → rest_result
   → 训练：training_selection → training_result
   → 事件：event_choice 或 scenario_event

6. 处理动作结果
   → 更新 Energy、TurnIndex、目标和事件状态
   → 处理 training_event 或事件选项
   → 确认回到 career_main，或转入目标赛/结算页面

7. 页面确认成功后进入下一回合
   → 未知页面、未知结果或动作后页面不确定：安全暂停

事件处理只由当前画面识别结果驱动。`HasScenarioEvent` 可以记录剧本元数据给日志或后续策略使用，但不能单独触发事件动作；事件未实际出现时继续按目标赛、体力和训练策略运行。
```

### 8.2 普通训练流程

```text
career_main
→ strategy 判断没有目标赛、没有事件且体力高于阈值
→ training_selection
→ 选择 Speed
→ training_result
→ 如有 training_event：处理事件并继续
→ 点击 advance
→ career_main
```

首版只执行 Speed，不做训练收益 OCR、不自动购买技能、不参加可选比赛。后续策略替换时，页面执行层不变。

### 8.3 休息流程

```text
career_main
→ strategy 判断 Energy < 50%
→ rest_confirmation
→ confirm
→ rest_result
→ 更新 Energy
→ advance
→ career_main
```

### 8.4 事件流程

```text
career_main 或 training_result
→ 识别 training_event、event_choice 或 scenario_event
→ 选择第一项（首版固定规则）
→ 确认事件结果
→ 更新事件状态
→ career_main
```

### 8.5 目标赛流程

```text
career_main 或 career_races_ready
→ race_day
→ open race_list
→ 选择当前目标赛
→ race_details
→ confirm
→ race_attributes
→ start_playback
→ race_playback / race_playback_settings
→ race_live
→ 推进到 race_result
→ 识别并确认名次
→ 更新目标、粉丝数、重试次数
→ reward / reward_support / goal_update
→ 如普通目标完成：goal_complete
→ 否则回到 career_main
```

目标赛必须优先于普通训练。比赛结果无法由模板确认时，不得假定比赛成功或推进目标链。

### 8.6 URA Finale 流程

```text
goal_complete
→ scenario_event（如有 URA 入场事件）
→ ura_finale_qualifier
→ 确认名次并推进 FinaleStageIndex
→ ura_finale_semifinal
→ 确认名次并推进 FinaleStageIndex
→ ura_finale_finals
→ 确认名次
→ scenario_complete
```

三场 Finale 比赛必须按顺序完成；任一场结果未知或不满足目标时暂停，不能直接跳到下一阶段。

### 8.7 育成结算流程

```text
complete_career
→ career_rank
→ career_result
→ rewards
→ sparks
→ sparks_confirmation
→ career_complete
→ 返回 Home
→ 确认 Home 后才报告成功
```

养成阶段的页面、动作和结果素材按以上 8.1 至 8.7 的业务步骤逐项补齐；每个业务步骤完成后再进入下一个步骤。

## 9. 日志语义

任务日志应覆盖：

- 跳过 Opening Career Introduction。
- 选择 Shorten all events。
- 检测和推进 Skip 速度。
- 确认最快 Skip 速度。
- 确认 Quick Mode 设置。
- 进入 `career_main` 并启动回合引擎。

日志记录语义动作和结果，不直接向用户暴露 JSON task 名或坐标。

## 10. 测试与验收

### 启动流程

- 启动恢复只截图一次，并在同一帧上匹配全部候选模板。
- 分别从 Scenario、Trainee、Legacy、Support、Final Confirmation 和 Quick Mode 页面启动时，能够映射到正确的入口步骤或 setup stage。
- 从中间入口页面恢复时不会再次点击 Home 或 Career。
- 未识别到任何恢复页面时回退到原有 Home 起点。
- 多个候选意外同时命中时使用确定的安全优先级。
- 启动探测 screen 不包含 actions，不能被普通页面 dispatcher 当作可操作页面。
- Final Confirmation 启动识别可以同时恢复 Normal 和 Independent，但两者进入各自的后续 stage。
- Intro Skip semantic action 能解析到对应 JSON task。
- Quick Mode 使用收窄后的 `[280, 390, 340, 50]` ROI 和 `0.88` 阈值，可以在任务重启时稳定恢复到 `ConfigureQuickMode`。
- Shorten 已选中时不会点击并取消选择。
- Shorten 未选中时点击后必须验证 selected。
- Skip Off 能推进为单箭头，再推进为双箭头。
- Skip 单箭头只点击一次并到达双箭头。
- Skip 已为双箭头时不再点击。
- Shorten 或 Skip 未确认时不能执行 Confirm。
- Intro 和 Quick Mode 完成前不会启动 `CareerTrainingEngine`。

### 养成引擎

- 使用 fake runtime 跑通 `career_main → training_selection → training_result → career_main`。
- `career_main` 稳定彩色帧能够测量 Energy 条填充比例；低于 50% 记录为可观测体力并选择休息。
- Energy 条无法确认时安全暂停；正好 50% 不触发休息。
- 覆盖目标赛、事件、低体力休息、默认 Speed 和完整结算路径。
- 动作成功并确认新页面后才推进回合或目标。
- 未知页面和未知比赛结果不会错误推进状态。

### 回归与实机验证

- 现有 Independent Training、共享进入流程和任务路由测试全部继续通过。
- 所有 semantic action 都能解析到 execution task，所有模板引用存在。
- 模拟器 smoke test 至少覆盖 Skip Intro、Quick Mode、一次训练、一次休息、一次目标赛、URA Finale 和完整结算。

## 11. 实施默认值

- 首个目标剧本为 URA，运行引擎保持剧本可扩展。
- 默认训练为 Speed。
- 默认休息阈值为 50%，严格低于阈值时休息。
- 默认事件选择第一项。
- `pauseOnUnknownOutcome` 保持开启。
- 本方案不改变 Independent Training 的配置和执行。
