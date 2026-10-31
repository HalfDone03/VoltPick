# 项目规则（VoltPick）

## 工作方式

- **任务未完成不停止**：一个任务要么做到可交付（改完代码、跑完自测、覆盖更新并验证），要么明确说明卡在哪里；不要在中途、不要只做一半就停下来等指令。
- 每跑完一段就把结果告诉用户，不要默默长时间执行。
- 交付前必须自测：`开发\test.ps1` 必须 298 通过 / 0 失败 / 零警告（`build.ps1` 与宿主编译都用 `/warn:4 /warnaserror+`，`TestHarness` 里 `ExpectedChecks` 会核对条数，少跑一条也算失败）。
- 汇报用具体数字（尺寸、坐标、字节数、哈希、PID、日志行），不要只说“已改好”。

## 版本与交付

- 版本冻结在 v1.0，只修 bug，不加功能版本号。
- 更新流程固定：结束进程 `VoltPick` → `开发\build.ps1` → 原地覆盖 exe → 启动。
- 不做正式版打包、不做 zip，除非明确要求发布。
- 所有临时测试文件只能放 `开发\tests\`，交付时清理，只保留 `db-live_LiveLayout.cs`。

## 代码约定

- C# 5 / .NET Framework 4.8 / 系统自带 `csc.exe`，无 csproj；新增源文件放进 `src\**` 即参与编译。
- 界面尺寸一律走 `UiScale.Px/Scale`，字体走 `Theme.UiFont` 或 `FontBag.Take`，颜色走 `Theme.*`（深浅色主题都要能看）。
- 设置页布局由 `SettingsSection.ApplyLayout` 统一分配宽度：标签占 `labelColumn`，按钮保持自身宽度，输入控件平分剩余空间。
- 页面宽度以 `_tabs.DisplayRectangle` 为准（`TabPage.ClientSize` 未显示时是 0）。
- 源文件统一 LF（不要 CRLF）。`.cs` 不加 BOM —— `csc` 用 `/codepage:65001` 读源码，不需要 BOM。`.ps1` 必须带 UTF-8 BOM（`build.ps1`、`test.ps1` 都有）：Windows PowerShell 5.1 读无 BOM 脚本会按 GBK 解码，中文注释立刻变乱码乃至语法错误；用编辑器改完脚本要确认 BOM 还在。
- 产品硬规则：读不到电量的设备不显示（只有用户自己点「显示」显式放开的才出现，且卡片必须写清读不到的原因）。
