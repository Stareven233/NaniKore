---
kind: issue
title: "SQLite 极简本地知识库与设置面板 [收藏] Tab"
type: feature
status: completed
created: 2026-09-20
updated: 2026-09-20
---

# SQLite 极简本地知识库与设置面板 [收藏] Tab

> **读者：** 负责 SQLite 持久化收藏服务与设置面板收藏管理页的开发者。  
> **归属：** `codestable/epics/001-o-学习型演进-精讲假名与生词库/spec.md` 切片 5。  
> **关联代码落点：**  
> - `src/NaniKore/Favorites/SqliteFavoriteService.cs`（SQLite 持久化收藏服务）
> - `src/NaniKore/Favorites/FavoriteContracts.cs`（沿用切片 4 契约，补充 SQLite 实现）
> - `src/NaniKore/App/Settings/FavoritesPage.xaml` / `.xaml.cs`（设置面板 `[收藏]` Tab）
> - `src/NaniKore/App/Settings/SettingsWindow.xaml.cs`（注册新 Tab）
> - `src/NaniKore/App/NaniKoreApp.cs`（收藏服务切换为 SQLite 实现并注入设置窗口）
> - `src/NaniKore/Diagnostics/SelfTest.Favorites.cs`（持久化自测）

---

## 背景与目标

切片 4 已建立 `FavoriteItem` / `IFavoriteService` 契约与内存态实现，卡片划词收藏可在会话内工作，但刷新即丢，尚未兑现 Vision 中“精讲结果本地留存 / 生词本”的持久化闭环。

本切片（切片 5）把收藏契约落到 SQLite 并提供管理入口：
1. **SQLite 持久化收藏服务（`SqliteFavoriteService`）**：
   - 数据库文件 `nanikore.db` 固定落在应用当前根目录（`AppContext.BaseDirectory`），绿色便携、覆盖升级不丢数据；
   - **首次收藏时惰性初始化**：未发生任何收藏前不创建库文件，删除库文件后不崩溃、按空态工作；
   - 建表 `favorites`（`id / category / content / note / created_at`），`created_at` 为 Unix 毫秒时间戳；
   - 完整实现 `IFavoriteService`：`AddFavoriteAsync`（同内容大小写不敏感 upsert，与内存态语义一致）、`RemoveFavoriteAsync`、`IsFavorited`（忽略大小写与首尾空白）、`GetAllFavorites`（按收藏时间倒序快照）、`FavoriteAdded` / `FavoriteRemoved` 事件，保持切片 5 之前的跨组件星标同步能力；
2. **设置面板 `[收藏]` Tab（`FavoritesPage`）**：
   - 顶部类别筛选（`全部` / `单词` / `短语` / `语法` / `备注`）与关键词搜索框；
   - 卡片列表流式展示：类别徽章、核心文本、纯文本分层 note、收藏时间、单条 `复制` 与 `删除`；
   - 工具栏提供 `复制全部`、`刷新`，空态给出温和提示文案；
   - 收藏数据不写入 `config.yaml`，本页 `Validate()` 恒为空、`Apply()` 为空操作；
3. **自测覆盖（`SelfTest.Favorites.cs`）**：
   - 惰性初始化：构造后、只读查询前不落盘；
   - 增删查、大小写/空白容差命中、事件激发；
   - 跨实例持久化：关闭服务后重新打开同一库文件数据仍在；
   - upsert 语义：同内容不同 note 再收藏更新而非重复插入。

## 质量目标（ISO/IEC 25010）

- **可靠性（Reliability）**：库文件被手工删除/损坏路径不可写时，功能降级为空态并温和提示，不崩溃；
- **兼容性（Compatibility）**：`IF NOT EXISTS` 建表，旧版本无库、新版本无数据均为合法空态；`PublishSingleFile` 发布下 SQLite 本机库随包分发可用；
- **易用性（Usability）**：设置页筛选、搜索、删除、复制提供即时反馈，操作后列表自动刷新。

## 执行痕迹与闭环成果

1. **SQLite 持久化收藏服务 (`SqliteFavoriteService.cs`)**：
   - 数据库固定落在 `AppContext.BaseDirectory/nanikore.db`（`DatabasePath` 暴露给设置页展示）；
   - **惰性初始化**：仅 `AddFavoriteAsync` 首次写入时建库建表；读/删在库文件不存在时短路为空态，手工删库后功能不崩溃；
   - `favorites` 表 Schema 与 spec 逐字一致（`id/category/content/note/created_at`，`created_at` 为 Unix 毫秒）；`CREATE TABLE IF NOT EXISTS` 让无库、旧库、手工删库都是合法状态；
   - 操作语义与 `InMemoryFavoriteService` 完全对齐：同内容大小写不敏感 upsert（重收藏更新类别/note，不插重复行）、`IsFavorited` 忽略大小写与首尾空白、类别显式传入时要求同时匹配、`GetAllFavorites` 按收藏时间倒序；
   - `AddFavoriteAsync`/`RemoveFavoriteAsync` 以已完成任务透出 Task，保持切片 4 契约零改动切换；收藏/移除事件照常激发，维持星标跨组件同步；
   - 连接串显式 `Pooling=false`：桌面单用户场景无复用收益，且池化句柄会持续持有库文件锁，导致用户替换/删除 `nanikore.db` 报“正被另一进程使用”。
2. **设置面板 `[收藏]` Tab (`FavoritesPage.xaml` / `.xaml.cs`)**：
   - 顶部知识库分组展示数据库路径与便携语义；检索分组提供类别筛选（全部/单词/短语/语法/备注）与关键词搜索（同时匹配 content/note，忽略大小写），以及刷新、复制全部；
   - 收藏列表以卡片呈现类别徽章、核心文本、纯文本分层 note、收藏时间，单条支持复制与删除；空结果与空列表均有温和提示；内联 `InfoBar` 承载操作反馈；
   - 本页不参与 `AppConfig` 编辑副本：`Validate()` 恒为空、`Apply()` 为空操作，宿主“保存”不触及收藏；
   - 页面在 `Load` 时订阅、`Unloaded` 时退订收藏服务事件，防止设置窗反复开关时往应用级服务上堆叠废弃处理器。
3. **宿主接线 (`NaniKoreApp` / `SettingsWindow`)**：
   - `NaniKoreApp._favoriteService` 由 `InMemoryFavoriteService` 切换为 `SqliteFavoriteService`，结果卡与设置窗口共享同一应用级实例；
   - `SettingsWindow` 构造函数追加 `IFavoriteService? favoriteService = null` 参数，在热键页与关于页之间注册 `[收藏]` Tab。
4. **自动化验证（2026-09-20）**：
   - `scripts/dev.ps1 build`：0 警告 0 错误（`TreatWarningsAsErrors` 开启）；
   - `scripts/dev.ps1 test`：全量无 UI 自测 **351 项 PASS / 0 FAIL**，其中新增 38 项 SQLite 服务测试（惰性建库、增删查、大小写/空白容差、类别限定命中、事件、跨实例持久化、upsert、空内容拒绝）与 11 项收藏页 UI 测试（元数据、实时库路径、筛选/搜索、空态、事件自动刷新、配置契约）；
   - `scripts/dev.ps1 publish` 后以 `ProcessStartInfo` 重定向运行发布产物：**351 PASS / 0 FAIL、退出码 0**，确认单文件包内 SQLite 本机库（`e_sqlite3.dll`）可用，绿色便携闭环成立。
