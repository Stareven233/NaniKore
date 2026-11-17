# 派生数据与源码修改说明

本目录的文件随 `deps/phonetics/{ja,en}/` 解压语言包一同分发，用于交代上游归属与本项目的派生内容。

## 组件

| 组件 | 版本 | 许可 | 用途 |
| --- | --- | --- | --- |
| MisakiSharp | 2.2.0（提交 `beb91a9f06ebe5e25e595b5a3c990ee616ad75e5`） | Apache-2.0（`MisakiSharp-LICENSE.txt`） | 日语分词与英语 G2P 引擎，以最小源码补丁编译进原生包 |
| UniDic | 3.1.0 | BSD-3-Clause（`UniDic-BSD.txt`、`UniDic-COPYING.txt`） | 日语词表与字符表来源；连接矩阵由上游分发版本随包提供 |

上游 `NOTICE` 全文见 `MisakiSharp-NOTICE.txt`；其中对 misaki、spaCy、PaddleSpeech、jieba、
pypinyin、MeCab、UniDic 等来源的声明继续适用。

## 对 MisakiSharp 源码的修改

补丁原文见仓库 `scripts/phonetics/patches/misakisharp-2.2.0.patch`，只改行为必需的最小处：

1. `JapaneseTagger`：词条结构增加 `Kana` 字段并从词表第五列读取书面假名，`Parse` 返回书面假名而不是发音；
   静态表改为可重建，新增 `Release()` 释放词表与连接矩阵。
2. `EnglishG2P`：关闭 misaki 的 markdown 读音指令与前导空白裁剪（界面原文必须原样对齐）；
   tokenizer 的空白词元改为并回前一个词的 Whitespace，避免 tab/换行把空白粘到下一个单词；
   新增 `Candidates()` 暴露词典里明确收录的读音。
3. `EnglishTagger` / `EnglishTokenizer` / `EspeakReplay`：静态缓存改为可重建，新增 `Release()`。

未修改上游的词典内容、发音数据、分词与矩阵算法本身。

## 派生数据

`ja_lexicon.tsv.gz` 由 UniDic 3.1.0 的 `sys.dic` 再生，不是上游原样再分发：

- 保留 MisakiSharp 原有词表格式（词面 + 每词条 `左右连接 ID,成本,发音`）；
- 每条词条追加第五列：UniDic 的第 20 字段（`kana`，书面假名）；
- `kana` 为 `*` 的条目（符号、颜文字等）写空串，表示词典没有书面读音，不回退到发音字段；
- 逐条核对词面、词条数量与顺序、左右连接 ID、成本与原始发音，身份失配为 0；
- 词表结构、连接矩阵与字符表仍取上游 MisakiSharp 分发版本。

`en_lexicon_us.tsv.gz`、`en_tokenizer.tsv.gz`、`en_tagger.bin.gz` 为上游原样裁剪，未做数据修改。

## 转换口径

英语读音由 Kokoro/misaki 音素编码转换为可读美音 IPA：闪音 `ɾ` 与喉塞音 `ʔ` 作为合法变体原样保留，
不猜回 `/t/`、`/d/`；无法解释的字符判为不可展示，不静默丢字符。
