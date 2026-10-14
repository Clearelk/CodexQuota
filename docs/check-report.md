# Sequential CHECK report

- Active task: `.trellis/tasks/10-11-codex-quota-widget`
- Checker: 用户明确指定的 Codex，顺序 CHECK 角色，2026-10-11–12。
- 审查完成，直接修复后复测；未重新规划/选 runner、派生代理、初始化 Git、提交/推送/合并/归档或改变任务状态。
- 结论：已知阻断源码问题已修复，最终构建、测试、格式、发布、桌面事件、官方只读与资源/退出检查通过。真实混合 DPI/热插拔与物理输入等范围没有被伪装成已验证，见第 5 节。

## 1. 审查范围

首先读取 `.trellis/agents/check.md`，再加载 supplied absolute task 下的 task.json、prd.md、design.md、implement.md、check.jsonl、其全部引用资料、verification.md 和 research/root-review.md。按 CHECK 角色直接工作。工作区无 Git，因此检查新建源码、测试、脚本、文档和根构建配置；66 个 source/tests/scripts/docs 文件清单在 `artifacts/verification/reviewed-file-inventory.txt`。保留 root 的任务、研究与 spec 修改，只更新本人负责的应用材料及本报告/verification.md。

R1–R8 的逐项验证矩阵在 `verification.md`。下面列出实际发现与修复，不把单元状态机或旧二进制报告当作真实桌面验证。

## 2. 发现、修复与文件锚点

| 发现 | 修复与证据 | 文件锚点（workspace 相对路径） |
|---|---|---|
| 暗色主题选择器白底近白字；自定义模板初版又暴露 ThemeOption record 文本 | 控制选中区域、弹出项与焦点的完整模板，显式 Label ItemTemplate；检查选中表面/字体/弹出背景、绑定及键盘 Up；最终暗/浅设置和暗色弹出 PNG 目视可读 | `src/CodexQuota.Desktop/Resources/Theme.xaml:116`、`Views/SettingsWindow.xaml:49`、`Services/VerificationHarness.cs:157` |
| 非对象时段/非法 codex 映射可被静默跳过，造成更有利的剩余值；无 ID legacy 与辅助映射并存时主桶丢失 | 返回的非法主时段/主桶标记 invalid 并拒绝整次主额度；映射键权威；合法 legacy 缺 ID 插入时归一为 codex；辅助桶与积分不参加 min remaining | `src/CodexQuota.Core/Providers/CodexResponseDecoder.cs:53`、`:64`、`:193`、`Services/QuotaProjection.cs:60` |
| 官方账号 schema 可能只有邮箱，没有旧实现期待的 ID；新账号读取后额度失败仍可保留前账号 | 官方账号字段仅在内存中作 hash；新增立即失效事件，账号变化/登出/不支持账号清除 quota/daily/last success；刷新期间 account/updated 拒绝混合账号结果；账号套餐可供缺失 quota plan 回退 | `Providers/CodexResponseDecoder.cs:142`、`Providers/CodexQuotaProvider.cs:82`、`:104`、`Services/RefreshCoordinator.cs:355` |
| ReadLine 在换行前无长度边界，stderr 也可能大分配；写管道不在完整请求 deadline 内 | 4K 字符块读取，逐字符限制帧；无换行超长帧即失败；stderr 固定块丢弃；写入和响应共用 deadline；安全异常分类不被重复包装 | `src/CodexQuota.Core/Transport/JsonRpcProcessClient.cs:125`、`:194`、`:243` |
| 正常父进程退出可遗留子进程；只依赖存活父进程的 Kill(true) 不够 | Windows Job Object KILL_ON_JOB_CLOSE；启动后立即 assign；dispose 无论父进程是否已退出都终止 owned job；成功/失败/取消及正常父退出遗留子进程测试 | `Transport/WindowsProcessJob.cs:21`、`Transport/ITransportProcess.cs:45`、`Transport/JsonRpcProcessClient.cs:450`、`tests/CodexQuota.Tests/TransportTests.cs:200` |
| 更新 interval 仅改显示，实际旧 sleep 不醒；手动刷新没有重置实际周期；成功时间按开始记录；昨日用量/过期 reset 可滞留 | 单次 wake semaphore 驱动实际睡眠，手动/设置变更重新调度；完成时间用于 last success/next fetch；午夜单次期限清空昨日；reset 到期标 stale 并安排读取，不推断 100%；dispose 取消并等待手动/周期刷新 | `Services/RefreshCoordinator.cs:56`、`:174`、`:202`、`:248`、`:369` |
| 设置固定临时文件并发冲突；保存设置紧接拖拽会把新主题/透明度覆盖回旧值 | Store gate、唯一临时文件、校验后原子替换并清理取消残留；App 层串行设置/拖拽更新，成功写盘后提交候选设置；真实 SaveButton 后立即拖拽回归验证 | `Services/SettingsStore.cs:86`、`src/CodexQuota.Desktop/App.xaml.cs:314`、`Services/DesktopEventVerification.cs:126` |
| 未配对 mouse-up 可当点击；丢 capture 后状态未清；预览转固定不更新激活；OS details Close 未清 pin；拖拽结束的 hover 竞态 | paired press/release、丢 capture 清理、DPI 阈值及生产物理拖拽方法；已显示预览仍通知 pin 更新；实际 Closing hide/unpin；拖拽开始/完成取消 hover，离开球后才恢复预览 | `Views/FloatingBallWindow.xaml.cs:70`、`:90`、`:112`、`:136`、`src/CodexQuota.Core/Services/InteractionController.cs:57`、`:125`、`:138`、`App.xaml.cs:235` |
| 各显示器原点分别除 DPI 会构造错误的全局 DIP 平面；显示/DPI/工作区变化后未重新夹取 | 运行时原生物理坐标定位、当前窗口 DPI、保存 scale；当前工作区夹取与相邻面板位置；监听 WM_DISPLAYCHANGE/DPICHANGED/SETTINGCHANGE；当前屏消息/越界夹取通过 | `Services/MonitorService.cs:21`、`:31`、`:43`、`Views/FloatingBallWindow.xaml.cs:34`、`Models/WidgetSettings.cs:19` |
| single-instance 名称未真正区分用户；每次状态刷新重复创建托盘图标，菜单释放不完整 | Local namespace + Windows SID mutex；仅 severity 变化替换图标；释放 context menu；实际第二实例自动退出、各托盘回调与退出路径通过 | `Services/WindowsIntegration.cs:75`、`Services/TrayService.cs:96`、`:113`、`App.xaml.cs:444` |
| 开机开关失败分支通知属性不准确；真实注册集成不足 | 失败分支 RaiseAll；按注册表真实值反映 disable 失败；真实 CheckBox 失败启用回退；读/写/删除本组件 Run 值，验证带引号 exe 路径，并 finally 精确恢复原值/类型 | `ViewModels/SettingsViewModel.cs:199`、`Services/WindowsIntegration.cs:17`、`:44`、`Services/DesktopEventVerification.cs:205` |
| 小球常驻 ~193 MiB / private ~123 MiB；已有面板延迟创建不能算新优化 | 诊断显示托管堆仅 ~3.4 MiB，硬件渲染设备开销明显；采用 SoftwareOnly 保留 WPF/全部行为；减少重复托盘分配；最终纯球 120.41 MiB WS / 49.93 MiB private；不使用强制 GC、EmptyWorkingSet 或换框架 | `App.xaml.cs:51`、`:78`、`Services/TrayService.cs:96`；最终资源报告 |
| 过期截图是在绑定更新前捕获的前一状态；测试报告失败仍可能 exit 0；部分协议坏帧 fixture 在握手阶段竞态 | 实际显示面板并等待 WPF binding/render，再断言画面含 expired 与保留 46%；报告失败抛出并 exit 1；拆分初始化坏帧/账号读取坏帧与退出阶段；所有最终证据记录 MVID/哈希 | `Services/VerificationHarness.cs:122`、`:210`、`tests/CodexQuota.FakeServer/Program.cs`、`tests/CodexQuota.Tests/TransportTests.cs:152` |

上表省略目录的 Core 条目均相对于 `src/CodexQuota.Core/`，Desktop 条目相对于 `src/CodexQuota.Desktop/`。主球的保守规则仍只有 QuotaProjection 一个 owner；decoder 是官方字段的单一解码边界。

协议安全审查锚点：`Transport/JsonRpcProcessClient.cs:25` 的五方法白名单、`Transport/CodexLaunchSpec.cs:16` 的 official provider/backend 参数、`Transport/CodexExecutableResolver.cs:98` 的显式官方路径限制。生产无独立 HTTP 客户端、WebView、认证文件读、推理/登录/登出/兑换/邮件调用。服务端请求只返回 unsupported，不执行它。账号邮箱只在官方 account/read 结果中参与内存 hash，未记录或持久化；助手 stderr 始终丢弃。

## 3. 回归与真实事件证据

追加回归覆盖非法时段/映射、legacy fallback、email-only 身份、登出/变更后额度失败、账号通知、午夜失败、完成时间、并发原子保存、每日超时隔离、实际调度唤醒及取消释放。最初审查回归确实先复现失败，再修复；例如拖拽释放 hover 测试 `HoverEventsAroundDragReleaseCannotReopenThePreview` 明确 1 failed（Actual true），修复后 13 项交互测试通过。真实 SaveButton 后立即 drag 也捕获过设置回退，App gate 修复后通过。

真实窗口工具使用正常 App 的组合根、真实 WPF 窗口、XAML routed events、DispatcherTimers、生产 ApplyDragPosition、真实 WinForms MenuItem.PerformClick，以及隔离的 settings root/provider。它测了 110 ms 未打开、300 ms 前离开取消、410 ms 打开、球到面板 80 ms 过渡、100 ms 留存/340 ms 关闭、pin 留存/再次点击、显式 close 抑制、Escape/Enter、未配对 up、拖拽后无 preview/click、离开后恢复 hover、设置保存、托盘详情/设置/隐藏/显示/刷新、OS details Close unpin、当前显示消息夹取和 startup restore。hover/click/drag 的 provider read count 不增加。

Startup 集成保留既有 Run 值及 RegistryValueKind，实际写入最终 executable 的精确引号命令，再删除，finally 原样恢复；只动 `CodexQuotaWidget`。工具不产生物理输入或其他应用点击，指针不动，保存并尝试恢复原前景窗口。路由事件验证并不等同真实 OS 输入/命中测试。

## 4. 最终精确结果与资源

- 最终 Release 构建：0 warnings / 0 errors；136 tests passed，0 failed / skipped，约 11 秒；format verify exit 0；self-contained publish 成功，文件夹 154.12 MiB。
- 最终 `--verify` 连续 3 轮：每轮 41 render/binding + 39 real-event checks 全通过，exit 0。暗/浅设置、暗色下拉、fresh/stale 详情与球 PNG 已从本版生成并目视复核。
- 最终只读 `--verify-live`：成功，6,558 ms；每日桶 Missing，未虚构 Token；全局 codex before/after 都为 8，未动无关会话。
- 最终 `--verify-exit`：真实托盘 Exit invoked，refreshWasInProgress=true，coordinatorDisposed/windowsClosed/trayDisposed=true，exit 0。
- 最终正常在线两周期 + 30 秒纯球空闲：17 idle samples，WS 120.32–120.64 MiB / avg 120.41 MiB，private avg 49.93 MiB；CPU 区间增量均 0，helper-free。
- 刷新采样峰值：widget WS 123.25 MiB；helper tree WS 53.68 MiB；同时 combined WS 174.91 MiB、private 72.25 MiB；24 核机器归一化 CPU sampled peak 2.19548%。
- 两周期 observed PID 4（每周期 Node + native 2 个，但一会话）；初始 84×84 px / 144 DPI / topmost / (2452,1420)；第二实例退出、正常 WM_CLOSE 成功，owned helpers after exit=0。

最终 normal measurement 临时把本程序球移到远离当前指针的角落，不改指针或持久位置，6 秒探针与末次 owned HWND titles 均确认无详情/设置。较早 normal-probe 因实际 hover 打开面板而有激活峰值，且其 MVID 已旧，不作最终 idle 结论。另移除了资源探针对 RenderCapability.Tier 的非必要查询，避免图形能力查询影响测量；最终仍有显卡 DLL 映射，不能声称完全不加载驱动。硬件渲染历史诊断 196.59 / 125.69 MiB 与最终明显不同，但不是跨机器基准或保证。

测量没有强制 GC、裁剪/页面换出或只挑 private 代替 WS。CIM 采样会漏短峰值，退出前所采 helper CPU 是下限；idle 0 是本次区间计数，不是零 CPU 保证。WPF 约 120 MiB 常驻工作集仍是此框架的实际限制，未静默换框架。

最终 MVID `d53780bf-3bd3-46bc-897f-9a5b8e9c04cd`；Desktop.dll SHA256 `26B91B32BB1B3C2D3765281C22A4F4CC9BF0921B301EC89524ADB0946A4E7254`；Core.dll `3AC142386073D32D999EDCE3649E18480A01333F0B44BFC90A0A5BC97C05CB6C`。所有最终报告关联此版本，见 `artifacts/verification/final-evidence.json`。`verification.md` 包含完整可复现命令和证据路径。

## 5. 剩余实机范围与交接

没有已知阻断源码问题。未验证真实混合 DPI 多屏拖动、OS 热插拔、系统主题变更事件、Windows 10 实机、物理 pointer/keyboard/Explorer 托盘路径、完整 UIA/WCAG 审计、重新登录后的实际 Run 启动。没有主动登录/登出或切换真实账号，也未人为破坏真实网络；相关安全缓存、超时、退出与恢复路径用 fixture 和实际窗口事件验证。Job 在 Process.Start 后立即 assign，没有声称原子防止任意恶意启动瞬间脱离。

最终 release 保留可运行；结束时小组件进程 0、owned helper 0、本组件 startup 值不存在且先前状态恢复。任务/spec/归档/提交与后续实机验收交由 root，不在 CHECK worker 内更改。
