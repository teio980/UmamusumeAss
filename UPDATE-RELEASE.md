# Update release and runbook

## 概述与更新方式

本项目两端均采用**完整包自动更新**架构：
- **Windows**: 完整 ZIP (`UmamusumeAss-v{V}-win-x64-full.zip`)，包含程序与官方 `resource/` 目录。此外附带安装包 (`UmamusumeAss-v{V}-win-x64-setup.exe`) 供手动安装用户使用。
- **macOS**: 完整 `.app.zip` (`UmamusumeAss-v{V}-osx-arm64.app.zip`)，包含完整的 `UmamusumeAss.app`（内置 `Contents/Resources/resource/`）。

Release 不再需要附带 manifest JSON、`.sig` 签名文件或 delta 差分包。程序与资源打包一同发布，用户在应用内完成下载后点击“重启更新”，应用由后台助手安全替换并重新启动。

## 用户迁移说明

- **现有旧版用户**：由于更新协议切换，须完全退出正在运行的旧版本应用，手动下载最新安装包或完整 ZIP 进行覆盖安装/解压运行一次。
- **此后更新**：后续版本发布后，两端均可在应用内自动检查新版、下载校验并“重启更新”，无需再手动安装。

## 检查、下载与校验协议

1. **Release 过滤**：
   - 更新检查仅查询正式的 `vX.Y.Z` Release 标签，自动忽略 draft、prerelease、独立资源 Release（如 `resource-v*`）以及 GitHub 自动源码包。
   - 版本按 SemVer 从高到低匹配适合当前操作系统的完整包。若高版本 Release 缺少本机包则跳过，寻找较低的可用版本。
   - 当选中的完整包缺少有效 GitHub SHA-256 摘要（`digest: sha256:...`）时，系统明确提示无法校验（可重试或手动前往 Release 页面下载），不会误报为“没有更新”。
2. **下载与解压校验**：
   - 下载前刷新该附件的元数据信息。
   - 下载完成后校验文件大小与 SHA-256 摘要，确保与 GitHub Release API 报告一致。
   - 安全解压至受保护的暂存目录，防御路径遍历、绝对路径、重复路径、符号链接及超限解压包（限制单文件 1GB、解压总量 2GB、文件数 10000）。
   - 检查包内程序版本（Windows 为 `version.txt`，macOS 为 `Contents/Resources/version.txt`）必须与 Release tag 严格一致。
3. **本地文件清单 (Local Inventory)**：
   - 客户端解压并验证通过后，在暂存目录生成 `local-inventory.json`，供独立更新助手在应用退出后再次校验暂存文件与更新助手自身。该文件完全由本地生成，无需发布者上传。

## 安装与资源管理

1. **Windows 原生更新器 (`UmamusumeAssUpdater.exe`)**：
   - 互斥量保护：使用 `Local\UmamusumeAss.Updater.v2` 防止并发更新。
   - 等待主程序完全退出，备份原目录，替换文件；若复制失败则自动回滚原版本。
   - 官方 `resource/` 目录整体换新，避免旧版本已删除的资源残留生效。
   - 替换完成后自动拉起新版本主程序。
2. **macOS 更新助手 (`Umamusume.MacUpdater`)**：
   - 验证 candidate bundle 与本地 `local-inventory.json` 清单。
   - 使用文件系统同卷原子替换 (`renameatx_np`) 交换 `.app` 目录。
   - 拉起新版本进行 `--update-health` 健康检查（主窗口创建、原生核心初始化、资源目录读取）；若健康检查失败或超时则立即回滚至原版本备份。
   - 具备中断恢复机制，如果更新中途异常退出，下次启动时根据未完成日志优先执行安全恢复。
3. **资源处理与用户数据保留**：
   - 取消独立资源下载与应用流程。应用启动时始终以当前安装包内置的资源为基准。
   - 旧版本可能遗留的 `active.json` 指针不再优先生效，防止旧资源覆盖新版内置资源。
   - 用户自定义配置（`config.json`）、任务列表（`tasks.json`）以及用户资源覆盖（`resource-overrides/`）完全独立保存在用户数据目录，更新过程严格保留。

## 发布流程

每次发布新版本：
1. 确认 `version.txt` 已更新为正确的语义化版本号（如 `0.3.1`）。
2. 在 GitHub Actions 页面手动触发 `Release` workflow：
   - 输入 `version`（例如 `0.3.1`）。
   - 可选输入 `bundled_resource_version`（默认为版本号）。
3. Workflow 自动完成：
   - Windows 2022 runner 编译原生核心、运行 CTest 与 managed 测试、打包 Windows 完整 ZIP 与安装包。
   - macOS ARM64 runner 编译原生核心、运行 CTest 与 managed 测试、执行 ad-hoc codesign 并打包 `.app.zip`。
   - 创建 GitHub Draft Release 并自动附加三项资产：
     - `UmamusumeAss-v{V}-win-x64-full.zip`
     - `UmamusumeAss-v{V}-win-x64-setup.exe`
     - `UmamusumeAss-v{V}-osx-arm64.app.zip`
4. 发布者在 GitHub Release 页面核对资产列表与说明后，正式发布 Release。
