# 开发速查

## 常用命令

```powershell
./scripts/bootstrap.ps1
./scripts/dev.ps1 build
./scripts/dev.ps1 test
./scripts/dev.ps1 run
./scripts/dev.ps1 publish
./scripts/dev.ps1 publish -full
```

本地 OCR 依赖可从设置页下载，也可随完整发布包提供。依赖目录结构见 `deps/README.md`。

## 诊断入口

```powershell
dotnet run --project src/NaniKore -- --inspect-models
dotnet run --project src/NaniKore -- --ocr-file <图片路径>
artifacts/win-x64/NaniKore.exe --self-test
```

Native AOT 裁剪约束见 [DComposition 维护边界](001-nativeaot-dcomposition-substitutions.md)；本地 OCR HTTP 接口见 [OCR HTTP 服务](ocr-http-service.md)。
