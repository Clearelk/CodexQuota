> Current release: the 2026-10-12 quota-card/local-usage update is verified in [quota-card-final-review.md](./quota-card-final-review.md). This document retains the original release's evidence and resource observations.

# 验证报告 — Codex 额度悬浮球

本报告由用户指定的顺序 Trellis CHECK worker（Codex）在 2026-10-11 至 2026-10-12 完成审查、修复和复测后更新，替代实施阶段的 112 项测试 / 32 项 UI 检查及旧资源结论。源码检查与可自动验证的行为已通过；物理鼠标、真实混合 DPI/热插拔等未验证范围见下文。未创建 Git 仓库、提交、推送或修改任务状态。

## 1. 最终版本与环境

| 项目 | 最终值 |
|---|---|
| 操作系统 | Windows 11，NT 10.0.26200，x64，24 个逻辑处理器 |
| .NET SDK / 官方 Codex CLI | 10.0.301 / codex-cli 0.144.1 |
| 运行包 | `artifacts/win-x64/CodexQuota.Desktop.exe`，自包含 win-x64；保留整个文件夹，大小 154.12 MiB |
| Desktop 模块 MVID | `d53780bf-3bd3-46bc-897f-9a5b8e9c04cd` |
| Desktop.dll SHA-256 | `26B91B32BB1B3C2D3765281C22A4F4CC9BF0921B301EC89524ADB0946A4E7254` |
| Core.dll SHA-256 | `3AC142386073D32D999EDCE3649E18480A01333F0B44BFC90A0A5BC97C05CB6C` |

最终渲染、事件、在线读取、托盘退出及资源探针报告均含上述 MVID；资源报告的两个 DLL 哈希与发布目录一致。构建与报告时间/哈希汇总见 `artifacts/verification/final-evidence.json`。旧 `desktop.png`、旧诊断和其他尝试目录保留作历史材料，不作为本版通过证据。

## 2. 新鲜执行结果

| 检查 | 结果 | 证据 |
|---|---|---|
| `scripts/verify.ps1` Release 构建 | 0 警告、0 错误 | `artifacts/verification/build-test-publish.log` |
| `dotnet test` | 136 通过，0 失败，0 跳过，约 11 秒 | 同一日志 |
| `dotnet format --verify-no-changes --no-restore` | exit 0 | 同一日志，随后 `verify: OK` |
| self-contained win-x64 发布 | 成功，最终运行包保留 | `artifacts/win-x64/` |
| 发布版 `--verify` | 41 项渲染/绑定检查、39 项真实窗口事件检查通过；最终版连续 3 轮 exit 0 | `desktop-verification.json`、`desktop-events.json`、`repeat-results.json`，均在 `artifacts/verification/` |
| 发布版 `--verify-live` | exit 0；官方账号只读获取及会话清理用时 6,558 ms | `artifacts/measurements/live-official-read.json` |
| 发布版 `--verify-exit` | exit 0；真实托盘退出时刷新正在进行；协调器/窗口/托盘已清理 | `artifacts/measurements/tray-exit.json` |
| 正常在线运行 135 + 30 秒 | 两次刷新、单实例、仅球窗口捕获、正常 WM_CLOSE 与助手树清理通过 | `artifacts/measurements/desktop-smoke.json` |

在线报告只保存脱敏的账号类型、套餐、额度字段与每日可用性，不输出邮箱、令牌、原始响应、全局配置或助手 stderr。当前官方每日桶未提供匹配本机 2026-10-12 的数据，结果是 `Missing` / `暂无官方数据`，没有冒充 0 Token。全局 codex 进程数读取前后均为 8，包含其他会话；助手清理结论使用本组件的祖先/后代跟踪，未要求或终止这些无关进程。

## 3. R1–R8 审查矩阵

| 要求 | 已检查、修复与验证的内容 | 验证范围 |
|---|---|---|
| R1 官方访问 | 五方法白名单；显式 `openai` / `https://chatgpt.com/backend-api/`；官方 app-server stdio；未登录/不支持账号/方法错误提示；无独立登录表单、HTTP 复刻、认证文件读取或推理调用 | 源码、协议/账号测试、真实只读获取；未改变真实登录状态 |
| R2 悬浮球 | 56 DIP、topmost、ShowInTaskbar=false；主桶 min remaining；辅助桶/积分不参与；30/10 边界；空/单时段/非法标量时段/非法映射/重置过期均保守处理 | 单元测试、绑定/渲染；本机实际 144 DPI 下 84×84 px、topmost，初始 (2452,1420) |
| R3 详情与交互 | 300 ms hover 取消/打开；球→面板过渡；250 ms 离开宽限；固定、再次点击、显式关闭抑制、Escape/Enter；拖拽不点击、不查询；拖拽后离开恢复 hover；时段、套餐、重置/刷新/成功时间分开 | 真实 App 组合、真实 WPF 窗口/XAML 路由事件与 DispatcherTimers；不是物理输入验证 |
| R4 每日 Token | 官方日期字符串精确匹配；缺失/0/负数/溢出；可选接口 unsupported、错误、超时隔离；午夜清除昨日值，跨日失败不沿用昨日统计 | 日期/提供者/调度测试、当前 live `Missing`；不猜官方时区 |
| R5 刷新与生命周期 | 启动立即读取；默认 120 秒；串行/合并手动请求；实际休眠调度响应间隔变更；有界 30/60/120/240/300 秒退避；旧数据标注 stale；每周期一会话与完整树清理；托盘各命令；窗口隐藏/退出；按用户 SID + 会话的单实例 | 协议/并发/取消/孤儿子进程测试、真实事件/托盘回调、在线两周期、第二实例退出与正常关闭 |
| R6 设置与定位 | 40–100% 透明度、30–900 秒间隔；校验、唯一临时文件、原子替换与串行写入；保存设置紧接拖拽不回退主题；位置保存 DPI 比例；物理坐标夹取；真实 startup 注册值写/读/删及原值恢复，默认关闭 | 单元测试、实际窗口保存/拖拽、HKCU restore-safe 集成、当前显示器 WM_DISPLAYCHANGE 夹取；真实混合 DPI/热插拔未验证 |
| R7 外观/可访问性 | 中文标签、浅/深/系统选择、键盘 Up/Enter/Escape、焦点样式；暗色 ComboBox 选中面与弹出项可读；已有窗口切换主题；详情滚动避免长内容被截断 | 当前最终 PNG 逐张目视检查；非完整 UIA/WCAG 或 OS 主题切换测试 |
| R8 交付与资源 | Core/provider/状态/存储/Windows/UI 分层；仅 Codex 注册；源码清单、脚本、官方依据、最终 release 和本报告；软件渲染减少常驻开销，保留原本的按需面板创建 | 66 个 source/tests/scripts/docs 文件清单及根构建配置检查；完整最终运行测量如下 |

## 4. 最终正常运行资源

运行的是上述发布版的正常在线路径，未用 `--verify` 面板或模拟 provider。命令：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/desktop-smoke.ps1 -StartupSeconds 135 -IdleSeconds 30 -ResourceProbe -AvoidPointerOverlap
```

脚本先记录实际右下角初始位置，再仅将本程序的球临时移至远离当前指针的工作区角落（最终 (24,24)）；没有移动指针、点击其他程序或保存新位置。6 秒探针是一次元数据查询，不强制 GC 或裁剪工作集。探针显示详情/设置均未创建；结束时所有本程序窗口标题中也没有详情/设置窗口。测量因此覆盖纯悬浮球常驻，避免把正常悬停打开面板的激活开销算作空闲。

| 观测 | 最终结果 |
|---|---|
| 两次刷新后的 helper-free 空闲 | 30 秒窗口内 17 个样本；工作集 120.32–120.64 MiB，均值 **120.41 MiB**；私有内存均值 **49.93 MiB** |
| 空闲 CPU / 助手 | 17 个区间的 CPU 增量均为 0；空闲助手样本数 0 |
| 组件工作集采样峰值 | 123.25 MiB |
| 助手树工作集采样峰值 | 53.68 MiB |
| 同时合并采样峰值 | 工作集 **174.91 MiB**；私有内存 **72.25 MiB** |
| 刷新活动 CPU 采样峰值 | **2.19548%**，按本机 24 个逻辑处理器归一化 |
| 助手生命周期 | 两个周期共观察到 4 个 PID；每周期为 Node 官方启动器 + 官方原生进程（2 个），共用一个官方会话；6 个活动样本，两个周期之间无残留 |
| 正常退出 | WM_CLOSE 成功，组件退出，所跟踪助手树剩余 0 |

硬件渲染历史诊断（两面板均未创建）约 196.59 MiB 工作集 / 125.69 MiB 私有内存，托管堆仅约 3.4 MiB；主要改善来自软件渲染对硬件渲染设备开销的削减。原代码已经延迟创建面板，未把这一点当作新增优化。最终仍可见显卡 DLL 的映射，未声称完全不加载驱动。WPF 常驻约 120 MiB 对小球仍偏大；没有静默改写框架或声称严格内存预算。

限制：这些是同机观察值，不是通用性能保证。工作集含共享映射页；私有字节单列。CIM 采样可能漏掉短峰值，助手退出前采到的 CPU 时间只是下限，不能据此推导精确总 CPU 时间。空闲零增量不保证任意机器或任意时刻为零。面板打开、字体与系统缓存会增加资源；`normal-probe/` 是较早面板曾打开的尝试，MVID 不同，已被当前干净测量替代。

## 5. 桌面行为的已验证与未验证边界

已验证实际窗口上的事件接线、真实 Dispatcher 计时器、生产拖拽坐标处理、设置保存、WinForms 托盘 MenuItem 回调、OS 窗口关闭处理、当前显示器的显示消息夹取，以及 HKCU 本组件 Run 值的实际写入/删除/精确引号与原值/类型恢复。启动项测试只修改 `CodexQuotaWidget`，末次注册值不存在，未重启。

未注入物理鼠标/键盘，没有点击其他程序；指针保持不变，事件工具保存并尝试恢复原前景窗口。因此没有声称验证了 OS 命中测试、实际拖动手感、跨窗口物理移动或 Explorer 托盘菜单的实际指针路径。真实混合 DPI 多屏移动、显示器热插拔、系统主题变更事件、Windows 10 实机、重启/重新登录后的自动运行均未验证。真实账号切换与在线故障未主动制造；用模拟账号/进程/超时测试覆盖保守缓存与清理行为。当前没有已知阻断源码问题；上述实机范围仍由 root/用户决定是否补充。

## 6. 可复现入口

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/verify.ps1
.\artifacts\win-x64\CodexQuota.Desktop.exe --verify artifacts/verification
.\artifacts\win-x64\CodexQuota.Desktop.exe --verify-live artifacts/measurements/live-official-read.json
.\artifacts\win-x64\CodexQuota.Desktop.exe --verify-exit artifacts/measurements/tray-exit.json
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/desktop-smoke.ps1 -StartupSeconds 135 -IdleSeconds 30 -ResourceProbe -AvoidPointerOverlap
```

`--verify` 会实际临时写/删本组件的 startup 值，在 finally 中恢复原值与类型。请先停止已有小组件再运行隔离桌面检查。检查发现、修复锚点和回归说明见同目录 `check-report.md`；源码清单见 `artifacts/verification/reviewed-file-inventory.txt`。
