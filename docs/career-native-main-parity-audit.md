# Career 原生迁移回归核对

对照基线：本地 `main`，提交 `52619a03d008ffa98e35f0529c34af5583440fa7`。
实现保持 GameTasks、Career Runtime、CareerDomain、剧本适配器、TaskEngine、Bridge、WPF 的现有分层。

## 核对范围与修复

| 流程 | 对照 main 的行为 | 原生实现的处理 |
| --- | --- | --- |
| 首页、选马、继承 | 按入口阶段限制候选；选马任务已串联 Next | 移除重复 Next；保留阶段限制；首页未选中时不误进入结算返回状态 |
| 继承自动确认及手动卡片 | 自动确认使用 auto_select.confirm，访客勾选使用 guest.checkbox；自动选到自有卡时无需借用确认 | 修正两个不存在的区域引用；勾选探测使用新弹窗帧；自有结果可以继续；读取嵌套 badgeRoi 时不比较不同 JSON 对象的迭代器；缺少控制元数据时立即报告具体名称 |
| 配卡 | 普通配卡、自动确认和可用 Start 分开；预设为五张自有卡和一张借卡 | 使用稳定 Deck 标题；Start 使用彩色匹配；补齐无指定 ID 时的借卡和好友列表任务 |
| 入口稳定性 | 两帧确认，并容忍过渡期的识别空白和单次截图丢失 | 不接受预算末尾孤立匹配；入口、开局及动作确认均在有限预算内重试截图失败；取消立即终止；开始后只等待 Final Confirmation |
| 正常开局 | 模式、跑法、开场跳过、快速模式顺序执行 | 开局只识别开局和育成候选；确认的观测交接给协调器 |
| 配卡完成后的开局交接 | 新育成最终确认不能按结算续育成识别 | Native runner 把入口已确认的 screenId 传给开局模块；首次只确认该页面，开局候选排除 settlement，禁止 allowResume；Epithet 需额外验证标题文字，避免共享绿色标题栏误匹配 |
| 页面识别 | main 未将资源中的 requiredText 统一设为 OCR 硬门槛 | 默认恢复模板识别；只有显式 verifyRequiredText 才做硬性文字验证 |
| 运行阶段 | Career Observer 不扫描入口页面 | 增加 ObserveCareer；避免休息被判成配卡确认等跨流程错误 |
| 技能 | Back 只用于已进入技能流程后的导航 | 技能页要求 Learn 和 Skill Points；通用 Back 不再全局证明技能页；购买确认需要实际 Learn 按钮，防止 Final Confirmation 被当作技能弹窗 |
| 事件 | 特定事件可用标题 OCR 和已准备好的选项按钮回退 | 读取同一资源快照的事件目录；校验 Effects 彩色字形、标题、选项彩色字形、两帧事件身份；保留声明的语义动作；同帧标题只 OCR 一次 |
| 训练与智能扫描 | 选择页、结果页、确认后的训练分别处理 | 选择页增加类型图标证明；保留已有扫描与策略测试；确认计数支持出道前和决赛日历不变的回合 |
| 满体力连续休息 | 数字 OCR 参与训练增益/失败率读取，候选证据按当前回合使用 | 恢复任务包装层转发 RecognizeNumericImages；休息、医务室、外出和比赛确认后清理候选；以日期、倒计时和已确认行动计数检查扫描缓存。记录体力、决策原因、五类候选读数和置信度 |
| 继承 GO | 三月末行动后等待 GO，不操作其下方主页面 | 剧本产生等待决策，普通和智能策略均优先遵守 |
| 比赛入口 | 当前页面和按钮两帧未变时，只允许一次验证重试 | 30 秒入口窗口；两张完整截图相同才允许重试，动作图重新匹配按钮；最多重试一次，动画期间不重复点击 |
| 无赛事 | 返回主页面后，本回合不再重开空列表 | 剧本阻止当前回合的赛事决策；下一回合重新允许赛事 |
| 休息、医务室、外出、训练返回 | 使用运行阶段候选和 45 秒返回预算 | 不再统一用 15 秒和全页面扫描确认返回 |
| 赛事结果、闹钟和结算 | 结果确认后才应用剧本进度；终局后返回首页才完成 | 保留既有资源结果验证和拒绝闹钟处理；补回初次续育成从结算弹窗恢复的识别 |
| 可选探测 | Reset-disabled 和 Asc 探测未命中可走回退或跳过 | 正常未命中记为 Debug，不上报 TaskFailed；必需任务和输入失败仍报错 |
| 页面优先级 | 入口按明确页面顺序识别，不能只按片段中的局部优先级混排 | 恢复首页、剧本、选马、继承、配卡、最终确认的顺序，防止共享 Start 按钮误判剧本页 |
| 原生 OCR 生命周期 | main 的长期 OCR 服务保持运行时有效 | 原生模块固定 MTA 生命周期并在退出时清理工厂缓存，避免多次短调用或线程切换后崩溃 |
| 取消结果 | 任务取消时返回可用状态和说明 | 补齐无消息的取消与失败结果，保持进度桥接 |
| 日期恢复、独立育成、抓娃娃 | 保留检查点、设备身份、输入来源和确认状态 | 继续走原生恢复与专属流程，纳入完整已有回归和架构检查 |

## 验证方式

- 在已有执行回放之外，新增真实截图和 Windows 原生 OCR 检查，覆盖配卡、最终确认、继承、训练、休息、针灸和支援卡事件、Race Day、技能及其三类弹窗、终局页面。
- 三张 MuMu 现场截图保存在 `testdata/hachimi/ura/captures/`，包含借卡为空、已配齐和最终确认三种状态；现场打开最终确认后取消回配卡，TP 保持 100。
- 后续继承确认失败现场补了 Legacy Select 和 Confirm Auto-Select 两张截图；自动继承回放使用真实模板匹配与这两张截图，覆盖访客开启/关闭及无需借用的完成结果。手动继承回放验证两个槽位读取嵌套 badgeRoi。
- 新增出道前跳过结果、空赛事列表、继承等待、一次赛事重试和可选探测事件语义的回归。
- 入口、开局和赛事动作确认的回放包含一次截图失败后恢复的情况；赛事动画回放检查不重复点击。
- 验证使用实际画面内容作为断言依据；部分历史文件名与画面不一致，未以文件名代替画面事实。

## 最终验证结果

| 测试集 | 用例 | 断言 |
| --- | ---: | ---: |
| Career | 79 | 1,223 |
| GameTasks | 46 | 589 |
| TaskEngine | 22 | 415 |
| Recognition | 9 | 1,741 |
| 合计 | 156 | 3,968 |

- 上述四组完整测试均通过；资源包检查的 7 个场景和架构边界检查通过。
- 继承补修后重跑 GameTasks 全部 44 个用例（576 条断言）及架构检查；其余三组沿用此前结果。新增继承用例的独立执行结果为 2 个用例、20 条断言，日志为 `build/career-legacy-*`。再次构建并同步 Debug DLL，与 Release 和 staging 的 SHA-256 一致。
- 开局交接补修后完整 Career 76 个用例（1,166 条断言）、GameTasks 45 个用例（585 条断言）通过，真实感知矩阵为 55 条断言。资源包及架构检查再次通过，Debug DLL 和四个资源配置已同步、核对哈希；日志为 `build/career-start-phase-*`。
- 实机原生执行器复用已选六张卡，执行 Support Start、模式/跑法选择、最终 Start、开场跳过、快速模式配置，确认进入 Junior Year Pre-Debut 主界面；没有执行 career_epithet_confirm。验证专用设置 maxSteps=1，按预算退出的任务结果为失败，不能将其称为完整育成成功；只打开的外出弹窗已取消，未确认外出，游戏留在主界面。用户保存设置未改。证据为 `build/career-start-live-*` 及 `debug/career-entry-live/native-start-main-20261012.png`。
- 原生只读排序探测在同一会话实测：首次截图方法标定共 5,942 ms，后续探测 1,540 ms，均无输入；`build/career-sort-live-*` 记录两次任务成功。历史日志中约 30 秒的相邻排序日志间隔包含整张卡的筛选/排序循环，不能当作单次 Asc 探测耗时。
- 连续休息补修：ADB 截图为满体力出道 Race Day，像素测量 99%、置信度 0.74449，高于资源中的 0.70 门槛。发现恢复包装层遗漏数字 OCR 转发，以及无安全候选缓存未在休息后清空。新增 4 个回归覆盖真实体力像素、出道前缓存期限、休息后重新扫描，以及完整原生任务包装后的数字 OCR 调用；该集成回放仅真实匹配训练区域，截图白色遮罩之外的流程使用模拟状态。
- 连续休息补修后完整 Career 79 个用例（1,223 条断言）、GameTasks 46 个用例（589 条断言）及架构检查通过；其余两组沿用此前结果。WPF 构建通过，Debug DLL/回放工具同步并核对与 Release、staging 哈希一致。日志为 `build/career-smart-rest-*`，现场截图为 `testdata/hachimi/ura/captures/career_race_day_energy_full_mumu_20261012.png`。此次仅读取现场，未推进游戏，也未验证完整实机育成通关。
- WPF Release 构建通过，0 错误；NuGet 对现有 ImageSharp 3.1.11 依赖产生 10 条告警，此次未变更该依赖。
- Debug 输出的原生 DLL、回放工具和四个资源配置已更新。DLL 与最新构建产物 SHA-256 一致，旧 DLL 保留为 `.previous-*` 备份；正在运行的进程需要关闭再打开才能加载新版。

完整验证结果与构建记录见本次任务的 `build/career-parity-*` 日志。真实截图识别与完整实机育成不是同一项验证；此记录不将离线回放称为完整实机通关。

## 原生重构版行为补齐与 main (52619a03) 完整对齐核对

以下为实施阶段针对本地 `main`（`52619a03`）的 10 项静态修复记录。当时尚未编译或执行回归；后续实际验证发现的问题、补修及结果见文末“实际构建与回归验证”，不能仅据此静态记录认定全部行为已验证。

### 1. 已恢复行为

| 环节 | 问题与遗漏 | 恢复后行为与静态对应实现 | 对应模块与文件 |
|---|---|---|---|
| **比赛续跑** | 从 Race Day / Race List 恢复时目标为空，列表动作 target 无法解析；无法识别第 3 场连续赛事 | 根据已有目标或当前页面恢复赛事意图（`race_day` -> `"race"`, `race_list` -> `"fans"`, 或从 `state.observedGoalKind` 恢复）；从赛事列表/详情续跑时，若无主界面行动且触发 `ShouldDeferRace(state)`，暂停自动化并输出与 main 一致的连续赛事保护提示，避免误入第 3 场可选赛事；若 `MustRaceNow` 则允许继续 | `CareerFlowCoordinator.cpp`<br>`UraScenarioModule.cpp` |
| **粉丝目标** | 粉丝目标被错误映射为直接选择首场赛事 | 严格对齐 main 行为：只有 `grade_race_count` 目标使用 `race.fans_entry`；粉丝目标、推荐赛事目标、最终关卡及未知目标一律优先匹配 `race.recommended_entry`，未找到推荐时安全回退至首场 `race.fans_entry` | `CareerActionExecutor.cpp` |
| **医务室** | 普通与智能策略中赛事判断覆盖医务室，与 main 顺序不一致 | 在主界面等待条件（如继承 GO 等待）解除后，将 `state.infirmaryAvailable` 优先于赛事决策及训练决策处理；同时兼顾智能训练策略与普通/比率训练策略 | `TrainingStrategies.cpp`<br>`UraSmartTrainingStrategy.cpp` |
| **目标完成流程** | 缺少完整的目标页识别条件；比赛入口便开启完成标记，赛后主界面也缺少等待保护 | 1. 明确启用目标探测（Probe Armed）条件：仅非比赛行动（训练、休息、外出、医务室）确认后开启；比赛入口不开启；比赛结果确认或赛后回放完成时开启。<br>2. 页面识别门槛：`goal_objective_complete` 与 `goal_complete` 必须满足 probe armed 或赛后恢复；`goal_update` 严格要求上一页为 `goal_objective_complete`。<br>3. 赛后主界面等待保护：若处于 probe armed 且上一行动为比赛，在主界面等待 250ms 避免横幅动画未完成时抢跑 | `CareerObservationReader.cpp`<br>`CareerFlowCoordinator.cpp`<br>`UraScenarioModule.cpp` |
| **动作图结果** | 执行器未将 `race.replay.completed` 等动作图终态结果通知协调器与剧本 | 执行器通过 `TakeLastActionOutcome()` 将动作终态结果提交协调器；协调器分发至剧本更新 `raceReplayFlowCompleted`、根据目标剩余次数重新武装目标探测，并更新跑法已配置 (`raceStrategyConfigured`) 及闹钟重试选择 | `CareerActionExecutor.cpp`<br>`CareerFlowCoordinator.cpp`<br>`UraScenarioModule.cpp` |
| **比赛过渡** | 推荐弹窗、赛事列表进入详情缺少 main 的完整 30 秒窗口和一次重试保护 | 将 `raceTransition` 范围扩展至 `career_main`, `career_races_ready`, `race_day`, `race_recommendations`, `race_list`；配置 30 秒超时窗口与两帧完全相同时的一次重新匹配按钮的安全重试；候选页面补充 `race_details`, `race_attributes`, `race_runner` | `CareerActionExecutor.cpp` |
| **训练提交** | 智能扫描只接受结果页或日期增加；倒计时减少、直接到比赛日没有完整处理 | 统一按 main 规则通过 `CheckAndConfirmTrainingConsumed` 消费训练：接受训练结果页、强制比赛日到达（`race_day` / `career_races_ready`）、决赛倒计时减少（`turnsToGoal - 1`）以及主界面回合行动递增；确认后推进训练比例/策略，重置扫描器并清除待确认记录 | `CareerFlowCoordinator.cpp` |
| **停止后恢复** | 普通技能仅存在内存；智能训练确认记录没有持久化 | 引入 `CareerPersistenceStore.hpp`：<br>1. `NormalCareerSkillCache`：基于 `checkpointDirectory`、`traineeId` 和设备识别哈希（FNV-1a）隔离持久化已购技能；断点恢复自动加载，新育成或结算回首页时清理。<br>2. `SmartTrainingConfirmationStore`：记录未确认的智能训练点击；重启/恢复时在主界面核对并安全作废，在训练选择页暂停防重复点击；新育成或结算回首页时清理 | `CareerPersistenceStore.hpp`<br>`CareerSkillLearningFlow.cpp`<br>`CareerFlowCoordinator.cpp`<br>`NativeCareerTaskRunner.cpp` |
| **结算续跑** | 完成判断只接受两种结算完成页；从故事解锁等返回首页可能被判失败 | 扩展 `IsTerminalScreen` 集合，纳入 `career_complete`, `career_complete_close`, `career_story_unlocked`, `career_story_unlocked_compact`, `career_story_unlocked_to_home`, `home_unselected`；在识别到任一结算终态页后再返回 `home` 时，确认整场育成圆满完成并清理各持久化记录 | `CareerFlowCoordinator.cpp` |
| **主界面 OCR** | main 的成功文本缓存未迁移，主界面重复 OCR 相同区域 | 引入 `CareerMainTextCache`：当主界面关键字段（`scenario.phase`, `objective.turns_left`, `objective.title`, `mood.current`）ROI 的像素数据（灰度与 RGBA）与缓存帧完全一致时，直接复用上一次成功的文本与置信度；发生换页离开 `career_main`、提交行动、开局进入或恢复时清除缓存 | `CareerObservationReader.hpp`<br>`CareerObservationReader.cpp` |

### 2. 保留的设计差异

- **原生 C++ 架构与类型安全**：main 使用 C# 托管执行链；原生重构版采用 C++20、明确的所有权分层（`TaskEngine` -> `GameTasks` -> `Career` -> `CareerDomain`）、强类型 `ObservedValue<T>` 和 RAII 资源管理。
- **持久化设备散列**：main 采用 SHA-256 计算设备标识字符串并截断，原生实现采用标准 FNV-1a 64 位哈希生成统一的 16 位十六进制设备标识（`normal-skills-{traineeId}-{deviceHash}.json`），无需依赖额外加解密平台库。
- **扫描与决策解耦**：智能训练评分器（`UraSmartTrainingScorer`）与剧本动作决策解耦，通过 `IScenarioModule` 和 `ICareerStrategy` 纯虚接口进行领域模型交互，保证独立育成与各类剧本策略扩展能力。

### 3. 实施阶段的验证方式与未验证项（历史记录）

- **静态核对方式**：本次修改完全基于对本地 `main`（`52619a03`）与重构代码、资源及配置文件的静态逐行比对和静态语义核对。
- **未验证项声明**：
  - **未运行构建工具**：按照用户明确指令（"只负责改，不需要cmake，ctest"），本次未执行 `cmake` 配置与编译构建；
  - **未运行测试套件**：未执行 `ctest` 或其他自动化测试；
  - **未连接实机与模拟器**：未连接 ADB、未操作模拟器、未推进实际游戏育成。
  - 本次交付不把静态对照等同于实机验证通过。

## 实际构建与回归验证（2026-10-12）

用户随后明确要求运行验证。本轮执行 MSVC Release 原生构建、WPF Release 构建、原生 CTest、Bridge 集成测试及 WPF 离线回归。没有连接真实模拟器或推进游戏；设备接口测试使用测试进程及 FakeAdb。

### 验证发现并补修的问题

- 修正重复成员声明、把普通 `ActionIntent` 当作 optional、错误的 `MakeRace` 参数、错误的 optional 访问及不存在的 `confirmedByAction` 字段，使原生代码可以编译。
- 在首次运行观测之前加载智能训练确认记录，兼容开局模块已交接确认页面的情况；损坏或身份无效的记录阻止输入。训练输入前写入确认记录，写入失败不发送点击，取消后保留记录。
- 在观测动作目的页面之前交接动作结果并启用目标探测；同时同步协调器观察器和动作执行器内部观察器。推进已确认的赛果不再关闭探测。
- 按 main 补齐稳定主界面上的等待解除规则：识别到明确的后续目标后继续行动，覆盖决赛日期不变的情况。修复主界面目标分类未更新及未知目标残留。
- OCR 缓存按 900×1600 参考坐标映射截图区域，保留原置信度，校验灰度、RGBA、截图尺寸和会话/几何代次；缓存命中不提升置信度至 1.0。提交行动时清理缓存。
- 跑法配置在同一比赛内只执行一次，新比赛、接受闹钟重试及恢复时重新配置。续跑的可选赛事检查不再因导航操作把 `lastIntent` 改成 Race 而被绕过。
- 原完整育成回放模拟了 `goal_update → goal_objective_complete`，与 main 的 `goal_update` 识别前置条件矛盾；更正为先完成横幅再目标更新，并增加明确的顺序断言。比赛取消后的目标断言改为检查当前列表重建出的回退目标。

### 最终结果

| 检查 | 结果 | 日志 |
| --- | --- | --- |
| MSVC Release 原生全量构建 | 通过；52 个 C ABI 导出校验通过 | `build/career-behavior-validation-final-build.log` |
| 原生全量 CTest | 378/378 通过 | `build/career-behavior-validation-native-final.log` |
| 新增行为回归 | 11 个用例、59 条断言通过 | `build/career-behavior-regression-final.log` |
| Bridge 集成回归 | 163/163 通过 | `build/career-behavior-validation-bridge-final.log` |
| WPF Release 构建 | 0 错误；现有 ImageSharp 依赖有 NuGet 审计告警 | `build/career-behavior-validation-wpf-build.log` |
| WPF 离线回归 | 583/583 通过；发现阶段排除 Live/Packaging 用例 | `build/career-behavior-validation-wpf-final.log` |
| 原生育成资源包检查 | 7 个场景通过 | `build/career-behavior-validation-package.log` |
| 架构边界检查 | 通过 | `build/career-behavior-validation-architecture.log` |

完整育成离线回放已覆盖目标赛事、URA 决赛系列及结算返回 Home。它使用打包资源和模拟帧、输入、OCR 上下文，不代表完整实机育成通关。最终 Bridge 和 WPF 回归使用本轮构建的原生 DLL；只同步测试输出，没有替换正在运行的 GUI 的原生 DLL。

## 比赛查看结果的现场超时补修（2026-10-12）

用户提供的运行日志在 `race_runner.entry.view_results` 之后出现过渡确认超时。只读截图显示游戏已停在 Cosmos Sho 的第 3 名结果页，底部 Next 可见；截图保存为 `testdata/hachimi/ura/captures/race_view_results_cosmos_sho_3rd_mumu_20261012.png`。本次只执行截图读取，没有点击或继续育成。

对照 main 的 `HachimiJsonPipelineRunner`，发现 `repeatTapUntilTransition` 的等待语义被改变：main 在每次点击后、下一次重试前分别创建等待窗口；原生实现让所有点击尝试共享同一个截止时间。此处资源配置为 2,500 ms，较慢的截图/识别会提前耗尽整个窗口，尚未观察到结果便失败。

- 恢复每次尝试独立等待；重试前先等待上一操作的结果，再使用当前帧重新匹配源按钮。源按钮消失时继续观察，截图失败或取消时停止，不向未知页面继续发送点击。
- 新增慢截图回归，覆盖独立等待窗口、重试使用新坐标、源按钮消失后不重复输入；旧实现下两个分支均失败。原有“一个未命中立即发送第二次点击”的断言改为等待第一下的结果，与 main 一致。
- 用现场结果截图验证原资源的 Next 过渡模板能够命中，保留现有阈值。它只证明该截图可识别，不等同于重跑完整实机比赛。
- 过渡失败消息增加具体任务名，避免只看到没有动作位置的通用超时。

验证：Release 全量构建及 52 个 C ABI 导出检查通过；TaskEngine 全集 23 个用例、422 条断言通过；现场截图匹配用例 7 条断言通过；原生全量 CTest **380/380**、Bridge **163/163** 通过。日志为 `build/career-race-transition-*`。

新 DLL 和回放工具已同步到 staging、Debug 和 Release GUI 输出，旧文件保留备份，哈希见 `build/career-race-transition-runtime-hashes.json`。DLL SHA-256 为 `EC50B8A5D057032846E7EC29FB358DAC2A1B32655615B56785834F63F8FE13A7`。正在运行的助手仍加载旧模块，关闭并重新打开后才会使用更新文件；本次没有关闭助手、操作结果页或自动恢复任务。

## 直接查看赛果的外层交接补修（2026-10-12）

后续日志在 `race_runner.entry.view_results` 完成后报 `Career action produced no resource-confirmed screen transition: race_runner`。只读现场截图显示 Fuyo Stakes 第 1 名赛果，Next 可见，未出现 Replay 标记。本轮开始时构建输出和 Debug GUI 的 DLL 哈希均为上一节的 `EC50B8A...`，不是部署文件未同步导致。

对照 main 的 `CareerScreenObserver.cs`，确认原生识别器遗漏了直接查看赛果的兼容分支：存在待处理比赛、跑法已配置且上一页为 `race_runner` 时，使用 `race_runner_result` 的 required Next 模板识别无 Replay 标签的名次页。动作图的 Next 过渡验证已经成功，但外层重新识别仍要求 Replay，因此把实际成功的操作判为失败。

- 补齐该条件分支，把待处理比赛的参赛页结果识别条件同步到协调器观察器和动作执行器内部观察器；仍要求同帧模板证据和两帧稳定识别。
- 离开参赛页后停止使用此兼容分支，恢复任务时清空该条件；没有待处理比赛上下文或没有 Next 时不能只凭共享按钮宣告赛果。
- 两张现场截图（Cosmos Sho 第 3 名、Fuyo Stakes 第 1 名）在旧实现下都返回 `unknown`，共复现 4 条失败断言。新回归使用实际 PNG、打包模板和原生图像匹配，而非预设成功的模板匹配结果，覆盖识别、外层确认交接及条件清除。
- 本轮没有发送 ADB 输入或推进游戏。截图识别和离线回归不能作为整场实机育成通关的证明；此前“全部对齐”的静态结论应按这些实际遗漏修正。

验证结果：Release 全量构建和 52 个 C ABI 导出检查通过；比赛补修回归 **3 个用例 / 72 条断言**；原生 CTest **382/382**、Bridge **163/163** 通过。原生 CTest 明确排除 `ManagedBridgeTests` 和 `WpfGuiTests`，Bridge 使用已有 xUnit 直连执行器运行；本轮没有重跑 WPF 全集。并行构建曾遇到 MSBuild 子进程异常退出，串行全量构建完成。日志为 `build/career-runner-result-*`。

通过验证的 DLL 和回放工具已同步到 staging、Debug 和 Release GUI 输出，并保留旧文件备份，逐文件哈希见 `build/career-runner-result-runtime-hashes.json`。本轮 DLL SHA-256 为 `D0F8068EDC222BD1AA6DAA7C69C2D613EBC1E1A740E6A774E42EA68C89832DB9`。没有关闭或重启助手，旧进程需要重新打开才能加载新模块。
