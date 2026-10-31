# -*- coding: utf-8 -*-
"""把 _batch*.json 的新词条 merge 进 scopes 分表并按布局规则写回。

用法：python tools/_merge_missing_20261031.py [batch 文件…]
只新增/覆盖键，不动其它既有条目；写回使用 _table_layout.serialize 保持逐字节风格。
"""
import io
import json
import os
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import _table_layout as layout

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
DATA = os.path.join(ROOT, "data")

merged = layout.read_all(DATA)
print("现有条目:", len(merged))

total_new = 0
for batch in sys.argv[1:]:
    path = batch if os.path.isabs(batch) else os.path.join(ROOT, batch)
    entries = json.load(io.open(path, encoding="utf-8"))
    fresh = 0
    for key, value in entries.items():
        if key not in merged:
            fresh += 1
        merged[key] = value
    total_new += fresh
    print("%s: %d 条（新增 %d）" % (os.path.basename(path), len(entries), fresh))

layout.write_all(DATA, merged, apply=True)
print("合计新增 %d 条，写回完成" % total_new)
