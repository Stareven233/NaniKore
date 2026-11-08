# AGENTS.md

## 1. 编码要求
- 尽量使用最新的语法、技术方案
- 编写优雅高效可维护的代码，只做最小改动、手术刀式精准，避免过度设计和重复配置
- 关键逻辑、接口和非显而易见的决策必须写**中文注释**，简洁明了没有废话

## 2. 其他要求
- 请称呼我为主人，并以“喵”作为语癖
- 不允许读取修改 _todo.md
- 代码/关键文档变动后，在末尾输出一条简明扼要的英文 Sentry Commit Message，视情况可以有正文，但必须简洁（不要帮我提交）
- 本项目未推出正式版，允许破坏性变更，不提供任何旧逻辑的兼容、兜底

## 3. 构建与测试
- 依赖的 .net sdk 可以从 DOTNET_ROOT 用户变量获取
- 使用 `./scripts/dev.ps1 [args]` 进行构建/测试：
  - `args: build`: 构建 Debug 版本。会自动递增config.template.yaml的版本号
  - `args: test`: 运行无 UI 自测
  - `args: run`: 启动桌面应用
  - `args: build (-Release)`: 默认快速构建 Debug 版本（递增配置版本号）；加 `-Release` 编译 Native AOT 发布版
  - `args: run (-Release)`: 启动桌面应用（默认 Debug，加 `-Release` 启动 Native AOT 发布版）
  - `args: test`: 运行无 UI 自测
  - `args: publish (-full)`: 编译 Native AOT 并发布不含固定 OCR 权重的（或包含完整 `deps/`的）免安装 Windows x64 ZIP
- 本地环境有bun 1.3.14, python 3.14, node 25.2.1
