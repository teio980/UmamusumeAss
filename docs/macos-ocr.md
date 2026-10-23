# macOS OCR 适配与回归

所有生产 OCR 入口最终使用两套原生实现；修复需同时覆盖二者。

| 调用链 | 使用者 | 本轮适配与验证 |
| --- | --- | --- |
| `WindowsOcrRecognizer` → `NativeTaskExecutionContext::RecognizeText` | 任务文字匹配、商店、技能、育成观察、事件、比赛、独立育成 | Mac 全局 Otsu 与局部 Sauvola 合并；稀疏布局补漏；原坐标去重；真实截图及任务匹配验证 |
| `RecognizeImageText` / `CreateImageRecognitionContext` | 预处理后的心情、倒计时、训练扫描、抓娃娃 CREDIT | 同一文字引擎；独立上下文和取消测试；实际颜色预处理后的 CREDIT 解析 |
| `NumericOcrBatch` → `UmaNumericOcrWorker` | 倒计时、训练增益、失败率、数字裁剪 | Mac 未解析字段追加 Sauvola 重试；保留范围、字形、唯一字段和五秒预算校验；复用进程与取消测试 |
| `NumericOcrBatch` → Tesseract CLI | 工作进程不可初始化时的数字回退 | Mac 直接设置 TSV 输出参数，支持仅带 `eng.traineddata` 的模型目录；与真实工作进程对照验证 |
| `UmaRecognizeText` → CoreBridge → `UmaAutomationService` → 截图 OCR 页面 | 导入截图和设备截图 | Mac 支持 RGBA 与纯灰度输入；原生 ABI 缓冲区、JSON、坐标、托管桥接验证 |
| Avalonia 构建 / `.app` 目录 | 从 dotnet、Finder 或任意工作目录启动 | Mac 根据已加载库定位模型；核心库与数字工作进程配套复制；普通运行目录复制 `tessdata`；`.app` 使用 `Contents/Resources/resource/tessdata` |

## 实现约束

- 新的识别策略限于 `__APPLE__`，Windows 保留原有识别次数、分割模式及 CLI 命令。
- 不把 OCR 未知值转换成默认数值。局部重试只处理尚未解析的字段。
- 数字周围的引号可携带空白；数字内部空白、多个值、负数、单位和溢出仍被拒绝。
- 稀疏布局结果只补充缺失位置，不用零散单词替换已有完整行。重叠位置不会产生重复技能名；不同商店行仍保留为不同候选。
- 空白图像不运行易产生幻觉的单行识别。短裁剪在自动布局无结果时使用单行识别。
- Mac 默认构建优先复制 `build/osx-arm64` 的核心库，显式 `NativeDllPath` 优先级最高；工作进程必须取自所选核心库的同一目录，避免旧 `test-cmake` 产物和新工作进程混用。
- 技能点击后在同一帧核对原行名称和 Obtained，短暂重试期间保持原列表位置，并检查帧身份。
- 称号页标题模板使用实图验证后的 0.96 阈值：两份称号截图相关度均大于 0.999，启动页的误匹配约为 0.907；保留现有模板识别策略。

## 回归入口

原生测试构建：

```sh
cmake --build build/osx-arm64 --target RecognitionTests CareerTests TaskEngineTests NativeTaskContextTests UmaCallerTests GameTaskTests UmaAssistantCore
```

真实截图及调用链测试标签：`[MacOcr]`、`[OcrCorpus]`、`[OcrIntegration]`、`[NativePerceptionParity]`、`[CareerEpithetTemplate]`。测试覆盖技能选中前后、按钮与徽标、灰度与缩放、商店重复行、事件结果、比赛信息、日程日期、独立育成入口、抓娃娃、训练增益及奖励页。2026-10-23 的 BlueStacks 截图作为固定夹具保存。

CoreBridge 的 `RecognitionImageDecoderTests` 包含真实原生 OCR 测试，需设置 `UMA_NATIVE_INTEGRATION=1` 并通过 `NativeDllPath` 指定本轮构建。可选外部 OCR golden 语料未安装时，该项测试会明确跳过。

复制目标可独立验证，无需启动应用：

```sh
dotnet msbuild src/Umamusume.Avalonia/Umamusume.Avalonia.csproj -t:CopyNativeRuntimeForAvalonia -p:TargetDir=/tmp/uma-ocr-runtime/bin/
```

本轮还在普通运行目录、模拟 `.app` 目录和当前应用目录分别验证了 RGBA／灰度 OCR，以及从 `PATH` 移除 Tesseract 后数字工作进程的模型加载、请求编号和阈值切换复位。

2026-10-23 验证结果：Recognition 28 项通过、1 项可选语料跳过；Career 204、TaskEngine 32、NativeTaskContext 25、UmaCaller 34、GameTask 85、CoreBridge 163、AppCore 自动化服务 17 项通过，合计 588 项通过。Mac 实图扩展用例及复制／打包目录检查另外复核通过。

部署时必须同时更新 `UmamusumeCore.dylib` 和 `UmaNumericOcrWorker`。已运行进程需重启才能加载新的动态库。本地截图回归和只读 ADB 识别不能替代完整游戏操作回归；OCR 结果也不保证任意装饰字体或任意新画面逐字正确。
