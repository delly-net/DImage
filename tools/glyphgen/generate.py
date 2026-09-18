#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""DImage 单线笔画字库资源生成器。

本脚本产出 `Api/DImage.Api/Imaging/Glyphs/` 下的三份字形资源:

1. ``latin-rowmans.jhf`` —— Hershey Roman Simplex 的 JHF 原文(公有领域),
   **逐字节等于上游**,便于用一条 curl 直接 diff 复核。刻意**不在生成期转码**:
   运行期 `LatinGlyphs` 按上游同一套算法解码,「生成物 = 上游原件」使字形正确性
   的归因只剩「解码是否忠于上游」一个问题。
2. ``latin-ascii-map.txt`` —— ASCII 字符 → JHF 字符号映射(93 行)。
   刻意由上游 ``characterNumbers.js`` **解析生成**而非按经验公式推算:该字体
   的字符号毫无规律(A–Z = 501–526、a–z = 601–626、数字 = 700–709、
   标点散落 710–2273),公式化推算会**静默错位整段字母表**。
3. ``hanzi-medians.gz`` —— GB2312 一级 3755 字的单线笔画(medians),
   取自 Make Me a Hanzi(经 hanzi-writer-data 分发,Arphic Public License)。

   文件名只有一段扩展名,是**刻意**的:MSBuild 会把「像 ISO-639 语言代码」的中间扩展名
   当成 culture 剥掉(``bin`` = Bini 语),于是 ``hanzi-medians.bin.gz`` 的内嵌清单名
   会静默变成 ``….hanzi-medians.gz``,与按文件名后缀匹配的查找对不上,运行期才报
   「程序集内没有内嵌资源」。保持单段扩展名,「文件名 = 清单名后缀」才成立。

上游可达性(本机实测):``cdn.jsdelivr.net`` 可达;``raw.githubusercontent.com``
与 ``registry.npmjs.org`` **不可达**,故一律以 jsDelivr 为唯一来源并固定版本号。

已知偏差(实测,非遗漏):上游**映射表** ``characterNumbers.js`` 不含 ``^``(U+005E)
与 `` ` ``(U+0060),故 0x20–0x7E 的 95 个字符中覆盖 93 个;这两个字符按「缺字」路径走豆腐块占位。
缺失在**映射表**而非字体文件:``rowmans.jhf`` 有 96 条字形记录,其中 3 条(718 / 730 / 2262)
未被映射表引用;本脚本**不为它们猜测归属** —— 猜错的代价是「某字符画出别的字形」,比豆腐块难发现。

本脚本属**构建资产**(脚本入库、产出物入库),与「验收 harness 不入库」的约定不冲突。
详见 README.md。
"""

from __future__ import annotations

import concurrent.futures
import gzip
import hashlib
import json
import os
import re
import struct
import sys
import time
import urllib.parse
import urllib.request

# —————————————————————— 上游常量(固定版本,不随上游漂移) ——————————————————————

HERSHEY_JHF_URL = "https://cdn.jsdelivr.net/npm/hershey@2.1.7/font/jhf/rowmans.jhf"
HERSHEY_NUMBERS_URL = "https://cdn.jsdelivr.net/npm/hershey@2.1.7/src/characterNumbers.js"
HANZI_DATA_URL = "https://cdn.jsdelivr.net/npm/hanzi-writer-data@2.0/{name}.json"

# 统一 em 网格:1024 单位 = 1 em(沿用汉字数据自身的 1024×1024 网格)
EM_UNITS = 1024

HERE = os.path.dirname(os.path.abspath(__file__))
REPO_ROOT = os.path.abspath(os.path.join(HERE, "..", ".."))
GLYPH_DIR = os.path.join(REPO_ROOT, "Api", "DImage.Api", "Imaging", "Glyphs")
CACHE_DIR = os.path.join(os.environ.get("TEMP", "/tmp"), "DImageVerify", "Task14", "upstream-cache")

# —————————————————————— 通用工具 ——————————————————————


def fetch(url: str, retries: int = 4, timeout: int = 30) -> bytes:
    """下载一个 URL;失败重试并逐步退避。"""
    last: Exception | None = None
    for attempt in range(retries):
        try:
            # jsDelivr 的部分节点对无 UA 的请求返回 403
            req = urllib.request.Request(url, headers={"User-Agent": "DImage-glyphgen/1.0"})
            with urllib.request.urlopen(req, timeout=timeout) as resp:
                return resp.read()
        except Exception as exc:  # noqa: BLE001 —— 网络异常种类多,统一重试
            last = exc
            time.sleep(0.4 * (attempt + 1))
    raise RuntimeError(f"下载失败:{url}({last})")


def cached_fetch(url: str, cache_name: str) -> bytes:
    """带本地缓存的下载:缓存命中直接读盘,避免重复请求 jsDelivr。"""
    os.makedirs(CACHE_DIR, exist_ok=True)
    path = os.path.join(CACHE_DIR, cache_name)
    if os.path.exists(path):
        with open(path, "rb") as fh:
            return fh.read()
    data = fetch(url)
    with open(path, "wb") as fh:
        fh.write(data)
    return data


# —————————————————————— Hershey JHF ——————————————————————


class HersheyGlyph:
    __slots__ = ("number", "left", "right", "pen_commands")

    def __init__(self, number: int, left: int, right: int, pen_commands: list[list[tuple[int, int]]]):
        self.number = number
        self.left = left
        self.right = right
        self.pen_commands = pen_commands


def parse_jhf_descriptor(descriptor: str) -> HersheyGlyph:
    """解码单条 JHF 记录。

    逐条照搬上游 ``hershey@2.1.7/src/index.js`` 的 ``parseCharacterDescriptor``:

    - 列 0:5 = 字符号(十进制);
    - 列 5:8 = 顶点数(**含「左右手位」这一个伪顶点**,故实际顶点数为该值 − 1);
    - 列 8 = 左手位、列 9 = 右手位(即左右边距,决定步进);
    - 其后每 2 字符一对坐标,值 = 字符 − ``'R'``;
    - 坐标为 ``" R"``(即 x = −50、y = 0,远在字形外)表示**抬笔**,即开始新笔画。

    刻意不做任何「优化」:偏移差一位就会吃掉最后一个坐标或读越界,而字形仍
    「看起来还行」,是典型的静默错误。
    """
    base = ord("R")
    number = int(descriptor[0:5])
    left = ord(descriptor[8]) - base
    right = ord(descriptor[9]) - base
    num_vertices = int(descriptor[5:8], 10) - 1

    current: list[tuple[int, int]] = []
    pen_commands: list[list[tuple[int, int]]] = [current]
    for i in range(num_vertices):
        x = ord(descriptor[10 + i * 2]) - base
        y = ord(descriptor[11 + i * 2]) - base
        if x == -50 and y == 0:
            current = []
            pen_commands.append(current)
        else:
            current.append((x, y))
    return HersheyGlyph(number, left, right, pen_commands)


def parse_jhf(text: str) -> dict[int, HersheyGlyph]:
    glyphs: dict[int, HersheyGlyph] = {}
    for line in text.split("\n"):
        if line.strip():
            glyph = parse_jhf_descriptor(line)
            glyphs[glyph.number] = glyph
    return glyphs


def parse_character_numbers(js: str) -> dict[str, int]:
    """从上游 ``characterNumbers.js`` 解析「字符 → JHF 字符号」映射。"""
    body = js[js.index("{") + 1 : js.rindex("}")]
    entries = re.findall(r"'((?:[^'\\]|\\.)*)'\s*:\s*(\d+)", body)
    mapping: dict[str, int] = {}
    for raw_key, raw_value in entries:
        key = raw_key.replace("\\'", "'").replace("\\\\", "\\")
        if len(key) == 1:
            mapping[key] = int(raw_value)
    return mapping


def glyph_y_extent(glyph: HersheyGlyph) -> tuple[int, int]:
    ys = [y for command in glyph.pen_commands for (_, y) in command]
    return min(ys), max(ys)


# —————————————————————— 汉字 medians ——————————————————————


def gb2312_level1_chars() -> list[str]:
    """GB2312 一级汉字 3755 字:区位 0xB0A1–0xD7F9。"""
    return [
        bytes([0xB0 + i // 94, 0xA1 + i % 94]).decode("gb2312")
        for i in range(3755)
    ]


def hanzi_medians(character: str) -> list[list[list[int]]] | None:
    url = HANZI_DATA_URL.format(name=urllib.parse.quote(character))
    raw = cached_fetch(url, f"{ord(character):05X}.json")
    payload = json.loads(raw.decode("utf-8"))
    return payload.get("medians") or None


# —————————————————————— 二进制编码 ——————————————————————


def write_varint(out: bytearray, value: int) -> None:
    while True:
        byte = value & 0x7F
        value >>= 7
        if value:
            out.append(byte | 0x80)
        else:
            out.append(byte)
            return


def zigzag(value: int) -> int:
    """把有符号数映射为非负:0,-1,1,-2 → 0,1,2,3。折线坐标增量小而多,收益明显。"""
    return (value << 1) ^ (value >> 63)


def encode_hanzi(glyphs: dict[int, list[list[tuple[int, int]]]]) -> bytes:
    """编码:魔数 + 版本 + em 单位 + 码点表 + 偏移表 + varint 增量坐标数据区。

    坐标按 em 网格原样存 **y 向上**的整数(与上游一致),**不在生成期做 y 翻转**:
    翻转属渲染约定,归运行期,避免同一份数据在两处各有一半的坐标系假设。
    """
    codepoints = sorted(glyphs.keys())
    data = bytearray()
    offsets = [0]
    for codepoint in codepoints:
        strokes = glyphs[codepoint]
        write_varint(data, len(strokes))
        for stroke in strokes:
            write_varint(data, len(stroke))
            prev_x = 0
            prev_y = 0
            for (x, y) in stroke:
                write_varint(data, zigzag(x - prev_x))
                write_varint(data, zigzag(y - prev_y))
                prev_x, prev_y = x, y
        offsets.append(len(data))

    out = bytearray()
    out += b"DIMG"
    out.append(1)  # 版本
    out += struct.pack("<H", EM_UNITS)
    out += struct.pack("<I", len(codepoints))
    for codepoint in codepoints:
        out += struct.pack("<I", codepoint)
    for offset in offsets:
        out += struct.pack("<I", offset)
    out += data
    return bytes(out)


# —————————————————————— 主流程 ——————————————————————


def build_latin(report: dict) -> None:
    print("== 1/3 拉丁字库(Hershey Roman Simplex)==")
    jhf_bytes = cached_fetch(HERSHEY_JHF_URL, "latin-rowmans.jhf")
    numbers_js = cached_fetch(HERSHEY_NUMBERS_URL, "characterNumbers.js").decode("utf-8")

    glyphs = parse_jhf(jhf_bytes.decode("ascii"))
    mapping = parse_character_numbers(numbers_js)
    print(f"   JHF 字形 {len(glyphs)} 个,上游映射 {len(mapping)} 条")

    ascii_chars = [chr(c) for c in range(0x20, 0x7F)]
    covered = [c for c in ascii_chars if c in mapping and mapping[c] in glyphs]
    missing = [c for c in ascii_chars if c not in mapping]
    dangling = [(c, mapping[c]) for c in ascii_chars if c in mapping and mapping[c] not in glyphs]
    if dangling:
        raise SystemExit(f"映射指向不存在的字形,上游映射或字体不匹配:{dangling}")
    print(f"   0x20–0x7E 共 {len(ascii_chars)} 个,覆盖 {len(covered)} 个,缺 {missing!r}")

    # 黄金样例:rowmans 的 'H' —— 上下平头、无出锋,是「大写高度」的基准字形。
    #
    # 注意 y 的方向:实测该字体的 y 是**向下为正**,故 capTop 取 ymin、baseline 取 ymax。
    # 三条证据:逗号跨 [7,13] 落在基线下方;下划线在 y=11(基线之下 2 单位);
    # 下降部字母 g/j/p/q/y 取到 16,而 o 止于 9 —— 若 y 向上为准则下降部会高过大写字母,自相矛盾。
    h = glyphs[mapping["H"]]
    cap_top, baseline = glyph_y_extent(h)
    cap_height = baseline - cap_top
    print(f"   'H' 字符号={h.number} left={h.left} right={h.right} "
          f"大写顶 y={cap_top} 基线 y={baseline} 大写高度={cap_height}")
    expected_h = [[(-7, -12), (-7, 9)], [(7, -12), (7, 9)], [(-7, -2), (7, -2)]]
    actual_h = [list(cmd) for cmd in h.pen_commands]
    if actual_h != expected_h:
        raise SystemExit(f"'H' 解码与上游不符:\n  实际 {actual_h}\n  期望 {expected_h}")

    os.makedirs(GLYPH_DIR, exist_ok=True)
    jhf_out = os.path.join(GLYPH_DIR, "latin-rowmans.jhf")
    with open(jhf_out, "wb") as fh:
        fh.write(jhf_bytes)
    print(f"   -> {jhf_out}  {len(jhf_bytes)} B")

    # 映射表按字符码升序落 LF 文本:每行「十进制码点 = 字符号」,便于人工复核
    map_out = os.path.join(GLYPH_DIR, "latin-ascii-map.txt")
    lines = [
        "# ASCII → Hershey JHF 字符号;由 tools/glyphgen/generate.py 生成,勿手改。",
        f"# 来源 {HERSHEY_NUMBERS_URL}",
    ]
    lines += [f"{ord(ch)}={mapping[ch]}" for ch in ascii_chars if ch in mapping]
    with open(map_out, "wb") as fh:
        fh.write(("\n".join(lines) + "\n").encode("utf-8"))
    print(f"   -> {map_out}  {len(covered)} 条")

    report["latin"] = {
        "jhfGlyphs": len(glyphs),
        "asciiTotal": len(ascii_chars),
        "asciiCovered": len(covered),
        "asciiMissing": missing,
        "capTopUnits": cap_top,
        "baselineUnits": baseline,
        "capHeightUnits": cap_height,
        "sha256": hashlib.sha256(jhf_bytes).hexdigest(),
    }


def build_hanzi(report: dict) -> None:
    print("== 2/3 汉字字库(GB2312 一级 3755 字 medians)==")
    chars = gb2312_level1_chars()
    print(f"   目标 {len(chars)} 字")

    raw_em: dict[int, list[list[tuple[int, int]]]] = {}
    failed: list[str] = []

    # 并发度刻意取 4 而非 8:8 并发会在 jsDelivr 侧触发限流,表现为成批的
    # WinError 10054(连接被强制关闭),且失败**集中在相邻区位** —— 看起来像「某个区间没有字形」,
    # 实为限流。宁可慢,也不要一个「缺了 95 个字却像是数据如此」的结果。
    def fetch_all(targets: list[str], workers: int) -> None:
        if not targets:
            return
        with concurrent.futures.ThreadPoolExecutor(max_workers=workers) as pool:
            futures = {pool.submit(hanzi_medians, ch): ch for ch in targets}
            for done, future in enumerate(concurrent.futures.as_completed(futures), 1):
                ch = futures[future]
                if done % 500 == 0:
                    print(f"   ... {done}/{len(targets)}")
                try:
                    medians = future.result()
                except Exception as exc:  # noqa: BLE001
                    failed.append(f"{ch}({exc})")
                    continue
                if not medians:
                    failed.append(f"{ch}(上游无 medians)")
                    continue
                raw_em[ord(ch)] = [[(int(x), int(y)) for (x, y) in m] for m in medians]

    print(f"   第一遍({len(chars)} 字,并发 4)")
    fetch_all(chars, 4)

    # 第二遍:串行重取失败者。失败多为瞬时限流,串行 + 退避基本可清零。
    for attempt in range(1, 4):
        missing = [ch for ch in chars if ord(ch) not in raw_em]
        if not missing:
            break
        print(f"   重试第 {attempt} 遍({len(missing)} 字,串行)")
        failed.clear()
        fetch_all(missing, 1)
        time.sleep(2.0)

    print(f"   成功 {len(raw_em)}/{len(chars)},失败 {len(failed)}")
    if failed:
        print(f"   失败清单(前 40):{failed[:40]!r}")

    xs = [x for strokes in raw_em.values() for s in strokes for (x, _) in s]
    ys = [y for strokes in raw_em.values() for s in strokes for (_, y) in s]
    print(f"   坐标范围 x∈[{min(xs)},{max(xs)}] y∈[{min(ys)},{max(ys)}]")

    payload = encode_hanzi(raw_em)
    gz_path = os.path.join(GLYPH_DIR, "hanzi-medians.gz")
    with gzip.GzipFile(gz_path, "wb", compresslevel=9, mtime=0) as fh:
        fh.write(payload)
    size = os.path.getsize(gz_path)
    print(f"   -> {gz_path}  未压缩 {len(payload)} B / 压缩 {size} B")

    report["hanzi"] = {
        "target": len(chars),
        "covered": len(raw_em),
        "failed": failed,
        "xRange": [min(xs), max(xs)],
        "yRange": [min(ys), max(ys)],
        "rawBytes": len(payload),
        "gzipBytes": size,
    }


def main() -> int:
    report: dict = {}
    build_latin(report)
    build_hanzi(report)
    print("== 3/3 报告 ==")
    os.makedirs(CACHE_DIR, exist_ok=True)
    path = os.path.join(CACHE_DIR, "glyphgen-report.json")
    with open(path, "w", encoding="utf-8") as fh:
        json.dump(report, fh, ensure_ascii=False, indent=2)
    print(f"   {path}")
    print(json.dumps(report, ensure_ascii=False, indent=2))
    return 0


if __name__ == "__main__":
    sys.exit(main())
