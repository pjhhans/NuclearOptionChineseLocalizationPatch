#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""词表数据自检 —— 改动 data/ 之后跑一遍。

检查项（都是曾经真的踩过的坑）：
  1. 文件编码与行尾：无 BOM、纯 LF        —— 否则整文件行尾假 diff
  2. 键的唯一性（含忽略大小写的重复）      —— 运行时是 OrdinalIgnoreCase 字典，重键会静默覆盖
  3. 四类键的分类计数                     —— 与运行时载入逻辑对得上
  4. 译文里不应出现原文没有的括号         —— 译名规范
  5. 同原文多译文                         —— 同一段英文在表里只应有一个译文
  6. 片段键长度非空                       —— 空的片段键会匹配一切
  7. 键严格按码点升序、且无字面重复键     —— 保证「批量补词条」永远是干净的最小 diff
  8. 已裁决废弃的译法不得回流             —— 口径统一后靠这一条守住（见 RETIRED_TERMS）
  9. 英文舰名残留（提示级）               —— 舰名统一中文后，值里不该再有「英文词 + 级」

用法:
    python tools/check_data.py [数据目录]
"""

import collections
import io
import json
import os
import re
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
DEFAULT_DATA = os.path.join(os.path.dirname(HERE), "data")

SCOPE_RE = re.compile(r"^\[[^\]]{1,40}\]")
TAG_RE = re.compile(r"<[^>]*>")

# 已裁决废弃的译法 → 现行口径。改词表时若把旧写法带回来，这里会直接报错。
#   · 干扰弹：chaff(箔条弹) 与 flare(热诱弹) 统一 —— 游戏里这类对抗措施只对红外弹有效
#   · 转管炮：原「转轴炮台」，用户裁决「转轴 → 转管」
#   · 自动机炮：原「自动机枪 / 自动炮」，口径规则「≥20mm 为炮、<20mm 为枪」
#   · 内置机炮：原「内置火炮」，同一组 UI 标签（Internal Gun / Internal guns）应一致
#   · 舰名统一中文（2026-09-26）：Cursor Class 光标级 / Argus Class 阿尔戈斯级 /
#     Atlas Class 阿特拉斯级 / Devotion Class 忠诚级 / Ironside Class 堡垒级 /
#     Manticore Class 蝎尾狮级 / Styx-class 斯堤克斯级 / Surf Class 涌浪级 /
#     Tranche Class 裁波级 / Andromeda class 安德洛墨达级（注：「X 级」不留空格）
RETIRED_TERMS = {
    "箔条弹": "干扰弹",
    "热诱弹": "干扰弹",
    "转轴炮": "转管炮",
    "自动机枪": "自动机炮",
    "内置火炮": "内置机炮",
    "Cursor 级": "光标级",
    "Argus 级": "阿尔戈斯级",
    # 2026-09-26 「舰名统一中文」：以下 8 条英文舰名全部改中文（用户逐条裁决）
    "Atlas 级": "阿特拉斯级",
    "Devotion 级": "忠诚级",
    "Ironside 级": "堡垒级",
    "Manticore 级": "蝎尾狮级",
    "Styx 级": "斯堤克斯级",
    "Surf 级": "涌浪级",
    "Tranche 级": "裁波级",
    "Andromeda 级": "安德洛墨达级",
}

# 「英文词 + 级」= 还没中文化的舰名（型号 / 数字单位不会被匹配：要求 ≥3 个字母起头）
EN_SHIP_RE = re.compile(r"([A-Za-z][A-Za-z\-]{2,})\s*级")

problems = []
notes = []


def fail(message):
    problems.append(message)


def load_table(path):
    raw = open(path, "rb").read()
    if raw[:3] == b"\xef\xbb\xbf":
        fail("translation.json 带 BOM，应去掉")
    crlf = raw.count(b"\r\n")
    lone_lf = raw.count(b"\n") - crlf
    if crlf:
        fail("translation.json 含 %d 处 CRLF，应为纯 LF" % crlf)
    try:
        return json.loads(raw.decode("utf-8-sig"))
    except Exception as exc:                     # noqa: BLE001
        fail("translation.json 解析失败: %s" % exc)
        return {}


def classify(table):
    counts = collections.Counter()
    empty_fragment = []
    for key, value in table.items():
        if not isinstance(value, str):
            fail("译文不是字符串: %r" % key[:60])
            continue
        if key and key[0] == "~" and len(key) > 1:
            counts["模板"] += 1
        elif len(key) > 2 and key[:2] in (">>", "<<", "=="):
            counts["片段"] += 1
            if not key[2:].strip():
                empty_fragment.append(key)
        else:
            counts["普通"] += 1
    if empty_fragment:
        fail("片段键内容为空（会匹配一切）: %r" % empty_fragment[:5])
    return counts


def check_key_order(path):
    """键必须严格按码点升序，且不得有字面重复键。

    为什么单独查这一条：`json.loads` 装进 dict 会**静默丢掉重复键**，
    于是上面所有基于 dict 的检查都看不到它们，`len(table)` 也会少报。
    而「排序」是批量补词条流程的约定 —— 靠 append + 稳定排序 保证 diff 只动新增行；
    一旦顺序被打乱，一次补 10 条会在 git 里显示成整块重排，review 就失效了。
    """
    pairs = json.loads(open(path, "rb").read().decode("utf-8-sig"),
                       object_pairs_hook=list)
    keys = [k for k, _ in pairs]

    if len(keys) != len(set(keys)):
        seen, dup = set(), []
        for k in keys:
            if k in seen and k not in dup:
                dup.append(k)
            seen.add(k)
        fail("存在字面重复键（json 装 dict 时会静默丢键）: %r" % dup[:5])

    violations = [(keys[i], keys[i + 1]) for i in range(len(keys) - 1)
                  if keys[i] > keys[i + 1]]
    if violations:
        fail("键未按码点升序，%d 处（会破坏最小 diff）: %r"
             % (len(violations), violations[:3]))


def check_case_duplicates(table):
    buckets = collections.defaultdict(list)
    for key in table:
        buckets[key.lower()].append(key)
    dups = {k: v for k, v in buckets.items() if len(v) > 1}
    if not dups:
        return
    dangerous = [v for v in dups.values() if len({table[k] for k in v}) > 1]
    notes.append("忽略大小写后重复的键 %d 组（译文相同则无害）" % len(dups))
    for group in dangerous:
        fail("大小写重复且译文不同（会静默覆盖）: %r -> %r"
             % (group, [table[k] for k in group]))


def check_brackets(table):
    """译文不应引入原文没有的括号。原文有括号的忠实保留，不在此列。"""
    for key, value in table.items():
        if key[0] in "~><=":
            continue
        plain_key = TAG_RE.sub("", SCOPE_RE.sub("", key))
        if "(" in plain_key or "（" in plain_key or "[" in plain_key:
            continue
        if any(ch in value for ch in "()（）"):
            fail("译文引入了原文没有的括号: %r -> %r" % (key[:60], value[:60]))


def check_same_text(table):
    """剥掉作用域前缀后，同一原文只应有一个译文。"""
    groups = collections.defaultdict(set)
    for key, value in table.items():
        if not key or key[0] in "~><=":
            continue
        plain = SCOPE_RE.sub("", key)
        norm = re.sub(r"\s+", " ", TAG_RE.sub("\u0001", plain)).strip().lower()
        groups[norm].add(value)
    multi = {k: v for k, v in groups.items() if len(v) > 1}
    if multi:
        notes.append("同原文多译文 %d 组:" % len(multi))
        for norm, values in list(multi.items())[:10]:
            notes.append("    %r -> %r" % (norm[:50], sorted(values)))


def check_retired_terms(table):
    """口径统一之后，旧的译法不许再被写回来。

    注意「转轴炮」而不是「转轴」：`Roll Axis -> 滚转轴` 是合法译文。
    """
    hits = collections.Counter()
    for key, value in table.items():
        if not isinstance(value, str):
            continue
        for old in RETIRED_TERMS:
            if old in value:
                hits[old] += 1
                fail("译文里出现已废弃的译法 %r（现行 %r）: %r -> %r"
                     % (old, RETIRED_TERMS[old], key[:50], value[:70]))
    if not hits:
        notes.append("废弃译法扫描：%d 项口径均无回流" % len(RETIRED_TERMS))


def check_ship_names(table):
    """舰名已统一中文（2026-09-26 用户裁决）—— 值里不该再出现「英文词 + 级」。

    只提示不报错：型号（`PAB-250LR`）与数字单位（`8t 级装甲车`）都不会命中
    （正则要求 ≥3 个字母起头、且紧跟「级」），命中即说明有新的英文舰名待中文化。
    """
    hits = collections.Counter()
    for key, value in table.items():
        if not isinstance(value, str):
            continue
        for match in EN_SHIP_RE.finditer(value):
            hits[match.group(1)] += 1
    if hits:
        notes.append("待中文化的英文舰名 %d 个: %s"
                     % (len(hits), " / ".join(sorted(hits))))


def main():
    data_dir = sys.argv[1] if len(sys.argv) > 1 else DEFAULT_DATA

    table_path = os.path.join(data_dir, "translation.json")
    if not os.path.isfile(table_path):
        print("找不到词表: %s" % table_path)
        return 2

    table = load_table(table_path)
    print("词表: %d 条  (%s)" % (len(table), table_path))

    check_key_order(table_path)

    counts = classify(table)
    print("  普通 %d / 模板 %d / 片段 %d"
          % (counts["普通"], counts["模板"], counts["片段"]))

    check_case_duplicates(table)
    check_brackets(table)
    check_same_text(table)
    check_retired_terms(table)
    check_ship_names(table)

    for note in notes:
        print("提示: " + note)

    if problems:
        print("\n发现问题 %d 处:" % len(problems))
        for item in problems[:40]:
            print("  ✗ " + item)
        if len(problems) > 40:
            print("  ... 其余 %d 处省略" % (len(problems) - 40))
        return 1

    print("\n自检通过。")
    return 0


if __name__ == "__main__":
    sys.exit(main())
