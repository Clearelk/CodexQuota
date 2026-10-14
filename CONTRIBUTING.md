# 贡献指南 (Contributing)

感谢你对 **CodexQuota** 项目的关注与支持！我们欢迎各种形式的贡献，包括但不限于提交 Bug 报告、提出改进建议、完善文档以及提交代码（Pull Request）。

---

## 1. 提交 Issue 反馈

在提交 Issue 前，请先查阅已有 Issue 与 [README.md 常见问题排查](README.md#常见问题排查)，确认该问题是否已被提出或解答。

提交时建议包含以下信息：
- **运行环境**：Windows 版本（如 Windows 11 23H2 x64）与官方 Codex CLI 版本（`codex --version`）
- **问题描述**：清晰描述发生的问题，附带复现步骤
- **预期行为与实际行为**：说明原本期望的效果与实际发生的情况
- **截图或日志**：相关界面截图或控制台报错信息（注意遮挡个人敏感信息）

---

## 2. 本地开发与代码贡献

### 环境准备
- Windows 10 / 11 x64
- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)
- Visual Studio 2022+ 或 VS Code（配合 C# Dev Kit）

### 开发流程
1. **Fork** 本仓库到你的 GitHub 账号，并克隆到本地：
   ```bash
   git clone https://github.com/<your-username>/CodexQuota.git
   cd CodexQuota
   ```
2. 创建新的功能分支：
   ```bash
   git checkout -b feature/your-feature-name
   ```
3. 在本地进行开发，编码时请遵循项目根目录的 `.editorconfig` 规范。

### 自动化验证与测试
在提交 PR 之前，请务必在 PowerShell 中运行完整验证脚本，确保编译、代码格式校验及全部自动化测试通过：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/verify.ps1 -SkipPublish
```

### 提交 Pull Request
1. 提交你的修改并推送到远程分支：
   ```bash
   git commit -m "feat: add some amazing feature"
   git push origin feature/your-feature-name
   ```
2. 在 GitHub 上向主仓库的 `main` 分支发起 Pull Request。
3. 清晰描述本次 PR 的修改动机、实现思路及测试情况。
