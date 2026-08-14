# UmamusumeAss 多剧本育成模块化开发设计

> 状态：设计阶段
>
> 日期：2026-08-12
>
> 目标：在现有 UmamusumeAss 连接、截图、输入、视觉流水线和 WPF 架构之上，设计可扩展、可维护、可支持多育成剧本的育成逻辑与脚本运行时。

## 1. 文档结论

本项目不应把每个育成剧本实现成一个独立的“大脚本”，也不应把剧本判断散落在 WPF ViewModel、OCR 代码和 ADB 点击代码中。

推荐采用四层结构：

```text
┌─────────────────────────────────────────────────────────────┐
│ WPF GUI                                                     │
│ 任务配置、策略选择、实时状态、日志、调试工具                  │
└───────────────────────┬─────────────────────────────────────┘
                        │ 依赖接口，不依赖具体剧本实现
┌───────────────────────▼─────────────────────────────────────┐
│ Training Automation                                         │
│ 画面识别、状态同步、策略脚本、动作执行、重试与恢复             │
└───────────────────────┬─────────────────────────────────────┘
                        │ GameState / Action / Observation
┌───────────────────────▼─────────────────────────────────────┐
│ Training Domain                                             │
│ 通用育成回合引擎、领域状态、事件、目标、资源、剧本模块契约      │
└───────────────────────┬─────────────────────────────────────┘
                        │ JSON / 数据包 / 模块注册
┌───────────────────────▼─────────────────────────────────────┐
│ Training Data & Scenario Packs                              │
│ 马娘、支援卡、比赛、技能、事件、剧本定义、画面资源与本地化       │
└─────────────────────────────────────────────────────────────┘

现有 C++ Core 和 CoreBridge 继续负责设备连接、截图、坐标转换与 ADB 输入，不承载育成业务规则。
```

核心原则：

1. `CareerEngine` 只负责共通育成流程。
2. `ScenarioModule` 只负责剧本差异。
3. JSON/资源包负责内容和可配置规则。
4. 策略脚本只负责“现在应该做什么”，不负责模拟游戏内部规则。
5. OCR/模板匹配只负责“画面上看到了什么”，不负责决定养成策略。
6. 每个新剧本都必须通过注册和扩展点接入，不能在核心流程中增加一串 `if scenarioId == ...`。

本轮只写设计文档，不修改现有业务代码。当前工作区已有未提交改动，后续开发必须以现状为基线，避免覆盖这些改动。

## 2. 现有仓库架构基线

### 2.1 当前职责划分

根据当前仓库结构，已有系统大致分为以下部分：

| 层 | 当前位置 | 当前职责 | 育成扩展原则 |
|---|---|---|---|
| Native Core | `src/UmaAssistantCore` | ADB、设备连接、截图、输入、C ABI | 继续保持通用，不放剧本业务 |
| Managed Bridge | `src/Umamusume.CoreBridge` | P/Invoke、SafeHandle、回调、生命周期 | 只暴露设备能力和连接事件 |
| WPF GUI | `src/UmamusumeWpfGui` | 页面、ViewModel、配置、日志、DI | 只消费育成服务，不实现剧本规则 |
| Task Pipeline | `src/UmamusumeWpfGui/Services/Tasks` | 视觉流水线、任务模块、ADB 执行 | 作为未来自动化执行层的基础 |
| Static Data | `resource/uma` | 马娘、支援卡、图片、模板等静态资源 | 增加 scenario pack，不改变 ID 稳定性 |
| Tests | `tests` | C++ Core、Bridge、WPF、视觉流水线测试 | 增加领域、剧本、回放和视觉契约测试 |

### 2.2 可以复用的现有能力

现有项目已经具备几项适合育成自动化的基础能力：

- `UmaService` 以及 C++ Core 的设备生命周期管理。
- 截图、PNG 解码、标准 ADB tap/swipe。
- `IVisualPipelineRuntime` 和 `HachimiJsonPipelineRunner` 形式的视觉任务运行能力。
- 任务模块、任务选择、日志和 WPF DI 注册。
- `UmaDatabaseService` 对 `resource/uma/database/global` 的加载与 ID 查询。
- 现有马娘、支援卡和 system reference 图片资源。
- 已有的数据结构规划：[2026-08-06 Umamusume data structure plan](2026-08-06-umamusume-data-structure.md)。

### 2.3 当前不应继续扩大的部分

以下类或层不应成为育成系统的最终归宿：

- `UmaDatabaseService` 当前位于 WPF 层，后续应抽出领域无关的数据访问接口。
- 现有各类 `*TaskModule` 适合固定任务，不适合承载三年回合、剧本资源和策略决策。
- `DeveloperToolsViewModel` 可以继续提供视觉调试，但不能成为育成逻辑的中心。
- `Adb*Pipeline` 可以执行动作，但不应决定“训练还是比赛”“买什么技能”“什么时候触发剧本资源”。

### 2.4 马娘大数据必须沿用 Daily Race 的引用模式

当前 `DailyRace` 已经提供了本项目应采用的实际范式：

```text
启动阶段
  -> UmaDatabaseService.LoadAsync(resourceRoot)
  -> 从 resource/uma/database/global 读取大数据
  -> 建立内存 ID 索引
  -> DatabaseLoaded

配置阶段
  -> ViewModel 读取数据库集合
  -> 搜索/筛选/展示名称和缩略图
  -> 配置只保存 traineeId / supportCardId / raceId 等稳定 ID

运行阶段
  -> TaskModule 读取配置中的稳定 ID
  -> 通过 IUmaDatabaseService 查询完整记录
  -> 通过数据库服务解析图片、模板和资源路径
  -> Runner/Strategy 使用完整记录执行逻辑
```

当前 Daily Race 的具体表现是：

- `DailyRaceTaskSettingsViewModel` 通过 `IUmaDatabaseService.Trainees` 构造可选马娘列表。
- 搜索使用数据库服务的名称查询能力，而不是在任务 JSON 中维护名称表。
- 配置通过 `TraineeId` 保存选中的马娘。
- `DailyRaceTaskModule` 把 `traineeId` 序列化到任务设置。
- `DailyRaceRunnerSelector` 运行时通过 `TryGetTrainee(traineeId)` 取回完整记录。
- 运行时再通过 `GetTraineeImagePath`、`GetTraineeReferenceImagePath`、`GetTraineeTemplateDirectory` 等方法解析本地资源。
- 视觉匹配使用数据库记录关联的图片/模板，不把图片路径硬编码到业务配置中。

育成系统必须直接复用这条思想，而不是另起一套“育成专用大 JSON”或把完整马娘对象复制进策略脚本：

```text
training_task.json
  {
    "traineeId": 100601,
    "supportCardIds": [ ... ],
    "scenarioId": "ura",
    "strategyId": "default"
  }

  traineeId / supportCardIds / scenarioId / strategyId
        │
        ▼
  IUmaDatabaseService / ITrainingDataCatalog
        │
        ├── trainee record
        ├── support card records
        ├── skills
        ├── races
        ├── events
        ├── scenario definition
        └── image/template/resource paths
```

这里的“大数据引用”指的是：数据集中存放、启动时加载、内存索引查询、配置只存 ID、运行时按需解析资源。它不是指每次运行都重新读取和反序列化完整大文件，也不是指把全量数据复制到每个任务文件中。

## 3. 玩法调研与架构启示

### 3.1 共通育成循环

官方系统说明把训练、状态、支援角色、技能、比赛、粉丝、继承和支援卡作为育成的基础组成部分。[官方 Game System](https://umamusume.jp/contents/game/system/?step=3)

官方对育成剧本的总说明是：和马娘共同度过三年，通过训练、与竞争者及学园相关人物交流，最终挑战大赛。[官方 Scenario 页面](https://umamusume.jp/contents/game/scenario/)

因此，通用领域模型至少应支持：

- 三年或可配置的时间轴。
- 年、季度、月、前半/后半等时间单位。
- 训练、休息、外出、保健室、比赛、技能购买等动作。
- 基础属性、技能点、体力、干劲、状态、适性、粉丝数。
- 支援卡、友情值、事件链、技能提示。
- 目标比赛、任意比赛、强制比赛和最终比赛。
- 继承、因子、育成结果和后续任务引用。

“三年”和“通常按回合推进”应作为默认剧本配置，而不是硬编码成永远固定的常量；不同剧本可能改变可用回合、阶段和终局形式。

### 3.2 剧本不是故事文本，而是规则集合

官方当前的剧本目录已经包含多种完全不同的玩法结构。以下例子应直接影响系统设计：

| 剧本机制类型 | 官方例子 | 对系统的要求 |
|---|---|---|
| 自由赛程与年度评分 | Climax 允许自由选择出赛路线，并通过成绩 Pt、商店硬币、培育道具和 Rival 机制改变养成节奏 | 目标系统、比赛供给、商店、临时资源、年度结算必须可替换 |
| 多维资源与阶段结算 | Grand Live 用五种 Performance 资源、课程、乐曲和 Live Bonus 强化训练 | 资源池、资源消耗、无回合操作、阶段结算、效果叠加 |
| 知识板与组合效果 | Grand Masters 通过知识碎片、知识结晶、知识表和女神的智慧形成成长循环 | 资源生成、合并、候选选择、效果组合、延迟结算 |
| 多分组训练 | U.A.F. 有 15 种竞技训练，分为 3 个主题，并产生 Link Training 和 Heat Up | 标签/分组、选择候选、连锁触发、阶段目标 |
| 研究、仪器和超载 | Mecha Uma 有研究 Lv、博士关注训练、Overdrive、EN 分配和 Tuning | 研究状态、充能、主动技能、配置面板、效果预览 |
| 种植、收获和料理 | 大丰食祭有五类蔬菜、田地强化、收获和料理，料理可强化训练 | 周期资源、生产、库存、消费、动作前增益 |
| 设施建设 | 无人岛通过发展 Pt 建设六类训练设施，并使用岛训练券 | 建设计划、设施等级、发展资源、阶段评价、额外动作 |
| 团队成员成长 | Beyond Dreams 有目标 BC 比赛、团队成员、Dream Gauge、Team Rank、DP 和作战会议 | 多角色状态、队伍参数、共享训练、团队阶段结算 |
| 地区和配方选择 | Toresenken 选择地区，积累拉面技巧，并通过试吃会在不消耗回合的情况下强化训练 | 配方、候选集合、容量、非回合动作、周期活动 |
| 传奇角色与条件组合 | The Twinkle Legends 通过传奇角色、心得、组合条件和传奇指引强化训练 | 条件效果、候选生成、可组合 Buff、阶段选择 |

上述机制来自官方剧本说明：[Climax](https://umamusume.jp/contents/game/scenario/climax/)、[Grand Live](https://umamusume.jp/contents/game/scenario/grandlive/)、[Grand Masters](https://umamusume.jp/contents/game/scenario/grandmasters/)、[U.A.F.](https://umamusume.jp/contents/game/scenario/uaf/)、[Mecha Uma](https://umamusume.jp/contents/game/scenario/mechaumamusume/)、[大丰食祭](https://umamusume.jp/contents/game/scenario/daihoshokusai/)、[无人岛](https://umamusume.jp/contents/game/scenario/designyourisland/)、[Beyond Dreams](https://umamusume.jp/contents/game/scenario/beyonddreams/)、[Toresenken](https://umamusume.jp/contents/game/scenario/toresenken/)、[The Twinkle Legends](https://umamusume.jp/contents/game/scenario/thetwinklelegends/)。

架构上必须支持三种差异：

1. 改变动作集合：例如增加课程、料理、建造、调校、试吃会。
2. 改变状态集合：例如 Performance、知识碎片、研究等级、蔬菜库存、团队等级。
3. 改变结算方式：例如年度 Pt、Live 成功度、团队评价、设施评价、最终多场比赛积分。

## 4. 设计目标与非目标

### 4.1 设计目标

- 支持多个剧本并行存在。
- 新增普通数据型剧本时，不修改通用引擎。
- 新增复杂机制时，只新增一个可测试的模块，不污染其他剧本。
- 同一套策略脚本可以读取通用状态和剧本扩展状态。
- 同一套自动化执行器可以执行训练、比赛、购买、休息等动作。
- 允许游戏版本、地区和剧本版本同时存在。
- 能从崩溃、断连、识别失败和页面跳转失败中恢复。
- 所有决策和执行步骤可记录、可回放、可诊断。
- 对未知状态采取安全停止，而不是盲点屏幕。

### 4.2 非目标

第一阶段不做：

- 完整复刻游戏内部比赛物理或 AI。
- 先实现所有日服剧本的所有细节。
- 通过硬编码坐标覆盖所有分辨率和所有版本。
- 让 WPF ViewModel 直接驱动每个剧本的细节。
- 让用户脚本直接调用任意 ADB 命令。
- 以一个巨大的 JSON `Dictionary<string, object>` 取代类型化领域状态。

## 5. 推荐工程结构

后续建议新增以下项目；名称可以根据现有解决方案风格调整，但职责不要合并：

```text
src/
├── UmaAssistantCore/                  # 现有 C++ Core，设备与输入
├── Umamusume.CoreBridge/              # 现有 C# Native bridge
├── Umamusume.Training.Domain/         # 纯 C#，通用育成领域模型与引擎
├── Umamusume.Training.Data/           # JSON schema、数据包加载、索引
├── Umamusume.Training.Scenarios/      # 内置剧本模块与模块组合
├── Umamusume.Training.Automation/     # 画面识别、动作适配、运行时
└── UmamusumeWpfGui/                   # 现有 WPF，只做 UI 与组装

tests/
├── Umamusume.Training.Domain.Tests/
├── Umamusume.Training.Data.Tests/
├── Umamusume.Training.Scenarios.Tests/
├── Umamusume.Training.Automation.Tests/
└── UmamusumeWpfGui.Tests/              # 现有测试
```

依赖方向：

```text
WpfGui
  -> Training.Automation
  -> Training.Scenarios
  -> Training.Data
  -> Training.Domain

Training.Automation -> CoreBridge
Training.Scenarios  -> Training.Domain + Training.Data
Training.Data       -> Training.Domain
Training.Domain     -> 无 WPF、无 ADB、无 ImageSharp、无 UI 依赖
```

禁止反向依赖：

- `Training.Domain` 不引用 `UmamusumeWpfGui`。
- `ScenarioModule` 不引用具体 View 或控件。
- `AdbTrainingExecutor` 不包含训练策略。
- `ViewModel` 不直接解析剧本 JSON。

## 6. 通用领域模型

### 6.1 CareerSession

`CareerSession` 表示一次完整育成运行，不等同于 WPF 任务，也不等同于 ADB 连接。

```text
CareerSession
├── SessionId
├── ScenarioId
├── ScenarioVersion
├── Region / GameVersion
├── Seed（如果能控制或记录）
├── TraineeId
├── LegacySelection
├── SupportDeck
├── CareerState
├── ExecutionState
├── Checkpoint
└── RunHistory
```

必须支持：

- 开始前校验剧本、马娘、支援卡和区域版本。
- 保存当前回合和当前页面状态。
- 在动作执行前后记录状态快照。
- 断连后恢复到最近可靠 checkpoint。
- 将最终育成结果保存为可引用的 veteran/trainee 记录。

### 6.2 CommonCareerState

通用状态建议保持类型化：

```text
CommonCareerState
├── Time
│   ├── Year / Grade
│   ├── Month
│   ├── Half
│   ├── TurnIndex
│   └── Phase
├── Trainee
│   ├── TraineeId
│   ├── Stats { Speed, Stamina, Power, Guts, Wit }
│   ├── SkillPoints
│   ├── Aptitudes
│   ├── Mood
│   ├── Energy
│   ├── Conditions
│   └── Skills
├── Training
│   ├── TrainingLevels
│   ├── AvailableTraining
│   ├── Participants
│   └── FriendshipStates
├── Race
│   ├── AvailableRaces
│   ├── ScheduledRaces
│   ├── Goals
│   ├── Fans
│   └── Results
├── Support
│   ├── Deck
│   ├── BondLevels
│   ├── Hints
│   └── PendingEvents
├── Inheritance
│   ├── Parents
│   ├── Sparks
│   └── Compatibility
└── ScenarioState
```

`ScenarioState` 不要直接把所有剧本字段塞进 `CommonCareerState`。它应由剧本模块拥有，并以剧本 ID 隔离：

```text
ScenarioState[
  "grand_live"
] = GrandLiveState

ScenarioState[
  "uaf"
] = UafState
```

通用策略只能读取模块公开的 `ScenarioView` 或标准化指标，不能随意修改剧本内部状态。

### 6.3 Action 与 ActionResult

玩家/策略脚本提出的是领域动作，不是点击坐标：

```text
Train(trainingType, optionalTarget)
Race(raceId, runningStyle)
Rest()
Outing(destination)
Infirmary()
BuySkill(skillId)
UseItem(itemId)
OpenScenarioPanel(panelId)
ScenarioAction(actionType, payload)
FinishOrStop(reason)
```

每个动作都要有：

- `ActionId`：一次运行内唯一。
- `Kind`：通用动作或剧本动作。
- `Preconditions`：页面、回合、体力、资源、目标条件。
- `ExpectedEffects`：执行成功后应出现的状态变化。
- `Cost`：是否消耗回合、体力、资源或次数。
- `Risk`：失败概率、识别风险、不可逆程度。
- `Timeout` 和重试策略。
- `IdempotencyKey`：避免重试时重复购买或重复使用道具。

动作执行结果必须区分：

```text
Succeeded
RejectedByDomain
RejectedByGameState
RecognitionFailed
ExecutionFailed
TimedOut
DeviceDisconnected
UnknownOutcome
```

`UnknownOutcome` 不能自动当作失败后重新点击，因为比赛、购买和消耗物品可能已经成功。

### 6.4 Event 与 Effect

通用事件系统负责描述：

- 事件何时可触发。
- 条件是否满足。
- 玩家选择项。
- 选择项产生的效果。
- 事件是否一次性、周期性或可重复。

效果建议使用有限的类型化操作：

```text
ModifyStat
ModifySkillPoints
ModifyEnergy
ModifyMood
AddCondition
RemoveCondition
AddSkillHint
AddResource
ConsumeResource
UnlockAction
ChangeObjective
EmitScenarioSignal
```

复杂逻辑通过 `EmitScenarioSignal` 发给剧本模块，不让通用事件系统知道“知识板”或“团队等级”的具体实现。

## 7. ScenarioModule 设计

### 7.1 ScenarioManifest

每个剧本必须有可验证的 manifest：

```json
{
  "schemaVersion": 1,
  "scenarioId": "climax",
  "displayName": "Make a new track!!",
  "version": "1.0.0",
  "supportedRegions": ["global", "jp"],
  "minGameVersion": "2025.01",
  "capabilities": [
    "free_race_schedule",
    "shop",
    "rival_hint",
    "yearly_objective"
  ],
  "moduleType": "builtin:climax",
  "definition": "scenario.json",
  "screens": "screens/",
  "localization": "localization/"
}
```

manifest 校验失败时，剧本不能出现在可选列表中。

### 7.2 模块职责

推荐的概念接口如下，先作为设计契约，不要求一次性实现全部方法：

```text
IScenarioModule
├── Manifest
├── CreateInitialState(context)
├── GetAvailableActions(context)
├── GetScenarioView(context)
├── ValidateAction(context, action)
├── BeforeAction(context, action)
├── AfterAction(context, action, result)
├── OnRaceStarted(context, race)
├── OnRaceFinished(context, race, result)
├── OnPhaseChanged(context, phase)
├── OnPeriodicReview(context)
├── GetObjectives(context)
├── EvaluateRun(context)
└── BuildFinalRewards(context)
```

实际实现时不应强制所有模块都覆写一个巨大接口。可拆成多个小能力接口：

```text
IScenarioStateProvider
IScenarioActionProvider
IScenarioActionValidator
IScenarioTurnHook
IScenarioRaceHook
IScenarioObjectiveProvider
IScenarioRewardProvider
IScenarioScreenProfileProvider
```

剧本模块内部可以组合通用组件：

```text
ScenarioModule
├── TimelineRule
├── ObjectiveRule
├── ResourceLoopRule
├── TeamGrowthRule
├── FacilityRule
├── ReviewRule
├── ShopRule
├── EventRule
└── ScreenProfile
```

### 7.3 生命周期

通用引擎只认生命周期，不认具体剧本名称：

```text
SessionCreated
  -> ScenarioInitialized
  -> TurnStarted
  -> ActionOffered
  -> ActionSelected
  -> BeforeAction
  -> ActionExecuted
  -> AfterAction
  -> StateReconciled
  -> TurnEnded
  -> PeriodicReview（可选）
  -> PhaseChanged（可选）
  -> CareerCompleted / CareerFailed / CareerAborted
```

剧本示例：

- Grand Live 在训练后增加 Performance，在课程动作后增加乐曲/技巧效果，在 Live 前进行期待度结算。
- U.A.F. 在动作候选生成时提供竞技训练，在选择同一主题时触发 Link Training。
- Mecha Uma 在训练后增加研究等级，在满足能量条件时提供 Overdrive。
- Climax 在比赛后增加 Shop Coin，并在年份结束时检查成绩 Pt。
- Beyond Dreams 在阶段回顾后检查团队等级，并开放下一阶段作战会议。

这些都应通过 hook、signal 和模块状态实现，不应写入通用 `CareerEngine` 的剧本分支。

## 8. CareerEngine 通用运行逻辑

### 8.1 引擎职责

`CareerEngine` 负责：

1. 管理一次 `CareerSession`。
2. 读取当前领域状态。
3. 向剧本询问可用动作和目标。
4. 验证策略选出的动作。
5. 触发通用和剧本生命周期。
6. 接收执行层结果。
7. 更新状态和 checkpoint。
8. 判断继续、完成、失败或需要人工介入。

它不负责：

- 识别屏幕坐标。
- 直接调用 ADB。
- 决定某个具体剧本的最佳培养路线。
- 解析某个剧本的私有 JSON 字段。

### 8.2 推荐主循环

```text
RunCareerSession
  1. LoadScenarioPack
  2. ValidateScenarioAndAccountData
  3. CreateCareerSession
  4. NavigateToCareerStart
  5. ObserveInitialState
  6. ReconcileState
  7. while session is active:
       a. ObserveScreen
       b. DetectCurrentPage
       c. ReconcileGameState
       d. CheckPendingModalOrEvent
       e. Ask ScenarioModule for available actions
       f. Ask StrategyPolicy to choose an action
       g. Validate action and preconditions
       h. Convert domain action to UI execution plan
       i. Execute with timeout and idempotency guard
       j. Observe expected result
       k. Reconcile state
       l. Persist action log and checkpoint
       m. Recover, pause, or stop on uncertain outcome
  8. EvaluateFinalResult
  9. Persist veteran/result summary
```

### 8.3 状态不是单一真相

自动化程序无法直接读取游戏内部全部状态。因此要区分：

```text
ObservedState       # OCR、模板、页面结构直接观察到的内容
DerivedState        # 根据规则计算出的内容
EstimatedState      # 根据历史推断但未确认的内容
UnknownState        # 还没有可靠证据
```

每个重要字段应携带来源和置信度：

```text
Value: 82
Source: OCR
Confidence: 0.97
ObservedAt: timestamp
```

策略只能在满足最低置信度时执行高风险动作。比如：

- 训练页面识别置信度不足：暂停，不点击训练按钮。
- 比赛结果未知：重新截图确认，不立即重赛。
- 技能购买结果未知：进入技能列表重新读取，不重复点击。
- 设备断连：保存 checkpoint，等待连接恢复。

## 9. 脚本系统设计

这里的“脚本”必须拆成三种不同对象。

### 9.1 剧本内容包

描述游戏中“有什么规则和内容”：

- 资源类型。
- 目标和阶段。
- 事件和奖励。
- 比赛列表和特殊比赛。
- 剧本专属动作。
- 剧本联动卡/角色。
- 屏幕 profile。

它不描述当前这一局要怎么培养。

### 9.2 养成策略脚本

描述“在当前状态下应该选择什么”：

- 训练权重。
- 体力阈值。
- 比赛选择规则。
- 技能购买优先级。
- 剧本资源使用时机。
- 高风险动作是否允许。
- 失败后是否暂停。

策略脚本应读取标准化上下文：

```text
StrategyContext
├── CommonStateView
├── ScenarioView
├── AvailableActions
├── UpcomingGoals
├── UpcomingRaces
├── ResourceSummary
├── RiskSummary
└── HistorySummary
```

策略脚本输出 `ActionIntent`，而不是坐标：

```text
ActionIntent
├── actionKind
├── targetId
├── priority
├── reason
├── allowedRisk
└── fallbackActions
```

### 9.3 执行脚本/视觉流水线

描述“怎么在游戏界面上完成一个领域动作”：

- 等待某个页面。
- 定位按钮或列表项。
- OCR 读取文字和数字。
- 模板匹配确认页面。
- 点击、滑动、返回、关闭弹窗。
- 等待动画稳定。
- 检查动作结果。

它不应包含训练权重或剧本资源决策。

三者关系：

```text
Scenario Pack + Current State
              │
              ▼
        Strategy Script
              │ ActionIntent
              ▼
        Domain Validator
              │ Validated Action
              ▼
        UI Execution Script
              │
              ▼
       Observation / Reconcile
```

## 10. 自动化脚本运行时

### 10.1 Runtime 状态机

建议独立实现 `TrainingRuntime`，不要复用一个只适用于固定任务的循环：

```text
Disconnected
  -> Connecting
  -> VerifyingGame
  -> AtHome
  -> StartingCareer
  -> SelectingScenario
  -> SelectingTrainee
  -> SelectingLegacy
  -> SelectingSupportDeck
  -> InCareer
       ├── Observing
       ├── WaitingForStableFrame
       ├── ResolvingModal
       ├── SelectingAction
       ├── ExecutingAction
       ├── VerifyingOutcome
       ├── Recovering
       └── Checkpointing
  -> Completed / Failed / Paused / NeedsUser
```

每个状态必须有：

- 进入条件。
- 可执行动作。
- 退出条件。
- 超时时间。
- 最大重试次数。
- 不可恢复时的处理。
- 日志上下文。

### 10.2 页面识别

`ScreenRecognizer` 的输出应是标准页面类型，而不是一个散落的 bool 集合：

```text
ScreenKind
├── Home
├── CareerStart
├── ScenarioSelection
├── TraineeSelection
├── LegacySelection
├── SupportDeckSelection
├── CareerMain
├── TrainingSelection
├── RaceSelection
├── RacePreparation
├── SkillList
├── ScenarioPanel
├── EventChoice
├── Result
├── Modal
└── Unknown
```

屏幕结果还应包含：

- `ScreenProfileId`。
- 参考分辨率。
- 当前截图帧 ID。
- 可交互区域。
- OCR 字段。
- 模板匹配结果。
- 置信度。

### 10.3 动作执行器

推荐使用领域动作到执行计划的两步转换：

```text
Domain Action
  -> ActionExecutor.ResolvePlan
  -> UiExecutionPlan
  -> VisualPipelineRuntime / UmaService
```

例如：

```text
Train(Speed)
  -> ensure CareerMain or TrainingSelection
  -> open training panel if required
  -> locate Speed button using screen profile
  -> tap with current frame id
  -> wait for result transition
  -> verify next turn / stat delta / page change
```

执行器必须使用当前截图的 frame ID 或等效版本号，防止截图过期后点击旧坐标。现有 Core 的 frame 生命周期设计可以复用这一原则。

### 10.4 重试和幂等

动作分为三类：

| 类型 | 示例 | 允许的重试方式 |
|---|---|---|
| 可安全重试 | 打开页面、返回、等待动画 | 重新识别后重试 |
| 需确认后重试 | 训练、休息、比赛选择 | 先确认当前回合和页面 |
| 不可盲重试 | 买技能、购买道具、使用资源、确认比赛 | 重新读取结果后决定 |

每个高风险动作都要写入：

```text
ActionStarted
ActionSubmitted
OutcomeObserved
OutcomeConfirmed
```

如果只看到 `ActionSubmitted`，但没有 `OutcomeConfirmed`，运行时必须进入 `NeedsUser` 或执行安全的重新观察流程。

## 11. 策略逻辑设计

### 11.1 策略和剧本规则分离

剧本模块回答：

- 现在允许做什么？
- 做这个动作需要什么？
- 动作会消耗什么？
- 当前阶段的目标是什么？
- 这个剧本资源有哪些效果？

策略模块回答：

- 多个动作中选哪个？
- 什么时候休息？
- 是否参加可选比赛？
- 哪个技能优先购买？
- 哪个资源效果现在最有价值？

### 11.2 推荐策略接口

```text
ITrainingStrategy
├── Initialize(context)
├── ChooseOpeningSetup(context)
├── ChooseTurnAction(context)
├── ChooseRace(context)
├── ChooseSkillPurchases(context)
├── ChooseScenarioAction(context)
├── ChooseRecoveryAction(context)
└── OnRunCompleted(context)
```

策略不能直接修改 `CareerState`。它只能返回意图，由领域引擎验证。

### 11.3 默认策略阶段

不要先写一个巨大规则函数，建议按阶段组合：

```text
OpeningStrategy
  -> 早期优先羁绊、基础训练、低风险成长

GoalPreparationStrategy
  -> 根据下一个目标比赛补足属性、适性和技能

ResourceOptimizationStrategy
  -> 根据剧本资源的收益/成本比决定使用时机

RacePlanningStrategy
  -> 目标比赛、粉丝需求、技能点、疲劳和风险权衡

FinaleStrategy
  -> 终局前集中强化、购买必要技能、确认终局条件
```

策略应该提供解释信息，例如：

```text
选择 Speed 训练：
- 下一个目标：Medium G1，距离适性 A
- 当前体力：78
- Speed 训练参与人数：4
- 友情训练：2
- 预估收益：Speed +42 / Power +18 / SkillPt +6
- 选择理由：预计收益高于恢复体力的机会成本
```

这会直接提升日志、调试和用户信任度。

## 12. 数据与资源包布局

现有 `resource/uma/database/global` 可以继续作为静态实体数据库；剧本内容建议独立出来。整体引用方式必须和 Daily Race 使用马娘数据库的方式一致：数据库负责实体和资源索引，任务/策略只保存 ID。

```text
resource/
├── uma/
│   ├── database/
│   │   ├── global/
│   │   │   ├── meta.json
│   │   │   ├── base_characters.json
│   │   │   ├── trainees.json
│   │   │   └── support_cards.json
│   │   └── jp/
│   ├── assets/
│   │   ├── images/
│   │   ├── templates/
│   │   └── screens/
│   └── system_reference/
└── hachimi/
    ├── ura/
    │   ├── manifest.json
    │   ├── scenario.json
    │   ├── objectives.json
    │   ├── races.json
    │   ├── events/
    │   ├── screens/
    │   └── localization/
    ├── climax/
    ├── grand_live/
    ├── uaf/
    ├── daily_race.json
    ├── shop.json
    └── team_race.json
```

### 12.1 大数据目录和加载职责

第一版可以继续由现有 `UmaDatabaseService` 承担加载入口，但长期应将它抽象为不依赖 WPF 的数据目录服务：

```text
ITrainingDataCatalog
├── Region / GameVersion / DataVersion
├── Trainees
├── BaseCharacters
├── SupportCards
├── Skills
├── Races
├── Events
├── Scenarios
├── TryGetTrainee(id)
├── TryGetSupportCard(id)
├── TryGetSkill(id)
├── TryGetRace(id)
├── TryGetEvent(id)
├── FindTrainees(query)
├── FindSupportCards(query)
├── GetRelatedSkills(...)
├── GetRacesForScenario(...)
└── ResolveAsset(entityId, assetKind)
```

推荐的加载顺序：

```text
DataCatalog.LoadAsync(resourceRoot, region, gameVersion)
  1. 读取 meta.json
  2. 校验 schema、region、数据版本
  3. 读取 base_characters.json
  4. 读取 trainees.json
  5. 读取 support_cards.json
  6. 读取 skills.json / races.json / events.json
  7. 读取 hachimi/<scenarioId>/manifest.json
  8. 校验所有外键引用
  9. 建立 id/name/alias/type/character/scenario 索引
 10. 发布 DataLoaded
```

加载失败时必须 fail closed：

- 不显示“看起来可用但查不到数据”的育成任务。
- 不允许使用空数据库继续启动自动化。
- 清楚报告缺少文件、重复 ID、无效外键、区域不匹配或资源缺失。

当前仓库的 `UmaDatabaseService` 已经会从 `resource/uma/database/global` 读取 `meta.json`、`base_characters.json`、`trainees.json` 和 `support_cards.json`，并建立字典索引。后续增加 `skills`、`races`、`events` 时，建议保持相同的 `TryGet...` / `Find...` 访问风格，并把 `indexes.json` 作为可选的预生成索引或校验来源，而不是让上层直接解析 JSON。

### 12.2 任务配置的引用规则

育成任务配置应类似 Daily Race，只保存稳定 ID 和用户选择，不保存完整数据对象：

```json
{
  "scenarioId": "ura",
  "traineeId": 100601,
  "legacyIds": [101701, 100401],
  "supportCardIds": [300101, 300201, 300301, 300401, 300501, 300601],
  "strategyId": "default-speed-medium",
  "racePlanId": "goal-safe",
  "options": {
    "pauseOnUnknownOutcome": true,
    "allowOptionalRaces": false
  }
}
```

配置中不应保存：

- 马娘显示名称。
- 支援卡完整 JSON。
- 适性、成长率、技能效果等可更新数据。
- 图片绝对路径。
- OCR 模板的实际文件内容。
- 运行时计算出来的当前属性和资源。

这些内容全部通过 ID 从大数据服务解析。这样数据更新、语言切换、图片替换和新增服装不会使既有任务配置失效。

### 12.3 ViewModel 的引用方式

育成配置页面应仿照 `DailyRaceTaskSettingsViewModel`：

```text
TrainingTaskSettingsViewModel
  constructor(ITrainingDataCatalog catalog)
    -> 监听 DataLoaded
    -> RefreshTrainees()
    -> RefreshSupportCards()
    -> RefreshScenarios()

用户搜索
  -> catalog.FindTrainees(query)
  -> 显示名称、稀有度、缩略图
  -> SelectedTraineeId = record.TraineeId

用户保存
  -> 只写 traineeId/supportCardIds/scenarioId/strategyId
```

需要保留的 Daily Race 经验：

- 数据尚未加载时，页面显示空列表或“正在加载”，不假造选项。
- 数据加载完成后刷新集合。
- 原来选中的 ID 如果已经不存在，清空选择并提示用户。
- 只有拥有资源、区域可用且有必要模板的实体才显示为可执行选项。
- 缩略图加载失败不能破坏 ID 选择本身。
- 搜索和显示可以用名称，保存和运行必须用 ID。

### 12.4 Runner/Strategy 的引用方式

育成执行器应和 `DailyRaceRunnerSelector` 保持同一种边界：

```text
TrainingActionExecutor
  -> 接收 traineeId / supportCardId / raceId
  -> catalog.TryGet...(id)
  -> 检查记录是否存在、available、region 匹配
  -> 解析所需图片/模板/屏幕 profile
  -> 生成视觉执行计划
  -> 执行并确认结果
```

例如选择比赛时，策略输出 `raceId`，执行器再读取比赛的名称、距离、场地、目标月份和对应页面资源；策略不直接拼接比赛名称，也不直接查 `races.json`。

例如选择支援卡时，策略输出一组 `supportCardId`；执行器和校验器再读取支援卡类型、可用技能、剧本联动标签和图片资源。

### 12.5 运行时缓存和按需加载

大数据可以分为三类缓存：

| 数据 | 建议加载方式 |
|---|---|
| 马娘、支援卡、技能、比赛基础记录 | 启动时加载并建立 ID 索引 |
| 事件、剧本规则、策略定义 | 选择剧本/策略时加载并校验 |
| 头像、模板、截图样本 | 首次使用时按需加载，并由资源解析器缓存 |

服务内部应返回只读记录或不可变快照，避免 ViewModel、策略和 Runner 修改公共数据。运行时属性必须复制到 `CareerState`，不能写回 `UmaTraineeRecord`。

### 12.6 静态实体与账号数据分开

静态数据：

- 马娘、服装、支援卡、技能、比赛、事件、剧本。

账号/运行数据：

- 是否拥有。
- 支援卡等级和突破。
- 已训练马娘。
- 因子和继承记录。
- 用户策略。
- 最近运行 checkpoint。

账号数据不能写回公共静态 JSON，否则不同账号或不同区域会互相污染。

### 12.7 ID 稳定性

所有后续任务、脚本、日志和数据引用都使用稳定 ID：

```text
trainee_id
support_card_id
race_id
skill_id
scenario_id
event_id
strategy_id
```

显示名称、翻译、图片文件名和 OCR 别名都属于可变数据，不得成为配置主键。

## 13. Mod 与剧本包规范

### 13.1 两级扩展模型

#### Level 1：数据型剧本包

适用于：

- 目标和阶段变化。
- 新资源、商店、奖励。
- 事件和效果。
- 比赛列表。
- 页面模板。
- 多语言文本。

不需要重新编译程序。

#### Level 2：内置代码模块

适用于：

- 复杂资源合成。
- 多角色同步成长。
- 非线性候选生成。
- 特殊结算和连锁规则。
- 数据 DSL 无法表达的算法。

代码模块必须实现稳定接口并经过版本检查。第一阶段不开放任意用户 DLL 热加载，避免安全、兼容和崩溃问题。

### 13.2 包校验

加载剧本包时检查：

- manifest schema 版本。
- scenario ID 是否冲突。
- 所有引用的实体 ID 是否存在。
- 资源文件是否存在。
- screen profile 是否完整。
- region 和 game version 是否匹配。
- capability 是否有对应运行时支持。
- 数据循环引用和事件死循环。
- 文本、图片、模板是否超过大小限制。

### 13.3 能力声明

能力应作为可查询 metadata：

```text
free_race_schedule
yearly_review
shop
resource_inventory
non_turn_action
team_growth
facility_building
multi_stage_finale
special_training
scenario_link
```

策略和 UI 根据能力显示选项，而不是根据剧本名称硬编码显示逻辑。

## 14. Screen Profile 与区域/版本适配

剧本逻辑不应知道坐标。画面适配放入 `ScreenProfile`：

```text
ScreenProfile
├── profileId
├── region
├── gameVersionRange
├── referenceWidth
├── referenceHeight
├── screens
│   ├── page recognition
│   ├── anchors
│   ├── templates
│   ├── OCR regions
│   └── safe click regions
└── transitions
```

一个页面可以有多个识别方案：

```text
CareerMain.global.v1
CareerMain.global.v2
CareerMain.jp.v1
CareerMain.modded.v1
```

识别器输出抽象元素：

```text
UiElement
├── SemanticId: "training.speed"
├── Bounds
├── Enabled
├── Text
├── Confidence
└── FrameId
```

执行器只消费 `SemanticId`，不消费资源文件路径或固定坐标。

## 15. Checkpoint、日志与恢复

### 15.1 Checkpoint 内容

每个安全动作完成后保存：

```text
CareerCheckpoint
├── SessionId
├── ScenarioId / Version
├── TurnIndex
├── LastKnownScreen
├── CommonStateSnapshot
├── ScenarioStateSnapshot
├── Observations
├── LastConfirmedAction
├── PendingAction
├── ResourceVersion
└── CreatedAt
```

checkpoint 不应保存截图二进制本身，可以保存 frame ID、截图路径或调试 artifact 引用。

### 15.2 日志事件

日志需要同时支持人读和机器分析：

```text
CareerSessionStarted
ScreenRecognized
StateReconciled
ActionProposed
ActionValidated
ActionSubmitted
ActionOutcomeConfirmed
ScenarioHookApplied
CheckpointSaved
RecoveryStarted
UserInterventionRequired
CareerCompleted
CareerFailed
```

每条日志至少包含：

- session ID。
- scenario ID/version。
- turn index。
- screen kind。
- action ID。
- 领域状态摘要。
- 置信度。
- 结果和耗时。
- 失败原因。

### 15.3 恢复原则

恢复优先级：

1. 重新截图并识别当前页面。
2. 重新读取当前回合和关键资源。
3. 对照最近 checkpoint 判断动作是否已生效。
4. 只有确认动作未生效时才重试。
5. 无法判定时暂停并请求用户确认。

禁止：

- 断连后直接从旧坐标继续点击。
- 识别失败后盲目重复购买或使用道具。
- 进程重启后丢失当前剧本和回合上下文。

## 16. 测试计划

### 16.1 Domain 单元测试

测试不依赖 WPF、ADB 或真实截图：

- 回合推进。
- 体力和干劲变化。
- 训练收益。
- 比赛和目标判定。
- 技能点与技能购买。
- 事件条件和效果。
- 资源增加、消耗和上限。
- 目标失败与终局。
- checkpoint 序列化。

### 16.2 Scenario Contract Tests

所有剧本都必须通过统一契约测试：

- manifest 可加载。
- 能创建初始状态。
- 初始状态包含合法动作。
- 非法动作会被拒绝。
- 生命周期 hook 顺序稳定。
- 资源不会凭空变成负数。
- 终局一定能收敛。
- 模块状态可以序列化和恢复。
- 不引用不存在的实体。

### 16.3 Replay Tests

每次自动化运行都应能保存脱离设备的 replay：

```text
Input:
  scenario pack
  initial setup
  observations
  action intents

Output:
  state transitions
  decisions
  validation results
  final result
```

回放测试用于：

- 修复策略回归。
- 比较新旧剧本规则。
- 重现用户报告的问题。
- 不连接模拟器验证运行时。

### 16.4 Visual Contract Tests

沿用当前模板匹配测试思路，为每个页面 profile 建立：

- 页面识别样本。
- OCR 区域样本。
- 正确按钮定位样本。
- 相似但错误页面的反例。
- 不同缩放和设备尺寸样本。
- 页面动画中间帧样本。

视觉测试必须验证语义元素，而不是只验证某一个固定坐标。

### 16.5 Integration Tests

至少包含：

- Native Core 连接和断连。
- 截图和 frame 生命周期。
- 输入坐标转换。
- 运行时在设备断连后的安全暂停。
- 动作超时和取消。
- WPF DI 能加载默认剧本和策略。

真实模拟器 smoke test 只在领域和视觉契约稳定后进行，避免把游戏随机性当作单元测试依据。

## 17. 分阶段开发步骤

### Phase 0：基线保护和边界冻结

工作内容：

- 保留当前工作区未提交改动，不做无关重构。
- 记录现有项目构建和测试方式。
- 确认现有资源目录、全局数据库和 system reference 的打包规则。
- 确认当前 region/version 的实际目标。
- 写出第一版 `Training.Domain` API 草图。

完成标准：

- 现有 CoreBridge/WPF 测试仍可运行。
- 新模块依赖方向已确定。
- 不把育成逻辑放入现有固定任务类。

### Phase 1：建立纯领域层

新增：

- `CareerSession`。
- `CareerState`。
- `Turn`、`Action`、`ActionResult`。
- `Event`、`Effect`。
- `ScenarioModule` 契约。
- `Checkpoint` 和 replay 模型。

暂不接：

- WPF。
- OCR。
- ADB。
- 真实游戏截图。

完成标准：

- 使用内存状态可以运行一局简化育成。
- 状态变化和动作验证都有单元测试。
- 可以序列化和恢复中途状态。

### Phase 2：数据加载与默认剧本

新增：

- scenario manifest loader。
- JSON schema validator。
- `ScenarioRegistry`。
- 统一 ID 索引。
- 最小 URA/基础目标型剧本。

URA 只实现通用规则和简单目标，作为所有后续剧本的基准，不要一开始追求完整数据覆盖。

完成标准：

- 新增一个数据包可以在不改 `CareerEngine` 的情况下被加载。
- 无效数据包能明确报错。
- 所有引用使用稳定 ID。

### Phase 3：实现剧本扩展能力

按差异类型逐步实现，不按发布时间一次性搬运全部剧本：

1. Climax：自由赛程、年度成绩 Pt、商店和 Rival。
2. Grand Live 或大丰食祭：资源生产、库存、消耗和阶段结算。
3. U.A.F.：分组、候选训练、Link Training、Heat Up。
4. Mecha Uma 或无人岛：研究/调校或设施建设。
5. Beyond Dreams：团队成员、等级、团队资源和阶段回顾。

完成标准：

- 每个扩展能力都有独立测试。
- 通用引擎没有剧本名称分支。
- 同一策略上下文可以读取不同剧本的标准化视图。

### Phase 4：接入屏幕识别和执行层

新增：

- `GameStateSnapshot`。
- `ScreenRecognizer`。
- `ScreenProfile`。
- `ActionExecutor`。
- `TrainingRuntime`。
- 当前帧校验和动作后验证。

接入顺序：

1. 读取首页和育成入口。
2. 读取剧本/马娘/支援卡选择。
3. 识别育成主页面。
4. 只执行训练动作。
5. 增加休息和外出。
6. 增加比赛选择和比赛前策略。
7. 增加技能购买。
8. 增加剧本专属面板和动作。

不要在第一版同时接入所有页面和所有剧本。

### Phase 5：策略脚本和用户配置

新增：

- 默认策略。
- 训练权重。
- 体力阈值。
- 比赛策略。
- 技能优先级。
- 剧本资源使用规则。
- 暂停条件和人工确认条件。

配置与策略代码分开：

- 普通用户配置 JSON/表单。
- 复杂用户策略可使用受限 DSL。
- 不允许策略脚本直接执行任意系统命令。

### Phase 6：WPF 集成

新增页面或面板：

- 剧本选择。
- 区域/版本选择。
- 马娘和支援卡配置。
- 策略选择。
- 剧本资源面板。
- 当前回合和目标。
- 当前状态置信度。
- 运行日志和恢复提示。
- replay 和 checkpoint 管理。

WPF 只绑定服务和 ViewModel，不直接读取 scenario JSON 或调用模板匹配器。

### Phase 7：Mod SDK 和维护工具

新增：

- scenario pack 模板。
- manifest/schema 校验命令。
- 资源引用检查。
- 页面 profile 检查。
- replay 运行器。
- 事件/资源调试面板。
- 版本兼容报告。

完成标准：

- 外部开发者可以创建数据型剧本包。
- 包校验可以在运行前发现大多数错误。
- 不需要修改核心引擎即可加载普通 Mod。

## 18. 推荐的第一批实现文件

以下是下一轮真正开始编码时的建议落点，不代表本轮已经创建：

```text
src/Umamusume.Training.Domain/
  CareerSession.cs
  CareerState.cs
  CareerTime.cs
  CareerAction.cs
  CareerActionResult.cs
  ScenarioContracts.cs
  EventContracts.cs
  Checkpoint.cs
  ReplayModels.cs

src/Umamusume.Training.Data/
  ScenarioManifest.cs
  ScenarioPackLoader.cs
  ScenarioRegistry.cs
  ITrainingDataCatalog.cs
  UmaTrainingDataCatalog.cs
  ScenarioDataRepository.cs
  ScenarioPackValidator.cs

src/Umamusume.Training.Scenarios/
  BaseScenarioModule.cs
  UraScenarioModule.cs
  ClimaxScenarioModule.cs
  ResourceLoopScenarioModule.cs
  TeamScenarioModule.cs

src/Umamusume.Training.Automation/
  TrainingRuntime.cs
  ScreenRecognizer.cs
  ScreenProfile.cs
  GameStateReconciler.cs
  TrainingActionExecutor.cs
  RecoveryCoordinator.cs
  TrainingStrategy.cs

tests/Umamusume.Training.Domain.Tests/
tests/Umamusume.Training.Data.Tests/
tests/Umamusume.Training.Scenarios.Tests/
tests/Umamusume.Training.Automation.Tests/
```

第一步只应创建 Domain、Data 和最小测试，不要同时修改 WPF 页面、Native ABI 和所有视觉资源。第一版 `UmaTrainingDataCatalog` 可以包装现有 `UmaDatabaseService` 的读取结果；后续再把数据服务从 WPF 项目移动到 `Umamusume.Training.Data`，让 `DailyRace` 和育成共同依赖同一个数据目录接口。

## 19. 必须避免的架构陷阱

### 19.1 一个剧本一个巨大 Runner

问题：

- 复制粘贴大量页面逻辑。
- 修复训练点击时影响比赛脚本。
- 不能共享事件、目标、checkpoint 和重试逻辑。

解决：通用运行时 + 场景模块 + 语义动作。

### 19.2 所有剧本字段都放进一个 CommonState

问题：

- 状态不断膨胀。
- 新剧本字段污染旧剧本。
- 序列化和版本兼容困难。

解决：通用状态 + `ScenarioState[scenarioId]` + typed module state。

### 19.3 策略直接点击

问题：

- 策略无法测试。
- 分辨率和页面变化会破坏决策。
- 重试时无法判断动作是否已经生效。

解决：策略输出领域动作，执行层负责转换成 UI 操作。

### 19.4 OCR 结果直接当作绝对真相

问题：

- 动画、遮罩、缩放和误识别会产生危险点击。
- 比赛/购买等不可逆动作可能重复执行。

解决：状态来源、置信度、动作后验证和 `UnknownOutcome`。

### 19.5 直接把所有规则做成 JSON 表达式

问题：

- 复杂规则变成不可调试的字符串表达式。
- 类型错误只能在运行时发现。
- 很难做 IDE、测试和版本迁移。

解决：简单内容数据化，复杂算法使用小型内置模块；两者通过稳定契约连接。

### 19.6 过早追求完整游戏模拟

问题：

- 需要大量隐藏规则和随机数据。
- 与自动化助手的实际目标不一致。
- 视觉识别和执行可靠性反而被推迟。

解决：先做“观察—决策—执行—确认”的助手闭环；必要时只做足够支持策略评估的领域模拟。

## 20. 当前默认假设

本设计默认项目目标是“赛马娘育成自动化助手”，而不是独立的完整游戏模拟器。因此：

- 游戏画面是外部事实来源。
- 领域层是决策、校验、资源和回合模型。
- 真实结果必须通过截图/OCR/模板重新确认。
- 无法确认的状态优先暂停。
- C++ Core 不承载育成规则。
- 首个交付目标是稳定支持一个基础剧本，再用不同机制剧本验证扩展边界。

如果后续目标改为完整模拟器，应在 `Training.Domain` 之外增加独立的 `RaceSimulation`，不能把比赛模拟逻辑塞进自动化执行器。

## 21. 开始实现前需要确认的事项

正式编码前需要锁定：

1. 第一目标区域：Global、JP，还是同时支持。
2. 第一目标剧本：URA 基础流程，还是直接从当前主力剧本开始。
3. 是否允许策略脚本使用外部 DSL。
4. 是否只支持视觉识别，还是未来接入更可靠的游戏数据源。
5. 运行失败时的默认行为：自动重试、暂停，还是请求确认。
6. 第一版是否需要完整技能购买和比赛策略。
7. scenario pack 是否允许第三方加载。

在这些事项没有完全确定前，建议先按“Global + 基础目标型剧本 + 视觉识别 + 安全暂停”实现第一条闭环。

## 22. 最终验收标准

完成第一阶段架构后，应满足：

- `CareerEngine` 不包含具体剧本名称判断。
- 通过注册即可加载多个剧本。
- 普通剧本可以用数据包描述。
- 复杂剧本可以通过独立模块扩展。
- 策略只输出领域动作，不操作坐标。
- 执行层只执行已经验证的动作。
- 识别结果带置信度和来源。
- 高风险动作有动作后确认。
- 断连、超时、识别失败和未知结果可以安全暂停。
- 中途状态可以 checkpoint 和恢复。
- 运行记录可以 replay。
- 新剧本不会要求修改 WPF 主页面结构。
- 现有连接、CoreBridge、视觉流水线和固定任务测试不被破坏。

这套结构的核心价值是：把《赛马娘》不断新增的剧本，归类为可组合的时间轴、目标、资源、团队、设施、事件和阶段结算规则；把设备控制和屏幕变化限制在自动化适配层；让后续开发从“复制一份脚本”变成“新增一个剧本包或小型规则模块”。

## 23. URA Finale ADB 实跑与模板验收记录

本节是第一条真实闭环的验证记录，不改变前面的架构契约。运行方式严格使用 ADB，设备为 `emulator-5554`，游戏画面为 `900x1600`；实体选择使用现有全局马娘数据库中的 `oguri_cap`，脚本仍只传递实体 ID，不把角色名称或图片路径写入策略。

### 23.1 已验证的运行链

```text
Career Start
  -> Junior / Classic / Senior common training loop
  -> career objective chain
  -> All goals achieved
  -> Going to the URA Finale!
  -> URA Finale Qualifier
  -> URA Finale Semifinal
  -> URA Finale Finals
  -> Challenge Complete / After the URA Finale Finals
```

三场 URA Finale 的实际采集结果：

| 阶段 | 场地 | 距离 | 闸位 | 结果 | 代表结果截图 |
|---|---|---:|---:|---:|---|
| Qualifier | Hanshin Turf | 1800m | 18 | 1st，1:44.7 | `screens/captures/ura_prelim_finish.png` |
| Semifinal | Kyoto Turf | 1800m | 18 | 1st，1:45.3 | `screens/captures/ura_semifinal_result.png` |
| Finals | Kyoto Turf | 1600m | 18 | 1st，1:30.1 | `screens/captures/ura_final_finish.png` |

### 23.2 采集产物与职责边界

- `manifest.json`、`scenario.json`、`objectives.json`、`races.json`：场景包内容和引用关系。
- `events/events.json`、`localization/en.json`：场景事件与可替换文本入口。
- `screens/screen_profile.json`：页面识别、OCR 区域和语义动作映射；不保存执行器直接点击坐标，不属于策略脚本。
- `screens/execution.json`：按 Daily Race/Hachimi 任务 schema 描述模板匹配与 `ClickSelf` 执行；ROI 只限制搜索范围。
- `screens/captures/`：ADB 原始运行证据和稳定页面代表帧；动态比赛播放、加载和 Connecting 画面不得作为稳定模板。

当前 profile 覆盖 38 个可加载 screen contract，包含育成回合、训练/休息结果、事件、目标比赛、比赛播放、比赛结果、奖励、URA Finale 和回到 Home 的页面。

### 23.3 失败与重试分支

本次 Arima Kinen 实跑出现了真实失败分支：第 5、第 8、第 3，随后使用游戏提供的最后一次重试获得第 1。该过程证明执行器需要保留：

1. `UnknownOutcome` / 失败结果的独立状态。
2. `retryCount` 和闹钟资源变化。
3. 只有在结果页确认目标完成后，才推进 `objectiveId`。
4. 最后一次重试失败时必须安全暂停，不得把普通第 3 名误判为目标完成。

因此 `race_retry` 被声明为 URA capability，且不应写成某个按钮坐标的特殊分支。

### 23.4 验收结论

- JSON 场景包、事件包、英文 localization 和 screen profile 均可解析。
- profile 引用的代表模板和比赛结果截图均存在。
- 目标链覆盖普通育成目标、URA 三阶段和终局状态。
- 通过 `raceId`、`objectiveId`、`scenarioId` 引用数据，符合 Daily Race 的大数据引用方式。
- URA 专属逻辑集中在场景包与模块 hook；通用执行器只消费 `SemanticId` 和已验证动作。

### 23.5 终局结算到主页的完整闭环

在完成 URA Finale Finals 后，继续使用 ADB 完成了终局页面链，并以回到主页作为本次实跑完成条件：

```text
Complete Career
  -> Finish confirmation
  -> Career Rank
  -> Sparks
  -> Sparks keep confirmation
  -> Career result / major wins
  -> Rewards: bond and fans
  -> Rewards: support cards, support points and items
  -> Career Complete
  -> To Home
  -> Home
```

本次终局稳定页面证据：

| 语义页面 | 代表截图 |
|---|---|
| Complete Career confirmation | `screens/captures/ura_complete_career_next.png` |
| Career Rank | `screens/captures/ura_post_finish_loaded.png` |
| Sparks | `screens/captures/ura_career_rank_next.png` |
| Sparks keep confirmation | `screens/captures/ura_sparks_confirmed.png` |
| Career result | `screens/captures/ura_final_result_close.png` |
| Rewards: bond / fans | `screens/captures/ura_rewards_loaded.png` |
| Rewards: support cards / items | `screens/captures/ura_rewards_support_loaded.png` |
| Career Complete | `screens/captures/ura_rewards_support_next.png` |
| Returned Home | `screens/captures/ura_returned_home.png` |

这些页面属于通用育成终局流水线，不应写进 URA 的策略分支；后续剧本只需提供自己的终局奖励、剧本结算和 capability 定义，复用 `CareerComplete`、`RewardSettlement`、`ReturnToHome` 等通用状态。`Career Complete` 页的 `To Home` 是本次运行的最终安全终止动作，不能把中间的加载页、奖励动画或模糊过渡帧注册为模板。

### 23.6.1 实现校正：点击模板与剧本状态必须同时遵守契约

上一版实现曾把 `screen_profile.json` 的 `anchors.bounds` 当成直接点击坐标，这不符合本仓库 `daily_race.json` 的执行标准，也不符合本设计第 9、10、19 节的职责边界。本版以以下规则为准：

- `screen_profile.json` 只描述页面模板和语义动作映射；不保存执行器直接点击的按钮坐标。
- `screens/screen_profile.json` 负责把页面动作名绑定到 execution task；新增剧本只需提供自己的 screen profile，不需要在 Pipeline 中复制按钮映射。
- `screens/execution.json` 使用现有 Hachimi JSON schema：`algorithm: MatchTemplate`、`action: ClickSelf`、`template`、`templThreshold`、`roi`、超时、轮询和状态转移字段。动态数据库选择也通过 JSON 的自定义 action 节点进入可插拔适配器。
- `roi` 仅是按钮模板的搜索范围；真实点击点来自当前截图中的模板匹配结果 `TemplateMatchResult.CenterX/CenterY`。
- 场景加载器同时校验目标、赛事、事件、页面 profile 和 execution task 的 ID/资源引用，任一层不完整都 fail closed。
- `UraScenarioModule` 持有目标链、赛事链、URA Finale 阶段和重试规则；视觉执行器不决定目标推进，策略也不读取坐标。
- `UraCareerSessionState` 区分观察值、来源和置信度，并把回合、目标、当前赛事、重试次数、粉丝、比赛结果和完成目标写入 checkpoint。
- 未确认的比赛结果不会自动推进目标；可重试赛事只有在数据包声明 `retryPolicy` 且重试次数未超限时才允许回到同一目标，否则进入安全暂停。

### 23.6 第一版代码接入

本阶段已将上述契约接入现有 WPF/ADB 任务架构，入口为 `ura-training`：

- `UraScenarioPackLoader` 加载并校验 manifest、scenario、目标、比赛、事件、screen profile 和 execution；场景内资源引用必须位于场景包目录内，公共大数据目录作为共享 ID 引用记录，screen ID 不得重复。
- `AdbUraTrainingPipeline` 实现 Home → Career → 选择剧本/马娘/支援卡 → 育成回合 → 目标赛 → URA 三阶段 → 终局结算 → Home 的可恢复式观察-决策循环；所有静态按钮动作均通过 `screen_profile.json` 调用 JSON execution task。
- `UraTraineeSelector` 复用全局 system reference，按数据库 `traineeId` 在选择网格中匹配并验证后点击；不满足置信度时安全暂停。
- `supportCardIds` 只保存全局数据库 ID；当前资源包没有支援卡模板时，空列表才允许游戏自动填充，配置了 ID 则直接安全暂停，不会静默选择错误卡组。
- `UraTrainingPlanner` 只输出领域动作，ADB 坐标、模板和页面语义均由 screen profile 与执行层负责。
- 训练结果、休息确认、事件选择、比赛播放设置、不同比赛阶段结果/奖励、目标更新和奖励支援卡页均作为独立 screen contract，避免把页面差异塞进策略分支。

验证结果：应用和测试项目均以 0 warning / 0 error 构建；URA 场景包当前包含 38 个可加载 screen contract、48 个 `MatchTemplate + ClickSelf` execution task，所有 profile/execution/result 资源引用均存在；场景加载、目标链、重试和决赛阶段定向测试 8/8 通过。全量测试在现有打包测试处超时，未产生失败断言；尚未把这版代码再次驱动真实模拟器跑完整局，因此真实运行仍应保留 `pauseOnUnknownOutcome`，先做受控 smoke run。
