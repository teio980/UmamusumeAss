# Phase 5：原生队列与迁移收尾

本轮延续原工作区的 Phase 0–4 迁移，按用户要求使用三个 GPT-6 Luna
xhigh 子代理分别完善 Native、CoreBridge 和 WPF，再由主代理统一构建与回归。
不执行打包、发布或安装器生成，也不操作真实游戏设备。历史验证见
[Phase 1–4 记录](phase4-handoff.md)，当前架构见
[架构迁移记录](architecture-migration.md)。

## 执行与数据边界

- C++ `TaskQueueRuntime` 接收整个不可变任务队列，负责顺序执行、取消、
  断连、普通失败后继续、剩余任务跳过、进度快照和一次性终止事件。
  每项任务具有独立实例 ID，相同业务任务可以重复加入。
- Core API 为 11，保留之前导出并增加六个队列/目录接口；导出检查要求
  恰好 52 个未修饰 C ABI 符号。JSON 使用调用方缓冲区，字符串使用 UTF-8。
- CoreBridge 只处理调用、DTO、操作关联、事件顺序、取消与生命周期。
  队列替换失败会回滚；异步方法等待终止事件交付后完成，避免 UI 取消订阅时丢失尾部事件。
- WPF 将设置快照一次性提交 Core，映射进度和终止状态，不执行自动化循环。
  普通失败不再显示整队列全成功；断连导致的取消按设备断连显示。
- 游戏数据库、技能、赛事、事件、支援卡和策略目录由 Core 读取并投影给 UI。
  `UmaDatabaseService` 保留界面查询/选择职责，删除原业务数据库加载入口。
  目录尚未加载时使用有效的空投影；资源替换后各设置列表持续刷新，编辑器
  销毁时解除订阅。回归覆盖连续两次替换，确认列表更新且订阅数不增长。
- 已物理删除旧 C# 日常任务、育成流程、URA 策略/状态、截图/输入/OCR
  执行器及场景包业务解析器；没有用项目排除项隐藏旧实现。
  C# 保留设置、权重预览、编辑器 DTO、图像展示、模拟器管理和更新功能。

## 开发工具与独立宿主

`uma_pipeline_replay` 直接链接生产 Hachimi 解析器、通用图引擎和游戏动作注册表。
离线回放消费固定模板/OCR 证据，记录拟执行输入，不连接设备；证据耗尽、
预算耗尽和输入失败均显式报告。固定证据只验证流程编排，图像/OCR 的实际
算法另有原生与适配器回归。

`--inspect-career-package` 使用生产 `CareerVisualResources` 解析与合并逻辑，
返回源文件、合并定义、明确的任务顺序和诊断。WPF 场景编辑器的加载、验证
和保存检查均调用该工具并提交当前未保存的文本；文件越界、重复覆盖、
无效 JSON、失效动作绑定均由原生层验证。检查本身不写资源文件。

纯 .NET 10 的 `Umamusume.CoreBridge.IntegrationHost` 没有 WPF 引用，支持
`catalog`、`validate-pipeline`、`queue-run` 和已有 `career-run`。
完整队列由 Core 执行，Ctrl+C 取消并等待终止；Career 检查点默认写入持久用户目录。
队列正常结束与所有任务成功分别判断：普通任务失败后继续，最终进程仍返回失败。
命令、输入格式和退出码见 [宿主说明](../tools/Umamusume.CoreBridge.IntegrationHost/README.md)。

## 可重复验证

```powershell
pwsh -NoProfile -File tools/RunPhase5Tests.ps1
```

脚本构建 MSVC Debug Core、原生工具和 Release WPF，依次运行原生 CTest、
回放、场景检查、架构边界、Bridge、独立宿主和所有保留的 WPF 离线测试。
运行日志位于 `build/phase5/`；Native DLL 与工具仅复制到本地测试/运行输出，
脚本不调用 install、publish、package 或签名。WPF 测试发现阶段排除
`Live`、`Packaging`，包括需要环境开关才运行的实机测试。

已有原生构建时可用 `-SkipNativeBuildAndTests` 接续其他检查；仅修改界面时可用
`-WpfOnly` 运行 WPF 构建与离线套件。这两个选项不代表省略的原生检查已通过。

最终验证于 2026-10-11 完成，所有下列检查通过：

| 检查 | 结果 | 证据 |
|---|---|---|
| MSVC Debug Core、原生回放工具 | 构建通过，52 个精确 C ABI 导出 | `build/phase5/native-build.log` |
| 原生 CTest | 339/339，386.40 秒 | `build/phase5/native-tests.log` |
| CoreBridge 与独立进程集成 | 163/163，0 失败、0 跳过 | `build/phase5/bridge-tests.log` |
| Release WPF 与测试项目 | 构建通过，0 编译错误 | `build/phase5/managed-build.log` |
| 全部保留的 WPF 离线测试 | 582/582，0 失败、0 跳过；发现时排除 1 项打包测试 | `build/phase5/wpf-tests.log`、`wpf-trace.jsonl` |
| 原生证据回放 | 9 个场景通过 | `build/phase5/pipeline-replay.log` |
| 原生场景包检查 | 7 个场景通过，未修改源资源 | `build/phase5/career-package-inspection.log` |
| 无 WPF 命令行宿主 | 5 个场景通过，含普通失败后继续及正确退出码 | `build/phase5/headless-commands.log` |
| 架构边界与源码检查 | 无旧执行器/场景业务解析器；Bridge/Host 无 WPF 引用；脚本语法与 `git diff --check` 通过 | `tools/TestArchitectureBoundary.ps1` |

WPF 场景检查工具已从实际 GUI 输出目录运行，现有 URA 包返回有效，包含
27 份源 JSON 和 387 个任务。Core DLL 与回放工具的构建产物、staging、GUI、
WPF 测试目录一致；Bridge 测试和独立宿主使用相同 Core DLL。

原生上下文测试目录改为每次新建，避免系统复用进程 ID 时追加旧 FakeAdb
点击日志；保留了失败输入消耗重试额度等原断言。旧 C# 自动化镜像测试已随
被删除实现撤除，生产 Core 的流程/策略测试保留，因此不能用旧 WPF 总数
直接比较覆盖率。

## 验收范围

本轮交付源码整合、构建与离线行为回归。真实日常任务闭环、完整普通 URA、
完整独立训练及中断重入仍属于实机验收；发布包验收按本次要求未执行。
原有 ImageSharp 3.1.11 的 NuGet 审计提示仍会出现在构建日志，本轮没有关闭
默认审计或升级该依赖。
