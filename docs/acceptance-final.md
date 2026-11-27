# 最终验收报告（PRD 第 7 节逐条）

- 验收时间：2026-10-28 17:07
- 环境：Windows 10 21H2（19044）、单屏 2560×1440、100% DPI、DWM Tier 2、UAC 开启
- 产物：`dist\PhoneDesktop\PhoneDesktop.exe`（框架依赖，需 .NET 8 Desktop Runtime）
- 自动验收：`verify.ps1` / `verify-phase2.ps1` / `verify-phase3.ps1` / `verify-phase4.ps1` / `verify-final.ps1`

## 一、PRD 7 验收标准逐条结果

| # | 验收标准 | 结果 | 证据 |
|---|---|---|---|
| 1 | 启动后原生桌面被不透明覆盖层盖住，显示壁纸和 Dock | ✅ | 覆盖层 2560×1440、父宿主为桌面层、z 序在图标层之上；`final/40-desktop.png`（图标区无图标、任务栏已隐藏） |
| 2 | 普通窗口正常盖住覆盖层和 Dock；关闭/最小化后覆盖层露出 | ✅ | `phase1/02`、`phase1/03`；`final/41-window-over-all.png`（最大化窗口同时盖住覆盖层与 Dock） |
| 3 | 全屏游戏/视频正常盖住覆盖层，覆盖层不弹出不置顶 | ✅ | `phase1/05`、`phase1/06`；`final/43-fullscreen.png` |
| 4 | Dock 最左任务管理器左键可打开任务管理器 | ✅（链路） | 点击→启动链路由 `verify-phase2` 用 `PHONEDESKTOP_TASKMGR` 替换命令验证；真实命令为 `taskmgr.exe`，点击后 Windows 弹 UAC 确认框（平台行为） |
| 5 | Dock 常用应用可拖拽排序，重启后顺序保持 | ✅ | `verify-phase4`：拖拽把"命令提示符"移到首位，重启后顺序一致 |
| 6 | 抽屉右键固定到 Dock；Dock 右键取消固定 | ✅ | `verify-phase4`：5→6→5 项，`settings.json` 同步；`phase4/30`、`phase4/31` |
| 7 | 退出按钮退出并恢复桌面 | ✅ | `verify-phase2`：右键一次给提示、右键双击退出；左键单击也直接退出（按使用反馈调整，原 PRD 要求左键无动作） |
| 8 | 悬浮按钮桌面可见时可点开抽屉；有普通窗口时被盖住 | ✅ | `verify-phase3`（打开）+ `final/42-button-covered.png`（最大化窗口覆盖时点击落到窗口，抽屉未打开） |
| 9 | 全局快捷键在普通窗口打开时临时呼出抽屉，抽屉置顶，关闭后恢复底层 | ✅ | `verify-phase4`：记事本前台 → Ctrl+Space → 抽屉出现并取得前台；`phase4/34` |
| 10 | 抽屉为全屏网格、可滚动、顶部搜索框、列数随宽度自适应、图标大小固定 | ✅ | `verify-phase3`：全屏 2560×1440、21 列、图标 58px、顶部搜索框；`verify-final`：把抽屉压到 720 高后滚动 0%→35.4% |
| 11 | 抽屉内容包含桌面与开始菜单应用，混排按名称排序 | ✅ | `verify-phase3`：168 项（桌面 67 + 开始菜单 101）、排序自检通过、名字唯一 |
| 12 | 搜索支持名称、文件名和路径，实时筛选 | ✅ | `verify-phase3`：`windows` → 102/168，结果均命中名称/路径 |
| 13 | 双击抽屉图标启动应用，抽屉立即关闭 | ✅ | `verify-phase3`：双击 Notepad → 进程启动 + 抽屉关闭 |
| 14 | 点击抽屉空白区域或按 Esc 关闭抽屉 | ✅ | `verify-phase3`：两种方式均通过 |
| 15 | 默认开机自启，设置中可关闭 | ✅ | `verify-phase4`：Run 键写入；本次额外用 UIA 切换设置页开关：关闭 → 键删除，开启 → 键写回 |
| 16 | 退出后原生桌面图标和壁纸正常显示，无残留 | ✅ | `phase1/07`、`final/45-desktop-restored.png`；进程与覆盖层窗口均已销毁，任务栏恢复 |

## 二、性能实测（最终构建）

| 指标 | 结果 |
|---|---|
| 冷启动到覆盖层可见 | **202 ms**（发布版同口径 203 ms；含进程启动） |
| 启动到自托管初始化完成 | 260–270 ms |
| 空闲 8 秒 CPU | **0%** |
| 空闲内存（未打开抽屉） | 私有 **72.6 MB** / 工作集 104.8 MB / 21 线程 |
| 打开抽屉后 | 私有 ~180 MB（全屏 WPF 窗口合成表面，见已知限制） |
| 抽屉打开耗时 | 35–190 ms（首次含目录扫描 157 ms + 图标预热 ~800 ms，均在后台） |
| 应用目录 | 168 项（扫描 157 ms） |

## 三、打包

```
build.ps1 -Action publish
  → dist\PhoneDesktop\
      PhoneDesktop.exe        152 KB
      PhoneDesktop.dll        110 KB
      PhoneDesktop.deps.json
      PhoneDesktop.runtimeconfig.json
```

- 框架依赖发布（需 .NET 8 Desktop Runtime，本机已装）；临时文件约 300 KB。
- 发布版实测：覆盖层 203 ms 出现、任务栏隐藏、退出后恢复、进程无残留。
- 运行发布版后，开机自启项会自动改指 `dist\PhoneDesktop\PhoneDesktop.exe`（应用用 `Environment.ProcessPath` 写 Run 键）。

## 四、已知限制（如实记录）

1. **全屏抽屉的内存**：空抽屉窗口本身约 +81 MB（DWM/WPF 对 2560×1440 全屏窗口的合成表面），加 168 个瓦片约 +110 MB，窗口销毁后系统不立即归还。空闲（不开抽屉）72.6 MB 达标。
2. **任务栏**：运行期间隐藏（退出自动恢复）；强制结束进程会留下隐藏态，用 `PhoneDesktop.exe --restore-taskbar` 恢复。
3. **任务管理器需要 UAC 确认**：系统行为，非工具缺陷。
4. **单显示器**：按需求只适配主屏。
5. **排序引擎差异**：应用内用 .NET 8（ICU）zh-CN 排序，PowerShell（NLS）结论不同，因此排序由应用自检输出结论。

## 五、截图

| 文件 | 内容 |
|---|---|
| `artifacts/shots/final/40-desktop.png` | 覆盖层盖住桌面（无任务栏） |
| `artifacts/shots/final/41-window-over-all.png` | 最大化窗口盖住覆盖层与 Dock |
| `artifacts/shots/final/42-button-covered.png` | 普通窗口覆盖时点击悬浮按钮位置无效 |
| `artifacts/shots/final/43-fullscreen.png` | 全屏窗口盖住覆盖层 |
| `artifacts/shots/final/44-drawer-scroll.png` | 抽屉内容溢出的滚动验证 |
| `artifacts/shots/final/45-desktop-restored.png` | 退出后桌面恢复 |
| `artifacts/shots/final/46-published.png` | 发布版运行效果 |

原始输出：`artifacts/verify-final-report.txt` 及各阶段报告。
