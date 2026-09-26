#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""翻译审核台 —— 零依赖的本地 Web 工具，用来审阅与修改 data/ 下的整套词表。

词表是**按分类拆成多个文件**的（`translation.json` / `templates.json` /
`fragments.json` / `scopes/*.json`，规则见 `tools/_table_layout.py`）。审核台对外
只暴露一份**合并视图**（键按码点升序，与运行时查表口径一致），写回时逐文件做最小 diff、
新增的键按分类规则落进它该去的文件 —— 于是「分类」这件事在编辑体验上是透明的。

为什么是这样设计的（每一条都是被这个仓库的既有约定逼出来的）
------------------------------------------------------------------
* **只用标准库。** 本目录随仓库分发，不接受第三方依赖；`http.server` 足够。
* **默认只读。** 只有显式点「保存」才会写盘，写之前先备份、写之后必自检。
* **只动该动的行。** 词表是「一键一行、按码点升序」的巨型 JSON，补词条的 diff
  之所以可读，全靠这个约定。整表 dump 重排会制造几千行假 diff、让 review 失效，
  所以编辑走**逐行替换**：没被编辑的键，那一行连一个字节都不动。
* **写完必自检、自检失败必回滚。** 绕过 `json.loads` 的手工行替换有风险，因此每
  次落盘后重新解析并按不变量复核（升序 / 无字面重复键 / 无 BOM / 纯 LF / 行数一致 /
  未触碰行逐字节不变），任一条不成立就从备份还原，绝不把坏表留在磁盘上。
* **序列化必须逐字复现既有风格。** `json.dumps(x, ensure_ascii=False)` 恰好就是这个
  仓库的写法：控制字符（U+0001 占位符）转义成 `\\u0001`，中文与其它字符原样保留。
  这一点有断言兜底（见 `--selftest`），风格一旦漂移会立刻报错而不是静默产生 diff。

只读的审视能力（审核台的主要价值）
------------------------------------------------------------------
1. 逐条风险标签：译文==原文 / 不含中文 / 含拉丁字母 / 首尾空白 / 引入原文没有的括号 /
   `\\u0001` 占位数量不匹配 / 同原文多译文 / 忽略大小写重复键 / 译文过长。
2. 显示宽度估算（CJK 记 2、其余记 1）—— 短槽位标签要按同组兄弟的字数定长，这个数字有用。
3. 同组键：同一个作用域前缀下的所有键排在一起，便于对齐措辞与长度。
4. 运行期清单：把插件目录里的 `missing.json` / `untranslated.json` 拉进来对照
   （**近似判定**，只查键是否存在与排除名单，不复刻片段拼接与切片，见 README）。

用法:
    python tools/review/review_server.py                 # 自动开浏览器，默认 127.0.0.1:8765
    python tools/review/review_server.py --port 9000
    python tools/review/review_server.py --no-browser
    python tools/review/review_server.py --plugin-dir "D:\\...\\plugins\\NuclearOptionChineseLocalizationPatch"
    python tools/review/review_server.py --selftest      # 只跑一致性自检后退出（不进服务）
"""

import argparse
import bisect
import collections
import datetime
import difflib
import hashlib
import io
import json
import os
import re
import shutil
import subprocess
import sys
import tempfile
import threading
import unicodedata
import webbrowser
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from urllib.parse import urlparse, parse_qs, unquote

try:                                            # 让 Windows 控制台也能打印中文
    sys.stdout.reconfigure(encoding="utf-8")
    sys.stderr.reconfigure(encoding="utf-8")
except Exception:                               # noqa: BLE001
    pass

PLUGIN_NAME = "NuclearOptionChineseLocalizationPatch"
HERE = os.path.dirname(os.path.abspath(__file__))
REPO = os.path.dirname(os.path.dirname(HERE))   # tools/review -> tools -> <repo>
STATIC_DIR = os.path.join(HERE, "static")

# 词表里出现的两种键前缀之外，还有作用域写法 `[Scope]原文`
SCOPE_RE = re.compile(r"^\[([^\]\n]{1,40})\]")
TAG_RE = re.compile(r"<[^>]*>")
CJK_RE = re.compile(r"[\u3400-\u4dbf\u4e00-\u9fff\uf900-\ufaff]")
LATIN_RE = re.compile(r"[A-Za-z]")

# 词表布局（分类文件规则）的唯一事实来源，与插件加载器、check_data.py、split_table.py 共用。
sys.path.insert(0, os.path.dirname(HERE))
from _table_layout import CATEGORIES, home_file, table_files   # noqa: E402

KIND_LABELS = {
    "plain": "裸键",
    "scoped": "作用域键",
    "template": "模板",
    "prefix": "前缀片段",
    "suffix": "后缀片段",
    "middle": "中段片段",
}

# 风险标签：level 用于默认排序（high 优先看）
FLAG_META = {
    "bracket":   ("引入原文没有的括号", "high", "译文里出现了原文没有的 ()（）——译名规范禁止，check_data.py 会直接判失败"),
    "esc":       ("占位符多于原文",     "high", "模板键里的 \\u0001 是原文字符占位符。译文里的占位符**多于**键里时，回填原文标签会错位；少于（改写成字面 <b> 标签）是合法写法。"),
    "casedup":   ("大小写重复键",       "mid",  "运行时是 OrdinalIgnoreCase 字典，译文不同则后者静默覆盖前者"),
    "multi":     ("同原文多译文",       "mid",  "同一段英文在表里应有唯一译文；只有目标代号 identity 与作用域分歧是刻意例外"),
    "space":     ("译文首尾空白",       "mid",  "译文与原文的首尾空白不一致（原文有则忠实保留，无则不该多）"),
    "ascii":     ("译文含拉丁字母",     "low",  "可能是未译尽，也可能是有意保留的型号/缩写，逐条判断"),
    "no-cjk":    ("译文不含中文",       "low",  "identity 键或刻意保留英文的项；若不该保留则是漏译"),
    "identity":  ("译文等同原文",       "low",  "刻意的 identity（目标代号等）不算漏译，但值得确认是否真有必要"),
    "long":      ("译文显著偏长",       "low",  "显示宽度超过原文 3 倍，短槽位标签有撑破风险"),
    "runtime":   ("出现在运行期清单",   "mid",  "实机 missing/untranslated 里出现过这条（近似判定）"),
}


# --------------------------------------------------------------------------
# 路径解析
# --------------------------------------------------------------------------
def _read_game_dir_props():
    """仓库内的 GameDir.props（gitignore，每台机器自己填）里的 GameDir。"""
    path = os.path.join(REPO, "GameDir.props")
    if not os.path.isfile(path):
        return None
    try:
        text = io.open(path, encoding="utf-8").read()
    except OSError:
        return None
    m = re.search(r"<GameDir>(.*?)</GameDir>", text, re.S)
    return m.group(1).strip() if m else None


def resolve_plugin_dir(explicit):
    """插件运行目录：--plugin-dir > NUCLEAR_OPTION_DIR > GameDir.props > 仓库上两级。

    与 csproj 的四级 GameDir 解析链保持同一口径，这样「审核台保存后同步过去」
    和「dotnet build -t:DeployData」指向的一定是同一个目录。
    """
    if explicit:
        return os.path.abspath(explicit)
    env = os.environ.get("NUCLEAR_OPTION_DIR")
    if env:
        return os.path.join(os.path.abspath(env), "BepInEx", "plugins", PLUGIN_NAME)
    props = _read_game_dir_props()
    if props:
        return os.path.join(props, "BepInEx", "plugins", PLUGIN_NAME)
    return os.path.abspath(os.path.join(REPO, "..", "..", "BepInEx", "plugins", PLUGIN_NAME))


# --------------------------------------------------------------------------
# 词表：内存模型 + 序列化 + 逐行写回
# --------------------------------------------------------------------------
def dumps_line(key, value):
    """复现仓库既有的行序列化风格（这是全文件唯一一处序列化入口）。"""
    return "  %s: %s," % (json.dumps(key, ensure_ascii=False),
                          json.dumps(value, ensure_ascii=False))


def strip_comma(line):
    line = line.rstrip()
    return line[:-1] if line.endswith(",") else line


# 文件结构：`{` + N 行条目 + `}` + 末尾的 `"\n"`。
# `text.split("\n")` 得到 **N + 3** 个元素（最后一个是空串），所以「行数 = 条目数 + 3」。
# 这个 3 曾被写成 2 —— 那是把「编辑器里显示的行数」当成了 split 后的元素数，
# 于是每次保存的自检都误判「行数与条目数不匹配」而回滚。留成常量，别再手算。
LINE_OVERHEAD = 3


def display_width(text):
    """CJK 全角记 2、其余记 1 —— 用来判断短槽位标签会不会撑破。"""
    return sum(2 if unicodedata.east_asian_width(ch) in ("F", "W") else 1 for ch in text)


class Table(object):
    """data/ 下**整套分类词表**的内存模型（不是一个文件）。

    对外只暴露一份**合并视图**：`pairs` 是全表键值对（按码点升序），`flags` /
    `entry()` / `group_keys()` 都基于它。写回则逐文件做 —— 每个文件持有自己的
    `raw` 与 `lines`，没被改的行原样搬过去，这是「最小 diff 写回」的前提。

    文件清单与「新键该落哪个文件」由 `_table_layout` 决定（`table_files` /
    `home_file`），与插件加载器、`check_data.py`、`split_table.py` 共用同一份规则。
    """

    def __init__(self, data_dir):
        self.data_dir = data_dir
        self.load()

    def _path(self, rel):
        return os.path.join(self.data_dir, rel.replace("/", os.sep))

    # ---- 载入 ----------------------------------------------------------
    def load(self):
        self.files = [rel for rel, _ in table_files(self.data_dir)]
        self.file_raw = {}
        self.file_lines = {}
        self.file_pairs = {}
        self.load_problems = []

        for rel, path in table_files(self.data_dir):
            raw = open(path, "rb").read()
            self.file_raw[rel] = raw
            text = raw.decode("utf-8-sig")
            self.file_lines[rel] = text.split("\n")
            self.file_pairs[rel] = json.loads(text, object_pairs_hook=lambda kv: kv)
            self.load_problems += ["%s: %s" % (rel, p) for p in
                                   self._structure_problems(rel, raw, text)]

        # 合并视图。**跨文件重键**必须报出来：加载器是「后读到的覆盖先读到的」，
        # 同一个键出现在两个文件里就是同原文两份译文，且哪份生效取决于文件名排序。
        self.pairs = sorted(((k, v) for rel in self.files for k, v in self.file_pairs[rel]),
                            key=lambda kv: kv[0])
        # 合并视图按**码点升序**排序，于是「第几行」在合并视图里没有意义 ——
        # 但「这条在哪个文件、文件内第几行」有意义（要能定位到磁盘）。
        self.file_of_key = {}       # key -> 相对路径（如 scopes/ui.json）
        self.entry_index = {}       # key -> 在 file_pairs[rel] 里的**下标**（0 起）
        self.line_of_key = {}       # key -> 真实文件行号（1 起；第 1 行是 "{"）
        seen = {}
        for rel in self.files:
            for index, (key, _) in enumerate(self.file_pairs[rel]):
                if key in seen and seen[key] != rel:
                    self.load_problems.append(
                        "跨文件重键：%r 同时出现在 %s 与 %s（同一原文只能有一个译文）"
                        % (key, seen[key], rel))
                seen[key] = rel
                self.file_of_key[key] = rel
                self.entry_index[key] = index
                self.line_of_key[key] = index + 2
        self.key_to_index = {k: i for i, (k, _) in enumerate(self.pairs)}

        self.md5 = hashlib.md5(
            b"".join(self.file_raw[rel] for rel in self.files)).hexdigest()
        self.mtime = max(os.path.getmtime(self._path(rel)) for rel in self.files) \
            if self.files else None
        self._build_flags()

    def _structure_problems(self, rel, raw, text):
        """单文件的首尾结构 + 行数与条目数一致 —— 逐行替换的全部前提都挂在这上面。"""
        problems = []
        lines = self.file_lines[rel]
        pairs = self.file_pairs[rel]
        if raw[:3] == b"\xef\xbb\xbf":
            problems.append("文件带 BOM，应为无 BOM")
        if raw.count(b"\r\n"):
            problems.append("含 %d 处 CRLF，应为纯 LF" % raw.count(b"\r\n"))
        if len(lines) < 3 or lines[0] != "{" or lines[-1] != "" or lines[-2] != "}":
            problems.append("首尾结构异常（应为 `{` / 各一行条目 / `}` / 空尾行）")
        body = lines[1:-2] if len(lines) >= 3 else []
        if len(body) != len(pairs):
            problems.append("正文行数 %d 与条目数 %d 不一致" % (len(body), len(pairs)))
        keys = [k for k, _ in pairs]
        if len(keys) != len(set(keys)):
            problems.append("存在字面重复键（json 装 dict 时会静默丢键）")
        violations = [(keys[i], keys[i + 1]) for i in range(len(keys) - 1)
                      if keys[i] > keys[i + 1]]
        if violations:
            problems.append("键未按码点升序，%d 处" % len(violations))
        return problems

    def _build_flags(self):
        """全局统计一次（大小写重复、同原文多译文），再逐条打标。"""
        buckets = collections.defaultdict(list)
        for k, _ in self.pairs:
            buckets[k.lower()].append(k)
        case_dups = set()
        for group in buckets.values():
            if len(group) > 1:
                case_dups.update(group)

        groups = collections.defaultdict(set)
        group_members = collections.defaultdict(list)
        for k, v in self.pairs:
            if not k or k[0] in "~><=":
                continue
            norm = re.sub(r"\s+", " ", TAG_RE.sub("\u0001", SCOPE_RE.sub("", k))).strip().lower()
            groups[norm].add(v)
            group_members[norm].append(k)
        multi_keys = set()
        for norm, values in groups.items():
            if len(values) > 1:
                multi_keys.update(group_members[norm])

        self.case_dups = case_dups
        self.multi_keys = multi_keys

        self.flags = {}
        for k, v in self.pairs:
            self.flags[k] = self._flags_of(k, v, case_dups, multi_keys)

    @staticmethod
    def _flags_of(key, value, case_dups, multi_keys):
        out = []
        if value == key:
            out.append("identity")
        if not CJK_RE.search(value):
            out.append("no-cjk")
        if LATIN_RE.search(value):
            out.append("ascii")
        if value != value.strip() and value.strip():
            out.append("space")
        if key[0:1] not in "~><=":
            plain = TAG_RE.sub("", SCOPE_RE.sub("", key))
            if not any(ch in plain for ch in "()[]（"):
                if any(ch in value for ch in "()（）"):
                    out.append("bracket")
        # `\u0001` 只在**模板键**里出现（键 = 归一化原文，标签被替换成占位符）。
        # 规则有方向性：模板的值允许占位符**少于**键 —— 译者把 `\u0001` 换成字面
        # `<b>…</b>` 是合法写法（实测 12 条这么做）。只有「值里比键里多」才真会
        # 回填错位。按「数量不等」判会造出 12 条假警报（本工具第一版就是这么错的）。
        if value.count("\u0001") > key.count("\u0001"):
            out.append("esc")
        if key in case_dups:
            out.append("casedup")
        if key in multi_keys:
            out.append("multi")
        plain_key = key[1:] if key[:1] == "~" else (key[2:] if key[:2] in (">>", "<<", "==") else key)
        if display_width(value) > max(display_width(plain_key) * 3, 12) and len(value) > 12:
            out.append("long")
        return out

    # ---- 查询 ----------------------------------------------------------
    def scope_of(self, key):
        m = SCOPE_RE.match(key)
        return m.group(1) if m else ""

    @staticmethod
    def kind_of(key):
        if key[:1] == "~" and len(key) > 1:
            return "template"
        if key[:2] in (">>", "<<", "=="):
            return {">>": "prefix", "<<": "suffix", "==": "middle"}[key[:2]]
        return "scoped" if SCOPE_RE.match(key) else "plain"

    @staticmethod
    def plain_of(key):
        if key[:1] == "~":
            return key[1:]
        if key[:2] in (">>", "<<", "=="):
            return key[2:]
        return SCOPE_RE.sub("", key)

    def entry(self, key):
        if key not in self.file_of_key:
            return None
        index = self.key_to_index[key]
        value = self.pairs[index][1]
        plain = self.plain_of(key)
        return {
            "key": key,
            "value": value,
            "kind": self.kind_of(key),
            "scope": self.scope_of(key),
            "plain": plain,
            "file": self.file_of_key[key],           # 这条词条落在哪个分类文件
            "home": home_file(key),                  # 写出时该去哪个文件（可能有布局漂移）
            "line": self.line_of_key[key],           # 该文件内的真实行号（1 起）
            "flags": list(self.flags.get(key, [])),
            "w_src": display_width(plain),
            "w_dst": display_width(value),
        }

    def group_keys(self, key):
        """「同组键」—— 校对短标签长度与措辞时要并排看的那一批。

        作用域键 → 同一个 `[Scope]` 下的全部；模板/片段 → 同类全部；
        裸键没有前缀可依，退而取**文件顺序上的邻居**：词表按码点升序，
        同族字符串（`FUEL factory` / `FUEL depot`）天然挨在一起。
        """
        kind = self.kind_of(key)
        if kind == "scoped":
            prefix = "[%s]" % self.scope_of(key)
            return [k for k, _ in self.pairs if k.startswith(prefix)]
        if kind != "plain":
            return [k for k, _ in self.pairs if self.kind_of(k) == kind]
        keys = [k for k, _ in self.pairs]
        index = self.key_to_index.get(key, 0)
        lo = max(0, index - 15)
        return keys[lo:index + 16]

    # ---- 写回 ----------------------------------------------------------
    def _build_edits(self, edits, adds, deletes):
        """按文件生成新行数组，返回一个**计划字典**（不落盘）。

        与单文件版的关键差别：每一条增删改都先经 `home_file()` / `file_of_key()`
        落到某个分类文件，再在该文件**内部**做行级最小 diff：

        * 改一条 `scopes/ui.json` 里的词条 → 只有那一个文件的那一行会变；
        * 新增的键按分类规则落进它该去的文件（「分类」对编辑者是透明的）；
        * 某个文件被删空 → 整个文件移除。这一条不能省：`split_table.py --check`
          的 `partition()` 不含空文件，会判定它是「残留旧文件」= 布局漂移。
          两处口径必须一致，否则审核台保存一次就把布局检查搞红。

        定位一律走 `bisect`（每个文件的正文本身按码点升序），**不去解析行文本**
        （键里可能含冒号，`split(":")` 会解析错）。
        """
        notes = []
        changed, dropped = set(), set()
        removed = []

        # rel -> [(键, 行)] 与 rel -> [键]，两者下标**平行**，所有增删都成对进行
        bodies = {rel: [(k, self.file_lines[rel][i + 1])
                        for i, (k, _) in enumerate(self.file_pairs[rel])]
                  for rel in self.files}
        orders = {rel: [k for k, _ in self.file_pairs[rel]] for rel in self.files}
        touched = set()

        def slot(key):
            """键 -> (所属文件, 在当前正文里的下标)；找不到返回 (None, -1)。"""
            rel = self.file_of_key.get(key)
            if rel is None:
                return None, -1
            order = orders[rel]
            index = bisect.bisect_left(order, key)
            if index >= len(order) or order[index] != key:
                return None, -1
            return rel, index

        for key in deletes:
            rel, index = slot(key)
            if rel is None:
                notes.append("待删除的键不存在，已跳过：%r" % key)
                continue
            del bodies[rel][index]
            del orders[rel][index]
            dropped.add(key)
            touched.add(rel)

        for key, value in edits.items():
            rel, index = slot(key)
            if rel is None:
                notes.append("待编辑的键不存在，已跳过：%r" % key)
                continue
            if not isinstance(value, str):
                notes.append("译文必须是字符串，已跳过：%r" % key)
                continue
            bodies[rel][index] = (key, dumps_line(key, value))
            changed.add(key)
            touched.add(rel)

        for key, value in adds.items():
            if key in self.file_of_key:
                notes.append("待新增的键已存在，已跳过：%r" % key)
                continue
            if not isinstance(value, str):
                notes.append("译文必须是字符串，已跳过：%r" % key)
                continue
            rel = home_file(key)                     # 分类规则决定它去哪个文件
            if rel not in bodies:                    # 该文件还不存在（例如 templates.json 被删过）
                bodies[rel], orders[rel] = [], []
            index = bisect.bisect_left(orders[rel], key)
            bodies[rel].insert(index, (key, dumps_line(key, value)))
            orders[rel].insert(index, key)
            changed.add(key)
            touched.add(rel)

        # 逗号归一（逐文件）：正文每行都要有尾逗号，只有该文件的最后一行没有。
        # 这一趟只可能改动「成为末行」或「不再是末行」的那一行 —— 其余行的字符串
        # 本来就以逗号结尾，赋值是空操作（不会污染最小 diff）。
        new_file_lines = {}
        for rel in sorted(touched):
            body = bodies[rel]
            if not body:
                removed.append(rel)                  # 删空 → 撤掉整个文件
                continue
            last = len(body) - 1
            for i in range(len(body)):
                line = body[i][1]
                want, has = i != last, line.rstrip().endswith(",")
                if want and not has:
                    body[i] = (body[i][0], line.rstrip() + ",")
                elif not want and has:
                    body[i] = (body[i][0], strip_comma(line))
            lines = [line for _, line in body]
            # 首行 `{` 与末两行 `}` / 空串：既有文件沿用原样，新建文件补标准骨架。
            if rel in self.file_lines:
                new_file_lines[rel] = [self.file_lines[rel][0]] + lines + self.file_lines[rel][-2:]
            else:
                new_file_lines[rel] = ["{"] + lines + ["}", ""]

        return {"files": new_file_lines, "removed": removed, "changed": changed,
                "dropped": dropped, "notes": notes,
                "total": sum(len(body) for rel, body in bodies.items())}

    def _diffs_of(self, plan):
        """逐文件 unified diff —— 合并视图里「第几行」没有意义，diff 只能按文件给。"""
        out = []
        for rel in sorted(plan["files"]):
            old = self.file_lines.get(rel)
            new = plan["files"][rel]
            if old == new:
                continue                                 # 只是被逗号归一擦到，不算改动
            out.append({"file": rel, "created": old is None, "removed": False,
                        "diff": list(difflib.unified_diff(
                            old or [], new,
                            fromfile="a/" + rel, tofile="b/" + rel,
                            lineterm="", n=1))})
        for rel in plan["removed"]:
            out.append({"file": rel, "created": False, "removed": True,
                        "diff": ["--- a/" + rel, "+++ /dev/null",
                                 "@@ 整个文件被删空，按布局规则直接移除 @@"]})
        return out

    def preview(self, edits, adds, deletes):
        """不落盘的改动预览（逐文件 unified diff）。"""
        plan = self._build_edits(edits, adds, deletes)
        return {"diffs": self._diffs_of(plan), "notes": plan["notes"],
                "files": sorted(plan["files"]) + plan["removed"],
                "touched": sorted(plan["changed"]), "dropped": sorted(plan["dropped"]),
                "old_count": len(self.pairs), "new_count": plan["total"]}

    def save(self, edits, adds, deletes, backup_dir, keep_backups=20):
        """备份 -> 逐文件写回 -> 自检 -> 失败回滚。返回结果字典。

        没有备份目录就**拒绝写盘**：「自检失败必回滚」这条保证完全建立在备份上，
        拿不到备份就不该动真表。
        """
        plan = self._build_edits(edits, adds, deletes)
        changed, dropped, notes = plan["changed"], plan["dropped"], plan["notes"]
        if not changed and not dropped:
            return {"ok": False, "reason": "没有实际改动", "notes": notes}
        if not backup_dir:
            return {"ok": False, "reason": "未指定备份目录，拒绝写盘", "notes": notes}

        # 1) 备份：逐文件复制到 data/.backups/<时间戳>/，保持相对路径（回滚时原样搬回）。
        #    回滚粒度是**文件**，所以只备份这一轮真正会动的那些。
        stamp = datetime.datetime.now().strftime("%Y%m%d-%H%M%S")
        root = os.path.join(backup_dir, stamp)
        backup_of, created = {}, []
        for rel in sorted(plan["files"]):
            src = self._path(rel)
            if not os.path.isfile(src):
                created.append(rel)                  # 新建文件：回滚时要把它删掉
                continue
            dst = os.path.join(root, rel.replace("/", os.sep))
            os.makedirs(os.path.dirname(dst), exist_ok=True)
            shutil.copy2(src, dst)
            backup_of[rel] = dst
        self._prune_backups(backup_dir, keep_backups)

        # 2) 写回。没被编辑的行逐字节原样搬过去，diff 精确等于改动本身。
        for rel, lines in plan["files"].items():
            path = self._path(rel)
            parent = os.path.dirname(path)
            if parent and not os.path.isdir(parent):
                os.makedirs(parent)
            with io.open(path, "w", encoding="utf-8", newline="") as fh:
                fh.write("\n".join(lines))
        for rel in plan["removed"]:
            try:
                os.remove(self._path(rel))
            except OSError:
                pass

        # 3) 自检：**重新从磁盘读**，验的是真落盘结果而不是内存里的字符串
        problems = self._verify(plan)
        if problems:
            for rel, dst in backup_of.items():
                shutil.copy2(dst, self._path(rel))
            for rel in created:
                try:
                    os.remove(self._path(rel))
                except OSError:
                    pass
            self.load()
            return {"ok": False, "reason": "自检未通过，已从备份回滚",
                    "problems": problems, "backup": root, "notes": notes}

        diffs = self._diffs_of(plan)
        self.load()
        return {"ok": True, "backup": root, "notes": notes,
                "diffs": diffs, "files": sorted(plan["files"]) + plan["removed"],
                "touched": sorted(changed), "dropped": sorted(dropped),
                "count": len(self.pairs)}

    def _verify(self, plan):
        """落盘后的不变量复核（**从磁盘重新读**，不是验内存字符串）。

        任何一条不成立都说明手工行替换出了岔子。逐文件查，最后再查一次
        「磁盘上的词表文件集合」是否符合预期 —— 布局是这套工具的存在理由，
        写坏了文件集合比写坏一行更难发现。
        """
        problems = []
        for rel in sorted(plan["files"]):
            path = self._path(rel)
            if not os.path.isfile(path):
                problems.append("%s: 写盘后文件不存在" % rel)
                continue
            raw = open(path, "rb").read()
            if raw[:3] == b"\xef\xbb\xbf":
                problems.append("%s: 写出的文件带 BOM" % rel)
            text = raw.decode("utf-8-sig")
            if "\r" in text:
                problems.append("%s: 写出的文件含 CR（应为纯 LF）" % rel)
            lines = text.split("\n")
            if lines[:1] != ["{"] or lines[-2:] != ["}", ""]:
                problems.append("%s: 首尾结构异常（应为 `{` / 条目 / `}` / 空尾行）" % rel)
            try:
                new_pairs = json.loads(text, object_pairs_hook=lambda kv: kv)
            except Exception as exc:                 # noqa: BLE001
                problems.append("%s: 写出的不是合法 JSON：%s" % (rel, exc))
                continue

            keys = [k for k, _ in new_pairs]
            if keys != sorted(keys):
                bad = [(keys[i], keys[i + 1]) for i in range(len(keys) - 1)
                       if keys[i] > keys[i + 1]]
                problems.append("%s: 键序被破坏，%d 处：%r" % (rel, len(bad), bad[:3]))
            if len(keys) != len(set(keys)):
                problems.append("%s: 出现字面重复键" % rel)
            if len(lines) != len(new_pairs) + LINE_OVERHEAD:
                problems.append("%s: 行数 %d 与条目数 %d 不匹配"
                                % (rel, len(lines), len(new_pairs)))
            if any(not isinstance(v, str) for _, v in new_pairs):
                problems.append("%s: 有译文不是字符串" % rel)
            body = lines[1:-2]
            for i, line in enumerate(body):
                if line.rstrip().endswith(",") != (i != len(body) - 1):
                    problems.append("%s: 第 %d 行逗号位不对" % (rel, i + 2))
                    break

            # 未触碰的键：那一行必须逐字节不变（这是「最小 diff」的硬保证）
            if rel in self.file_pairs:
                new_line_of = {k: line for k, line in zip(keys, body)}
                for index, (key, _) in enumerate(self.file_pairs[rel]):
                    if key in plan["changed"] or key in plan["dropped"]:
                        continue
                    if key not in new_line_of:
                        problems.append("%s: 键凭空消失：%r" % (rel, key))
                        break
                    if strip_comma(new_line_of[key]) != \
                            strip_comma(self.file_lines[rel][index + 1]):
                        problems.append("%s: 未编辑的键却变了：%r" % (rel, key))
                        break

        for rel in plan["removed"]:
            if os.path.isfile(self._path(rel)):
                problems.append("%s: 应被删空移除却仍在磁盘上" % rel)

        # 全局：磁盘上的词表文件集合 == 原集合 - 移除 + 新建
        now = {rel for rel, _ in table_files(self.data_dir)}
        want = ({rel for rel in self.files} - set(plan["removed"])) | set(plan["files"])
        if now != want:
            problems.append("词表文件集合与预期不符：多出 %s / 缺少 %s"
                            % ("、".join(sorted(now - want)) or "无",
                               "、".join(sorted(want - now)) or "无"))
        return problems

    @staticmethod
    def _prune_backups(backup_dir, keep):
        """只清理本工具自己建的时间戳目录；`split_table.py` 写的扁平备份不碰。"""
        if not backup_dir:
            return
        try:
            names = sorted(n for n in os.listdir(backup_dir)
                           if re.match(r"^\d{8}-\d{6}$", n)
                           and os.path.isdir(os.path.join(backup_dir, n)))
        except OSError:
            return
        for name in (names[:-keep] if keep > 0 else names):
            shutil.rmtree(os.path.join(backup_dir, name), ignore_errors=True)


# --------------------------------------------------------------------------
# 附带数据：排除名单 / 作用域 / 运行期清单
# --------------------------------------------------------------------------
def load_json(path, default=None):
    try:
        return json.loads(io.open(path, encoding="utf-8").read())
    except Exception:                                # noqa: BLE001
        return default


def table_signature(data_dir):
    """整套分类词表的合并签名 —— 用来判断「仓库」与「运行目录」是否同源。

    把**相对路径一起喂进摘要**：布局变了（比如新建了 `templates.json`）即使内容
    逐字节相同，签名也会变。这个方向是对的 —— 布局也是要同步的一部分，
    运行目录少一个模板文件就是真的不一致。

    返回 ``(md5 或 None, 文件数)``。
    """
    try:
        rels = table_files(data_dir)
    except OSError:
        return None, 0
    if not rels:
        return None, 0
    digest = hashlib.md5()
    for rel, path in rels:
        digest.update(rel.encode("utf-8"))
        digest.update(b"\0")
        try:
            digest.update(open(path, "rb").read())
        except OSError:
            return None, 0
    return digest.hexdigest(), len(rels)


class Workspace(object):
    """把「仓库数据 + 运行期产物」统一成一份可查询的状态。"""

    def __init__(self, data_dir, plugin_dir):
        self.data_dir = data_dir
        self.plugin_dir = plugin_dir
        self.table = Table(data_dir)                 # 整套分类词表，不是一个文件
        self.exclusions = load_json(os.path.join(data_dir, "exclusions.json"), {}) or {}
        self.force_scopes = load_json(os.path.join(data_dir, "force_scopes.json"), []) or []
        self.reload_runtime()

    # ---- 运行期 --------------------------------------------------------
    def reload_runtime(self):
        def read(name):
            path = os.path.join(self.plugin_dir, name)
            if not os.path.isfile(path):
                return None, None
            try:
                data = json.loads(io.open(path, encoding="utf-8").read())
            except Exception:                        # noqa: BLE001
                return [], os.path.getmtime(path)
            if isinstance(data, dict):
                data = list(data.keys())
            return data, os.path.getmtime(path)

        self.missing, self.missing_mtime = read("missing.json")
        self.untranslated, self.untranslated_mtime = read("untranslated.json")

    def runtime_records(self):
        """把运行期清单转成「大概能不能翻出来」的近似判定。

        ⚠ 这里**不复刻**流水线的片段拼接 / 切片 / 前缀尾缀匹配 —— 静态比对会把一批
        本来能翻的条目报成缺口（历史上误报率约 23%）。所以给四档，宁可多一档
        「疑似」也不硬凑结论：
          covered  键在表里能命中（裸键 / 该作用域 / 片段键 / 去掉作用域后）
          intended 命中排除名单或保持英文名单（本就不该翻译）
          likely   同作用域或全表里存在**包含这段文字**的键（片段拼接、括号切片、
                   `Soon(tm)` 这类会被分隔符切开的字符串都落这里）—— 只是线索
          gap      三样都对不上，才值得人工看
        """
        texts = set(self.exclusions.get("texts", []))
        scopes = {s.lower() for s in self.exclusions.get("scopes", [])}
        terms = set(self.exclusions.get("terms", []))

        out = []
        for source, records in (("missing", self.missing or []),
                                ("untranslated", self.untranslated or [])):
            for item in records:
                scope, plain = "", item
                m = SCOPE_RE.match(item)
                if m:
                    scope, plain = m.group(1), item[m.end():]

                if item in self.table.line_of_key:
                    verdict, detail = "covered", "整键命中"
                elif scope and ("[%s]%s" % (scope, plain)) in self.table.line_of_key:
                    verdict, detail = "covered", "该作用域的键命中"
                elif plain in self.table.line_of_key:
                    verdict, detail = "covered", "去掉作用域后可命中"
                elif any((sigil + plain) in self.table.line_of_key
                         for sigil in (">>", "<<", "==", "~")):
                    verdict, detail = "covered", "命中模板 / 片段键"
                # 排除名单按**对象名（scope）**匹配，所以要比的是 scope，
                # 不是条目本身 —— 第一版只比条目名，于是 NOXContactLabel 下
                # 40 多条已被排除的呼号全被误报成「缺口」。
                elif scope and scope.lower() in scopes:
                    verdict, detail = "intended", "该作用域整体排除"
                elif item in scopes or plain in scopes:
                    verdict, detail = "intended", "按对象名排除"
                elif item in texts or plain in texts:
                    verdict, detail = "intended", "按文本排除"
                elif any(t and t.lower() in plain.lower() for t in terms):
                    verdict, detail = "intended", "命中保持英文名单"
                else:
                    hint = self.contains_key_hint(scope, plain)
                    if hint:
                        verdict, detail = "likely", "同表有以它开头/结尾的键：%s（片段拼接）" % hint
                    else:
                        verdict, detail = "gap", ("静态比对查不到通路"
                                                  "（玩家名 / 动态呼号 / 拼装字符串会落在这里）")
                out.append({"source": source, "item": item, "scope": scope,
                            "plain": plain, "verdict": verdict, "detail": detail})
        return out

    def contains_key_hint(self, scope, plain):
        """在表里找「以这段文字开头或结尾」的键，作为片段通路的线索。

        判据必须是**前缀/后缀**，不能用「键里包含这段文字」：
        `GS25` 会命中一条炸弹描述里的「释放 12 枚 GS25 子弹药」——那是纯巧合，
        报成「疑似有通路」比报「缺口」更糟，因为它让人不去看。
        而真正的片段机制（`>>` 前缀 / `<<` 后缀 / 模板被切掉尾段）产生的正是
        前缀或后缀关系：`Doing so will kick` 是整句的开头，`Soon` 是 `Soon(tm)` 的开头。

        太短的串（<3 字符）会命中一大片，没有信息量，直接放弃。
        """
        if len(plain) < 3:
            return None
        needle = plain.lower()
        prefix = "[%s]" % scope if scope else None
        fallback = None
        for key in self.table.line_of_key:
            body = self.table.plain_of(key).lower()
            if not (body.startswith(needle) or body.endswith(needle)):
                continue
            if prefix and key.startswith(prefix):
                return key[:70]
            if fallback is None:
                fallback = key[:70]
        return fallback

    # ---- 元信息 / 自检 -------------------------------------------------
    @staticmethod
    def _describe(role, rel, path):
        """一个词表文件的「大小 / md5 / 最后修改」。不存在时三个字段都是 None。"""
        if os.path.isfile(path):
            raw = open(path, "rb").read()
            return {"role": role, "rel": rel, "path": path, "size": len(raw),
                    "md5": hashlib.md5(raw).hexdigest(),
                    "mtime": datetime.datetime.fromtimestamp(
                        os.path.getmtime(path)).strftime("%Y-%m-%d %H:%M:%S")}
        return {"role": role, "rel": rel, "path": path, "size": None,
                "md5": None, "mtime": None}

    def meta(self):
        # 词表按分类拆成多文件，所以「仓库 / 运行目录是否同源」要比的是**整套**签名，
        # 而不是某一个 translation.json 的 md5（那样改了 scopes/ 也会显示「一致」）。
        files = []
        for rel, path in table_files(self.data_dir):
            files.append(self._describe("repo", rel, path))
        for rel, path in table_files(self.plugin_dir):
            files.append(self._describe("plugin", rel, path))
        for role, base in (("repo", self.data_dir), ("plugin", self.plugin_dir)):
            for rel in ("exclusions.json", "force_scopes.json", "fonts/font.ttf"):
                files.append(self._describe(role + "-aux", rel,
                                            os.path.join(base, rel.replace("/", os.sep))))

        repo_md5, repo_n = table_signature(self.data_dir)
        plugin_md5, plugin_n = table_signature(self.plugin_dir)

        counts = collections.Counter()
        flag_counts = collections.Counter()
        for key, _ in self.table.pairs:
            counts[self.table.kind_of(key)] += 1
            for flag in self.table.flags.get(key, []):
                flag_counts[flag] += 1
        files_per_cat = collections.Counter(
            self.table.file_of_key[k] for k, _ in self.table.pairs)

        return {
            "repo": REPO,
            "data_dir": self.data_dir,
            "plugin_dir": self.plugin_dir,
            "plugin_dir_exists": os.path.isdir(self.plugin_dir),
            "counts": dict(counts),
            "total": len(self.table.pairs),
            "flag_counts": dict(flag_counts),
            "kind_labels": KIND_LABELS,
            "flag_meta": {k: {"label": v[0], "level": v[1], "tip": v[2]}
                          for k, v in FLAG_META.items()},
            "categories": [{"name": name, "label": label, "desc": desc,
                            "entries": files_per_cat.get("scopes/%s.json" % name, 0)}
                           for name, label, desc in CATEGORIES],
            "file_entries": dict(files_per_cat),
            "table_md5": self.table.md5,
            "table_mtime": datetime.datetime.fromtimestamp(
                self.table.mtime).strftime("%Y-%m-%d %H:%M:%S")
            if self.table.mtime else None,
            "table_file_count": len(self.table.files),
            "load_problems": self.table.load_problems,
            "exclusions": {
                "scopes": self.exclusions.get("scopes", []),
                "texts": self.exclusions.get("texts", []),
                "terms": self.exclusions.get("terms", []),
                "useDefaults": self.exclusions.get("useDefaults"),
                "longGuard": self.exclusions.get("longGuard"),
            },
            "force_scopes": self.force_scopes,
            "runtime": {
                "missing": None if self.missing is None else len(self.missing),
                "missing_mtime": self.missing_mtime and datetime.datetime.fromtimestamp(
                    self.missing_mtime).strftime("%Y-%m-%d %H:%M:%S"),
                "untranslated": None if self.untranslated is None else len(self.untranslated),
                "untranslated_mtime": self.untranslated_mtime and datetime.datetime.fromtimestamp(
                    self.untranslated_mtime).strftime("%Y-%m-%d %H:%M:%S"),
            },
            "repo_signature": {"md5": repo_md5, "files": repo_n},
            "plugin_signature": {"md5": plugin_md5, "files": plugin_n},
            "files": files,
        }

    def entries(self, query, kinds, scopes, flags, sort, offset, limit):
        """筛选 + 排序 + 分页。返回 (行列表, 命中总数)。"""
        needle = (query or "").strip().lower()
        rows = []
        for key, value in self.table.pairs:
            kind = self.table.kind_of(key)
            if kinds and kind not in kinds:
                continue
            scope = self.table.scope_of(key)
            if scopes and scope not in scopes:
                continue
            entry_flags = self.table.flags.get(key, [])
            if flags and not any(f in entry_flags for f in flags):
                continue
            if needle:
                haystack = (key + "\n" + value).lower()
                if needle not in haystack:
                    continue
            plain = self.table.plain_of(key)
            rows.append({
                "key": key, "value": value, "kind": kind, "scope": scope,
                "plain": plain, "flags": list(entry_flags),
                # 合并视图里「第几行」没有意义，列表要显示的是**所属分类文件**
                "file": self.table.file_of_key.get(key),
                "home": home_file(key),
                "line": self.table.line_of_key.get(key),
                "w_src": display_width(plain), "w_dst": display_width(value),
            })

        if sort == "risk":
            order = {k: v[1] for k, v in FLAG_META.items()}
            rank = {"high": 0, "mid": 1, "low": 2}
            rows.sort(key=lambda r: (min([rank[order[f]] for f in r["flags"]] or [3])
                                     if r["flags"] else 3,
                                     -sum(rank[order[f]] == 0 for f in r["flags"]),
                                     r["key"]))
        elif sort == "key":
            rows.sort(key=lambda r: r["key"])
        elif sort == "width":
            rows.sort(key=lambda r: -(r["w_dst"] - r["w_src"]))
        # 默认保持文件顺序（= 码点升序），这样「同组键」天然相邻

        total = len(rows)
        return rows[offset:offset + limit], total, rows

    def groups(self):
        """同原文多译文 / 大小写重复键：两张「不一致清单」。"""
        by_norm = collections.defaultdict(set)
        for key, value in self.table.pairs:
            if not key or key[0] in "~><=":
                continue
            norm = re.sub(r"\s+", " ", TAG_RE.sub("\u0001",
                          SCOPE_RE.sub("", key))).strip().lower()
            by_norm[norm].add((key, value))
        multi = []
        for norm, items in by_norm.items():
            values = {v for _, v in items}
            if len(values) > 1:
                multi.append({"norm": norm,
                              "items": [{"key": k, "value": v} for k, v in sorted(items)]})
        multi.sort(key=lambda g: g["norm"])

        by_case = collections.defaultdict(list)
        for key, value in self.table.pairs:
            by_case[key.lower()].append((key, value))
        case = [{"norm": norm, "items": [{"key": k, "value": v} for k, v in items]}
                for norm, items in sorted(by_case.items()) if len(items) > 1]
        return {"multi": multi, "case": case}

    def audit(self):
        """把 check_data.py 的规则 + 审核台自己的标签合成一份体检报告。"""
        t = self.table
        problems, warnings, notes = [], [], []

        for item in t.load_problems:
            problems.append(item)
        for key, value in t.pairs:
            if not isinstance(value, str):
                problems.append("译文不是字符串：%r" % key[:60])
        for key, value in t.pairs:
            kind = t.kind_of(key)
            if kind in ("prefix", "suffix", "middle") and not t.plain_of(key).strip():
                problems.append("片段键内容为空（会匹配一切）：%r" % key[:60])
        for key, value in t.pairs:
            flags = t.flags.get(key, [])
            if "bracket" in flags:
                problems.append("译文引入了原文没有的括号：%r -> %r" % (key[:50], value[:50]))
            if "esc" in flags:
                problems.append("译文占位符多于原文（标签回填会错位）：%r -> %r"
                                % (key[:50], value[:50]))

        case_bad = [g for g in self.groups()["case"]
                    if len({i["value"] for i in g["items"]}) > 1]
        for group in case_bad:
            problems.append("大小写重复且译文不同（会静默覆盖）：%r -> %r"
                            % (group["norm"], [i["value"] for i in group["items"]]))

        multi = self.groups()["multi"]
        for group in multi:
            warnings.append("同原文多译文：%r -> %r"
                            % (group["norm"][:44], sorted(i["value"] for i in group["items"])))

        space = [k for k, _ in t.pairs if "space" in t.flags.get(k, [])]
        if space:
            notes.append("译文首尾带空白 %d 条（多数是原文自带的排版空白，逐条判断）" % len(space))

        # 布局漂移：键没待在 home_file() 指定的分类文件里。**不影响运行**
        # （加载器按键上的前缀分流，与它在哪个文件无关），但 check_data.py 会判失败，
        # 且 `split_table.py --check` 会一直报。审核台保存时**不**顺手归位 ——
        # 那会把 diff 从「你改的那几行」放大成「整文件搬迁」。
        drifted = collections.defaultdict(list)
        for key, _ in t.pairs:
            home = t.file_of_key[key]
            want = home_file(key)
            if home != want:
                drifted[(home, want)].append(key)
        if drifted:
            total = sum(len(v) for v in drifted.values())
            sample = "；".join("%s → %s（%d 条，例：%s）"
                              % (a, b, len(v), v[0][:40])
                              for (a, b), v in sorted(drifted.items())[:4])
            notes.append("布局漂移 %d 条：%s　—— 跑 tools/split_table.py --apply 可归位"
                         % (total, sample))

        notes.append("忽略大小写后重复的键 %d 组（译文相同则无害）" % len(self.groups()["case"]))
        if t.mtime:
            age = (datetime.datetime.now() - datetime.datetime.fromtimestamp(t.mtime))
            notes.append("词表最后修改：%s（%.1f 小时前）"
                         % (datetime.datetime.fromtimestamp(t.mtime)
                            .strftime("%Y-%m-%d %H:%M:%S"), age.total_seconds() / 3600))

        return {"problems": problems, "warnings": warnings, "notes": notes,
                "multi_count": len(multi), "case_dup_count": len(self.groups()["case"])}

    def deploy(self):
        """把仓库 data/ 全量同步到插件运行目录（对应 csproj 的 -t:DeployData）。

        只推数据、不碰 DLL —— DLL 由构建产出，工具不该动它。

        词表拆成多文件后，同步必须**按相对路径**成组进行：漏推一个
        `scopes/*.json` 不会报任何错，只会静默少掉一批词条；反过来，仓库里删掉的
        文件如果运行目录还留着，加载器照样会读它。两种都是「看起来同步成功了」的
        故障，所以这里顺带清掉运行目录里已不在仓库的旧词表文件。
        """
        if not os.path.isdir(self.plugin_dir):
            return {"ok": False, "reason": "运行目录不存在：%s" % self.plugin_dir}

        wanted = list(table_files(self.data_dir))
        for rel in ("exclusions.json", "force_scopes.json", "fonts/font.ttf"):
            path = os.path.join(self.data_dir, rel.replace("/", os.sep))
            if os.path.isfile(path):
                wanted.append((rel, path))

        results = []
        for rel, src in wanted:
            # 字体在运行目录里是平铺的 `font.ttf`（csproj 与旧布局都是这样）
            dst_rel = "font.ttf" if rel.endswith("font.ttf") else rel
            dst = os.path.join(self.plugin_dir, dst_rel.replace("/", os.sep))
            parent = os.path.dirname(dst)
            if parent and not os.path.isdir(parent):
                os.makedirs(parent)
            before = open(dst, "rb").read() if os.path.isfile(dst) else None
            shutil.copy2(src, dst)
            after = open(dst, "rb").read()
            results.append({"file": dst_rel, "changed": before != after,
                            "md5": hashlib.md5(after).hexdigest()})

        keep = {("font.ttf" if rel.endswith("font.ttf") else rel) for rel, _ in wanted}
        for rel, path in table_files(self.plugin_dir):
            if rel in keep:
                continue
            try:
                os.remove(path)
            except OSError:
                continue
            results.append({"file": rel, "changed": True, "removed": True})

        return {"ok": True, "results": results, "plugin_dir": self.plugin_dir,
                "mtime": datetime.datetime.now().strftime("%Y-%m-%d %H:%M:%S")}


# --------------------------------------------------------------------------
# HTTP
# --------------------------------------------------------------------------
class Handler(BaseHTTPRequestHandler):
    server_version = "NOReview/1.0"
    workspace = None
    lock = threading.Lock()

    # ---- 基础 ----------------------------------------------------------
    def log_message(self, fmt, *args):                # 静音默认访问日志
        pass

    def _local_only(self):
        """工具会写文件，只服务本机并挡掉 DNS rebinding。"""
        host = (self.headers.get("Host") or "").split(":")[0]
        return host in ("127.0.0.1", "localhost", "[::1]")

    def _send(self, code, body, ctype="application/json; charset=utf-8"):
        if isinstance(body, str):
            body = body.encode("utf-8")
        self.send_response(code)
        self.send_header("Content-Type", ctype)
        self.send_header("Content-Length", str(len(body)))
        self.send_header("Cache-Control", "no-store")
        self.end_headers()
        try:
            self.wfile.write(body)
        except (BrokenPipeError, ConnectionResetError):
            pass

    def _json(self, obj, code=200):
        self._send(code, json.dumps(obj, ensure_ascii=False))

    def do_GET(self):                                # noqa: N802
        if not self._local_only():
            self._json({"error": "只允许从本机访问"}, 403)
            return
        url = urlparse(self.path)
        path = unquote(url.path)
        params = parse_qs(url.query)

        if path in ("/", "/index.html"):
            self._static("index.html")
            return
        if path.startswith("/static/"):
            self._static(path[len("/static/"):])
            return
        try:
            if path == "/api/meta":
                self._json(self.workspace.meta())
            elif path == "/api/entries":
                self._api_entries(params)
            elif path == "/api/detail":
                self._api_detail(params)
            elif path == "/api/groups":
                with self.lock:
                    self._json(self.workspace.groups())
            elif path == "/api/audit":
                with self.lock:
                    self._json(self.workspace.audit())
            elif path == "/api/runtime":
                with self.lock:
                    self._json({"records": self.workspace.runtime_records()})
            else:
                self._json({"error": "未知接口：%s" % path}, 404)
        except Exception as exc:                     # noqa: BLE001
            self._json({"error": "%s: %s" % (type(exc).__name__, exc)}, 500)

    def do_POST(self):                               # noqa: N802
        if not self._local_only():
            self._json({"error": "只允许从本机访问"}, 403)
            return
        path = urlparse(self.path).path
        try:
            length = int(self.headers.get("Content-Length") or 0)
            payload = json.loads(self.rfile.read(length).decode("utf-8")) if length else {}
        except Exception as exc:                     # noqa: BLE001
            self._json({"error": "请求体不是合法 JSON：%s" % exc}, 400)
            return

        try:
            if path == "/api/preview":
                with self.lock:
                    self._json(self.workspace.table.preview(
                        payload.get("edits") or {}, payload.get("adds") or {},
                        payload.get("deletes") or []))
            elif path == "/api/save":
                self._api_save(payload)
            elif path == "/api/reload":
                with self.lock:
                    self.workspace = Workspace(self.workspace.data_dir,
                                               self.workspace.plugin_dir)
                    Handler.workspace = self.workspace
                    self._json({"ok": True, "meta": self.workspace.meta()})
            elif path == "/api/deploy":
                with self.lock:
                    self._json(self.workspace.deploy())
            elif path == "/api/runtime/reload":
                with self.lock:
                    self.workspace.reload_runtime()
                    self._json({"ok": True, "runtime": self.workspace.meta()["runtime"]})
            else:
                self._json({"error": "未知接口：%s" % path}, 404)
        except Exception as exc:                     # noqa: BLE001
            self._json({"error": "%s: %s" % (type(exc).__name__, exc)}, 500)

    # ---- 具体接口 ------------------------------------------------------
    def _static(self, name):
        safe = os.path.normpath(name).replace("\\", "/").lstrip("/")
        if safe.startswith(".."):
            self._json({"error": "非法路径"}, 400)
            return
        target = os.path.join(STATIC_DIR, safe)
        if not os.path.isfile(target):
            self._json({"error": "找不到静态文件：%s" % safe}, 404)
            return
        ctype = {".html": "text/html; charset=utf-8",
                 ".js": "application/javascript; charset=utf-8",
                 ".css": "text/css; charset=utf-8"}.get(os.path.splitext(safe)[1],
                                                        "application/octet-stream")
        self._send(200, open(target, "rb").read(), ctype)

    def _api_entries(self, params):
        def one(name, default=None):
            values = params.get(name)
            return values[0] if values else default

        def many(name):
            out = []
            for value in params.get(name, []):
                out.extend(x for x in value.split(",") if x)
            return out

        kinds = many("kind")
        scopes = many("scope")
        flags = many("flag")
        query = one("q", "")
        sort = one("sort", "file")
        try:
            offset = max(0, int(one("offset", "0")))
            limit = min(1000, max(1, int(one("limit", "200"))))
        except ValueError:
            offset, limit = 0, 200

        with self.lock:
            rows, total, all_rows = self.workspace.entries(
                query, kinds, scopes, flags, sort, offset, limit)
            scope_list = collections.Counter(
                self.workspace.table.scope_of(k) for k, _ in self.workspace.table.pairs)
        self._json({
            "rows": rows, "total": total, "offset": offset, "limit": limit,
            "grand_total": len(all_rows),
            "scopes": [s for s, _ in scope_list.most_common() if s],
        })

    def _api_detail(self, params):
        key = (params.get("key") or [""])[0]
        with self.lock:
            entry = self.workspace.table.entry(key)
            if entry is None:
                self._json({"error": "找不到该键"}, 404)
                return
            group = self.workspace.table.group_keys(key)
            siblings = []
            for other in group[:400]:
                if other == key:
                    continue
                sib = self.workspace.table.entry(other)
                if sib:
                    siblings.append(sib)
            entry["group_size"] = len(group)
            entry["siblings"] = siblings
            self._json(entry)

    def _api_save(self, payload):
        with self.lock:
            backup_dir = os.path.join(self.workspace.data_dir, ".backups")
            result = self.workspace.table.save(
                payload.get("edits") or {}, payload.get("adds") or {},
                payload.get("deletes") or [], backup_dir)
            if result.get("ok") and payload.get("deploy"):
                result["deploy"] = self.workspace.deploy()
            result["meta"] = self.workspace.meta()
            self._json(result)


# --------------------------------------------------------------------------
# 自检（--selftest）：不开服务，直接验证「读 -> 逐行写回 -> 自检 -> 落盘」整条链
# --------------------------------------------------------------------------
def key_of_diff_line(line):
    """从 unified diff 的正文行里抠出键。

    只认「去掉 +/- 前缀后，行首是一个 JSON 字符串 + 冒号」这一种形态 ——
    也就是词表正文行的形态。`@@` 之类的 hunk 头自然抠不出来，返回 None。
    """
    body = line[1:] if line[:1] in "+-" else line
    match = re.match(r'^\s*("(?:[^"\\]|\\.)*")\s*:', body)
    if not match:
        return None
    try:
        return json.loads(match.group(1))
    except ValueError:
        return None


def _layout_md5(data_dir):
    """整套词表的指纹（**相对路径 + 内容**都喂进去）。

    比单个 `translation.json` 的 md5 强：把某个键从 `translation.json` 搬到
    `scopes/ui.json` 而内容一字未改，指纹也会变。自检前后比对它就够了。
    """
    digest = hashlib.md5()
    for rel, path in table_files(data_dir):
        digest.update(rel.encode("utf-8"))
        digest.update(b"\0")
        digest.update(open(path, "rb").read())
    return digest.hexdigest()


def selftest(data_dir, plugin_dir):
    """四件事：① 序列化风格没漂移；② 在**临时副本**上真跑一次多文件保存；
    ③ 两条极端路径（删空整个分类文件 / 重建被删的分类文件）；④ 真词表没被碰。"""
    print("仓库根目录 : %s" % REPO)
    print("数据目录   : %s" % data_dir)
    print("运行目录   : %s" % plugin_dir)

    before = _layout_md5(data_dir)
    ws = Workspace(data_dir, plugin_dir)
    t = ws.table
    print("词表       : %d 条 / %d 个分类文件，合并 md5=%s"
          % (len(t.pairs), len(t.files), t.md5[:12]))
    for rel in t.files:
        print("             %-24s %5d 条" % (rel, len(t.file_pairs[rel])))
    print("结构问题   : %s" % (t.load_problems or "无"))
    problems = []
    if t.load_problems:
        problems.append("载入期就报出 %d 条结构问题，先修数据再谈工具" % len(t.load_problems))

    # ---- ① 序列化往返：既有每一行都能被 dumps_line 逐字复现（风格未漂移的证明）----
    total_lines, bad = 0, []
    for rel in t.files:
        for index, (key, value) in enumerate(t.file_pairs[rel]):
            total_lines += 1
            if strip_comma(t.file_lines[rel][index + 1]) != strip_comma(dumps_line(key, value)):
                bad.append((rel, key))
    print("① 序列化往返 : %d/%d 行逐字复现" % (total_lines - len(bad), total_lines))
    if bad:
        problems.append("序列化风格已漂移，%d 行不一致（例：%r）" % (len(bad), bad[:2]))

    # ---- ② 在临时副本上真跑一次保存（整套布局一起复制，不然分类文件会缺）----
    tmp = tempfile.mkdtemp(prefix="no_review_selftest_")
    tmp_data = os.path.join(tmp, "data")
    try:
        for rel, path in table_files(data_dir):
            dst = os.path.join(tmp_data, rel.replace("/", os.sep))
            os.makedirs(os.path.dirname(dst), exist_ok=True)
            shutil.copy2(path, dst)
        tt = Table(tmp_data)
        if tt.files != t.files:
            problems.append("临时副本的文件清单与原目录不一致：%r vs %r"
                            % (tt.files, t.files))

        target = tt.pairs[len(tt.pairs) // 3][0]     # 大概率落在某个 scopes 文件里
        old_value = tt.entry(target)["value"]
        scoped_new = "[Radar]zzz-selftest \u4e2d\u6587"   # 应落 scopes/hud.json
        plain_new = "zzz-selftest plain \u4e2d\u6587"     # 应落 translation.json

        empty = tt.save({}, {}, [], os.path.join(tmp, "backups"))
        print("②a 空改动    : %s（应拒绝）" % ("已拒绝" if not empty.get("ok") else "竟然写了盘！"))
        if empty.get("ok"):
            problems.append("空改动竟然落盘")

        no_backup = tt.save({target: old_value + "\u3002"}, {}, [], None)
        print("②b 无备份目录 : %s（应拒绝）"
              % ("已拒绝" if not no_backup.get("ok") else "竟然没备份就写了盘！"))
        if no_backup.get("ok"):
            problems.append("没有备份目录却落盘了")

        result = tt.save({target: old_value + "\u3002"},
                         {scoped_new: "\u81ea\u68c0\u5360\u4f4d",
                          plain_new: "\u81ea\u68c0\u5360\u4f4d"}, [],
                         os.path.join(tmp, "backups"))
        print("②c 编辑+新增 : ok=%s%s" % (
            result.get("ok"), "" if result.get("ok")
            else "  ！%s %s" % (result.get("reason"), result.get("problems"))))
        tt2 = Table(tmp_data)                        # 落盘后重新读一份，③ ④ 也要用
        if not result.get("ok"):
            problems.append("保存失败：%s %s" % (result.get("reason"),
                                                result.get("problems")))
        else:
            if result["count"] != len(t.pairs) + 2:
                problems.append("条目数应为 %d，实际 %d"
                                % (len(t.pairs) + 2, result["count"]))

            # 逐文件 diff 断言**按改动键集合**，不按行数 —— 行数会被逗号机制干扰：
            # 新增的键若落在文件末尾，旧末行必须补一个逗号，于是合法地多出 1 行
            # 「+（同一个键、只多了个逗号）」。这正是最小 diff 的边界，不是 bug。
            added_keys, removed_keys = set(), set()
            for item in result["diffs"]:
                for line in item["diff"]:
                    if line[:3] in ("+++", "---") or line[:1] not in "+-":
                        continue
                    key = key_of_diff_line(line)
                    if key is None:
                        continue
                    (added_keys if line[:1] == "+" else removed_keys).add(key)
            expect_add = {target, scoped_new, plain_new}
            touched_files = {item["file"] for item in result["diffs"]}
            expect_files = {tt.file_of_key[target], home_file(scoped_new),
                            home_file(plain_new)}
            print("②d 改动键     : + %d 个 / - %d 行，触达文件 %s"
                  % (len(added_keys), len(removed_keys), sorted(touched_files)))
            # 边界：新增键落在文件末尾时，旧末行补逗号，于是它成对出现在 - 与 + 里。
            # 所以「- 行」允许有 1 个不在预期内的键，但它**必须同时出现在 + 里**
            # （是重写，不是删除）—— 这一条正是「不会丢数据」的证明。
            extra_del = removed_keys - expect_add
            if target not in removed_keys:
                problems.append("被编辑那条的旧行没出现在 diff 里：%r" % target)
            if not extra_del <= added_keys:
                problems.append("这些键的行被删掉却没重写回来（会丢数据）：%r"
                                % sorted(extra_del - added_keys))
            if len(extra_del) > 1 or len(added_keys - expect_add) > 1:
                problems.append("改动行数超出「1 条编辑 + 2 条新增 + ≤1 处补逗号」："
                                "+%r / -%r" % (sorted(added_keys - expect_add),
                                               sorted(extra_del)))
            if touched_files != expect_files:
                problems.append("触达文件集不符：期望 %r，实际 %r"
                                % (sorted(expect_files), sorted(touched_files)))
            if result.get("backup") and not os.path.isdir(result["backup"]):
                problems.append("备份目录没生成：%r" % result["backup"])

            # 分类规则必须生效：新增的作用域键落到分类文件，而不是主表
            if tt2.file_of_key.get(scoped_new) != home_file(scoped_new):
                problems.append("新增的作用域键没落到分类文件：%r 在 %r"
                                % (scoped_new, tt2.file_of_key.get(scoped_new)))
            # 落盘结果复核：未触碰的键，那一行必须逐字节不变。
            # 必须**按键**比对而不是按行号 —— 新增键会让它后面的行整体位移。
            mutated = []
            for rel in tt.files:
                for index, (key, _) in enumerate(tt.file_pairs[rel]):
                    if key in (target, scoped_new, plain_new):
                        continue
                    new_index = tt2.entry_index.get(key)
                    if new_index is None:
                        mutated.append((rel, key, "凭空消失"))
                        continue
                    new_rel = tt2.file_of_key[key]
                    if strip_comma(tt2.file_lines[new_rel][new_index + 1]) != \
                            strip_comma(tt.file_lines[rel][index + 1]):
                        mutated.append((rel, key))
            print("②e 未触碰行  : %d 行被动了（应为 0）" % len(mutated))
            if mutated:
                problems.append("%d 行未触碰的表行被改写（例：%r）"
                                % (len(mutated), mutated[:3]))

            # ---- ③ 极端路径：把一个分类文件删空（应连同文件一起移除）----
            victim = "templates.json"
            if victim in tt2.files:
                keys = [k for k, _ in tt2.file_pairs[victim]]
                dropped = tt2.save({}, {}, keys, os.path.join(tmp, "backups"))
                gone = not os.path.isfile(os.path.join(tmp_data, victim))
                print("③a 删空分类文件 : ok=%s，%s 已移除=%s（删 %d 条）"
                      % (dropped.get("ok"), victim, gone, len(keys)))
                if not dropped.get("ok"):
                    problems.append("删空整个分类文件失败：%s %s"
                                    % (dropped.get("reason"), dropped.get("problems")))
                elif not gone:
                    problems.append("删空后 %s 仍留在磁盘上（会与 split_table.py 的布局检查打架）"
                                    % victim)

                # 再加回一条同类的键：文件必须被重建（走 created 分支）
                back = tt2.save({}, {"~zzz-selftest": "\u4e2d\u6587\u6a21\u677f"}, [],
                                os.path.join(tmp, "backups"))
                rebuilt = os.path.isfile(os.path.join(tmp_data, victim))
                print("③b 重建分类文件 : ok=%s，%s 已重建=%s"
                      % (back.get("ok"), victim, rebuilt))
                if not back.get("ok") or not rebuilt:
                    problems.append("删空后再新增未能重建 %s：%s %s"
                                    % (victim, back.get("reason"), back.get("problems")))
            else:
                print("③ 极端路径 : 跳过（临时副本里没有 %s）" % victim)

        # ---- ④ 非法输入：既不存在的键编辑，必须跳过并给提示 ----
        bad_result = tt2.save({"不存在的键": "x"}, {}, [], os.path.join(tmp, "backups"))
        print("④ 非法改动   : %s（应拒绝或跳过并提示）"
              % ("已跳过" if bad_result.get("notes") or not bad_result.get("ok") else "无提示"))
        if bad_result.get("ok") and not bad_result.get("notes"):
            problems.append("非法键既没被拒绝也没给提示")
    finally:
        shutil.rmtree(tmp, ignore_errors=True)

    # ---- ⑤ 真词表必须原封不动（整套布局，不只主表）----
    after = _layout_md5(data_dir)
    print("⑤ 真词表指纹 : %s（%s）" % (after[:12], "未改动" if after == before else "被改了！"))
    if after != before:
        problems.append("自检竟然修改了真词表")

    print()
    if problems:
        print("自检未通过，%d 项：" % len(problems))
        for item in problems:
            print("  ✗ %s" % item)
        return 1
    print("自检通过。")
    return 0


def _excluded_tcp_ranges():
    """读 Windows 的 TCP 保留端口段（Hyper-V / WSL 会占掉成百上千个口）。

    本机实测：默认的 8765 起连续顺延 25 个全被 `WinError 10013` 挡掉 ——
    那**不是权限问题**，是这段落在系统排除区里。与其让用户去查
    `netsh interface ipv4 show excludedportrange`，不如直接读出来绕开。
    拿不到就返回空表，退化成顺延扫描。
    """
    try:
        flags = getattr(subprocess, "CREATE_NO_WINDOW", 0)
        out = subprocess.run(["netsh", "interface", "ipv4", "show",
                              "excludedportrange", "protocol=tcp"],
                             capture_output=True, text=True, timeout=6,
                             creationflags=flags).stdout
    except Exception:                                # noqa: BLE001
        return []
    ranges = []
    for line in out.splitlines():
        parts = line.split()
        if len(parts) >= 2 and parts[0].isdigit() and parts[1].isdigit():
            ranges.append((int(parts[0]), int(parts[1])))
    return ranges


def bind_with_fallback(preferred, handler, tries=40, span=6000):
    """监听端口：首选 → 顺延（跳过系统排除段）→ 最后交给系统分配。

    返回 (httpd, 实际端口, 是否顺延)。

    注意 `tries` 是**可用候选的个数**，不是「顺延多少个数字」——
    排除段是连续的（本机 8525–9024 整整 500 个口被 Hyper-V 占着），
    按「顺延 40 个」去数的话窗口会整段落在排除区里，一个候选都收不到。
    """
    excluded = _excluded_tcp_ranges()

    def blocked(port):
        return any(lo <= port <= hi for lo, hi in excluded)

    candidates, port = [], preferred
    while len(candidates) < tries and port < preferred + span:
        if not blocked(port):
            candidates.append(port)
        port += 1
    candidates.append(0)                             # 0 = 让系统挑一个必然可用的

    last = None
    for candidate in candidates:
        try:
            httpd = ThreadingHTTPServer(("127.0.0.1", candidate), handler)
            actual = httpd.server_address[1]
            return httpd, actual, actual != preferred
        except OSError as exc:                       # 端口被占 → 试下一个
            last = exc
    raise last


def main():
    ap = argparse.ArgumentParser(description="Nuclear Option 汉化词表审核台")
    ap.add_argument("--data", default=os.path.join(REPO, "data"), help="数据目录（默认 <仓库>/data）")
    ap.add_argument("--plugin-dir", default=None, help="插件运行目录（默认按 GameDir 解析链推导）")
    ap.add_argument("--port", type=int, default=8765, help="首选监听端口（被占用或落在系统排除段时自动顺延）")
    ap.add_argument("--no-browser", action="store_true", help="不自动打开浏览器")
    ap.add_argument("--selftest", action="store_true", help="只跑一致性自检后退出")
    args = ap.parse_args()

    data_dir = os.path.abspath(args.data)
    plugin_dir = resolve_plugin_dir(args.plugin_dir)

    if args.selftest:
        return selftest(data_dir, plugin_dir)

    if not table_files(data_dir):
        print("找不到词表：%s（应至少有一个 translation.json）" % data_dir)
        return 2

    Handler.workspace = Workspace(data_dir, plugin_dir)
    meta = Handler.workspace.meta()
    httpd, port, shifted = bind_with_fallback(args.port, Handler)

    print("=" * 66)
    print("  Nuclear Option 汉化词表审核台")
    print("=" * 66)
    print("  数据目录   : %s（%d 个分类文件）" % (data_dir, meta["table_file_count"]))
    print("  条目       : %d 条（合并 md5 %s）" % (meta["total"], meta["table_md5"][:12]))
    for item in meta["categories"]:
        print("               %-14s %4d 条   %s"
              % (item["label"], item["entries"], item["desc"]))
    print("  运行目录   : %s%s" % (plugin_dir,
                                   "" if meta["plugin_dir_exists"] else "  ← 不存在（部署会失败）"))
    print("  地址       : http://127.0.0.1:%d/" % port
          + ("   （%d 不可用，已顺延）" % args.port if shifted and port else ""))
    print()
    print("  提示：工具只在本机监听；写盘只发生在点「保存」时，且会先备份到")
    print("        data/.backups/<时间戳>/，保存后自动复核不变量，失败即回滚。")
    print("     按 Ctrl+C 退出。")
    print("=" * 66)
    sys.stdout.flush()

    if not args.no_browser:
        threading.Timer(0.6, lambda: webbrowser.open(
            "http://127.0.0.1:%d/" % port)).start()
    try:
        httpd.serve_forever()
    except KeyboardInterrupt:
        print("\n已退出。")
    return 0


if __name__ == "__main__":
    sys.exit(main())
