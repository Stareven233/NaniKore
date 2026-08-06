# NaniKore 开发笔记

快速索引和提示，供后续开发参考。

## 快速启动

```bash
# 编译
cd d:/Code/projects/NaniKore
go build -o bin/nanikore.exe ./cmd/nanikore

# 运行（前台）
./bin/nanikore.exe

# 运行（后台）
./bin/nanikore.exe &

# 停止
taskkill //F //IM nanikore.exe

# 查看版本
./bin/nanikore.exe --version  # TODO: 尚未实现 flag
```

## 当前热键

- **Ctrl+Shift+A** - 触发屏幕捕获和选区
- **ESC** - 取消选区（理论上，目前事件不工作）

## 调试技巧

**查看日志：**
程序使用 `log.Printf` 输出调试信息，后台运行时日志在临时文件：
```bash
# 实时查看最新日志
cat C:/Users/Noe/AppData/Local/Temp/claude/d--Code-projects-NaniKore/.../tasks/*.output | tail -50
```

**编译错误快速定位：**
```bash
go build -o bin/nanikore.exe ./cmd/nanikore 2>&1 | head -20
```

**检查依赖：**
```bash
go mod tidy
go list -m all
```

## 常见问题

### 热键已被占用
```
Failed to register hotkey: RegisterHotKey failed: Hot key is already registered.
```
**原因：** 上次的进程没有完全退出
**解决：** `taskkill //F //IM nanikore.exe`

### Gio 版本冲突
**现象：** 编译错误提示 `undefined: xxx`
**原因：** Gio API 在不同版本间变化很大
**当前版本：** v0.10.1
**解决：** 查阅对应版本的文档，不要混用不同版本的示例代码

### 窗口不显示或显示异常
**可能原因：**
1. 其他窗口遮挡（特别是全屏游戏）
2. Gio 全屏模式在某些系统上的 bug
3. DPI 设置问题

## 代码组织

```
NaniKore/
├── cmd/nanikore/main.go          主入口
├── pkg/
│   ├── version/                  版本管理
│   │   ├── version.go            加载 config.yaml
│   │   └── config.yaml          → 符号链接到根目录
│   ├── hotkey/                   全局热键
│   │   ├── hotkey.go             热键注册和消息循环
│   │   └── window.go             隐藏消息窗口
│   ├── capture/                  屏幕捕获
│   │   ├── capture.go            Capturer 接口和 Frame 定义
│   │   ├── manager.go            捕获管理器（降级链）
│   │   ├── dxgi.go               DXGI 实现（占位符）
│   │   └── gdi.go                GDI 实现（工作中）
│   └── overlay/                  选区浮层
│       └── selection.go          Gio 全屏窗口（事件问题）
├── config.yaml                  版本号单一来源
├── go.mod                        Go 模块配置
└── README.md                     用户文档
```

## 修改版本号

编辑 `config.yaml`：
```yaml
version: 0.1.0
build: 0
```

格式：`major.minor.patch+build`
- `build: 0` 时不显示 `+0`
- 编译时自动嵌入二进制

## 待实现功能清单

- [ ] 修复选区浮层鼠标事件（**阻塞**）
- [ ] DPI 信息收集
- [ ] DXGI 捕获实现
- [ ] 接入 leaf-ocr
- [ ] 阅读顺序重建
- [ ] LLM 流式翻译
- [ ] 结果卡片（不抢焦点）
- [ ] 精讲面板
- [ ] 托盘图标
- [ ] 设置界面
- [ ] API Key 存储（DPAPI 加密）
- [ ] 剪贴板翻译（第二热键）

## 记忆位置

项目记忆保存在：
```
C:\Users\Noe\.claude\projects\d--Code-projects-NaniKore\memory\
```

当前记忆：
- `nanikore-vision-established.md` - 产品定位与选型决策
- `nanikore-fullscreen-capture-limits.md` - 全屏截取的平台硬边界

## 相关命令速查

```bash
# Git（尚未初始化）
git init
git add .
git commit -m "Initial commit: hotkey, GDI capture, Gio overlay (WIP)"

# 依赖管理
go get <package>
go mod tidy
go mod download

# 清理
go clean
rm -rf bin/

# 查看文档
go doc <package>
go doc gioui.org/io/pointer Event
```

---

*创建时间：2026-08-05*
