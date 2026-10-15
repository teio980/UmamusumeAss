# Career 原生迁移回归核对

## 赛前检查点统一为 Runner（2026-10-15）

按用户要求删除 `race_attributes` 页面及其五个重复动作绑定。赛事详情、连赛检查、跑法状态和直接赛果续接统一使用 `race_runner`；Runners 局部模板的 ROI 与 0.88 阈值保留。五张旧整图只保存在 `ura/testdata/captures/runtime_frames/`，供 Runner 像素回归使用，不再作为运行识别模板。下方涉及旧页面及“全部绑定保留”的内容为历史记录，当前衔接以重新导出的 [全操作清单](career-flow-action-inventory.md) 为准。

逐张核对发现，旧整图中 `ura_prelim_attributes.png` 是 Race Day，`ura_prelim_attributes2.png` 与 `ura_semifinal_attributes.png` 是赛前剧情，`ura_final_attributes.png` 是连接中的赛事列表；只有 `ura_final_attributes2.png` 是实际 Runner 页面。原主模板以外的这些图片本来就没有 Runners 控件，不能继续作为属性页的识别证据。实际 URA Runner 的 Runners 字样从原图 `[738, 1102, 113, 38]` 提取为小模板变体，原小模板、ROI 和阈值保留；重试后的 Runner 交接也接受这个局部变体。

Release 构建及 52 个 C ABI 导出检查通过。Career **172/172**（5,045 断言）、GameTasks **54/54**（890 断言），Runner 专项包含在 Career 中，覆盖六张实际截图及局部标记消失的分支。资源检查 7 项、生产回放工具检查 9 项通过，重新生成的 72 页面／253 动作清单及 main 动作图差异审计通过；审计明确记录旧页面与任务的合并，不再把替换后的绑定描述为完全未变。本轮只进行离线验证，没有执行新的实机比赛。日志、差异报告、旧文件备份和运行文件 SHA-256 位于 `build/race-runner-unification-20261015/`。DLL 与资源已同步到 native-staging、GUI Debug 和 Release；运行中的助手需重新打开加载新版。

## 比赛播放到闹钟弹窗的实际出口核对（2026-10-14）

本次日志 `05:29:57 race_playback_start.race.play` 后，`05:30:30 race_runner_playback_result_monitor` 超时。只读 ADB 现场为 Oguri Cap 第三名的 Try Again 闹钟弹窗，保存到 `testdata/hachimi/ura/captures/race_retry_oguri_3rd_mumu_20261014.png`，没有点击游戏或消耗闹钟。

对照本地 `main@52619a03` 的资源及托管 `CareerRaceFlow`，区分三处问题：

- **main 也存在的动作图缺口**：播放监视器的成功出口只有奖杯及 Replay，没有闹钟。协调器虽有重试处理，内部动作图仍先耗尽 30 秒，无法把弹窗交给它。新增使用现有闹钟提示模板、原 ROI 与 0.88 门槛的无输入出口 `race.retry.pending`；此出口不代表重试已经接受，也不代表赛果成功。随后由观察器两帧确认完整闹钟页面，再按设置执行 Try Again／Cancel。保留普通赛果及奖杯出口。
- **局部识别迁移遗漏**：Cancel 的后继候选只有资源期望赛果及结算，没有普通 `race_runner_result`（Replay + Next）检查点。main Cancel 后显式执行该页 Next／Final Next。补回这个局部候选，继续原动作图，禁止回到 Main 或另开比赛。
- **目标完成状态迁移差异**：main 拒绝重试后推进赛果，但不启用成功比赛的 GOAL 探测。原生的 `OnRaceReplayCompleted` 会对必赛目标启用探测；现在已拒绝重试时保持该探测关闭，不应用未知名次或完成目标。

新的 [全动作图审计工具](../tools/AuditCareerActionGraphs.py) 对照 main 的 **72 页、254 绑定及 387 个内部任务**，检查每个绑定可达的内部图、所有任务引用、执行字段和两个并行监视器的真实出口，输出到 `build/career-alarm-audit-20261014/main-action-graphs.json`。当前增加一个技能页和一个闹钟等待出口任务；原任务及绑定均保留。

Career Main 的八个绑定分别为 Goal Details、Training、Rest、Recreation、Summer Rest、Infirmary、Races、Finale Races，绑定及其入口动作图均与 main 相同；Goal Details 仍只是资源声明。全部旧任务中十项有字段变化：八个普通开局可选探测的 `required=false`、既有剧情无输入交接、本次播放监视器补充闹钟出口。识别策略、回合确认、技能缓存及恢复守卫的历史迁移差异仍须结合下方专项证据，不能凭资源相同就认定行为相同。

原“72 页、254 绑定”清单证明了结构保留，**没有证明播放监视器能到达闹钟分支**。新增测试从现场像素经过生产监视器、Career 局部确认和实际按钮执行验证该路径；测试上下文模拟按钮之后的截图序列，不是新的实机通关。

新增实际像素用例覆盖播放完整动作 → 闹钟无输入交接、闹钟两帧确认、Cancel → Replay + Next 检查点，以及 Try Again 只点击一次、没有 Runner 证据就不返回 accepted。启用重试的 accepted 终态仍有原有模拟动作回放；本轮没有捕获实机重试后的 Runner 页，不能把它表述为实际完整重试成功。

最终 Release 构建及 52 个 C ABI 导出检查通过。Career **130/130**（2,895 断言）、GameTasks **54/54**（890 断言）、TaskEngine **23/23**（422 断言）、NativeTaskContext **11/11**（337 断言），合计 **218/218、4,544 断言**。生产资源包 7 项、生产回放工具 9 项及架构边界检查通过。日志集中在 `build/career-alarm-audit-20261014/`；全动作图与识别字段差异在 `main-action-graphs.json`，最终 Career 导出与测试在 `career-tests.log`。

DLL、回放工具及比赛执行 JSON 同步到 native-staging、Debug、Release 并逐项校验 SHA-256；旧文件保留 `.previous-career-alarm-20261014-*` 备份。DLL：`C95D10BDAB5427F1C6F9E7D9DCCC1BAFD43A45AF38069B68E7BB5BCC8702FECB`；执行 JSON：`09B4C4740A437E05C7047C31F18E9E0E0B665269D32E3DD2CCF1CD8ED3F65D05`。详见 `runtime-hashes.json`。运行中的助手未重启，仍需重新打开后加载新模块和资源快照。

## Session Error 闲置失效弹窗补齐（2026-10-14）

现场训练提交后等待后继页面 45 秒失败；只读 ADB 截图显示 `Session Error / Returning to Title screen due to inactivity.`，其 Title Screen 按钮居中。之前的恢复流程已经存在，但只匹配 Date Changed 和 Connection Error；后者的模板标题是 `Connection Error`，按钮搜索限定在左侧区域，无法覆盖这张 Session Error 实际画面。本地 main 的同一守卫也没有此变体，本次不是把已有 Connection Error 处理删掉。

从原始 ADB 截图提取并保留完整颜色、背景和不透明像素，增加 Session Error 标题、闲置正文及居中按钮三个模板。按用户要求，任意一个命中即点击恢复，不要求同时命中，也不要求第二帧确认。只有标题或正文命中时，按其当前位置确定居中按钮点击位置。复用现有关闭弹窗 → StartGame → 验证 Home → 续接原育成流程。保留未知训练结果不计数、持久确认记录恢复及不重复提交保护。现场原图保存为 `testdata/hachimi/ura/captures/session_error_inactivity_mumu_20261014.png`。

Session Error 实际截图在 900×1600、720×1280、1080×1920 下覆盖三个模板分别单独命中及全部消失。生产守卫回放验证单帧单模板恢复、长等待恢复及未确认提交不会重复；完整 GameTasks **54/54**、890 断言通过，Release DLL 构建及 52 个 C ABI 导出检查通过。新版 DLL 与三张新增模板同步到 Debug／Release；运行中的助手需重启以加载模块及新的不可变资源视图。本次没有点击实际 Title Screen、推进游戏或执行新的实机完整恢复。

## URA 剧情返回主界面：移除 Skip 查找与点击（2026-10-14）

现场日志已经完成跨日恢复、休息、训练和比赛，最后在 `scenario_event_event_advance` 等待 Skip 模板 12 秒后失败；ADB 实际已经在 Finale Underway 主界面，目标为 URA Finale Qualifier、剩余 1 回合。该截图保存为 `testdata/hachimi/ura/captures/career_main_oguri_finale_qualifier_20261014.png`。

按用户要求，`scenario_event_event_advance` 改为无输入交接：移除 Skip 模板、匹配区域和匹配门槛，取消 ClickSelf、12 秒搜索及点击后等待。保留原事件的局部后继确认；主界面两帧确认后交回协调器，继续常规训练决策。仍处于剧情画面时只观察其自动返回，不把动画或背景变化当作已经可以训练。

新增真实截图回归覆盖两条路径：剧情自动返回 Main，以及来源观测还叫 scenario_event、实际已经是 Main。两条路径均确认 Main、没有输入尝试、没有查找 Skip 模板，也没有原来的 12 秒等待。Career 相关 27 个用例（1,173 断言）、完整 GameTasks 53 个用例（716 断言）及资源包检查 7 项通过。本次生产改动仅在事件执行资源；Debug／Release 对应 JSON 已同步并核对 SHA-256：`7522BC7FDFE03708408D2670AA56DE9E736D02F490540BECE7984B4B4405373B`。运行中的助手需要重新打开以加载新的不可变资源视图。本次没有点击或推进游戏。

## 全局恢复弹窗补修（2026-10-14）

ADB 现场停在 `Date Changed / It's a new day!`。使用当时 GUI 输出同哈希的 DLL 检查现场截图，默认两像素搜索使标题得分 0.911146（门槛 0.92）、正文得分 0.897414（门槛 0.90）；恢复 main 的逐像素参数后两处均为 1.0。源码、GUI 和当前资源视图中的五张恢复模板一致。本次没有调整图片、阈值或 Career 动作绑定。

- `DetectGameRecoveryDialog` 直接检查当前帧像素，恢复 main 的一像素文字搜索及完整彩色画面归一化；按钮仍携带原截图的帧、会话、几何身份，使用 900×1600 参考坐标交给输入层映射。
- 长等待每秒获取独立观察帧。观察帧立即释放，不替换当前行动的截图或续点凭据；短暂单帧弹窗不触发恢复。稳定弹窗仍进入原有两帧确认、最多三次关闭及 Home 恢复流程。
- 独立 StartGame 入口也使用相同守卫。启动过程中再次出现弹窗可以关闭后重新识别；点击前被中断的启动控件不会使用旧观测重放，也不会递归重启游戏。
- `GameRecoveryDialogTests` 使用原始 Date Changed 实际截图，验证 900×1600、720×1280、1080×1920 和六类竞争页面。Connection Error 使用原始标题及 Title Screen 裁图在原坐标组成测试夹具，不能称为新的完整实机断线测试。
- `GameTaskTests` 的恢复回放改为输入实际弹窗像素，覆盖提交前、提交后、长等待、单帧闪现、重复启动弹窗及启动输入中断；不再模拟恢复模板直接命中。`NativeTaskContextTests` 用 FakeAdb 验证独立观察不会破坏普通或续点输入依据，已释放的观察帧不能点击。

Release 构建及 52 个 C ABI 导出检查通过。相关离线完整测试：Career **123/123**（2,828 断言）、GameTasks **53/53**（716 断言）、TaskEngine **23/23**（422 断言）、NativeTaskContext **11/11**（337 断言）；合计 **210/210**、4,303 断言。资源包检查 7 项、生产回放工具检查 9 项和架构边界检查通过。下方历史“72 页面、254 绑定”记录只证明绑定保留，不能替代全局守卫参数及实际像素验证。

最终 DLL SHA-256：`06DAD9FE4AC954FECE8A795D40A46031AA4E8F1662ACF0C53EA177D1563C1271`。运行文件同步到 native-staging、Debug 和 Release GUI 输出，旧文件保留备份；已运行的助手需重启加载新版。本次未点击 ADB、未重启助手或游戏，也未执行新的实机育成通关。

## 当前交付：全流程局部识别与性能优化（2026-10-13）

本轮以 `main@52619a03` 为行为基准，在 `codex/newframework` 上完成普通、独立育成的流程调整。全部验证离线运行，没有连接或操作实际模拟器。

完整的 **72 个页面、254 个动作绑定** 对照见 [Career 全操作衔接清单](career-flow-action-inventory.md)。清单由生产资源及 `CareerTransitionPolicy` 导出，并逐项验证 main 页面、语义动作和 task 绑定保留。表中说明实际自动操作、配置动作图内部步骤及未调用声明；参数化赛程操作使用动作族回放和绑定检查，不能把此清单称为 254 个独立实机操作均已验证。

### 行为与调用差异的处理

| 环节 | 重构问题／差异 | 本轮实现 |
|---|---|---|
| 动作后的识别 | 执行器除训练返回、部分赛事外使用所有 runtime 页面；协调器再次观察也广泛扫描 | 统一 `CareerTransitionContext` 和 `CareerTransitionPolicy`；来源页面、实际语义动作、outcome、事件返回阶段决定局部候选；运行中未命中不会扩大全局扫描 |
| 初次与恢复 | 入口到模式启动仅传页面 ID，随后重新识别；独立育成有检查点仍调用全局观察 | 共用入口交接已确认观测；Normal 和 Independent 接受交接；独立检查点直接进入已知阶段；新一轮及已证明回到 Home 的恢复从 Home 局部入口开始，无上下文恢复保留一次全量路由 |
| 两帧确认 | 观察器已确认两帧，入口又等两次完整观察，稳定边界至少四帧 | 观察器统一稳定证明；入口接受 `stableFrameCount=2`，实际截图回归仅两帧；第二帧限制为首帧赢家及可覆盖它的高优先级候选 |
| 打开训练 | 动作图已有 `training.selection.confirmed`，仍重复截图搜索目的页 | 内部 action handoff 保留实际语义动作、outcome 和有效帧；一秒内且仍为当前帧的图终态可作为第一样本，再以新帧完整验证训练页；过期或失效帧重新截图 |
| 训练提交与连续事件 | 各处各自推进比例和扫描器；最近一次事件输入覆盖原行动上下文 | 单一消耗确认入口；训练结果、可靠日期／倒计时推进、必赛边界确认后只推进一次；事件保留训练、外出或比赛的原续接阶段 |
| 事件恢复 | 重启落在事件页时来源未知，局部流程可能漏掉随后训练结果 | 未知来源只扩展事件的局部续接集合，包含训练结果、外出抓娃娃及赛后奖励；不引入技能、入口或完整结算扫描；新行动开始后解除恢复歧义，未知训练不获计数 |
| 持久训练恢复 | 仅必赛边界恢复剧本待确认动作，结果页恢复可能保留旧连赛计数 | 已持久化的训练到达明确结果页时，先恢复非比赛行动账目，再由后续 Main／必赛证据确认回合；恢复记录清理与比例推进仍只执行一次 |
| 比赛衔接 | 局部规则需要完整覆盖必赛提示到 Race Day，以及最后奖励直接到完成页 | 补齐这两条合法分支；保留推荐／首场回退、空列表、连赛、跑法、回放／直接赛果和闹钟 outcome；首次赛事重试仍仅在两张完整帧相同后允许一次 |
| 医务室状态 | 原生版全屏灰度比较两个相似模板，未使用 main 的按钮区域、彩色分数差 | 恢复 `career.infirmary.button` 两个位置、0.90 彩色阈值及 0.08 分数差；实际正常、变暗及夏合宿移位截图回归通过；消除两次全屏扫描及相似灰度图导致的误判 |
| Main 训练按钮 | 每帧重复搜索较大的原始区域 | 上次位置仅作为小范围搜索提示；每帧重新匹配，未命中回退原始 ROI；设备会话、几何及截图尺寸变化、恢复或新入口后不使用旧提示；输入动作仍重新验证按钮 |
| 结算 | 任意页面变化确认，普通流程可能探测结算共享按钮 | 结算按局部后继与原门槛识别，保留评分、称号、奖励同页变化及可选弹窗；终局后确认 Home 才完成 |
| 独立育成 | 入口重复观察，配置与 post-start 恢复额外扫描 | 按原 Stage、赛程／技能索引和 Start 持久标记执行；重复勾选、Start 和未证明 Home 的完成报告仍被拒绝 |

原识别阈值、资源绑定、输入保护和过渡时间预算保留；不通过降低阈值、盲点坐标或减少稳定样本提速。设置格式、Independent 检查点及 Normal 技能缓存兼容；C ABI 仍为 52 个导出。

### 性能对照

相同三张真实截图、相同资源、同一回放上下文，每项 22 次观察；前两次暖机，后 20 次计算中位数和 P95。截图使用 WIC 解码和生产模板算法；OCR 回调在此性能用例中为空结果，真实 Windows／数字 OCR 另由功能回归验证。`OCR` 列仅计 `RecognizeText` 的区域／整页文本调用；倒计时图像及数值回退仍保留，不计入此列。`waitMs` 是请求等待量，回放的 Sleep 不实际睡眠；这些数值不是 ADB 截图或完整实机行动耗时。

| 固定画面 | 截图数（前→后） | 模板调用（前→后） | 文本 OCR（前→后） | 请求等待 ms（前→后） | 中位耗时 ms（前→后） | P95 ms（前→后） |
|---|---:|---:|---:|---:|---:|---:|
| Main：Oguri Pre-Debut | 44→44 | 1,992→132 | 154→110 | 2,640→2,640 | 14,560.300→166.336 | 15,217.600→176.853 |
| Rest Confirmation | 44→44 | 1,422→446 | 44→44 | 2,640→2,640 | 2,650.300→324.391 | 2,825.760→345.224 |
| Rewards：Support Cards | 44→44 | 940→304 | 2→0 | 2,640→2,640 | 1,534.730→408.436 | 1,586.040→415.834 |

三项合计模板调用 **4,354→882，减少 79.7%**；各画面的两帧窗口保留。入口四帧到两帧由单独实际截图回归验证；训练目的页复用图终态后只增加一张新截图，失效旧帧则增加两张。已确认入口到 Normal 启动及运行协调器的交接不重复观察。这些结构性节省不混入上表重复观察的截图计数。

性能原始日志：`build/career-flow-performance-before.log`、`build/career-flow-performance-after.log`。首轮对照为调整前全 runtime 识别，最终对照为实际运行使用的局部请求及限定 Main 区域。模板调用数量之外，医务室全屏搜索改为资源区域和 Main 搜索提示也降低单次匹配成本。

### 最终离线验证与运行文件

| 检查 | 本轮最终结果 | 日志 |
|---|---|---|
| MSVC Release 全量构建／导出 | 通过，52 个 C ABI 导出保持一致 | `build/career-flow-infirmary-build.log` |
| 原生全量 CTest | **414/414**，排除两项托管测试，由下列专用执行器独立运行 | `build/career-flow-native-delivery.log` |
| Bridge 与原生集成 | **163/163**，0 跳过，FakeAdb 与测试宿主 | `build/career-flow-bridge-delivery.log` |
| WPF Release 构建 | 0 错误；原有 NuGet 依赖告警未改依赖 | `build/career-flow-wpf-build.log` |
| WPF 离线回归 | **591/591**，0 跳过；发现阶段排除 Live／Packaging | `build/career-flow-wpf-delivery.log` |
| 资源包／架构 | 7 个资源包场景与架构边界通过 | `build/career-flow-package-delivery.log`、`build/career-flow-architecture-delivery.log` |
| 254 动作绑定清单 | 72 个页面与 254 个原绑定逐项校验通过 | `build/career-flow-contracts.log`、`tools/GenerateCareerFlowAudit.py` |

回放覆盖训练后连续事件、Quick 跳过结果、直达可靠必赛列表、出道前／决赛固定日期、继承 GO、赛事与闹钟分支、同名事件与同页奖励、终局 Home、独立育成配置与检查点恢复。新增断言检查局部候选不含无关页面、旧帧失效、搜索提示回退及几何变化、未知训练不计数、持久训练恢复后的连赛账目和入口零重复观察。

旧高层测试省略了若干导航页面。本轮将主循环及赛事意图回放改为实际入口／确认／结果顺序；外部恢复明确调用恢复入口。另保留异常 Home 交接的负向测试，确认没有终局证据时仍拒绝完成。没有以放宽生产局部候选或移除行为断言来消除这些测试失败。

最新 DLL 和回放工具已同步到 staging、Debug、Release GUI 输出；GUI 原文件保留 `.previous-career-flow-20261013-*` 备份。DLL SHA-256：`7741C702956E81B27C3C187F509594526C5E6F21BBED90DD91B122C2810263F4`，副本校验见 `build/career-flow-runtime-hashes.json`。正在运行的助手进程 19440 没有重启，仍使用已映射的旧模块；重新启动助手后加载新版。

下方章节保留历史核对与现场记录；本节是此次最终离线交付结果，不将离线回放称为新的实机育成通关。

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
| **结算续跑** | 完成判断只接受两种结算完成页；从故事解锁等返回首页可能被判失败 | 扩展终态集合至 `career_complete`, `career_complete_close`, `career_story_unlocked`, `career_story_unlocked_compact`, `career_story_unlocked_to_home`；终态后若到达 `home_unselected`，先执行 Home 标签选择，确认 `home` 后完成并清理持久化记录。未选中的 Home 标签本身不是育成完成证据 | `CareerFlowCoordinator.cpp` |
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

## Career Rank 结算阈值补修（2026-10-13）

用户日志在 `complete_career.career.finish` 后报外层过渡确认失败。只读 ADB 截图确认当前页面是 Career Rank：B+、Rating 8,533、底部 Next。现场截图保存为 `testdata/hachimi/ura/captures/career_rank_bplus_8533_mumu_20261013.png`，本轮没有发送游戏输入。

main 的 `CareerSettlementFlow` 已有 `career_rank -> career.next`，该动作和资源在原生版本中也存在。真正差异位于资源解析：main 的 `UraScreenRecognition.TemplateThreshold` 和 `RequiredTemplateThreshold` 默认值均为 **0.78**，原生加载器却用 **0.86**。Career Rank 配置省略了阈值，现场 B+ 图对现有 A 等级样图的匹配分数为 **0.8390682615**，因错误默认值被判为 `unknown`。旧实现的现场回归共出现 5 条失败断言，包含默认值及 B+ 图识别；这不是育成完成按钮未点击成功。

恢复屏幕主模板和 required 模板的默认值为 0.78，资源中的显式阈值继续优先，目录资源资产和动作图阈值不受此改动影响。同一默认值用于 Career Rank、Sparks 及其确认页等省略阈值的屏幕，因此修复解析契约而非只给当前截图设置特例。

新增回归使用真实 B+ 截图和既有 A 等级样图，验证结算页稳定识别、`complete_career -> career_rank` 外层交接及原 `career.next` 按钮匹配；同时检查省略/显式阈值及 Career Rank、Sparks、Sparks Confirmation 现有截图在结算阶段的分类。

验证结果：Release 全量构建及 52 个 C ABI 导出检查通过；结算补修回归 **3 个用例 / 63 条断言**；原生 CTest **385/385**、Bridge **163/163** 通过。原生 CTest 排除 `ManagedBridgeTests` 和 `WpfGuiTests`，Bridge 使用已有 xUnit 直连执行器运行，本轮没有重跑 WPF 全集。日志为 `build/career-rank-*-20261013.log`。本轮仅用 ADB 读取截图，实际 Next 点击在离线测试上下文中验证，没有推进实机结算流程。

修复后的 DLL 和回放工具已同步到 staging、Debug 和 Release GUI 输出，旧文件保留备份；逐文件哈希见 `build/career-rank-runtime-hashes-20261013.json`。DLL SHA-256 为 `29362B51F9FF21257ACBA66A78562D42D3CFDDE8A4B339A14EB1AC86850329EC`。旧进程需要重新打开助手才能加载修复。

## Rewards 同页翻页确认补修（2026-10-13）

后续日志成功通过 Career Rank、Sparks、Epithet 和 Career Result，在 `rewards.rewards.next` 后报外层过渡确认失败。只读 ADB 截图显示游戏已经进入 Support Cards 奖励页，仍使用 `rewards` 页面 ID。main 的结算分发器会继续处理同 ID 的下一页；原生通用确认器此前只接受不同页面 ID、回合变化或事件状态变化，未处理两页 Rewards 的内容推进。

使用既有 Bond Level 奖励截图和现场 Support Cards 截图复现：一次模板确认的 Next 点击成功，目的页仍为 `rewards`，旧确认器超时并没有交回观测。新增两份 Support Cards 现场截图，在背景彩纸位置不同的情况下验证标题标记保持稳定。

资源为 Rewards 指定第一条奖励分组标题的 `stateSignatureRoi`，不使用整帧动画差异判断翻页。观察器仅在两帧均认出同页、标记像素一致且会话、画面尺寸及几何代次一致时提供页面状态证据。动作确认器允许相同页面 ID 上该证据发生变化，并交回实际目的观测；不自动发送重试点击。其他页面未配置该区域时维持原有确认规则。

回归覆盖 Bond Level 到 Support Cards 的一次点击交接、相同内容、区域外动画、标题不稳定、会话/几何变更及两次现场截图。ADB 仅用于读取画面，没有推进实际结算。

验证结果：Release 全量构建和 52 个 C ABI 导出检查通过；奖励页补修回归 **3 个用例 / 48 条断言**；原生 CTest **388/388**、Bridge **163/163** 通过。原生 CTest 排除 `ManagedBridgeTests` 和 `WpfGuiTests`，Bridge 使用既有 xUnit 直连执行器运行，本轮没有重跑 WPF 全集。日志为 `build/career-rewards-*-20261013.log`。

新 DLL 和回放工具已同步到 staging、Debug 和 Release GUI 输出，新增 ROI 配置也同步到两套 GUI 的 `resource/hachimi/career/settlement/profile.json`，旧文件均保留备份。逐文件哈希见 `build/career-rewards-runtime-hashes-20261013.json`。DLL SHA-256 为 `C8EFC826733FBF0FCE5FD6FA590A3B3EDBE6509DD99CF2DB7EFC6C7F97E3C651`，资源配置 SHA-256 为 `B3E9781D66998D9990A4C3EFEAD9039386674BA37C4085820B395F6E5A7ECEAC`。没有重启助手，旧进程需重新打开以加载修复。

## 整条结算链核对与实机验证（2026-10-13）

再次失败时，ADB 截图已是 Rewards Collected 弹窗。日志中的两帧不稳定与外层失败发生于同一毫秒。旧通用确认窗口只有 15 秒，捕获与识别一对帧本身即可耗尽它；一次观测跨过弹窗动画后，没有预算再确认稳定画面。main 的引擎允许多次稳定识别重试，而非仅以一次观测判失败。

- 结算过渡窗口为 60 秒，其他普通动作过渡为 45 秒；赛事入口原有的 30 秒保护继续生效。观测包含实际采集和识别耗时，没有因等待窗口延长增加点击重试。
- 两帧不稳定日志现在记录两个页面 ID；超时诊断记录完整观测次数、源页面、末次页面和窗口长度，区分动画中的识别失败与动作图失败。
- 补齐结算后 `home_unselected` 的处理：已确认终态时执行资源中的 Home 标签选择，随后在 `home` 完成；未确认终态时不能仅凭未选中的 Home 标签宣告育成结束。此前纳入终态集合却未处理 Unknown 分类的实现遗漏已修正。
- 回归使用生产图像算法，包含颜色文字匹配及原生 OCR。19 份结算/返回截图中，18 份识别到对应阶段，另有一份旧奖励截图的 Next 分数只有 0.709，低于资源要求的 0.78，明确拒绝发送输入；不通过降低按钮阈值让动画帧通过。
- 慢截图回归模拟每次采集 8 秒，第一对帧跨过弹窗动画：旧实现超时，修复后下一对稳定帧确认 Rewards Collected。完整协调器用本次实际截取的 7 个阶段重放 6 次动作并确认 Home。

实机验证使用临时 `build/settlement-live-probe` 工具，调用同一 `NativeTaskExecutionContext`、`CareerScreenObserver` 和 `CareerActionExecutor`，输入只来自已识别结算页面的资源动作；只允许单动作结算页，进入 Home 即停止。没有开始下一次育成。当前育成实际通过：

`rewards_collected -> rewards -> event_reward -> rewards -> follow_trainer_limit -> career_complete -> home`

6 次原生动作均确认成功，最终 Home 识别分数为 0.980908。日志为 `build/career-settlement-live-advance.log`；实际截图已纳入 `testdata/hachimi/ura/captures/*_mumu_20261013.png`。本次实机只验证当前剩余结算路径，其他可选结算分支由真实截图和协调器回归验证，不声明重新通关整场育成。

最终验证：Release 全量构建、52 个 C ABI 导出检查通过；整链审查回归 **4 个用例 / 148 条断言**；原生 CTest **392/392**、Bridge **163/163** 通过。原生 CTest 排除 `ManagedBridgeTests` 和 `WpfGuiTests`，Bridge 使用既有 xUnit 直连执行器；本轮没有重跑 WPF 全集。日志为 `build/career-settlement-audit-*`。

最终 DLL、回放工具及结算配置已同步到 staging 和两套 GUI 输出，保留旧文件备份。哈希记录为 `build/career-settlement-audit-runtime-hashes.json`，DLL SHA-256 为 `039B44F7A512741B07F00E044093577820878680FE7DF3812621BDDA1A00863B`。游戏已留在 Home；没有重启助手，旧进程需要重新打开才能加载新模块。

## 新育成误入目标完成流程补修（2026-10-13）

用户日志在开局 Skip 配置后直接执行 `goal_objective_complete.goal.next`，随后找不到 `goal_complete_goal_next`。本次 ADB 现场为小栗帽出道前主界面，剩余 **11 回合**，满体力，比赛尚未解锁。截图保存为 `testdata/hachimi/ura/captures/career_main_oguri_predebut_mumu_20261013.png`。这张稳定截图本身能正确识别为 `career_main`，没有把它称为失败瞬间的截图。

对照仍为 `main` 的 `52619a03`。自动逐项比对九个 Career profile 片段中的 **254 个动作绑定**，没有删除或改名任何 main 动作绑定；逐项记录为 `build/career-main-actions-audit-20261013.csv`。资源绑定存在不能证明执行行为已迁移，以下核对同时检查相应的原生执行层与已有回归。

| main 操作范围 | 本轮核对的原生执行路径与验证 |
| --- | --- |
| 入口、选马、继承、配卡、续育成 | `CareerEntryFlow`、原生任务包装；入口及完整 runner 回放 |
| 普通模式、四种跑法、开场 Skip、Quick | `NativeNormalCareerStartup`；补充两种 Final Confirmation 来源的开局边界回归 |
| 主界面八个资源动作 | 目标详情、训练、休息、夏合宿休息、外出、医务室、普通赛事、决赛入口的资源绑定；执行器、协调器、剧本和策略既有回归 |
| 五种训练与智能/比率策略 | 训练选择与结果确认、当前回合候选、训练数字 OCR；Career/URA/完整 runner 回放 |
| 休息、医务室、外出及继承 GO | 专属确认动作、行动后观测、GO 等待；行为和资源动作回归 |
| 目标、粉丝、指定等级及连续比赛 | 推荐/首场回退、第三场保护、跑法、回放/直接赛果、闹钟；赛事意图和完整 runner 回放 |
| 技能、事件及抓娃娃 | 技能点与价格、购买确认、技能缓存、事件标题/选项证据、专属抓娃娃流程；对应现有测试组 |
| 单项目标、目标更新、全部目标完成 | 目标探测前置条件、目标页 Next 证据、真实单项目标截图；与结算区分 |
| 结算、奖励及返回首页 | 保留前述结算补修；真实截图、完整结算协调器与 runner 回放 |
| 中断、恢复、多次育成 | 数字 OCR 包装层转发、智能训练确认记录、日期恢复、取消与 runCount；原生任务/Bridge 回归 |

### 已确认的迁移遗漏

- **新开局候选未限制。** main 的 `careerStartTransitionExpected` 只允许开场和首个主界面；原生开局仍加入目标和比赛候选，Quick 配置后还允许任意 runtime 页面结束开局。使用已识别的开场后返回目标页的回放，旧实现出现 **10 条失败断言**，并把 `goal_objective_complete` 交给运行器。开局候选现限定为开场、Quick、`career_main` 和 `career_races_ready`，首个主界面确认后才交接。已有育成从真正目标页恢复的路径保留。
- **单项目标页只有标题证据。** 增加彩色 GOAL 字形匹配和同帧 Next 必需模板。真实单项目标完成截图及原 Next 动作仍能匹配；人为模拟标题相关误命中时，真实主界面缺少 Next，因此不能被路由成目标完成页。没有降低按钮阈值或添加坐标点击回退。
- **倒计时数字回退未接齐。** Windows OCR 对现场数字只返回 `turn(s) left`，原路径丢失 11。接回有界数字 OCR，使用明确的无符号整数格式，并为倒计时保留彩色卡片。训练增益的 `+N` 和失败率的 `N%` 默认解析保持原语义。数字子区域排除卡框，拒绝字母、负数、单位、多值和溢出；两种裁剪读数冲突时保持未知。成功值按同一设备/几何的实际 ROI 像素缓存，原置信度保留。
- **条件探测被标成必需失败。** 普通模式、四种跑法的前置状态以及三个 Quick/Skip 探测均已有资源回退，现在显式 `required: false`。未命中走原回退并记录 Debug，实际按钮点击和后置确认仍为必需步骤。

全量并行验证还暴露了测试自身的临时文件名冲突：`ConnectionProfileTests` 每个进程的首次 `std::rand()` 都生成同名 `uma_profile_test_41.json`，导致另一个测试读到空文件。临时文件名增加时钟标识，避免并发测试互相覆盖；没有修改连接配置的生产解析规则。

### 最终验证与运行文件

- MSVC Release 全量构建通过，52 个 C ABI 导出检查通过；最终原生 CTest **396/396**、Bridge **163/163** 通过，资源包 **7 个场景**和架构边界检查通过。原生 CTest 排除 `ManagedBridgeTests`、`WpfGuiTests`，Bridge 使用已有 xUnit 直连执行器；本轮没有重跑 WPF 全集。
- 新增开局、真实主界面/目标页和数字解析回归合计 **4 个用例、167 条断言**。旧版本开局边界回放失败，新版本通过；图像回归实际调用生产模板算法、Windows OCR 和本机数字 OCR。
- 实机工具使用同一 `NativeTaskExecutionContext`、`CareerScreenObserver` 和 `CareerActionExecutor`。首次读取为 `career_main / 11 回合 / 99% 体力`；开启目标探测后仍识别为 `career_main`。随后实际完成 `career_main -> training_selection -> career_main` 两次资源匹配输入，返回后仍为 11 回合和 99% 体力，没有提交训练或消耗回合。日志为 `build/career-startup-live-navigation-final.log`，返回截图为 `debug/career-regression-20261013/after.png`。这项实测没有重新完成整场育成。
- 初次实机工具的默认临时目录写入被系统拒绝，数字读取保持未知且工具在发送输入前退出。添加了可读的数字 OCR 失败原因；验证子进程随后单独使用项目内临时目录，未修改系统环境或助手的用户设置。
- DLL、回放工具及四个 Career 资源配置已同步到 staging、Debug 和 Release GUI 输出，GUI 中原文件保留备份。12 个副本逐项哈希一致，记录为 `build/career-startup-runtime-hashes-20261013.json`。最终 DLL SHA-256 为 `76BCCD9EA219DD50BFB57353E545ECEDF840D62EA1A39C304F863194C3CCA59A`。正在运行的助手仍映射旧模块，需要关闭再打开后继续当前育成。

最终日志为 `build/career-regression-verified-build.log`、`build/career-regression-verified-native-tests.log`、`build/career-regression-verified-bridge.log`、`build/career-regression-verified-package.log` 和 `build/career-regression-verified-architecture.log`。

## 最后一次训练直接进入必赛列表补修（2026-10-13）

用户在 `training_selection.training.guts` 后收到 45 秒、14 次观测、末次 `unknown` 的过渡失败。只读 ADB 截图显示当前已进入出道赛 Race List，左上卡片为 **Race Day**，目标为 Run in Junior Make Debut；截图保存为 `testdata/hachimi/ura/captures/race_list_debut_after_training_mumu_20261013.png`。这是报错后的稳定目的页，不是训练点击未成功的证据。

原生完整观察器已有 `race_list` 资源；训练/休息后的专用候选集却没有它，因此同一截图在指定 `race_list` 时能识别，在训练交接确认时一直返回 unknown。旧代码的真实截图交接回归失败，日志为 `build/career-training-race-before.log`。对照 main 的 `CareerScreenObserver.TurnActionReturnScreens`，该候选集同样未包含 Race List；本次按实际 Quick 路径补齐边界，不把 main 的遗漏当成正确行为。前轮实机只验证了打开和退出训练页，未覆盖训练结束直达比赛列表。

- 训练及非比赛回合动作的目的候选集补入 Race List、空比赛列表、推荐和连赛提醒，保持现有等待窗口与输入策略。
- Race List 增加卡片状态 OCR：只有实际读取到 **Race Day** 才提供到目标 0 回合的证据，普通可选赛事列表不能提供这一必赛边界。没有把任意 Race List 写成 0 回合。
- 协调器与 URA 剧本在这个可靠边界确认最后一次训练已消耗，提交训练计数一次，重置非比赛行动后的连赛计数。重启恢复的持久训练确认记录在相同边界重建剧本待确认行动，再确认并清除记录；普通可选赛事列表继续保留未知状态和确认记录。
- 增加三项回归，覆盖真实截图交接、原生 OCR 区分必赛/可选列表、运行中及重启后的单次训练提交和连赛状态。修复后 **3 个用例 / 32 条断言**通过，日志为 `build/career-training-race-recheck.log`。

实机只读工具调用同一 `NativeTaskExecutionContext` 和 `CareerActionExecutor.ConfirmCompletionFromObservation`，对已提交的训练确认当前目的页，实际返回 `training_selection -> race_list`，识别分数 1、可靠倒计时 0、原始 OCR 为 Race Day。工具没有调用动作执行方法或发送游戏输入，日志为 `build/career-training-race-live-confirm.log`，原生截图为 `debug/career-training-transition-20261013/native-confirmed.png`。未重复训练、推进当前比赛或重新完成整场育成。

最终验证：Release 全量构建和 52 个 C ABI 导出检查通过；原生 CTest **399/399**、包含原生集成的 Bridge **163/163**（无跳过）通过，资源包 **7 个场景**及架构检查通过。原生 CTest 排除 ManagedBridgeTests 和 WpfGuiTests，Bridge 使用既有 xUnit 直连执行器；本轮没有重跑 WPF 全集。日志为 `build/career-training-race-build-20261013.log`、`build/career-training-race-native-tests-20261013.log`、`build/career-training-race-bridge-integrated-20261013.log`、`build/career-training-race-package-20261013.log` 和 `build/career-training-race-architecture-20261013.log`。

新 DLL、回放工具及比赛配置已同步到 staging（原生文件）、Debug 和 Release GUI 输出，GUI 原文件保留 `.previous-training-race-20261013-130456` 备份；8 个副本的哈希记录为 `build/career-training-race-runtime-hashes-20261013.json`。DLL SHA-256 为 `B20D3C81CE7156CAA2E354D24B66F711250EF06BCE99120BDAB8E59BC660CCCA`，比赛配置为 `B6BF1CF1492CCC24FECE4A2C6EF6B3BA377E0997F9C8D10A28CBDC2F3F4BACAA`。助手进程 31392 仍映射旧模块，需要重开后从当前比赛页继续。
