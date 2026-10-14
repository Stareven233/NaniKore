# AGENTS.md

## 1. 编码要求
- 尽量使用最新的语法、技术方案
- 编写优雅高效可维护的代码
- 关键逻辑、接口和非显而易见的决策必须写**中文注释**，简洁明了没有废话
- 若有必要请使用系统已安装的pwsh，而非powershell

## 2. 其他要求
- 请称呼我为主人，并以“喵”作为语癖
- 不允许读取修改 _todo.md
- 代码/关键文档变动后，在末尾输出一条简洁明了的英文 Sentry Commit Message，视情况可以有正文（不要帮我提交）
- 本项目未推出正式版，允许破坏性变更，不提供任何旧逻辑的兼容、兜底

## 3. 构建与测试
- 依赖的 .net sdk 可以从 DOTNET_ROOT 用户变量获取
- `./scripts/dev.ps1 [args]` 负责构建/测试：
  - `args: build`: 构建 Debug 版本。会自动递增config.template.yaml的版本号
  - `args: test`: 运行无 UI 自测
  - `args: run`: 启动桌面应用
  - `args: publish (-full)`: 发布不含固定 OCR 权重和 native DLL 的（或包含完整 `deps/`的） Windows x64 ZIP
  - `args: -Launch build|publish`: 启动已有的 Debug|发布 exe
- 本地环境有bun 1.3.14, python 3.14, node 25.2.1
