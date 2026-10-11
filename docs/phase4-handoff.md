# Phase 1–4 迁移记录（2026-10-11）

此文件保留 Phase 4 当时的验收记录。后续整队列 ABI、旧执行器清理和当前验证结果见
[Phase 5 迁移记录](phase5-handoff.md)；下文的 Managed 兼容路线已在 Phase 5 移除。

## 本轮范围与状态

本轮按用户最后确认的范围完成代码接入、构建与离线回归：**不打包，不做实机验收**。
原有未提交的 Phase 0–3 修改及隔离育成草稿作为基线保留；修正草稿后接入现有 Core、Bridge 和 WPF。
普通育成与独立训练现在默认交给 Core。显式 `Managed` 兼容路由暂时保留，原生失败不会切换旧执行器重做操作。

工作分支为 `codex/newframework`，基准提交为 `52619a03d008ffa98e35f0529c34af5583440fa7`。
当前修改未提交。三项子任务按用户要求使用 GPT-6 Luna xhigh，分别检查领域、运行时和 Bridge/应用接入；构建与测试由主代理串行管理输出目录。

本轮代码、构建与离线回归已完成，最终结果见下表。离线流程回放证明真实组件的组合行为，不能表示游戏实际画面已完成实机闭环。

## 分层与接入

- `UmaAssistantCareerDomain` 只含通用状态、观察值、语义意图和策略接口，不链接设备、图像、WinRT 或 WPF。
  `ObservedValue<T>` 保留来源及有限、正值置信度；缺失观察不会被当作已识别。
- `UmaAssistantUraScenario` 单独编译 URA 目标、比赛规则、普通比例策略、智能评分和扫描状态机。
  剧本工厂为每次育成创建独立实例；七个既有策略 ID 和兼容别名保留。
- `UmaAssistantCareerRuntime` 通过场景接口与 `ICareerTrainingFlow` 编排流程；
  `UmaAssistantUraCareer` 提供 URA 视觉扫描适配器。通用协调器不引用具体 URA 模块或做场景类型判断。
- `NativeCareerTaskRunner` 在 `GameTasks` 组合资源、GameData、入口、普通育成、独立训练和恢复，
  通过 API 10 的游戏任务接口运行。Core 内部截图和输入直接使用原生设备上下文。
- Hachimi v1 的旧游戏元数据与业务时间字段由 `Resources/PipelineMetadataSchema` 校验；
  通用 `TaskEngine` 不保留未使用的角色、卡组、商店或比赛元数据。已使用的默认轮询间隔仍为 300ms。
- WPF 的队列服务冻结任务配置并提交 Core，显示 Bridge 的类型化进度和日志。
  本轮不新增 Phase 5 的整队列 ABI，当前仍为 API 10、46 个 C 导出。

## 已修正的有效行为

### 设备、识别与资源

- 原生设备实现保留二进制截图、WIC 解码、Raw/Gzip、坐标变换、帧租约、会话与几何代次校验，
  使用串行输入和五秒断线监控。取消、旧帧、断线及关闭由 FakeAdb/API 回归验证。
- WPF 图像解码、模板匹配、灰度/颜色/缩放/遮罩、数字预处理及 Windows OCR 使用 Core；
  没有匹配评分的 C# 回退实现。
- 修正数字字形验证对短 token 的越界读取；MSVC Debug 断言使用 stderr，避免测试弹出模态窗口。
- Windows OCR 的 COM apartment 按调用线程创建和释放，STA 调用转到 MTA 工作线程；
  预处理灰度输入转成 OCR 支持的 RGBA。实际 Windows OCR、MTA/STA 和数字样本已纳入测试。
- 原生智能训练保留可选本地 Tesseract 的 PSM、填充、范围、超时、取消及进程树回收规则，
  只补读未解出的数值；临时文件由作用域管理。
- 模板集合按固定资源根目录解析，补齐 runtime frame、角色、继承和支援卡等已使用集合。
- 已接受的流程在等待前一个工作线程结束时固定 `userDirectory`；
  之后修改全局目录不会改变该次运行的相对路径。测试显式使 FakeAdb 尺寸与 PNG fixture 一致。

### 育成观察、领域与策略

- 同帧观察页面、能量、心情、日期、目标和事件；缺失的主页面数值清为未知，避免使用上一回合值决策。
- 保留旧目标文本、粉丝数、比赛级别与次数解析，日历映射和同回合比赛顺序。
- 普通策略只在确认实际训练后推进比例；打开抽屉、扫描、选择前恢复均不推进。
  未知能量沿用已有休息处理。
- 目标比赛、粉丝差额、级别比赛、连续参赛和 URA 决赛规则放在剧本模块。
  训练距离先查全局当前比赛 ID，再查最近角色目标，之后使用剧本比赛及决赛回退，保持旧策略优先级。
- 比赛结果使用带期望目标、来源、帧代次和置信度的中立契约。
  匹配指定的结果证据后，剧本模块才更新名次、粉丝、目标、重试及下一阶段；
  未知结果、错误帧、低置信度、取消或重复提交不会推进状态。
- 单个目标完成与整个育成完成分开：正常成功需要终局结算，再确认 Home。
  修正 `goal_objective_complete` 被归为未知页面的问题，使单项目标完成后继续执行已注册的下一步动作。

### 动作、入口与恢复

- 动作通过资源图执行并验证过渡，缺失资源或未确认过渡显式失败，移除草稿中的默认盲点坐标。
- 修正普通启动顺序、卡组、剧情跳过及快速模式资源路径；入口复用角色/继承/支援卡目录。
- 休息、医务室和外出只在最终确认动作后提交回合；选择页面不是回合提交。
  同名事件结合正文与选项签名区分，日期恢复保留已确认策略进度与待处理比赛。
- 技能购买保持配置顺序，确认价格、点数和学习结果，再返回比赛；抓娃娃使用同帧触控证据与取消释放。
- 独立训练编排模式、专注、卡组、日程、技能、Start 及 Home 确认；
  检查点绑定设备、角色、配置和资源版本，保留旧格式读取。
  Start 前写入 `independent_start_issued`，恢复时不会重复点击 Start。
- C# 和原生检查点均用唯一临时文件、落盘和原子替换；失败保留旧文件，覆盖并发及锁冲突回归。
- 日志区分开始执行、输入发送与过渡确认。中文训练名称、日期、策略和回合参数在界面本地化，
  Core 只输出语义事件和诊断；精确语义的现有翻译先于动态模板，避免育成训练消息被通用训练名称覆盖。
- WPF 退出改为同步的 Dispatcher 关闭屏障：后台关闭任务不捕获 UI 上下文，
  UI 在同一 10 秒预算内继续处理回调，完成后才交给框架释放 ViewModel。
  视觉释放有两秒子预算，仍在 `finally` 调用 Core 关闭；屏障超时不会等待未完成任务，
  后台 Core 继续使用现有句柄和回调保留策略，晚到故障会被观察。

## 清理边界

已移除重复的 C# ADB 会话/截图/触控实现、旧 Windows OCR 实现及旧模板评分代码；
本轮另外删除未被生产代码使用的隔离 `UraTrainingPlanner` 和镜像测试，
仍被兼容执行器使用的 `UraPlannedAction` 移到 C# URA 目录。

保留显式 Managed 兼容执行器及仍参与设置预检、目录展示的 C# 代码。
它们仍有调用者，不能按文件名当作无用代码删除；整队列迁入 Core 与最终移除属于 Phase 5。

## 最终验证

测试使用 Windows x64、C++20、VS2022 MSVC 和 .NET 10。
`build/` 日志是本机证据且可能被 Git 忽略；可复现命令及 runner 源码保存在版本库。

| 检查 | 最终结果 | 日志 |
|---|---|---|
| MSVC Debug 全量构建和精确导出检查 | 通过，精确 46 个 C 导出，0 警告、0 错误 | `build/phase14-native-final-build.log` |
| 原生全量 CTest（不含两个 VSTest 入口） | 326/326，零失败，450.42 秒 | `build/phase14-native-final-tests.log` |
| Release WPF、Bridge、测试项目构建 | 最终源码通过，0 警告、0 错误 | `build/phase14-managed-post-replay-build.log` |
| 无 WPF 命令行宿主 Release 构建 | 通过，0 警告、0 错误；只引用 Bridge | `build/phase14-host-final-build.log` |
| Bridge 全量，包括新进程原生集成 | 最终 Debug DLL 再验证 147/147，零失败、零跳过；新 runner 构建通过 | `build/phase14-bridge-post-replay-tests.log` |
| WPF 识别、设备、任务、育成离线组合回归 | 830/830，零失败、零跳过 | `build/phase14-wpf-final-tests.log` |
| 最终退出屏障、日志、队列接入与检查点回归 | 74/74，零失败、零跳过；含三个 STA Dispatcher 用例 | `build/phase14-wpf-post-replay-tests.log` |
| 相对 Phase 3 的匹配轨迹比较 | 3,169 个既有唯一输入全部覆盖；分数和位置最大误差均为 0，阈值判断零差异 | `build/phase14-recognition-comparison.json` |
| 原生完整组件的普通/独立育成回放 | 2/2，53 断言通过；普通模式覆盖 11 场比赛、URA 决赛、结算和 Home，独立训练覆盖 Start 与完成检查点 | `build/phase14-native-runner-replay-tests.log` |
| 接受运行后修改全局目录的快照回归 | 1/1，17 断言通过 | `build/phase14-native-directory-final-tests.log` |

完整组件回放使用真实 manifest、页面观察器、策略、动作执行器与执行图，
在注入的固定帧、匹配和 OCR 上运行；整个目标完成页面使用实际 WIC 解码的资源图片与原生颜色匹配。
该回放将不参与链路的图像观察固定为未命中，抓娃娃等算法由专项样本测试覆盖。
它覆盖目标与比赛链路，不等同于逐回合实际游戏画面的完整重放。图像与实际 Windows OCR 另有样本回归；
两者共同覆盖组件行为，未做实机画面验收。

```powershell
$tempPath = Join-Path (Get-Location) 'build/msvc-debug/test-temp'
New-Item -ItemType Directory -Path $tempPath -Force | Out-Null
$env:TEMP = $tempPath
$env:TMP = $tempPath
cmake --preset msvc-debug
cmake --build --preset msvc-debug
ctest --preset msvc-debug -E 'ManagedBridgeTests|WpfGuiTests' --output-on-failure

dotnet build tests/UmamusumeWpfGui.Tests/UmamusumeWpfGui.Tests.csproj -c Release
& tools/RunCoreBridgeTests.ps1
$filters = Get-Content tools/phase1-4-offline-test-filters.json -Raw | ConvertFrom-Json
& tools/RunTemplateMatcherGolden.ps1 -Filter $filters -TracePath build/phase14-offline-trace.jsonl
& tools/CompareTemplateMatcherTraces.ps1 -ReferenceTrace build/phase3-recognition-trace.jsonl -ActualTrace build/phase14-offline-trace.jsonl -ReportPath build/phase14-recognition-comparison.json
```

本机 VSTesthost 无法启动，因此两个 .NET CTest 入口由可追踪的直接 xUnit runner 执行，
没有通过禁用断言或跳过原生集成替代。Golden runner 首次使用需先构建其项目；
不传 `-NoBuild` 会同时构建测试项目和 runner。
830 项组合回归完成后，最后的退出与日志修正另以 74 项受影响回归验证；
新的三个 Dispatcher 用例已加入上述离线过滤表，之后完整执行该表会同时运行它们。
轨迹比较脚本要求两轮测试均完整成功；重复轮询按唯一输入归组，实际输出逐项比较。
本轮较大的回归集新增 518 个唯一输入；既有输入没有缺失，6,384 条既有输入的实际输出全部一致。
本机 Release WPF 构建用了进程级 `-p:NuGetAudit=false`，以隔离已有 ImageSharp 依赖审计提示；
没有更换依赖版本或修改仓库的审计配置。

## Phase 5 的后续工作

- 整队列归 Core：补齐 Append/SetParams/Clear/Start、运行快照与 Catalog 契约。
  现有 `UmaSetInstanceOptions` 和 `UmaValidatePipelineJson` 已实现，后续继续接入相关界面工具。
- 开发者编辑、试运行、离线回放全部使用 Core 解析器与执行上下文。
- 移除显式 Managed 执行器、临时路由，以及设置预检或目录仍在调用的重复业务加载实现。
- 独立命令行宿主已有 `career-run`，不引用 WPF；后续按发布要求做最终包验收。

本轮不执行打包或实机测试，未对新增剧本、训练策略设计或 OCR 引擎做扩展。
