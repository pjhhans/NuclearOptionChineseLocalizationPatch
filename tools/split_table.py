#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""词表布局工具 —— 把 data/ 下的词表规整成分类布局，或检查是否已经规整。

布局与判定规则全在 `_table_layout.py`（读宽松、写确定）。本脚本只是它的 CLI：

    python tools/split_table.py            # 报告：分类统计 + 布局漂移
    python tools/split_table.py --check    # 只检查，有漂移就 exit 1（CI / 提交前用）
    python tools/split_table.py --apply    # 落盘（写前备份到 data/.backups/）

三种「漂移」都会被报出来：
  · 某个文件的内容与 partition() 的结果不一致（有人在错误的文件里加了词条）
  · 该存在的分类文件不存在（或为空）
  · 残留的旧文件（上一版布局留下的，内容已被吸收）

用法:
    python tools/split_table.py [数据目录] [--check|--apply]
"""

import os
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))

from _table_layout import (  # noqa: E402
    MAIN_FILE, read_all, report, partition, serialize, write_all, write_backup,
)

HERE = os.path.dirname(os.path.abspath(__file__))
DEFAULT_DATA = os.path.join(os.path.dirname(HERE), "data")


def collect_drift(data_dir, merged):
    """返回 ``[(相对路径, 说明)]``。空列表 = 布局已规整。"""
    plan = partition(merged)
    drift = []

    for rel, entries in sorted(plan.items()):
        path = os.path.join(data_dir, rel.replace("/", os.sep))
        if not os.path.isfile(path):
            drift.append((rel, "缺少该分类文件（应为 %d 条）" % len(entries)))
            continue
        on_disk = open(path, "rb").read()
        if on_disk != serialize(entries):
            drift.append((rel, "内容与分类规则不符（应为 %d 条）" % len(entries)))

    # 残留：磁盘上还有、但 partition() 不再产出的词表文件
    for name in (MAIN_FILE, "templates.json", "fragments.json"):
        path = os.path.join(data_dir, name)
        if os.path.isfile(path) and name not in plan:
            drift.append((name, "残留文件（内容已并入其他分类）"))

    scopes_dir = os.path.join(data_dir, "scopes")
    if os.path.isdir(scopes_dir):
        for name in sorted(os.listdir(scopes_dir)):
            if not name.lower().endswith(".json"):
                continue
            rel = "scopes/" + name
            if rel not in plan:
                drift.append((rel, "残留分表（内容已并入其他分类）"))
    return drift


def main():
    argv = [a for a in sys.argv[1:] if not a.startswith("--")]
    flags = {a for a in sys.argv[1:] if a.startswith("--")}
    unknown = flags - {"--check", "--apply"}
    if unknown:
        print("未知参数: %s" % " ".join(sorted(unknown)))
        return 2
    data_dir = argv[0] if argv else DEFAULT_DATA
    if not os.path.isdir(data_dir):
        print("找不到数据目录: %s" % data_dir)
        return 2

    merged = read_all(data_dir)
    print("数据目录: %s" % data_dir)
    report(merged)
    print()

    drift = collect_drift(data_dir, merged)
    if drift:
        print("布局漂移 %d 处：" % len(drift))
        for rel, why in drift:
            print("  ! %-26s %s" % (rel, why))
    else:
        print("布局已规整：磁盘内容与分类规则逐字节一致。")

    if "--apply" in flags:
        if not drift:
            print("无需改动。")
            return 0
        touched = sorted({rel for rel, _ in drift})
        backups = write_backup(data_dir, [rel for rel in touched if os.path.isfile(
            os.path.join(data_dir, rel.replace("/", os.sep)))])
        print("已备份 %d 个文件到 data/.backups/" % len(backups))
        actions = write_all(data_dir, merged, apply=True)
        for action, rel, count in actions:
            if action == "keep":
                continue
            print("  %-6s %-26s %s" % (action, rel,
                                       ("%d 条" % count) if count else ""))
        print("已按分类规则重写。")
        return 0

    if "--check" in flags and drift:
        return 1
    return 0


if __name__ == "__main__":
    sys.exit(main())
