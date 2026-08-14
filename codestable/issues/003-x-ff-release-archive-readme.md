---
kind: issue
title: "发布 ZIP 与 README 收敛"
type: ff
status: closed
created: 2026-08-14
---

# 发布 ZIP 与 README 收敛

为 Windows 发布流程补齐可分发 ZIP，并将 README 收敛为当前能力、配置与发布方式的入口文档。

- 改动：`scripts/dev.ps1` — `publish` 默认生成不含模型的 ZIP，`-OcrModels` / `-ocr-models` 生成根层携带 `models/` 的模型包；包名读取 `config.yaml` 产品版本。
- 改动：`README.md`、`codestable/spec/index.md` — 更新项目说明、运行要求、配置边界和两种发布包的命名与布局。
- 验证：PowerShell AST 解析通过；`./scripts/dev.ps1 test` 全部通过；两种发布命令均生成 ZIP，并验证 EXE、DLL、`config.yaml` 与模型目录清单。
- codestable：已同步 `codestable/spec/index.md`。
