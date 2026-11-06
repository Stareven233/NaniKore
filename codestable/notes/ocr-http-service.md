# 本地 OCR HTTP 服务使用说明

NaniKore 可以把内置的本地 OCR 引擎（PP-OCRv6-small + ONNX Runtime）通过一个极简 HTTP 接口开放给同一台机器上的其他程序复用。
这样脚本、编辑器插件、截图工具不必各自再装一套 OCR 运行时，只要调用这个端口即可。

> 这是**开发者选项**，默认关闭。开启后仅做 OCR（检测 + 识别），不做翻译、不写收藏、不读你的 API Key。

---

## 1. 开启与关闭

在 `程序目录/.data/config.yaml` 的 `general` 段落里填 `ocr_listen`：

```yaml
general:
  # 格式 host:port；留空表示不开启
  ocr_listen: "127.0.0.1:23335"
```

支持的写法：

| 写法 | 绑定到 |
|---|---|
| `127.0.0.1:23335` | 仅本机回环（推荐） |
| `localhost:23335` | 同上（等价 `127.0.0.1`） |
| `0.0.0.0:23335` / `*:23335` / `+:23335` / `:23335` | 所有网卡（局域网可达，见第 6 节安全提醒） |
| `23335` | 只写端口，绑定回环 |
| `192.168.1.10:23335` | 指定网卡地址 |
| `my-host:23335` | 主机名，走 DNS 解析 |

行为要点：

- **改完 `.data/config.yaml` 后重启程序生效**。服务没有独立的开关入口，也没有先行的热重载按钮；唯一的热切换路径是「程序内保存设置触发配置重写」时的比对（`ApplySettings` 比较新旧 `OcrListen` 后重建监听）。
- 端口被占用、地址非法、主机解析失败时，程序照常运行，只在托盘弹一次「OCR HTTP 服务启动失败：…」并写一行 `Console.Error`。OCR 的截图主流程不受影响。
- 启动成功时控制台会打印一行 `OCR HTTP service listening on 127.0.0.1:23335`。
- 端口写 `0` 表示由系统分配临时端口，实际端口见上面那行日志（自测即用此方式跑随机端口）。

---

## 2. 前置条件：OCR 依赖必须就位

服务只是把已有的 OCR 引擎包了一层 HTTP，**本身不下载任何东西**。没装依赖时请求会失败（`500`，错误信息指向缺失的模型文件）。

依赖的安装方式与截图 OCR 完全一致：设置页 → 通用 → 本地 OCR 依赖 → 「下载依赖」（或使用 `-full` 完整离线包）。装好后 `deps/` 应包含：

```text
deps/
├─ ocr-onnx/pp-ocr-v6-small/
│  ├─ det/inference.onnx
│  ├─ det/inference.yml
│  ├─ rec/inference.onnx
│  └─ rec/inference.yml
└─ onnxruntime/
   ├─ onnxruntime.dll
   └─ onnxruntime_providers_shared.dll
```

引擎是**首次收到 OCR 请求时**才惰性加载的：服务启动本身开销极小，但第一次 `/ocr` 调用会慢一点（数秒级，取决于磁盘与模型大小）。

---

## 3. 接口

所有响应都是 `Content-Type: application/json; charset=utf-8`，且带 `Connection: close`（一次请求一条连接，不要指望 keep-alive 复用）。

### 3.1 探活

```http
GET /health
GET /healthz
GET /
```

响应 `200`：

```json
{"status":"ok"}
```

三个路径等价，随便挑一个做健康检查即可。注意 `/` 也被占用为探活，返回的不是首页。

### 3.2 OCR 识别

```http
POST /ocr
```

图片来源有**三种**，按优先级从高到低：

1. **查询串路径**：`POST /ocr?path=<绝对或相对路径>`（路径参数只配合 `POST /ocr` 生效，`GET /ocr` 是 404）
2. **JSON Body 指定路径**：`Content-Type: application/json`，Body 为 `{"path": "C:\\shots\\a.png"}`
3. **原始图片二进制**：Body 直接是 PNG / JPEG / WebP / BMP 等任意 Skia 能解出的字节，`Content-Type` 无所谓（不解析）。

判定顺序：`?path=` → JSON body 的 `path` → 原始二进制。三者都没有则 `400`。

> `path` 是**服务进程所在机器**的文件路径（相对于 NaniKore 的工作目录）。客户端在别的机器上时不要用路径方式，直接 POST 二进制。

响应 `200`：

```json
{
  "text": "第一行文本\n第二行文本",
  "elapsed_ms": 42.7,
  "lines": [
    {
      "text": "第一行文本",
      "confidence": 0.9812,
      "bounds": {"x": 10, "y": 20, "width": 320, "height": 44}
    },
    {
      "text": "第二行文本",
      "confidence": 0.9655,
      "bounds": {"x": 12, "y": 72, "width": 298, "height": 41}
    }
  ]
}
```

字段说明：

| 字段 | 含义 |
|---|---|
| `text` | 全部行按阅读顺序用 `\n` 拼成的整段文本（就是你在结果面板原文区看到的内容） |
| `elapsed_ms` | 检测 + 识别的墙钟耗时（毫秒，一位小数） |
| `lines[]` | 逐行明细，已按自然阅读顺序排序 |
| `lines[].text` | 该行文本 |
| `lines[].confidence` | 该行置信度，`sqrt(检测置信度 × 识别置信度)`，取 4 位小数 |
| `lines[].bounds` | 该行在原图中的矩形（**像素**，左上角为原点）；`width` / `height` 是宽高而非右下角坐标 |

`lines` 的顺序就是自然阅读顺序（多栏、多段会被重排），直接按数组顺序拼接即可还原 `text`。

### 3.3 错误响应

错误统一是 `{"error": "..."}`，HTTP 状态码区分性质：

| 状态码 | 场景 |
|---|---|
| `400` | 路径文件不存在、图片无法解码、既没给二进制也没给路径、请求行/头格式非法、Body 中途断开 |
| `404` | 未知路由（如 `POST /foo`） |
| `413` | Body 超过 50 MiB |
| `431` | 请求头超过 64 KiB |
| `500` | OCR 依赖缺失、引擎加载失败、推理异常等内部错误 |

```json
{"error":"File not found: C:\\shots\\missing.png"}
```

---

## 4. 调用示例

### PowerShell（二进制上传）

```powershell
$bytes = [System.IO.File]::ReadAllBytes('C:\shots\a.png')
$r = Invoke-RestMethod -Uri 'http://127.0.0.1:23335/ocr' -Method Post -Body $bytes
$r.text
$r.lines | ForEach-Object { "{0,6:P1}  {1}" -f $_.confidence, $_.text }
```

### PowerShell（服务端本地路径）

```powershell
Invoke-RestMethod -Uri 'http://127.0.0.1:23335/ocr?path=C:\shots\a.png' -Method Post
```

### curl

```bash
# 二进制
curl -s --data-binary @a.png http://127.0.0.1:23335/ocr

# 路径
curl -s -X POST "http://127.0.0.1:23335/ocr?path=C:\\shots\\a.png"

# 探活
curl -s http://127.0.0.1:23335/health
```

### Python

```python
import requests

with open("a.png", "rb") as f:
    data = requests.post("http://127.0.0.1:23335/ocr", data=f.read(), timeout=120).json()

print(data["text"])
for line in data["lines"]:
    print(f'{line["confidence"]:.2%}  {line["text"]}  @ {line["bounds"]}')
```

### JavaScript（Node 18+ / 任意 fetch 环境）

```js
import { readFile } from 'node:fs/promises'

const res = await fetch('http://127.0.0.1:23335/ocr', {
  method: 'POST',
  body: await readFile('a.png'),
})
const { text, lines } = await res.json()
```

---

## 5. 行为与限制

- **串行推理**：服务内部对引擎调用加了锁，同一时刻只跑一个 OCR。并发请求会排队而不是并行——这是刻意的，本地 ONNX 会话本身不适合多路并发抢 CPU。
- **每次请求都新建连接**，不保持长连接；不适合做超高频轮询。
- **请求超时**：服务端对连接设了 15 秒读写超时，但异步读取路径上这个值不一定严格生效；客户端侧建议统把超时放到 120 秒，尤其是首次调用（要加载模型）。
- **不做鉴权、不做跨域头**；传入什么图就识别什么图，不做旋转矫正（旋转/弯曲/竖排文字本来就是本项目的已知边界）。
- **无状态**：不写 `.data`，不产生收藏记录，不消耗翻译额度。
- 图片按原始尺寸识别，不缩放、不增强。

---

## 6. 安全提醒

- 默认建议只绑 `127.0.0.1`。绑 `0.0.0.0` 等于把「识别本机任意路径下的图片」这一能力开放给局域网——它能读的是**NaniKore 进程有权限读的任何文件**，而接口没有任何认证。
- 如果要暴露到局域网，请自行在防火墙/反向代理层做来源限制。
- 该接口不接触 API Key、不接触配置档，因此泄露不会直接导致密钥外泄；但路径读取能力本身需要按「本机权限」对待。

---

## 7. 与命令行 OCR 的关系

同一套 OCR 引擎还有两个不走 HTTP 的用法，输出 JSON 的结构与 `/ocr` **完全一致**（同一个 `OcrResponseDto`）：

```powershell
# 人类可读输出
NaniKore.exe --ocr-file C:\shots\a.png

# 打印与 /ocr 相同的 JSON，便于脚本管道处理（stdout 只有 JSON）
NaniKore.exe --ocr-file C:\shots\a.png --json
```

如果只是「一次性识别一张图」，用 `--ocr-file --json` 更省事；需要常驻服务、被多个程序反复调用时，才开 `ocr_listen`。

---

## 8. 自测覆盖

`./scripts/dev.ps1 test` 里的 `OcrHttpServer` 四条断言覆盖：

1. `GET /health` → `200` 且含 `"status":"ok"`；
2. `POST /ocr` 原始二进制 → `200` 且含识别文本与 `elapsed_ms`；
3. `POST /ocr?path=` → `200` 且含识别文本；
4. 未知路由 → `404`。

这四条跑在随机端口（`127.0.0.1:0`）与**桩识别器**上，不依赖真实模型，因此不下载 `deps/` 也能跑通；真实推理链路由 `--ocr-file` 与截图主流程的断言覆盖。
