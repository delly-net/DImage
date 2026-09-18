# glyphgen —— 单线笔画字库资源生成器

`generate.py` 产出 `Api/DImage.Api/Imaging/Glyphs/` 下的三份字形资源，它们随 `dimage`
程序集内嵌发布，是 `image_draw_text` 工具的全部字形来源。

```bash
python tools/glyphgen/generate.py          # 直接写入 Api/DImage.Api/Imaging/Glyphs/
```

脚本**无参数、无配置**：上游 URL 与版本号写死在文件顶部的常量里，输出目录由仓库位置推出。
这不是偷懒——字形数据的字节必须可复现，「同一个脚本、不同参数得到不同资源」会让
「图像里的字为什么变了」变成一个无法回答的问题。

## 产出物

| 文件 | 大小 | 内容 |
|---|---|---|
| `latin-rowmans.jhf` | 3470 B | Hershey Roman Simplex 原文，**逐字节等于上游** |
| `latin-ascii-map.txt` | 849 B | 93 行 `码点=字符号`，由上游 `characterNumbers.js` 解析生成 |
| `hanzi-medians.gz` | 553181 B | 3755 个汉字的笔画骨架（解压后 644371 B） |

拉丁字体**刻意不在生成期转码**：运行期 `LatinGlyphs` 按上游同一套算法解码，
「生成物 = 上游原件」把字形正确性的归因收敛成一个问题——解码是否忠于上游。
ASCII 映射表**刻意由上游 JS 解析生成**而非按公式推算：该字体的字符号毫无规律
（`A–Z` = 501–526、`a–z` = 601–626、数字 = 700–709、标点散落 710–2273），
公式化推算会**静默错位整段字母表**。

汉字资源是自有格式（`DIMG` 魔数 + 版本 + em 刻度 + 升序码点表 + 偏移表 + zigzag 变长整数数据区），
整体 gzip。相邻顶点增量绝大多数落在一个字节内，故 3755 字压到 0.5 MB。

## 上游与可达性

| 上游 | 固定版本 | 用途 |
|---|---|---|
| `https://cdn.jsdelivr.net/npm/hershey@2.1.7/font/jhf/rowmans.jhf` | 2.1.7 | 拉丁字形 |
| `https://cdn.jsdelivr.net/npm/hershey@2.1.7/src/characterNumbers.js` | 2.1.7 | ASCII 映射 |
| `https://cdn.jsdelivr.net/npm/hanzi-writer-data@2.0/{字}.json` | 2.0 | 汉字 medians |

**jsDelivr 是本机唯一可达的上游**：`raw.githubusercontent.com` 与 `registry.npmjs.org`
实测不可达（curl 返回 `000` / SSL 35）。因此脚本以 jsDelivr 为唯一来源，并固定版本号——
上游发新版本不会改变本仓库的产出，除非有人显式改脚本里的常量。

3755 次逐字请求必然遇到限流（实测连续报 `WinError 10054`，且集中在相邻的若干区段，
看起来像「那一区没有字形」）。脚本据此把并发降到 4、加最多 3 轮串行重试与 2 秒退避，
并把每个字的响应**落本地缓存**：重跑只补缺的那几个，不再打满 3755 次请求。

## 缓存位置与「harness 不入库」的边界

上游响应缓存在 `%TEMP%\DImageVerify\Task14\upstream-cache\`。这个路径**仅仅是一个缓存目录**，
放在 `%TEMP%` 下是为了不往仓库里塞 3755 个中间文件；它是可删的，删掉后重跑会重新下载。

本目录与「验收 harness 不入库」的约定**不冲突，因为二者性质相反**：

- `tools/glyphgen/` 是**构建资产**——脚本入库、产出物入库，**删掉它字形数据就不可复现**；
- `%TEMP%\DImageVerify\` 是**临时验证代码**——随时可删，不入库。

若后续任务整理仓库时看到 `tools/glyphgen/`，请勿按「临时脚本」一类的直觉删除。

## 复现与校验

产出是确定性的：同一组上游 URL 反复运行得到逐字节相同的文件。校验和见仓库根的
[THIRD-PARTY-NOTICES.md](../../THIRD-PARTY-NOTICES.md)，其中也记录了许可义务与派生链。

脚本自带**黄金断言**：解析出的 `H` 若与预期笔画不符，或大写高度/基线偏离
`TextLimits.LatinCapHeightJhfUnits` / `LatinBaselineJhfUnits`（21 / 9），直接 `SystemExit`。
断言失败只可能意味着上游数据被替换或解码算法被改错——两者都必须当场停下，
而不是产出一份「看起来能画」的资源。

## 四处与常见资料相反的实测结论

写在代码注释里，也抄在此处，因为每一条都曾被参考资料写反：

1. **JHF 的顶点数含左右手位**：顶点数须按 `cols5:8 − 1` 计（照搬上游算法，不做「优化」）。
2. **Hershey 的 y 向下为正**，基线在 `y = +9`：故拉丁坐标只做 `(y − 9)` 偏移，**不得取负**。
   证据是自洽的：逗号跨 `[7, 13]`、下划线在 `y = 11`、下伸部 `g j p q y` 到 `16` 而 `o` 停在 `9`。
   若按「y 向上为正」取负，全部文字**上下颠倒**——仍是可辨认的形状，不报错。
3. **汉字的 y 向上为正**，基线在 `y = 0`：故汉字坐标**必须取负**。
   两套数据的翻转各自留在自己的加载器里，字形对象不带来源侧的方向假设。
4. **资源文件名不得带多段扩展名**（本 SDK 实测）：MSBuild 会把「像 ISO-639 语言代码」的
   中间扩展名当 culture 剥离——`alpha.bin.gz` 的清单名是 `…​.alpha.gz`（`bin` 是 Bini 语的三字母
   代码），而 `epsilon.tar.gz` 原样保留（`tar` 不是语言代码）。故产出物名为 `hanzi-medians.gz`
   而非 `hanzi-medians.bin.gz`：后者会让内嵌清单名比文件名**少一段**，与运行期按文件名后缀
   匹配的查找对不上，只在真的画第一个汉字时才炸。**改这个文件名前先读 `DImage.Api.csproj`。**
