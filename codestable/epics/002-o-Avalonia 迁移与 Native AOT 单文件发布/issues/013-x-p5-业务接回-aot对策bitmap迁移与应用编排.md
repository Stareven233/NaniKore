---
kind: issue
title: "P5 业务接回：AOT 对策、Bitmap 边界迁移与应用编排整合"
type: feature
status: closed
created: 2026-11-02
closed: 2026-11-03
---

## 目标

落实 Epic 002 P5 业务接回批次的所有要求：
1. **JSON 源生成改造（阻断项 2 与 11）**：
   - 移除 `SecretStore` 与 `OcrHttpServer` 的运行期反射 JSON 序列化，引入 `NaniKoreJsonSerializerContext` 源生成器；
   - 为 `keys.bin` 增加方案版本头（魔数 `NKKEY` + Scheme 1 + Version 1），并实现旧版无版本头 DPAPI 密文透明向下识别（零迁移体验）；
   - 统一承接 CLI `--dump-prompt` 的 DTO 序列化。
2. **LLM 请求组装与序列化 AOT 静态化**：
   - 彻底重构 Claude、Gemini、OpenAI 三大 Provider 请求体组装：改用 `System.Text.Json.Nodes`（`JsonObject` / `JsonArray` / `JsonValue`）；
   - 在基类 `TranslationProviderBase` 封装 AOT 安全的 `NodeArray` 与 `AppendNode`，完全规避 C# 集合初始化器误绑带 `IL2026` / `IL3050` 警告的泛型 `JsonArray.Add<T>(T)` 扩展方法；
   - 彻底移除对 `[UnconditionalSuppressMessage]` 的依赖，项目自身源码达到真正的 **0 抑制、0 警告**。
3. **OCR 核心系统性 Bitmap→Skia 边界迁移**：
   - 彻底废除 `System.Drawing` 与 GDI+ 引用，将 `OcrEngine`、`TextDetector`、`TextRecognizer`、`TensorImageConverter` 及剪贴板图像链路公开 API 和内部表示全部切换为 `SKBitmap` 与 SkiaSharp 几何类型（`SKRectI` / `SKSizeI` / `SKPointI`）；
   - 从 `GlobalUsings.cs`、`PngEncoder.cs`、`SelectionGeometry.cs` 中移除任何过渡期 `System.Drawing` 代码与别名；
   - 移除 csproj 中的 `System.Drawing.Common` 依赖；
   - 确保从屏幕截取、剪贴板获取（Win32 `GetDIBits` / `SetDIBits` 直接通道）、文件读取（`--ocr-file`）到 HTTP 接收（`OcrHttpServer`）的图像管线完全由 SkiaSharp 承接。
4. **平台层与应用生命周期编排（NaniKoreApp 整合）**：
   - 将原 WPF 宿主 `App/NaniKoreApp.cs` 的核心业务编排逻辑（热键分发、浮层截图、选区翻译、剪贴板获取、OCR 惰性单例、可抢占流式翻译与精讲、代际取消流控、结果面板单例定位与尺寸记忆、设置窗口与尺寸写回）全量迁入 Avalonia 的 `NaniKoreApp`（`App.axaml.cs`）；
   - 物理清理废弃的 WPF 残留文件（`src/NaniKore/App/NaniKoreApp.cs`、`MainWindow.axaml`、`OcrProgressWindow.xaml*`、`OcrResultWindow.xaml*`），保持「常驻托盘、无主窗口、显式退出」的桌面软件定位。
5. **嵌入资源与全球化安全审计**：
   - 验证 `Phonetics` 嵌入资源读取与 AOT 剪裁存活性，停用词表与字符表在 `IlcTrimMetadata=true` 下正常加载；
   - 维持 `InvariantGlobalization=true` 安全运行，多语言词典加载与分词零异常。
6. **CLI 入口与全量自测全绿**：
   - 适配全部 CLI 入口；更新 `SelfTest` 套件全部位图断言；
   - 达成 `./scripts/dev.ps1 build`（0 警告 0 错误）、`./scripts/dev.ps1 test`（全量自测通过）；
   - 达成 `./scripts/dev.ps1 aot`（0 警告 0 错误），且在 Native AOT 单文件可执行文件（`artifacts\win-x64-aot\NaniKore.exe`）上直接运行 `--self-test` 获得全绿通过。

---

## 落地成果与实测验证

### 1. 编译与 Native AOT 构建指标
- `./scripts/dev.ps1 build`：**0 个警告，0 个错误**。
- `./scripts/dev.ps1 aot`：**0 个警告，0 个错误**，ILC 原生代码生成与链接成功。
  - 产物目录：`artifacts\win-x64-aot\NaniKore.exe`。
  - 体积：**20.79 MB**（21,807,104 字节），完全契约符合 Native AOT 精简单文件预期。

### 2. Native AOT 产物自测与 CLI 入口全矩阵实测
在 Native AOT 原生 exe 产物上实测：
- `NaniKore.exe --self-test`：**全量单元测试全绿通过**（非 UI 88 + UI 23，共 609 条断言，`ExitCode: 0`）。
- `NaniKore.exe --dump-config`：正常加载配置，`SecretStore` DPAPI 密文解密正确（`ExitCode: 0`）。
- `NaniKore.exe --dump-prompt --text "こんにち は" --json`：强类型源生成序列化准确输出，`ExitCode: 0`。
- `NaniKore.exe --inspect-models`：动态从 `deps/` 载入 ONNX Runtime 探查模型输入输出张量成功，`ExitCode: 0`。
- `NaniKore.exe --phonetics-probe "日本語の漢字"`：MeCab 与注音词典在 AOT 下探查与注音计算完全正确，`ExitCode: 0`。
- `NaniKore.exe --ocr-file artifacts\pierce\p1\window.png --json`：真实图像 Skia 解码、张量转换、OCR 检测与识别完整跑通，`ExitCode: 0`。
- `NaniKore.exe --translate-text "こんにちは" --profile openai-index-translate`：真实网络端点交互、JsonNode 组装、流式解析并输出“你好”，`ExitCode: 0`。

---

## 结论

P5 批次的所有目标与阻断项全量达成，代码质量与架构干净纯粹，无任何 AOT 警告抑制或动态反射妥协。具备开启 P6（收尾与发布通道接入）批次条件。
