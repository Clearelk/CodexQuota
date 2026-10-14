<div align="center">

# CodexQuota · Codex 额度查看与剩余额度监控悬浮球 (Desktop Quota Tracker)

<p align="center">
  <strong>Windows 桌面 Codex 额度查看与实时监控悬浮球 · 让 ChatGPT / Codex 订阅剩余额度常驻桌面</strong>
</p>
<p align="center">
  <em>Codex 额度查看 · 额度查询 · 5小时/7天额度监控 · 桌面悬浮球小组件 · 贴边胶囊 · 低额度预警</em>
</p>

[![Release](https://img.shields.io/github/v/release/Clearelk/CodexQuota?color=0078D4&label=Release)](https://github.com/Clearelk/CodexQuota/releases)
[![Platform](https://img.shields.io/badge/Platform-Windows%2010%20%2F%2011%20x64-blue.svg?logo=windows&logoColor=white)](https://github.com/Clearelk/CodexQuota)
[![Framework](https://img.shields.io/badge/.NET-10-512BD4.svg?logo=dotnet&logoColor=white)](https://dotnet.microsoft.com/)
[![UI](https://img.shields.io/badge/UI-WPF%20XAML-8A2BE2.svg)](https://github.com/Clearelk/CodexQuota)
[![License](https://img.shields.io/badge/License-MIT-green.svg)](LICENSE)
[![GitHub Stars](https://img.shields.io/github/stars/Clearelk/CodexQuota?style=social)](https://github.com/Clearelk/CodexQuota)

<p align="center">
  <a href="README.md">English</a> &nbsp;|&nbsp; <strong>简体中文</strong>
</p>

<p align="center">
  <img alt="CodexQuota 额度详情主界面" src="images/main.png" width="800" />
</p>

</div>

---

## 简介

**CodexQuota** 是一款专为 Windows 平台打造的轻量级 **Codex 额度查看、剩余额度监控与桌面悬浮小组件**。

在写代码或使用 AI 编程助手时，无需频繁在终端敲命令或切网页查询余额。CodexQuota 通过本机已登录的官方 Codex CLI 本地接口（`codex app-server` JSON-RPC），以纯本地、零侵入、安全只读的方式实时获取 **5 小时及 1 周订阅额度**，并将剩余百分比优雅地呈现在桌面悬浮球上，随时掌握 Codex / ChatGPT 订阅额度消耗情况。

- ⚡ **额度常驻桌面**：环形进度条配合三档电量色（绿/黄/红），剩余额度与消耗进度一目了然。
- 🔍 **悬停即看详情**：鼠标悬停即刻预览 5 小时/1 周额度、精确到秒的本地重置倒计时与今日用量。
- 🧲 **贴边智能收起**：拖动到屏幕左右外侧边缘自动吸附为微型额度胶囊，悬停平滑展开，不挡代码视野。
- 🎨 **外观与双语切换**：支持浅色/深色模式及跟随系统，内置中英双语（简体中文 / English）即时切换。
- 📊 **本地用量估算**：独立统计本机今日有效对话会话数，并在运行期间根据额度快照提供消耗趋势参考。
- 🔔 **低额度预警**：支持自定义电量阈值，触底时通过 Windows 系统横幅通知提醒。
- 🛡️ **安全与隐私**：零模型推理、零外部代理、不上传任何 Cookie/Token、不接触对话正文，纯本地进程通信。

> [!NOTE]
> 当前版本主要针对 **Codex 的 ChatGPT 订阅账号（Plus / Pro / Team）** 额度展示。第三方 API Key 计费与非 ChatGPT 登录模式不在本版本额度统计范围内。

---

## 目录

- [简介](#简介)
- [目录](#目录)
- [功能预览](#功能预览)
- [快速开始](#快速开始)
  - [前置条件](#前置条件)
  - [安装与运行](#安装与运行)
- [使用指南](#使用指南)
- [设置面板](#设置面板)
- [未来规划](#未来规划)
- [常见问题排查](#常见问题排查)
- [从源码构建](#从源码构建)
- [项目结构](#项目结构)
- [保持更新](#保持更新)
- [参与贡献](#参与贡献)
- [开源协议](#开源协议)
- [参考与致谢](#参考与致谢)

---

## 功能预览

### 1. 额度悬浮球
圆形进度环与百分比直观反映 5 小时时段的可用额度。支持全屏任意拖拽、多显示器跨屏移动与吸附。
- **67% 及以上**：绿色（额度充足）
- **34% – 66%**：黄色（中等用量）
- **低于 34%**：红色（额度告急）

<p align="center">
  <img alt="CodexQuota 悬浮球" src="images/floating-ball.png" width="300" />
</p>

### 2. 额度详情主界面
鼠标移至悬浮球悬停约 300ms 即可滑出详情面板，也可单击悬浮球固定面板。卡片清晰呈现 5 小时与 7 天主额度桶的已用/剩余情况、重置具体时间点及本地今日会话数统计。

<p align="center">
  <img alt="CodexQuota 额度详情主界面" src="images/main.png" width="450" />
</p>

### 3. 贴边收起（侧边胶囊形态）
当悬浮球被拖动到屏幕最左侧或最右侧并移出约 1/4 时，松手会自动收缩为精巧的侧边“额度胶囊”。胶囊内部以液位条显示剩余比例；鼠标悬停即刻平滑展开，拖回屏幕内部恢复球体。

<p align="center">
  <img alt="CodexQuota 侧边贴边胶囊形态" src="images/edge-docking.png" width="450" />
</p>

### 4. 设置面板（中英双语界面）
提供直观的分组卡片设置面板。支持界面语言即时切换（简体中文 / English），并可自由调节悬浮球尺寸、透明度、阈值预警及快速停靠预设。

<p align="center">
  <img alt="设置面板 · 中文" src="images/settings-zh.png" width="380" />
  &nbsp;&nbsp;&nbsp;&nbsp;
  <img alt="Settings · English" src="images/settings-en.png" width="380" />
</p>

---

## 快速开始

### 前置条件

| 检查项 | 建议与要求 |
|---|---|
| **操作系统** | Windows 10 / 11 (x64) |
| **官方 Codex CLI** | 本机已安装官方客户端，且已在命令行通过 ChatGPT 账号完成登录 |
| **.NET 运行时** | 自包含（Self-Contained）独立发布包已打包完整运行库，**普通用户无需单独安装 .NET** |

> 若本机尚未安装或登录 Codex CLI：
> 1. 参考官方指引安装 CLI：[OpenAI Codex CLI 指南](https://developers.openai.com/codex/cli)
> 2. 打开系统终端（PowerShell 或 CMD），运行 `codex`
> 3. 根据屏幕提示选择 **Sign in with ChatGPT** 并完成浏览器网页授权登录即可。

---

### 安装与运行

#### Windows

1. 从 [GitHub Releases](https://github.com/Clearelk/CodexQuota/releases) 下载最新版本的 `CodexQuota_v{version}_windows_x64.zip`。
2. 解压后双击运行 `CodexQuota.Desktop.exe` 即可（免安装绿色自包含版本，无需预装 .NET 运行时环境）。
3. 启动后，程序会自动探测本地官方客户端并完成初次刷新，悬浮球即刻出现在桌面角落！

> [!TIP]
> 悬浮球首次启动会自动探测并连接官方环境。本应用**不提供且不需要**独立的账号密码登录表单，只要你的系统终端能正常使用 `codex` 命令，悬浮球即可自动读取。

---

## 使用指南

### 交互操作一览

| 操作手势 / 动作 | 触发效果 |
|---|---|
| **鼠标悬停悬浮球** | 约 300 毫秒后自动弹出额度详情，光标离开面板后平滑隐退收起 |
| **单击悬浮球** | 固定保持详情面板开启状态；再次单击关闭 |
| **按住左键拖拽** | 自由移动悬浮球位置，支持多屏跨屏；松开自动记忆坐标；详情面板同步跟随 |
| **拖至屏幕左右边缘** | 将约 1/4 拖出屏幕外侧后松手，自动吸附收缩为“额度胶囊” |
| **悬停贴边胶囊** | 平滑展开为完整悬浮球；拖动胶囊向屏幕内移动即可解除贴边 |
| **右键悬浮球** | 呼出快捷上下文菜单（刷新数据 / 额度详情 / 首选项 / 退出） |
| **双击系统托盘图标** | 快速呼出/隐藏额度详情面板 |
| **右键托盘图标** | 托盘菜单（显示或隐藏悬浮球、设置、立即刷新、开机启动项等） |
| **键盘快捷键** | 焦点落在悬浮球时按 `Enter` 或 `空格` 可开关详情；按 `Esc` 快速关闭面板 |

---

## 设置面板

在详情面板右上角点击 ⚙️ 齿轮图标，或在悬浮球/托盘右键选择 **设置** 即可开启：

| 分类卡片 | 配置项 | 说明与可选范围 |
|---|---|---|
| **外观与语言** | 界面语言 | 支持 `简体中文`、`English`（即时动态切换全界面文本与提示） |
| | 主题风格 | 支持 `跟随系统`、`浅色模式`、`深色模式` |
| | 缩放比例 | `75%` 至 `150%`（默认 100%），球体尺寸、文字字号、圆环同比例无损缩放 |
| | 不透明度 | `40%` 至 `100%`（仅作用于悬浮球，不影响详情面板可读性） |
| **位置与停靠** | 快速停靠预设 | 一键切换至当前显示器的左上、右上、左下、右下角预设位置 |
| **刷新与提醒** | 数据刷新周期 | 可选 `30秒`、`1分钟`、`2分钟`（默认）、`5分钟`、`10分钟`、`15分钟` |
| | 低额度告警阈值 | 可选 `1%` – `100%`（默认 20%），5小时主额度低于阈值时触发状态高亮 |
| | Windows 气泡通知 | 低额度时通过系统通知横幅提醒，同一次低额度状态智能去重不轰炸 |
| **系统集成** | 开机自动启动 | 随当前 Windows 用户登录自动在后台静默启动 |

> 本地配置文件保存路径：
> ```text
> %LOCALAPPDATA%\CodexQuotaWidget\settings.json
> ```

---

## 未来规划 (Roadmap)

- 🚀 **兼容多 AI 服务商**：计划接入支持 Claude、Gemini、DeepSeek、Grok 等多种主流 AI 模型的订阅额度与 API 余额查看，打造一站式桌面 AI 额度监控中心。
- 📈 **用量历史与消耗走势**：支持周期性用量图表、历史会话 Token 消耗走势统计分析。
- 🔔 **更多告警渠道**：支持邮件、Webhook 机器人等多种低额度预警提醒方式。

---

## 常见问题排查

### Q1: 提示“未找到官方 Codex 客户端”？
1. 打开 PowerShell，运行 `codex --version` 验证 CLI 是否可在任意路径调用。
2. 若命令不存在，请先安装官方 Codex CLI。本程序支持自动检索系统环境变量 `PATH`、npm 全局路径及官方独立安装路径。
3. 如果是通过 npm 安装且使用脚本包装，请确保系统已安装 Node.js。

### Q2: 提示“未登录官方客户端”？
1. 在终端执行 `codex`，登录你的 ChatGPT 账号。
2. 登录成功后，右键悬浮球点击 **“立即刷新”** 即可。

### Q3: 悬浮球找不到了怎么唤回？
1. 在 Windows 任务栏右下角系统托盘找到 CodexQuota 图标。
2. 右键选择 **“显示悬浮球”**。
3. 若跨显示器导致漂移，可进入设置面板，点击 **“右上角”** 或 **“右下角”** 预设位置将其拉回主屏幕可视区域。

### Q4: 为什么没有收到低额度系统通知？
1. 请检查设置面板中 **“Windows 通知”** 开关是否开启并点击了保存。
2. 确认 Windows 系统的“专注助手 / 免打扰模式”是否屏蔽了应用通知。
3. 为避免打扰，程序针对同一段低额度只会弹出一次横幅提醒，直至额度刷新回升后才会重新武装。

---

## 从源码构建

本仓库基于 **.NET 10 (C# latest) + WPF** 编写，全平台测试与自包含发布环境推荐：

- Windows 10 / 11 x64
- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)

在仓库根目录下运行 PowerShell 构建与测试脚本：

```powershell
# 1. 运行完整验证管道（编译、单元测试、格式校验并发布自包含成品）
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/verify.ps1

# 2. 仅发布自包含 Windows x64 独立运行包
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/publish.ps1

# 3. 快速启动发布后的产物进行验证
.\artifacts\win-x64\CodexQuota.Desktop.exe
```

> 发布产物位于 `artifacts/win-x64/` 目录，生成的 `CodexQuota.Desktop.exe` 为独立单体免安装版本。

---

## 项目结构

```text
CodexQuota/
├── .github/
│   └── workflows/
│       └── ci.yml               # GitHub Actions CI 自动化构建与测试流水线
├── src/
│   ├── CodexQuota.Core/         # 核心层：官方 JSON-RPC 通信、额度解析、本地统计与设置持久化
│   └── CodexQuota.Desktop/      # 桌面层：WPF 悬浮球窗口、详情视图、设置界面与系统托盘
├── tests/
│   ├── CodexQuota.Tests/        # 核心逻辑与 UI 投影的回归自动化测试（300+ 测试用例）
│   └── CodexQuota.FakeServer/   # 传输层进程模拟服务，用于脱机与管道测试
├── scripts/
│   ├── publish.ps1              # 自包含 x64 发布脚本
│   ├── verify.ps1               # 编译、测试与代码格式全流程验证
│   └── desktop-smoke.ps1        # 桌面进程冒烟与内存采样测试
├── docs/                        # 官方协议解析依据、交互设计规范与技术文档
├── images/                      # README 预览截图与示意图资源
├── CHANGELOG.md                 # 版本更新日志
├── CONTRIBUTING.md              # 社区贡献指引
├── LICENSE                      # MIT 开源协议文本
├── README.md                    # 英文主说明文档（默认展示）
├── README.zh-CN.md              # 简体中文说明文档
└── CodexQuota.slnx              # Visual Studio 解决方案文件
```

---

## 保持更新

- 🌟 如果这个项目对你的日常编码有所帮助，欢迎在 GitHub 上点一个 **Star** 给予鼓励！
- 👁️ 点击页面右上角的 **Watch** 可以第一时间获悉新版本的发布通知。
- 🐛 如遇任何 Bug 或有新功能设想，非常欢迎在 [GitHub Issues](https://github.com/Clearelk/CodexQuota/issues) 提交反馈，反馈时请附带系统版本与操作步骤。

---

## 参与贡献

欢迎提交 Pull Request 参与改进！详细说明请参阅 [贡献指南](CONTRIBUTING.md)。在提交 PR 前，请确保运行以下命令使全部单元测试通过且代码格式规整：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/verify.ps1 -SkipPublish
```

---

## 开源协议

本项目采用 [MIT 许可证](LICENSE) 进行开源。你可以自由地使用、修改和分发本项目的代码。

---

## 参考与致谢

- [OpenAI Codex CLI](https://developers.openai.com/codex/cli)：官方客户端架构与 `app-server` 接口设计。
