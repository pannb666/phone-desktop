# 阶段 4 验收记录：Dock 管理 + 快捷键 + 设置页 + 自启

- 验收时间：2026-10-28 16:51
- 环境：Windows 10 21H2（19044）、单屏 2560×1440、任务栏运行期隐藏
- 自动验收：`tools/verify-phase4.ps1`（17 项全部 PASS）
- 截图：`artifacts/shots/phase4/`

## 一、从抽屉固定到 Dock

| 检查项 | 结果 |
|---|---|
| 抽屉瓦片右键弹出菜单 | PASS（菜单项「固定到 Dock」） |
| 点击后写入 Dock | PASS（Dock 5 → 6 项，`settings.json` 同步更新） |
| 重复固定同一应用 | 不产生重复项（`DockService.Add` 按路径判重） |

## 二、Dock 右键取消固定

| 检查项 | 结果 |
|---|---|
| Dock 项右键菜单 | PASS（「打开」/「取消固定」） |
| 取消固定 | PASS（6 → 5 项，`settings.json` 移除该路径） |

## 三、拖拽排序

| 检查项 | 结果 |
|---|---|
| 拖动最后一个图标到首位 | PASS（顺序实时变化，松手落库） |
| 重启后顺序保持 | PASS（命令提示符 → 文件资源管理器 → 记事本 → 画图 → 计算器） |

实现要点：4px 阈值区分点击与拖拽；拖拽中按游标位置实时 `Move`，松手调用一次 `Save()`。

## 四、设置页

| 检查项 | 结果 |
|---|---|
| Dock 齿轮按钮打开设置页 | PASS |
| 窗口尺寸 | PASS（620×640，无边框深色） |
| 内容 | 开机自启、全局快捷键（可改键/恢复默认/状态显示）、兜底色（8 个色板）、关于（版本/运行环境/配置与日志路径/打开文件夹） |
| Esc 关闭 | PASS |

## 五、全局快捷键

| 检查项 | 结果 |
|---|---|
| 默认 Ctrl+Space 注册 | PASS（本机注册成功，日文/中文输入法环境可能被占用，失败会有提示） |
| 普通窗口在前台时呼出抽屉 | PASS（记事本在前台 → Ctrl+Space → 抽屉出现） |
| 抽屉取得前台（可直接输入搜索） | PASS |
| 改键 | 设置页点击快捷键框后按新组合键；先注册新键成功再注销旧键，失败保持原键可用 |
| 注册失败提示 | 正常模式弹提示并可跳转设置；`--dev` 只记日志与轻提示（避免挡住自动化） |

## 六、开机自启

| 检查项 | 结果 |
|---|---|
| HKCU Run 写入 | PASS（`"…\PhoneDesktop.exe"`） |
| 与设置同步 | 启动时按 `RunAtStartup` 同步注册表；设置页勾选立即生效 |

## 七、回归

改动完成后重跑前三个阶段，全部通过：

| 阶段 | 结果 |
|---|---|
| 阶段 1 | 28 项 PASS / 0 FAIL |
| 阶段 2 | 20 项 PASS / 0 FAIL |
| 阶段 3 | 26 项 PASS / 0 FAIL |
| 阶段 4 | 17 项 PASS / 0 FAIL |

## 八、截图

| 文件 | 内容 |
|---|---|
| `artifacts/shots/phase4/30-drawer-pin-menu.png` | 抽屉瓦片右键「固定到 Dock」 |
| `artifacts/shots/phase4/31-dock-context.png` | Dock 项右键「取消固定」 |
| `artifacts/shots/phase4/32-dock-reordered.png` | 拖拽排序后的 Dock |
| `artifacts/shots/phase4/33-settings.png` | 设置页 |
| `artifacts/shots/phase4/34-hotkey-drawer.png` | Ctrl+Space 在记事本前台时呼出抽屉 |

## 九、实现备注

- 右键菜单是程序化 `IsOpen = true` 打开的，WPF 不会自动给键盘焦点；已显式聚焦第一项，键盘也能操作。
- 自动化验收用 **UI Automation** 直接 Invoke 菜单项（比按坐标点菜单稳定），拖拽与点击仍用鼠标事件。
- 快捷键解析要求至少一个修饰键（避免把裸字母注册成全局热键影响输入）。
- 测试钩子：`PHONEDESKTOP_HOTKEY` 环境变量可覆盖注册用的组合键（自动化验收避免与输入法冲突）。

原始输出：`artifacts/verify-phase4-report.txt`。
