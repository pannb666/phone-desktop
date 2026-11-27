# PhoneDesktop — Windows 10 手机风格桌面覆盖层

用一层不透明的桌面覆盖层替代原生桌面的视觉表现：桌面区域只有壁纸，底部常驻 Dock，应用收进全屏抽屉。
**不替换 Explorer、不改系统 Shell、不置顶、不检测全屏**。技术栈 .NET 8 + WPF，零第三方依赖。

## 当前进度

| 阶段 | 内容 | 状态 |
|---|---|---|
| 阶段 1 | 窗口层级（桌面之上、普通窗口之下）+ 壁纸绘制 | ✅ 通过验收（27 项） |
| 阶段 2 | Dock 基础（任务管理器/固定项/退出按钮/持久化） | ✅ 通过验收（20 项） |
| 阶段 3 | 应用抽屉（扫描、网格、搜索、启动） | ✅ 通过验收（26 项） |
| 阶段 4 | Dock 管理（固定/取消/拖拽）+ 全局热键 + 设置页 + 自启 | 待开始 |
| 阶段 5 | 全量验收、性能复测、打包发布 | ✅ 完成（PRD 16 条逐项通过） |

实测：冷启动到覆盖层可见 **202 ms**；空闲 CPU **0%**；空闲内存 **72.6 MB**（未打开抽屉时）；
打开全屏抽屉后约 **180 MB**，关闭后系统不归还（全屏合成表面成本，见"已知限制"）。

## 快速开始

```powershell
# 编译
.\build.ps1

# 运行（Dock 右端退出按钮：左键单击退出；右键双击同样可退出）
.\build.ps1 -Action run

# 调试模式（左上角角标 + Ctrl+Alt+Q 快速退出 + 输出 UI 位置探针）
.\build.ps1 -Action run-dev

# 优雅退出（任意模式可用）
.\build.ps1 -Action stop

# 发布
.\build.ps1 -Action publish
```

验收脚本：

```powershell
.\tools\verify.ps1             # 阶段 1：层级 / 输入 / 普通窗口 / 全屏窗口 / 资源 / 退出恢复
.\tools\verify-phase2.ps1      # 阶段 2：Dock 布局 / 任务管理器 / 退出按钮 / 持久化
.\tools\verify-phase3.ps1      # 阶段 3：悬浮按钮 / 抽屉内容 / 搜索 / 双击启动 / 关闭方式 / 置顶
.\tools\verify-phase4.ps1      # 阶段 4：固定 / 取消固定 / 拖拽排序 / 设置页 / 快捷键 / 自启
.\tools\verify-final.ps1       # 阶段 5：普通窗口盖住 Dock/悬浮按钮、抽屉滚动、退出恢复
.\tools\verify-running-dock.ps1 # 运行中的应用（高亮/最小化还原）+ 最大化铺满整屏
.\tools\test-wallpaper.ps1     # 壁纸主路径验证（临时换壁纸，结束自动恢复）
```

## Dock（阶段 2）

- 运行期间**隐藏 Windows 任务栏**，胶囊栏贴屏幕底部（18px），水平居中，深色半透明底 + 圆角 + 投影。
- **最左**：任务管理器按钮，左键单击启动 `taskmgr.exe`（Windows 会弹 UAC 提权框，属系统行为）。
- **中间**：固定应用，左键单击启动；首次运行写入 5 个默认项（资源管理器/记事本/画图/计算器/cmd），
  之后完全以 `settings.json` 为准（阶段 4 提供右键固定/取消固定与拖拽排序）。
- **最右**：退出按钮。左键单击无动作；右键单击显示"再右键一次退出"提示；**右键双击（≤500ms）退出并恢复原生桌面**。
- Dock 与覆盖层同层，普通窗口出现时自然被盖住；不自动隐藏、不置顶。

## 任务栏处理

手机风格桌面底部不该露着 Windows 任务栏，但任务栏（`Shell_TrayWnd`）是系统里最上层的 shell 窗口，
我们按需求"不修改系统 Shell"，所以**不改变它的任何设置**，只做两件事：

- **运行期间隐藏**：`ShowWindow(SW_HIDE)`；退出（正常退出 / 未处理异常 / 会话结束 / 进程退出）时 `SW_SHOW` 恢复；
- **崩溃兜底**：万一被强杀导致任务栏留在隐藏状态，运行 `PhoneDesktop.exe --restore-taskbar` 即可恢复。

隐藏后 Dock 与右下角悬浮按钮会自动贴到屏幕底部（不再按工作区留出任务栏高度）。
任务栏隐藏不影响 Alt+Tab、开始菜单等其它系统功能。

## 应用抽屉（阶段 3）

- **入口**：右下角悬浮按钮（Dock 上方）。全局快捷键在阶段 4 加入。
- **形态**：独立全屏窗口，`WindowStyle=None` + 不透明背景；仅在显示期间 `Topmost`，关闭即销毁。
- **内容**：用户桌面 + 公共桌面（快捷方式/文件/文件夹）+ 开始菜单 Programs 目录下的 `.lnk/.url`，
  按"目标路径 + 参数 / 名称 + 目标文件名"去重、并对同名不同文件消歧后，按名称（zh-CN 排序）混排；本机实测 168 项（桌面 67 + 开始菜单 101）。
- **网格**：`WrapPanel` 自适应列数（2560 宽下 21 列），图标固定 58px，一屏可容纳全部条目。
- **搜索**：顶部搜索框，实时筛选，匹配名称、文件名、完整路径与快捷方式目标路径。
- **启动**：双击图标 → 立即关闭抽屉 → ShellExecute 启动（.lnk 保留参数/工作目录）。
- **关闭**：Esc、点击空白区域、双击启动后自动关闭。
- **懒加载**：目录扫描与图标预热推迟到首次打开抽屉（157 ms 扫描 + ~800 ms 预热，后台线程），
  不打开抽屉时进程保持低内存。

## Dock 管理（阶段 4）

- **固定**：抽屉里右键任意应用 →「固定到 Dock」；已固定则菜单项置灰，按路径判重不会重复。
- **取消固定**：Dock 项右键 →「取消固定」；同一菜单还有「打开」。
- **拖拽排序**：按住图标拖动（4px 阈值区分点击），松手落库；重启后顺序保持。
- **设置入口**：Dock 右端齿轮按钮，或 Dock 空白处右键 →「设置…」。
- 所有变更立即写入 `settings.json`。

## Dock 外观与系统托盘（2026-10-28 新增）

- Dock 改为**半透明**（`#8A0E0F14`），壁纸会透出来。
- 托盘区位于退出按钮左侧：Wi-Fi（连接状态用亮度表示，点击打开网络列表）、蓝牙（打开蓝牙设置）、
  音量（点击静音）、输入法（显示"中/EN"，点击切换语言）、时间与日期（每 15 秒刷新）。
- "软件状态"由 Dock 左侧的"运行中的应用"区承担（切换/最小化/关闭）。

## 深色右键菜单与悬停提示

- 右键菜单（Dock 项、运行项、抽屉瓦片、Dock 空白处）用自绘模板：深色圆角、无系统白色底板。
  > 之前默认 `ContextMenu` 模板会在左侧留一条白色底板，右键时看起来像"白框"。
- 悬停提示 `ToolTip` 也改为深色（默认是系统白底），避免右键/悬停时在图标旁出现白色方块。

## 运行中的应用（任务栏语义，2026-10-28 新增）

- Dock 右侧多出一段"运行中的应用"：每个应用一个图标，来自 `EnumWindows` 的可见顶层窗口（按 exe 归组）。
- **当前窗口高亮**（浅色底 + 亮色状态点）；**最小化的应用图标变淡**，点击即恢复。
- 点击语义与任务栏一致：最小化 → 恢复并激活；前台 → 最小化；其它 → 切换到前台。
- 右键菜单：切换到该窗口 / 最小化 / 关闭窗口。
- 每 900 ms 轮询一次（空闲 CPU 实测 0.06%，私有内存 81.6 MB）。

## 工作区铺满（消除"任务栏位置空白条"）

隐藏任务栏后，Windows 工作区仍是 2560×1400，最大化窗口会在底部留下 40px 空白。
工具运行期把工作区扩到整屏（`SPI_SETWORKAREA`，不广播 `WM_SETTINGCHANGE`），退出时按任务栏实际位置恢复。
代价：最大化窗口会盖住 Dock（与"窗口始终盖住覆盖层"一致），用 Win+D / Win+Tab 或最小化回到桌面。

## 快捷键与设置页（阶段 4）

- **默认 Ctrl+Space** 呼出抽屉（普通窗口在前台时也可用，抽屉临时置顶并取得前台）。
- 可改键：设置页点击快捷键框 → 按下新组合键（Esc 取消）。改键时**先注册新键成功再注销旧键**，失败保留原键。
- 注册失败：正常模式弹提示并可跳转设置；`--dev` 只记日志 + 轻提示（避免挡住自动化验收）。
- 设置页：开机自启、全局快捷键、壁纸兜底色（8 色板）、关于（版本/运行环境/配置与日志路径）。
- 开机自启：`HKCU\Software\Microsoft\Windows\CurrentVersion\Run`，启动时按设置同步。

## 窗口层级方案（阶段 1 的核心）

```
z 序高 ┌────────────────────────────────────┐
       │ 普通窗口 / 全屏游戏 / 任务栏          │  ← 任意顶层窗口
       ├────────────────────────────────────┤
       │ 应用抽屉（阶段 3，临时 Topmost）      │
       ├────────────────────────────────────┤
       │ Progman（桌面窗口）                  │
       │   ├ 覆盖层（WS_CHILD）◀ 壁纸 + Dock   │
       │   └ SHELLDLL_DefView（桌面图标）      │
z 序低 └────────────────────────────────────┘
```

- 覆盖层用 `HwndSource(ParentWindow = Progman, WS_CHILD)` **直接创建为桌面子窗口**，不经过顶层窗口阶段。
- 创建后 `SetWindowPos(HWND_TOP | SWP_SHOWWINDOW)` 排到 `SHELLDLL_DefView` 之前，从而盖住桌面图标。
- 层级由父窗口模型**结构性保证**：覆盖层不设 `WS_EX_TOPMOST`、不调用置顶、不做全屏检测。
- 先隐藏创建再置顶显示，避免"已可见但仍排在图标层之下"的瞬间。
- 1 秒守护：父窗口失效（Explorer 重启）或 z 序被改时自动重建/修复。实测 Explorer 重启后数秒内恢复。
- **跟随图标层宿主**：Explorer 在"显示桌面"时会把 SHELLDLL_DefView 从 Progman 挪到 WorkerW（且不带外部子窗口），
  覆盖层会检测并 `SetParent` 跟过去后重新置顶，避免图标盖住覆盖层。

## 目录结构

```
phone-desktop/
├─ PhoneDesktop.sln
├─ build.ps1                     # 编译 / 运行 / 停止 / 发布
├─ src/PhoneDesktop/
│  ├─ PhoneDesktop.csproj        # net8.0-windows / WPF / 无第三方包
│  ├─ app.manifest               # PerMonitorV2 DPI
│  ├─ App.xaml(.cs)              # 启动编排、单实例、退出信号
│  ├─ Core/                      # 日志、单实例、MVVM 基类、运行时开关
│  ├─ Models/                    # 持久化模型 + 抽屉条目模型
│  ├─ Interop/
│  │  ├─ NativeMethods.cs
│  │  ├─ DesktopHost.cs          # Progman 定位 + z 序维护
│  │  ├─ HotkeyWindow.cs         # 全局热键宿主（阶段 4 复用）
│  │  ├─ ShellIcon.cs            # IShellItemImageFactory 图标提取
│  │  ├─ ShellLink.cs            # .lnk 目标/参数解析
│  │  └─ ShellLauncher.cs        # ShellExecute 启动
│  ├─ Services/
│  │  ├─ SettingsService.cs
│  │  ├─ DockService.cs          # Dock 列表 + 首次运行默认项
│  │  ├─ AppCatalog.cs           # 桌面 + 开始菜单扫描、去重、排序
│  │  ├─ IconCache.cs / IconWarmup.cs
│  │  └─ WallpaperService.cs     # SPI + 注册表 + 桌面背景色兜底
│  ├─ Overlay/
│  │  ├─ OverlayHost.cs          # 覆盖层宿主与守护、抽屉开关
│  │  ├─ OverlayView.xaml(.cs)   # 壁纸 + Dock + 悬浮按钮
│  │  └─ Dock/                   # DockView / ViewModel / 位置探针
│  ├─ Drawer/                    # DrawerWindow / ViewModel / 条目模板
│  └─ Themes/
├─ tools/
│  ├─ verify.ps1                 # 阶段 1 自动验收
│  ├─ verify-phase2.ps1          # 阶段 2 自动验收
│  ├─ verify-phase3.ps1          # 阶段 3 自动验收
│  ├─ test-wallpaper.ps1
│  └─ fullscreen-test.ps1
├─ docs/acceptance-phase1.md
├─ docs/acceptance-phase2.md
├─ docs/acceptance-phase3.md
├─ docs/acceptance-phase4.md
├─ docs/acceptance-final.md      # PRD 第 7 节逐条验收
├─ dist/PhoneDesktop/            # 发布产物（不入库）
└─ artifacts/                    # 截图与报告（不入库）
```

## 发布

```powershell
.\build.ps1 -Action publish
```

产物在 `dist\PhoneDesktop\`（框架依赖，约 300 KB，需要 .NET 8 Desktop Runtime）。
直接运行 `dist\PhoneDesktop\PhoneDesktop.exe` 即可；运行后开机自启会自动改指该路径。

### 桌面启动器

```powershell
.\tools\create-desktop-shortcut.ps1          # 在原生桌面创建「手机桌面」快捷方式（指向发布产物）
.\tools\create-desktop-shortcut.ps1 -Name 'PhoneDesktop'
```

- 快捷方式指向 `dist\PhoneDesktop\PhoneDesktop.exe`，图标使用 exe 内嵌的应用图标（深色圆角底 + 黄色壁纸 + 底部 Dock）。
- 双击即可启动；重新发布后无需再建（路径不变），若换了发布目录重新运行脚本即可。

## 运行期路径

- 配置：`%APPDATA%\PhoneDesktop\settings.json`
- 日志：`%APPDATA%\PhoneDesktop\log.txt`
- 调试用 UI 位置快照（仅 `--dev`）：`dev-ui.json`（Dock/悬浮按钮）、`dev-drawer.json`（抽屉）
- 截图/报告：`artifacts/`

## 调试与测试钩子

| 钩子 | 用途 |
|---|---|
| `--dev` | 显示左上角角标、注册 Ctrl+Alt+Q、写出 UI 位置探针 |
| `--exit` | 通知已运行实例优雅退出（验收脚本用） |
| `--restore-taskbar` | 恢复被隐藏的 Windows 任务栏（强杀后的兜底） |
| `PHONEDESKTOP_TASKMGR` 环境变量 | 替换任务管理器按钮的命令，自动化验收可避开 UAC 弹窗 |
| `PHONEDESKTOP_DRAWER_FILTER` 环境变量 | 首次打开抽屉时预置筛选词（调试空状态/排障用） |
| `PHONEDESKTOP_HOTKEY` 环境变量 | 覆盖注册用的全局快捷键（如 `Control+Alt+F10`），自动化验收避免与输入法冲突 |

## 已知限制

- **全屏抽屉的内存**：本机实测空抽屉窗口本身就要 +81 MB（DWM/WPF 对 2560×1440 全屏窗口的合成表面），
  加 168 个瓦片约 +110 MB，且窗口销毁后系统不立即归还。空闲（不开抽屉）72.6 MB 达标；
  抽屉打开后的峰值与成分见 `docs/acceptance-phase3.md`。软件渲染实测更差（194 MB），未采用。
- **置顶窗口不能反复 Show**：同一置顶窗口 `Hide()`→`Show()` 在本机会阻塞约 31 秒，已改为每次重建窗口。
- **任务管理器需要 UAC 提权**：点击后弹系统确认框，属平台行为。
- **单显示器**：按需求只适配主屏。
- **任务栏**：运行期间隐藏（退出自动恢复）；强制结束进程会留下隐藏态，用 `--restore-taskbar` 恢复。

## 说明

- 本仓库脚本（`.ps1`）统一保存为 **UTF-8 with BOM**：Windows PowerShell 5.1 对无 BOM 的 UTF-8 会按 GBK 解码，中文注释可能吞掉后续代码行。
