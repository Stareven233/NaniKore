---
kind: issue
title: "提示词预览链路去爬虫化：scripts/ 独立中转服务与 --dump-prompt 实时联动"
type: ff
status: open
priority: normal
created: 2026-09-24
---

# 提示词预览链路去爬虫化：scripts/ 独立中转服务与 --dump-prompt 实时联动

> **读者：** 负责开发期工具链的开发者。
> **背景：** `011-x-ff-prompt-preview-html-and-dump-prompt.md` 落地后，主人在复盘中指出当前预览架构的两个缺陷：
> 1. **交互区受限于预计算样本矩阵**：HTML 中任意未预置的文本输入都无法获得预览，只能提示运行 CLI 命令，交互边界由样本清单决定。
> 2. **职责边界应当彻底解耦**：C# 生产代码应当对 HTML 预览及其服务**完全不可知**。不应在 C# 主程序中塞入 HTTP 监听服务（如 `--serve-preview`）或反向寻址前端 HTML 文件；中转服务器与前端展示应当完全收拢在 `scripts/` 目录中。
> **决议：**
> - **C# 代码纯净且不可知**：C# 只保留现有的 `--dump-prompt` CLI，作为命令行唯一的提示词真值出口，主工程不增加任何 HTTP 依赖或预览命令。
> - **中转服务器收归 `scripts/`**：编写 `scripts/serve-preview.ps1`，利用原生 `System.Net.HttpListener` 启动轻量本地服务器，伺服 HTML 并代理调用 `NaniKore.exe --dump-prompt --json`。
> - **HTML 实时获取真值**：前端在服务环境下输入任意原句直接向中转服务 `/api/dump` 发送请求并防抖渲染，无预计算限制；若在 `file://` 直开则优雅降级为离线快照。

## 方案

### 一、C# CLI 接口（保持现状，零侵入）

C# 主程序维持现有 `NaniKore.exe --dump-prompt` 接口不变：
```
NaniKore.exe --dump-prompt [--text "..."] [--image <path>] [--kind translate|explain] [--target <language>] [--json]
```
- 读取真实配置，输出包含 `kind`、`path`、`target_language`、`source_preview`、`detection`、`sections`、`system_prompt`、`estimated_tokens` 的完整 JSON。
- C# 生产代码不感知 HTML、不引入 HTTP 监听、不增加额外 CLI 开关。

### 二、`scripts/serve-preview.ps1` 本地中转服务

在 `scripts/` 目录下新增纯脚本中转服务：
```powershell
pwsh scripts/serve-preview.ps1 [-Port 8788] [-NoLaunch]
```
- **服务绑定**：使用 .NET 原生 `System.Net.HttpListener` 绑定 `127.0.0.1:8788`；端口冲突时自动递增尝试。
- **自动唤起**：启动时打印服务地址并尝试打开系统默认浏览器。
- **路由**：
  - `GET /` 或 `GET /index.html`：返回 `scripts/prompt-preview.html`（UTF-8 编码，`text/html`）。
  - `GET /api/dump?text=&kind=&target=&image=`：解析前端参数，后台调用 Debug 构建产物 `NaniKore.exe --dump-prompt --json`，将 JSON 逐字代理回传给前端，支持 CORS。
  - `image=1` 处理：服务内置最小 1×1 占位 PNG，自动触发视觉一条龙组装路径。
- **生命周期**：Ctrl+C 时平滑停止并释放端口。

### 三、`scripts/prompt-preview.html` 实时 fetch 改造

- **双模自适应**：
  - **Live 模式（HTTP 服务下）**：检测到同源或本地服务时，顶部显示「实时服务已连接」状态；输入框变更时防抖（~150ms）请求 `/api/dump`，**任意输入均可实时获得真实 system prompt、分段来源高亮与探测特征**，不再受样本矩阵约束。
  - **离线快照模式（`file://` 直开）**：fetch 失败或协议为 file 时，顶部显示「离线快照模式」提示，降级使用内嵌数据；若输入未命中预置样本，提示用户启动 `scripts/serve-preview.ps1`。
- **保留一键复制**：支持复制完整 system prompt 与复现命令行。

## 验收标准

- [x] C# 生产代码（`src/`）保持 0 侵入，无需修改 `Program.cs`。
- [x] 启动 `scripts/serve-preview.ps1` 能成功启动本地监听并自动打开浏览器。
- [x] 页面在服务模式下输入任意文本，均能秒级拿到 `--dump-prompt` 真实 JSON 并高亮渲染。
- [x] 视觉一条龙与精讲模式在实时服务下切换正常、各段来源（builtin/custom）清晰展示。
- [x] 直接双击打开 `scripts/prompt-preview.html`（`file://`）时，优雅降级为离线快照并给出服务启动命令提示。
- [x] 既有 `./scripts/dev.ps1 test` 自测全部通过。

## 执行记录

- **脚本中转服务**：新增 `scripts/serve-preview.ps1`，监听 `127.0.0.1:8788`，将前端 `/api/dump` 代理至 `NaniKore.exe --dump-prompt --json`，C# 生产代码零侵入。
- **前端实时交互**：改造 `scripts/prompt-preview.html`，检测到服务连通后自动启用 Live API 模式，任意输入防抖调用中转服务，脱离写死样本矩阵限制；在 `file://` 下保持优雅降级。
- **自测与验证**：经实际启动中转服务，验证了普通文本、日英混排、未预置长句、视觉一条龙（`image=1`）及精讲模式（`kind=explain`），均能正确返回真值；`./scripts/dev.ps1 test` 自测无回归。

## 备注

- 严格遵循解耦原则：业务/发布代码对开发期调试工具零感知。
- 端口仅绑定本地回环（127.0.0.1），不落日志，仅供开发期本地调试。
