# 任务栏避让参考实现（issue #12）

本分支是 [issue #12](https://github.com/evren-oct/vigil-watt/issues/12) 讨论的"占位检测 + 空隙落位"方案的参考实现，**不是合并候选**——按 issue 里的分工，按钮区动态调整部分暂缓，这里只提供已验证的代码供后续实现时参考。

## 模块地图

| 文件 | 内容 | 状态 |
|---|---|---|
| `src/VigilWatt.App/Services/TaskbarOccupancy.cs` | **占位枚举**：收集 `Shell_TrayWnd` 与 `ReBarWindow32` 直接子窗口（第三方工具挂在这两层），排除系统部件与自身，合并成占用区间 | #12 认可后续评估的方向 |
| `src/VigilWatt.App/Services/TaskbarLayout.cs` | **空隙落位纯函数**：占用区间与"任务栏左端→托盘左边"求差集；默认位贴托盘 → 被占走最靠右能放下的空隙 → 都放不下逐档减少显示台数。纯函数无副作用，单测全在 `TaskbarLayoutTests.cs` | 同上 |
| `src/VigilWatt.App/Services/TaskbarPillWindow.cs` | **接线层**：把上面两个模块接进贴片窗口，**包含本次暂缓的按钮区限长状态机**（收窄/部分放宽/完全还原/等于现状不动 四态）。评估避让时不需要动这一部分的限长逻辑 | **暂缓部分**，本分支仅供阅读 |
| `src/tests/VigilWatt.Core.Tests/TaskbarLayoutTests.cs` | 布局纯函数单测：空隙合并、默认位回归、最右空隙、台数收窄等 | — |

## 验证状态

- 基于 1.2.2（`46efacb`）：`dotnet build` 0 警告 0 错误；`VigilWatt.Core.Tests` **509 项全部通过**（2026-10-21）。
- 实机（Win10 22H2，2560×1440 @175%）：与 TrafficMonitor 1.86、BatteryBar DeskBand、系统"资讯和兴趣"同机共存，贴片与所有第三方窗口零重叠；跨设备增减、显示器插拔保持稳定。
- 已知教训（详见 #12 正文）：两个工具同时收窄任务列表会振荡（一秒内三次跳位）——因此 TM 在场时只避让不收窄；限长状态机缺"部分放宽"或"等于现状不动"任何一态都会产生秒级抖动。

## 与 #26 的关系

#26 的手动偏移解决"我现在就想挪开"，本方案解决"以后不用手动管"。二者可并存：偏移给用户自由，占位检测保证不重叠。
