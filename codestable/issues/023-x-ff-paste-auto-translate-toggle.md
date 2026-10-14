---
kind: issue
title: "[快改] 选取翻译补自动翻译开关，与 OCR 路径同一口径"
type: ff
status: closed
created: 2026-10-21
---

<!-- 快改痕迹：轻。读者只要 30 秒扫完。禁止迷你 Design。 -->

# [快改] 选取翻译补自动翻译开关，与 OCR 路径同一口径

> **读者：** 以后搜到这条时——「改了啥、怎么信、动没动制度记忆」。  
> **自检（四答即可，可合成短段，勿空标题凑节）：** 做了什么 · 改了哪些文件 · 怎么验证 · 对 `codestable/` 有无影响。

---

主人反馈：按下选取翻译热键（`Ctrl+Shift+S`）后不仅把原文打出并标注注音，还会自动翻译，希望能像 OCR 路径那样在设置页开关自动翻译。根因是 `NaniKoreApp.PasteTranslateAsync` 的文本路径把 `autoTranslate` 硬编码成 `true` 并无条件启动翻译——该方法此前的文档注释还写明「本路径忽略 auto_translate（热键本身就是立即翻译的意图）」，与主人诉求正好相反。

修法不是复用 OCR 的 `general.auto_translate`（那条语义是「选区 OCR 后」，选取翻译复用它会让开关文案失真、还把两条热键耦合在一起），而是新增独立的 `general.paste_auto_translate`，默认开启以保留现有行为，主人可显式关闭。口径完全镜像 OCR 路径：文本路径（选区文本 / 剪贴板文本 / 图像 OCR）受该开关门控，关闭时面板只呈现原文（含注音）、由「翻译」按钮触发；视觉直喂（vision_oneshot）恒定一次完成识别与翻译，不受此开关约束（与截图链路视觉直喂同口径，本就恒定自动翻译）。

- 改动：`src/NaniKore/Config/AppConfig.cs` — `GeneralConfig` 增 `PasteAutoTranslate`（默认 `true`），`AppConfigCloner.Clone` 同步克隆。
- 改动：`src/NaniKore/App/NaniKoreApp.cs` — `PasteTranslateAsync` 文本路径改读 `config.General.PasteAutoTranslate`，仅在开启时启动翻译；视觉直喂分支补注释说明其恒定自动翻译、不受开关约束；更新方法文档注释。
- 改动：`src/NaniKore/App/Settings/GeneralPage.xaml` + `.xaml.cs` — 「翻译」组在「OCR 后自动翻译」下新增「选区/剪贴板后自动翻译」开关行，Load/Apply 接通配置。
- 改动：`config.template.yaml` — `general` 段加 `paste_auto_translate: true`（含注释）。
- 改动：`src/NaniKore/Diagnostics/SelfTest.Config.cs` — 配置往返用例补 `paste_auto_translate` 非缺省值的读写、重载与克隆保持，及旧格式回落时缺省为 `true`。
- 改动：`src/NaniKore/Diagnostics/SelfTest.Ui.Settings.cs` — 通用页补新开关的 Load/Apply 读写闭环断言。
- 改动：`README.md` — 选取翻译热键段补一句开关说明。
- 验证：`dev.ps1 build` 0 警告 0 错误；`dev.ps1 test` 全绿（新增 2 条 PASS：开关 Load/Apply；配置往返 3 条既有断言扩展后仍 PASS）。真实剪贴板 round-trip 两条按既定口径 SKIP（本会话剪贴板被其他进程占用，issue 021 已知 flake，与本次无关）。
- codestable：无影响（仅新增一个通用设置开关 + 消费点 + 自测，未触碰 spec 既定口径；OCR 路径的 `auto_translate` 语义与行为原样不变）。
