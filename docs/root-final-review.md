> Historical first-release review. For the current card/local-usage release, see [quota-card-final-review.md](./quota-card-final-review.md).

# 最终交付复核

2026-10-12，主协调会话在实施与独立 Codex CHECK 均结束后复核本次发布。

- 重新运行 Release 构建：0 警告、0 错误。
- 重新运行完整测试：136 通过、0 失败、0 跳过；格式验证 exit 0。
- 逐个核对检查阶段 30 个证据文件的 SHA-256，全部一致。
- 当前源码构建得到的 Desktop/Core DLL 与 `artifacts/win-x64/` 中的发布 DLL 完全一致；未在验证后修改应用代码。
- 目视检查最终暗色设置、暗色主题下拉和过期详情图片，文字对比度与旧数据标签可读。
- 记录并接受[独立检查报告](check-report.md)和[验证报告](verification.md)的测试边界。没有将路由事件当作物理输入、混合 DPI/热插拔、系统主题事件或重启后自启的实机证明。
- 常驻实测约 120.41 MiB 工作集、49.93 MiB 私有内存；刷新同时合并峰值 174.91 MiB。空闲 CPU 本次采样为 0；这些是本机样本，打开面板会增加占用。

复核命令与结果保留在 `artifacts/root-verification/root-verify.log` 和 `root-final.json`。官方只读在线、托盘退出、正常两周期和助手清理证据由独立检查阶段产生，并已核对发布版本。

项目规范新增官方 provider/缓存/进程生命周期契约与 Windows 交互/主题/定位/测量契约，保留其他 bootstrap 模板任务。工作目录不是 Git 仓库；Phase 3.4 的 Git 检查确认这一点，因此本次没有初始化仓库或制造提交记录。任务与 journal 以无提交方式收尾。

运行 `artifacts/win-x64/CodexQuota.Desktop.exe`，并保留整个发布文件夹。无需安装 .NET；复用已登录的官方 Codex CLI。开机启动默认关闭，可在小球右键菜单的设置中启用。
