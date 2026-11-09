---
kind: issue
title: "可配置本地 OCR 与注音资源的闲置回收时间"
type: ff
status: closed
created: 2026-11-09
---

- 做了什么：设置通用页新增始终可见的「本地资源 → 闲置回收时长」，提供 `0 / 30s / 10m / 24h` 预设并支持手填整数秒或整数加 `s/m/h`；默认 `0` 永不回收。空值、负数、小数、未知单位及溢出在 UI 保存时明确报错。YAML 使用 `general.local_resource_idle_timeout`，包含默认值、克隆、归一化和模板说明。
- 资源策略：OCR 引擎与三种语言词典分别计时；使用结束后才开始计算空闲，超时释放、下次按需重建。共享 `IdleResource<T>` 串行化资源使用与释放，单次定时避免后台轮询，超长时长分段等待；保存新策略不等待正在运行的推理，并防止旧定时回调覆盖新策略。OCR 的截图、剪贴板与 HTTP 入口统一受保护；词典更新废弃旧实例，关闭应用释放缓存；词典可用性探测移入后台，避免超时后的重载阻塞窗口。未加入强制 GC、原生 DLL 卸载或工作集裁剪。
- 改了哪些：配置与通用设置页；新增 `Config/ResourceIdleDuration.cs`、`Runtime/IdleResource.cs`、`Phonetics/CachedTextAnnotator.cs`；接入应用宿主与注音注册表；增加时长、配置往返、页面验证以及资源生命周期/并发自测。
- 怎样验证：Debug 构建及 `./scripts/dev.ps1 test` 通过；`./scripts/dev.ps1 build -Release` Native AOT 构建通过，发布版 `--self-test` 1346 checks 全部通过。覆盖空闲到期、自动定时回收、重建、独立计时、使用中不释放、失败操作后仍可清理、策略修改、零值和长时长。隔离 Native AOT 应用使用 `2s` 策略，经 HTTP 对同一张 1920×1080 十行文字图两次识别，第一次识别后私有内存约 99.5 MiB，空闲四秒后约 60.1 MiB；回收后第二次正常识别，文字、位置、置信度一致。该数值仅代表隔离 OCR 场景，不代表包含全部注音和 UI 缓存的后台稳态。
- codestable 影响：已同步 `codestable/spec/index.md` 的配置语法、资源所有权与生命周期契约。
