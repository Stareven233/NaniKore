# NaniKore（何これ）

屏幕 OCR 翻译与精讲工具 — 三秒看懂，三十秒学会。

## 当前状态：v0.1.0 开发中

**已实现：**
- ✅ 版本管理系统（config.yaml 单一来源）
- ✅ 热键注册（Ctrl+Shift+A 触发）
- ✅ 屏幕捕获（GDI 路径，支持多显示器）
- ✅ 选区浮层窗口创建（Gio）
- ⚠️  选区浮层鼠标事件（**阻塞问题**，见下方）

**待实现：**
- [ ] 修复选区浮层的鼠标交互
- [ ] DXGI 捕获路径（硬件加速内容支持）
- [ ] DPI 信息收集（混合 DPI 坐标映射）
- [ ] OCR 识别（接入 leaf-ocr）
- [ ] 阅读顺序重建
- [ ] 流式 LLM 翻译
- [ ] 流式精讲面板
- [ ] 托盘常驻
- [ ] 配置与 API Key 管理

**已知问题：**
- 🔴 **选区浮层鼠标事件不响应** - Gio v0.10.1 在 Windows 全屏模式下无法接收鼠标点击和拖动事件，窗口显示为灰色不透明而非预期的半透明黑色。这是当前的阻塞问题，需要优先解决。详见 `codestable/issues/001-o-选区浮层鼠标事件不响应.md`。

## 快速开始

### 编译

```bash
go build -o bin/nanikore.exe ./cmd/nanikore
```

### 运行

```bash
./bin/nanikore.exe
```

然后按 **Ctrl+Shift+A** 触发屏幕捕获和框选。

- 拖动鼠标框选区域
- 松开鼠标完成选择
- 按 ESC 取消

选区会保存到 `%TEMP%\nanikore_selection.png`，可以验证坐标是否正确。

## 项目结构

```
NaniKore/
├── cmd/nanikore/          # 主程序入口
├── pkg/
│   ├── version/           # 版本管理
│   ├── hotkey/            # 全局热键
│   ├── capture/           # 屏幕捕获（DXGI + GDI）
│   └── overlay/           # 选区浮层
├── codestable/            # 需求、设计、探索文档
│   ├── vision/            # 产品愿景
│   ├── epics/             # Epic 规划
│   ├── spec/              # 项目实际状态
│   └── talks/             # 技术调研与选型
└── config.yaml           # 版本号单一来源
```

## 技术栈

- **语言**: Go 1.26+（无 CGO 依赖）
- **GUI**: Gio（选区浮层、结果卡片）
- **捕获**: DXGI Desktop Duplication（主）+ GDI BitBlt（兜底）
- **OCR**: leaf-ocr（PP-OCRv6）
- **LLM**: OpenAI 兼容 API

## 版本管理

版本号在 `config.yaml` 中维护，遵循 `major.minor.patch+build` 格式：

- **major**: 重大版本（不兼容的改动）
- **minor**: 不兼容的改动
- **patch**: 一次足以发布的改动
- **build**: 微小改动累积（设为 0 时不显示）

编译时自动嵌入到二进制文件。

## 许可证

TBD
