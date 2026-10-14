> 2026-10-12 update: official quota/account retrieval remains unchanged. The normal widget now displays separately labelled device-local daily sessions/Tokens and no longer requests optional `account/usage/read`. Legacy official daily decoding is retained only for explicit opt-in compatibility/tests. See [quota-card-update.md](./quota-card-update.md).

# 官方 Codex 额度接口说明（公开依据）

本文记录本组件使用的**官方只读接口**及其字段依据，全部来自官方公开文档与已安装官方客户端自带的 JSON Schema。文中不含任何账号信息、令牌或本机私有路径。

## 1. 资料来源

| 来源 | 用途 |
|---|---|
| <https://learn.chatgpt.com/docs/app-server> | 官方 app-server 的账号 JSON-RPC 接口说明 |
| <https://learn.chatgpt.com/docs/pricing> | 用量面板与 CLI `/status` 的说明 |
| `codex app-server generate-json-schema`（官方客户端自带命令） | `v2/GetAccountResponse`、`v2/GetAccountRateLimitsResponse`、`v2/GetAccountTokenUsageResponse` 的字段类型 |

文档亦说明 app-server 属于实验性能力，需按已安装版本处理兼容性；本组件对不支持的方法给出明确提示，而不是猜测私有 HTTP 契约。

## 2. 传输与认证

- 使用组件自己启动的官方子进程：`codex app-server --listen stdio://`。
- 传输为 JSONL：客户端请求含 `id`、`method`（可选 `params`）；响应带同样的 `id` 与 `result` 或 `error`；通知不带 `id`。
- 必须先完成 `initialize`，再发送 `initialized` 通知，之后才能调用账号方法。
- 鉴权完全由官方客户端负责（其托管的 ChatGPT 登录与令牌刷新）。本组件不读取 `auth.json`，也不获取访问令牌。
- 账号类型可为 `null` / `apiKey` / `chatgpt` / `amazonBedrock`；本版本仅支持 `chatgpt` 的订阅额度。

本组件在进程参数中始终显式保留官方 provider（`openai`）与官方后端域名（`https://chatgpt.com/backend-api/`），不提供可配置的代理或后端地址。

## 3. 允许调用的方法

生产代码只允许以下五个方法：

```
initialize
initialized
account/read
account/rateLimits/read
account/usage/read
```

不发送任何推理（thread/turn）、登录/登出、额度兑换或邮件类方法。超出该白名单的方法会在本地被拒绝。

## 4. `account/read`

响应形如：

```json
{ "account": { "type": "chatgpt", ... }, "requiresOpenaiAuth": false }
```

- `account` 为 `null` 表示未登录。
- 账号对象可能包含可识别的账号字段；本组件优先使用 ID，官方响应只提供邮箱时，以邮箱在内存中计算短指纹，用于拒绝跨账号的缓存数据。原始字段及指纹都不持久化、不记录、不显示，也不读取认证文件。

## 5. `account/rateLimits/read`

响应包含向后兼容的 `rateLimits` 对象，以及可能为空的 `rateLimitsByLimitId` 映射。每个桶的字段：

| 字段 | 类型 | 含义 |
|---|---|---|
| `limitId` / `limitName` | 可空字符串 | 桶标识与可选标签 |
| `primary` / `secondary` | 可空对象 | 两个独立额度时段，任一都可能缺失 |
| 时段 `usedPercent` | 必需整数 | 该时段已用百分比 |
| 时段 `windowDurationMins` | 可空 int64 | 时段长度，界面标签由它推导 |
| 时段 `resetsAt` | 可空 int64 | Unix 秒，统一转换一次为本地时间显示 |
| `planType` | 可空字符串 | 官方返回的套餐 |
| `credits` | 可空对象 | `hasCredits`、`unlimited`、可空 `balance` |
| `individualLimit` | 可空对象 | 花费控制字段，与包含额度时段无关 |
| `rateLimitReachedType` | 可空枚举 | 服务端限制状态 |

`rateLimitResetCredits` 与剩余额度是两回事，不计入任何百分比。

**取值规则（本组件实现）：**

- 优先选择映射键 `codex`；`rateLimits` 只有在 `limitId` 为 `codex` 或缺失时才作为兼容回退使用。
- 剩余百分比为 `100 - usedPercent`；悬浮球取该桶所有可用时段中的**最小**剩余值。
- 百分比越界或必需字段缺失时判定本次额度读取无效，而不是静默丢弃后显示更有利的数值；时段本身缺失属于合法情况。
- 已过期的 `resetsAt` 不会让程序推断额度已重置为 100%。

## 6. `account/usage/read`

响应包含 `summary` 与可能为空/缺失的 `dailyUsageBuckets`，桶字段为 `startDate`（字符串）与非负 int64 `tokens`。

- 官方文档未定义 `startDate` 的时区，因此本组件按字符串匹配本机当天日期，并**原样显示官方日期**，不做时间转换。
- 没有匹配项时显示“暂无官方数据”，而不是 0；返回 `tokens = 0` 是有效值。
- 负数、畸形或溢出的取值视为不可用。
- 该接口失效**不会**丢弃已成功获取的额度数据。

## 7. 生命周期与资源

- 每次刷新只启动一个官方助手会话；额度与（可选的）每日数据都经这同一个会话读取，之后在 `finally`/异步释放路径中关闭并清理其整棵进程树，包括成功、失败与取消路径。原生 CLI 通常只有一个进程；Node 启动器可能带有官方子进程，不代表第二个会话。
- 在两次刷新之间不保留官方进程；刷新默认每 120 秒一次。
- Windows Job Object 在启动后立即接管助手树，关闭 job 会终止其成员；正常父进程退出后也清理遗留子进程。它在 `Process.Start` 后建立，未宣称原子地阻止启动瞬间的任意恶意脱离行为。
- 传输层分块读取，对单条消息设置 8,388,608 个字符的上限（UTF-16 字符存储约 16 MiB，另有 JSON 解析分配），在换行前就检查长度；请求写入与响应读取共同受超时约束。标准错误流以固定块持续读取并丢弃，**不显示、不保存**其原始内容。
- 悬浮球采用 WPF 软件渲染，减少静态小球的硬件渲染设备和驱动常驻开销；详情与设置仍按需创建。资源探针不读取会触发显卡初始化的 `RenderCapability.Tier`。未强制 GC、裁剪工作集或换用其他框架。工作集、私有内存与刷新峰值分别记录在任务 `verification.md` 中。

## 8. 兼容性与边界

- Windows 10/11 x64；本机验证版本为官方 CLI 0.144.1。
- 旧版本官方客户端可能缺少每日用量或 app-server 能力，此时仅每日区块降级。
- 本组件不捆绑、也不静默升级用户的官方客户端。
- 超出范围（未实现）的能力：Claude/Gemini 集成、多账号切换、云同步、API Key 计费面板、本地日志估算、第三方登录、安装器/自动更新。
