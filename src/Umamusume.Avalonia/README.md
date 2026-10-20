# Umamusume.Avalonia

macOS / Windows 共用的 Avalonia 客户端，使用 [FluentAvalonia](https://github.com/amwx/FluentAvalonia) 的 `AppWindow`、`NavigationView`、`InfoBar` 和 `SettingsExpander`。Windows WPF 客户端保留在 `src/UmamusumeWpfGui`。

## 功能

- **总览与连接**：ADB 文件选择和路径发现、在线设备列表、手动序列号/IP、最近连接、忘记连接、取消/超时、自动启动模拟器、连接状态与设备信息、游戏启动和截图。预设使用 `resource/connection.json` 中实际存在的配置键。
- **任务队列**：启动游戏、育成、每日竞赛、队伍竞技场、邮箱奖励、任务奖励、商店购买。支持添加、复制、删除、重排、批量勾选、真实队列事件、失败原因和取消；通过 `CoreBridge.RunQueueAsync` 运行，不使用延时模拟成功。
- **任务配置**：使用 Windows 相同的 profile 字段；包括育成剧本 manifest、常规/独立育成、训练权重、技能/比赛、支援卡与好友卡、继承因子、策略、TP 和重试设置。高级 JSON 编辑保留未知字段；商店设置在全局 Hachimi 配置中保存。
- **屏幕与 OCR**：设备截图或图像文件导入、预览与原图导出、框选裁剪、OCR 结果及坐标、模板匹配。
- **开发者工具**：资源与模板浏览、图像匹配、管线 JSON/节点编辑、转移预览、ROI 应用、撤销/重做、原生校验和设备运行；支持原生离线回放以及场景包 Native Inspector。
- **日志**：级别和关键字筛选、跟随最新记录、清空、导出当前筛选结果。
- **设置**：深浅/系统主题、即时中英文界面、ADB 与启动参数、资源/用户目录、当前核心状态、更新检查、跳过版本和 SHA-256 校验下载。

配置保存在 macOS 的 `~/Library/Application Support/UmamusumeAss/connection_settings.json`。任务 JSON 与 WPF profile 兼容；用户可通过高级编辑器继续保留新版核心增加的字段。客户端共用一个原生实例，连接、任务与诊断操作由业务层协调，退出时取消并释放原生会话。

## 构建与运行

需要仓库 `global.json` 指定的 .NET SDK，以及已构建的原生核心。

```bash
dotnet build src/Umamusume.Avalonia/Umamusume.Avalonia.csproj
dotnet run --project src/Umamusume.Avalonia/Umamusume.Avalonia.csproj
./tools/package_macos.sh build/test-cmake
```

构建会从本地 CMake 输出复制核心、OCR worker 及 `uma_pipeline_replay`。macOS 打包脚本会将离线回放/Inspector 工具纳入 `.app`；缺失时停止打包。

```bash
dotnet test tests/Umamusume.AppCore.Tests/Umamusume.AppCore.Tests.csproj
dotnet test tests/Umamusume.Avalonia.Tests/Umamusume.Avalonia.Tests.csproj
```

## 平台边界

原生核心决定可运行的剧本和策略。界面不会把没有对应资源的剧本列为已支持。离线回放/Inspector 需要随应用提供的原生工具；缺失或校验失败会显示具体错误。

程序更新选择当前平台/架构的发布包。macOS 下载校验后需退出应用，再解压并替换 `.app`；Windows 的 `UmamusumeAssUpdater.exe` 安装协议不能直接在 macOS 执行。资源编辑与更新不应覆盖正在使用的原生资源快照，保存后可在空闲时重载原生资源。
