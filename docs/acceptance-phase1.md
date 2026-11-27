# 阶段 1 验收记录：窗口层级 + 壁纸

- 验收时间：2026-10-28 15:59
- 环境：Windows 10 21H2（19044）、单屏 2560×1440、任务栏 40px、桌面为纯色 `#FFE300`（无壁纸文件）
- 可执行文件：`src/PhoneDesktop/bin/Release/net8.0-windows/PhoneDesktop.exe`
- 自动验收：`tools/verify.ps1`（29 项全部 PASS）
- 壁纸路径验证：`tools/test-wallpaper.ps1`（9 项全部 PASS）

## 一、层级与启动

| 检查项 | 结果 |
|---|---|
| 覆盖层窗口出现 | PASS，308 ms（冷启动到覆盖层可见） |
| 启动 < 1000 ms | PASS |
| 覆盖层尺寸 = 主屏 2560×1440 | PASS |
| 父窗口 = Progman（桌面窗口） | PASS |
| 无 `WS_EX_TOPMOST`（exStyle=0x0） | PASS |
| z 序在 `SHELLDLL_DefView` 之上 | PASS |
| 桌面图标被完全盖住 | PASS（见截图 01） |
| 覆盖层可接收 WPF 鼠标左键 | PASS（日志记录 `(640,300)`） |

## 二、遮挡与被遮挡

| 场景 | 结果 |
|---|---|
| 打开记事本 → 完整盖住覆盖层 | PASS（见截图 02） |
| 最小化记事本 → 覆盖层立即露出 | PASS（见截图 03） |
| 全屏窗口（模拟全屏游戏）→ 完整盖住覆盖层 | PASS（见截图 05） |
| 全屏期间覆盖层仍存活、未置顶、未重排 | PASS |
| 关闭全屏 → 覆盖层恢复 | PASS（见截图 06） |

## 三、资源占用

| 指标 | 实测 |
|---|---|
| 空闲 8 秒 CPU | 0%（一次采样 0.01%） |
| 私有内存 | 62.5–63.1 MB |
| 工作集 | ~89 MB |
| 线程数 | 21 |
| 启动到可见 | 308–344 ms |

## 四、退出与恢复

| 检查项 | 结果 |
|---|---|
| `--exit` 后进程正常结束 | PASS |
| 覆盖层窗口销毁 | PASS |
| 桌面图标与壁纸原样恢复 | PASS（见截图 07） |

## 五、壁纸路径（主路径 + 兜底）

| 检查项 | 结果 |
|---|---|
| 无壁纸文件时使用桌面背景色（纯色兜底） | PASS，读取到 `#FFE300` |
| 有壁纸图片时按屏幕像素宽度解码并铺满绘制 | PASS（渐变左右采样 R33/G51/B151 → R132/G43/B159） |
| 壁纸上的白色文字被完整绘制 | PASS（命中 124 个白色采样点） |
| 日志记录壁纸来源 | PASS |
| 测试后原壁纸恢复 | PASS |

## 六、健壮性（额外验证）

| 场景 | 结果 |
|---|---|
| Explorer 重启（Progman 句柄失效） | 5 秒内自动重建覆盖层并重新置顶（新 Progman `0x30208` → 覆盖层 `0x300FE`） |

## 七、阶段内发现并修复的问题

1. **`IDesktopWallpaper` COM 会阻塞 UI 线程**：一次启动中该 COM 调用长时间不返回，整个覆盖层冻结，Windows 给桌面窗口挂上了「未响应」幽灵窗口。已改为 `SystemParametersInfo(SPI_GETDESKWALLPAPER)` + 注册表 + 桌面背景色，全部是非阻塞调用。
2. **`BitmapCreateOptions.IgnoreImageCache` + 流式位图**：WPF 以 URI 作为图像缓存键，流式来源 URI 为 null，解码抛 `Value cannot be null. (Parameter 'key')`。已去掉该选项，壁纸解码恢复正常。
3. **可见但排在图标层之下的瞬间**：先以 `WS_CHILD`（不带 `WS_VISIBLE`）创建，`SetWindowPos(HWND_TOP|SWP_SHOWWINDOW)` 后再显示，消除该窗口。
4. **`.ps1` 编码**：无 BOM 的 UTF-8 脚本在 Windows PowerShell 5.1 下按 GBK 解码，中文注释吞掉下一行代码（`Add-Type` 被吃掉导致 WinForms 类型找不到）。脚本统一改存 UTF-8 with BOM。

## 八、截图

| 文件 | 内容 |
|---|---|
| `artifacts/shots/phase1/01-desktop-overlay.png` | 覆盖层盖住全部桌面图标（角标显示挂载成功） |
| `artifacts/shots/phase1/02-notepad-covering.png` | 记事本盖住覆盖层 |
| `artifacts/shots/phase1/03-overlay-after-minimize.png` | 最小化后覆盖层露出 |
| `artifacts/shots/phase1/04-click-overlay.png` | 点击事件送达（角标显示 `点击已接收 (640,300)`） |
| `artifacts/shots/phase1/05-fullscreen-covering.png` | 全屏窗口完整盖住覆盖层 |
| `artifacts/shots/phase1/06-after-fullscreen.png` | 全屏关闭后覆盖层恢复 |
| `artifacts/shots/phase1/07-desktop-restored.png` | 退出后原生桌面恢复 |
| `artifacts/shots/phase1/08-wallpaper-overlay.png` | 壁纸图片主路径（渐变 + 文字） |
| `artifacts/shots/phase1/09-desktop-restored.png` | 壁纸验证后恢复纯色桌面 |

原始输出：`artifacts/verify-report.txt`、`artifacts/wallpaper-report.txt`。
