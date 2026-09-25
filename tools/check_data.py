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


def main():
    data_dir = sys.argv[1] if len(sys.argv) > 1 else DEFAULT_DATA

    table_path = os.path.join(data_dir, "translation.json")
    if not os.path.isfile(table_path):
        print("找不到词表: %s" % table_path)
        return 2

    table = load_table(table_path)
    print("词表: %d 条  (%s)" % (len(table), table_path))

    counts = classify(table)
    print("  普通 %d / 模板 %d / 片段 %d"
          % (counts["普通"], counts["模板"], counts["片段"]))

    check_case_duplicates(table)
    check_brackets(table)
    check_same_text(table)

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
