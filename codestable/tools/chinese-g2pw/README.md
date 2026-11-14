# Isolated g2pW contextual-reading probe

当前状态：**执行被权限系统阻止，未取得任何模型预测**。`probe.py` 是待运行脚手架，尚未验证与官方当前 API 的兼容性；不能据此判断 g2pW 的准确率或可部署性。

固定目标在源码中预声明：

| 输入 | 目标（Unicode code point 索引，从 0 开始） |
| --- | --- |
| 他还在这里 | 1: hai2 |
| 把书还给我 | 2: huan2 |
| 这里的周长大于十米 | 4: chang2, 5: da4 |
| 孩子长大 | 2: zhang3, 3: da4 |
| 罅缝 | 0: xia4, 1: feng4 |

官方来源由用户指定：<https://github.com/GitYCC/g2pW>、<https://huggingface.co/YC-Chen/g2pW>。本次网络研究尚未完成；未确认源码提交、模型文件名、模型卡、许可证或依赖版本。模型 ZIP 的预期 SHA256 为 `699f3c1fd7fb0e2c2d49ed2486826fd5bff233fee7759350a91c3b49aedc4ed2`，来自任务要求，本次未实际验证。

运行准备均应位于 `artifacts/chinese-g2pw/`：官方未修改的固定提交源码、官方模型 ZIP、解压模型目录、依赖目录 `python-packages/`，以及必要的 tokenizer 缓存。先研究固定提交的 README、依赖及模型下载行为，再安装其明确依赖到隔离目录；不能在未读官方实现的情况下把下面的调用当作已验证流程。禁止全局 pip 安装。

准备完成后，使用 PowerShell 调用（用已核实的提交和实际目录替换占位符）：

```powershell
& E:/python/Python314/python.exe codestable/tools/chinese-g2pw/probe.py `
  --source artifacts/chinese-g2pw/source `
  --revision <full-official-commit-sha> `
  --model artifacts/chinese-g2pw/model `
  --model-zip artifacts/chinese-g2pw/model.zip `
  --packages artifacts/chinese-g2pw/python-packages `
  --output artifacts/chinese-g2pw/predictions.json
```

探针校验 ZIP 哈希，记录源码/模型文件哈希、隔离依赖版本、原始逐字预测、UTF-16 起点及长度、固定断言、导入/初始化/逐调用耗时。每句运行三次，首次调用保留冷启动影响；不改写或归一化预测。`--revision` 是调用方声明，文件哈希不能代替官方提交来源验证。工具关闭 Python 字节码写回；上游初始化是否联网或写缓存仍需先研究官方实现。

边界：仅五句默认读音；不评估生产集成、姓名、自定义词典、长文本、语言混杂、声调变调或跨平台性能。当前固定输入均在 BMP，UTF-16 映射未实测补充平面字符。脚本本身尚未执行或通过语法检查。

权限阻塞原文及本次结果保存在 `artifacts/chinese-g2pw/blocked.json`。该目录由项目现有 `/artifacts/` 规则忽略。未改生产文件、规格或 issue，未执行提交。
