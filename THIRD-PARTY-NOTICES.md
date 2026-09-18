# 第三方资源与许可

本项目自带的两份**单线笔画字库**数据随 `dimage` 程序集内嵌发布。它们不是本项目的原创作品，
此处记录来源、许可与改动，以及复核方式。

| 资源文件 | 内容 | 上游 | 许可 |
|---|---|---|---|
| `Api/DImage.Api/Imaging/Glyphs/hanzi-medians.gz` | 3755 个汉字的笔画骨架 | [hanzi-writer-data](https://www.npmjs.com/package/hanzi-writer-data) → [Make Me a Hanzi](https://github.com/skishore/makemeahanzi) → 文鼎字体 | Arphic Public License |
| `Api/DImage.Api/Imaging/Glyphs/latin-rowmans.jhf` | 95 个可打印 ASCII 字符的单线笔画 | [hershey](https://www.npmjs.com/package/hershey)（`font/jhf/rowmans.jhf` 的逐字节副本） | 字体数据属公有领域；打包代码为 MIT |
| `Api/DImage.Api/Imaging/Glyphs/latin-ascii-map.txt` | 字符 → Hershey 字形编号的映射表 | 同上（由该包的 `src/characterNumbers.js` 导出） | 同上 |

字库的**许可全文**见 [Licenses/Arphic-Public-License.txt](Licenses/Arphic-Public-License.txt)。

---

## 汉字字库：Arphic Public License

### 派生链

```
文鼎科技 AR PL 系列字体（AR PL UMing / AR PL UKai，1999）
        │  以 Arphic Public License 发布
        ▼
Make Me a Hanzi（skishore/makemeahanzi）—— 提取每字的笔画数据，产出 strokes / medians
        ▼
hanzi-writer-data@2.0.1（chanind）—— 把上述数据按字拆成 JSON，逐字一个文件
        ▼
本项目 tools/glyphgen/generate.py —— 取 medians，重编码为单一压缩资源
```

### 本项目做了哪些改动

Make Me a Hanzi 对上游字体做了字形提取，本项目在其上又做了以下改动，均属许可允许的范围：

1. **只取 `medians`（每笔的中心线），丢弃 `strokes`（轮廓）。** 这决定了渲染结果是**单线笔画字**，
   而非印刷体。这也是本项目资源只有 0.64 MB（解压后）的原因——两份字段的体积相差一个数量级。
2. **只保留 GB2312 一级字库的 3755 个汉字**，其余码点（含中文标点、全角符号）一律丢弃。
   因此 `，。！？「」` 等标点在本项目中会渲染为缺字豆腐块——**这是数据集的边界，不是缺陷**。
3. **重编码**：坐标由上游的绝对整数对改为 zigzag 变长整数增量，整体 gzip 压缩。
4. **未改动字形本身的形状**：每条笔画仍是上游 `medians` 的同一串顶点，仅做整数增量编码；
   `y` 轴的翻转发生在运行期（`HanziGlyphs`），不在数据里。

Arphic Public License 要求衍生作品保留许可证、且**以同一许可证发布**。本项目据此：
字库数据仍以 Arphic Public License 发布（见上表），许可全文随仓库提供。

### 上游许可声明的原文

`hanzi-writer-data@2.0.1` 的 `package.json` 中 `"license": "SEE LICENSE IN ARPHICPL.TXT"`；
其 `ARPHICPL.TXT` 即 [Licenses/Arphic-Public-License.txt](Licenses/Arphic-Public-License.txt)
的逐字节副本（本仓库未改名之外的内容，改名只为让文件名自解释）。
`Make Me a Hanzi` 的许可声明在 [skishore/makemeahanzi](https://github.com/skishore/makemeahanzi) 的
`COPYING.md`，指向同一份 Arphic Public License。

---

## 拉丁字库：Hershey 字体

`rowmans.jhf` 是 Hershey Roman Simplex 字体。Hershey 字体由美国海军武器实验室（NBS/NSWC）
于 1960 年代开发，属**公有领域**，可自由使用与再分发。

本项目使用的副本取自 npm 包 `hershey@2.1.7` 的 `font/jhf/rowmans.jhf`，**逐字节未改**；
该包的打包代码为 MIT。字符 → 字形编号的映射表由同一包的 `src/characterNumbers.js` 导出，
生成时只做「JS 对象字面量 → `码点=编号` 文本行」的搬运，编号本身未经计算或猜测。

**已知覆盖缺口**：上游的字符号映射表（`hershey@2.1.7` 的 `src/characterNumbers.js`）
只覆盖 95 个可打印 ASCII 字符中的 **93 个**——`^`（U+005E）与 `` ` ``（U+0060）不在表中，
故这两个字符在本项目中渲染为豆腐块。这不是解析错误。

需要说清的是缺口的**位置**：`rowmans.jhf` 文件本身有 **96 条字形记录**，映射表只引用了其中 93 条，
另有 3 条（编号 `718`、`730`、`2262`）未被任何 ASCII 字符引用。本项目**不为这 3 条记录猜测归属**——
把它们中的某一条指派给 `^` 或 `` ` `` 属于没有依据的推断，而猜错的代价是「某个字符画出了另一个字形」，
比豆腐块难发现得多。

---

## 复核方式

三份资源**均为确定性产物**：同一组上游 URL 反复运行生成脚本得到逐字节相同的文件。
校验和如下（`sha256sum`）：

```
8718fb129c0f6bce89c84fe41bc467e39534d215a6f7c3220cc5789a8a7d8618  latin-rowmans.jhf
544b73cba521ac1203f31d29509a5e49fa10fb3dbcf0fa11b644fa723f42621d  latin-ascii-map.txt
0b50e152f5c82c86f7ccbaac887c6cdf8c2bcd9a204bb2967837610c54b87238  hanzi-medians.gz
```

复核步骤：

```bash
# 1. 拉丁字库与上游逐字节比对(应当无输出)
curl -s https://cdn.jsdelivr.net/npm/hershey@2.1.7/font/jhf/rowmans.jhf \
  | diff - Api/DImage.Api/Imaging/Glyphs/latin-rowmans.jhf

# 2. 确认 ^(94) 与 `(96) 确实不在上游映射表中(应当无输出)
curl -s https://cdn.jsdelivr.net/npm/hershey@2.1.7/src/characterNumbers.js | grep -E '(^|[^0-9])(94|96)\s*:'

# 3. 重新生成全部资源并比对校验和(脚本直接写回仓库目录,无输出目录参数)
python tools/glyphgen/generate.py && sha256sum Api/DImage.Api/Imaging/Glyphs/*.jhf Api/DImage.Api/Imaging/Glyphs/latin-ascii-map.txt Api/DImage.Api/Imaging/Glyphs/hanzi-medians.gz
```

生成脚本的注释中记录了三处**实测结论**（与某些参考资料的说法相反，故写在代码旁）：

- Hershey JHF 的**顶点数含一个哨兵**，笔画数须按 `cols5:8 − 1` 计；
- Hershey 的 `y` **向下为正**、基线在 `y = +9`，故拉丁坐标只需偏移、**不得取负**；
- 汉字上游数据的 `y` **向上为正**、基线在 `y = 0`，故汉字坐标**必须取负**。
