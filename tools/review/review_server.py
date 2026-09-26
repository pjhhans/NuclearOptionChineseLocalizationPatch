#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""翻译审核台 —— 零依赖的本地 Web 工具，用来审阅与修改 data/translation.json。

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
  这一点有断言兜底（见 `_verify_roundtrip`），风格一旦漂移会立刻报错而不是静默产生 diff。

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


# 文件结构：`{` + N 行条目 + `}` + `split("\n")` 留下的末尾空串。
# 于是「总行数 = 条目数 + 3」—— 这个 3 曾经被我写成 2，导致每次保存的自检
# 都误判「行数与条目数不匹配」而回滚。留成常量，别再手算。
LINE_OVERHEAD = 3


def count_entries(lines):
    """从一个行数组反推条目数（供自检与预览显示用）。"""
    return len(lines) - LINE_OVERHEAD


def display_width(text):
    """CJK 全角记 2、其余记 1 —— 用来判断短槽位标签会不会撑破。"""
    return sum(2 if unicodedata.east_asian_width(ch) in ("F", "W") else 1 for ch in text)


class Table(object):
    """data/translation.json 的内存模型。

    持有**原始行**与**行号索引**，这是「最小 diff 写回」的前提：
    没被改的键，它那一行直接原样搬过去。
    """

    def __init__(self, path):
        self.path = path
        self.mtime = None
        self.load()

    # ---- 载入 ----------------------------------------------------------
    def load(self):
        raw = open(self.path, "rb").read()
        self.had_bom = raw[:3] == b"\xef\xbb\xbf"
        self.crlf = raw.count(b"\r\n")
        text = raw.decode("utf-8-sig")
        self.pairs = json.loads(text, object_pairs_hook=lambda kv: kv)   # 保留文件顺序
        self.lines = text.split("\n")
        self.load_problems = self._structure_problems()
        self.key_to_index = {k: i for i, (k, _) in enumerate(self.pairs)}
        self.line_of_key = {k: i + 1 for i, (k, _) in enumerate(self.pairs)}
        self.mtime = os.path.getmtime(self.path)
        self.md5 = hashlib.md5(raw).hexdigest()
        self._build_flags()

    def _structure_problems(self):
        """首尾结构 + 行数与条目数一致 —— 逐行替换的全部前提都挂在这上面。"""
        problems = []
        if self.had_bom:
            problems.append("文件带 BOM，应为无 BOM")
        if self.crlf:
            problems.append("含 %d 处 CRLF，应为纯 LF" % self.crlf)
        if len(self.lines) < 3 or self.lines[0] != "{" or self.lines[-1] != "" \
                or self.lines[-2] != "}":
            problems.append("首尾结构异常（应为 `{` / 各一行条目 / `}` / 空尾行）")
        body = self.lines[1:-2] if len(self.lines) >= 3 else []
        if len(body) != len(self.pairs):
            problems.append("正文行数 %d 与条目数 %d 不一致" % (len(body), len(self.pairs)))
        keys = [k for k, _ in self.pairs]
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
        if key not in self.line_of_key:
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
            "line": self.line_of_key[key],
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
        """生成新行数组。返回 (new_lines, changed_keys, dropped_keys, notes)。

        实现要点：把正文摊成 `[(key, 原始行)]` 的列表，所有增删改都在**行**这一层做，
        没碰到的行原样保留 —— 这样 diff 精确等于「被改的那些行」，一个字节都不多。
        定位一律走 `bisect`（正文本身按码点升序），**不去解析行文本**
        （键里可能含冒号，`split(":")` 会解析错）。
        """
        notes = []
        body = [(key, self.lines[i + 1]) for i, (key, _) in enumerate(self.pairs)]
        order = [key for key, _ in body]
        changed, dropped = set(), set()

        for key in deletes:
            if key not in self.key_to_index:
                notes.append("待删除的键不存在，已跳过：%r" % key)
                continue
            index = bisect.bisect_left(order, key)
            if index >= len(order) or order[index] != key:
                notes.append("待删除的键不存在，已跳过：%r" % key)
                continue
            del body[index]
            del order[index]
            dropped.add(key)

        for key, value in edits.items():
            if key not in self.key_to_index:
                notes.append("待编辑的键不存在，已跳过：%r" % key)
                continue
            if not isinstance(value, str):
                notes.append("译文必须是字符串，已跳过：%r" % key)
                continue
            index = bisect.bisect_left(order, key)
            body[index] = (key, dumps_line(key, value))
            changed.add(key)

        for key, value in adds.items():
            if key in self.key_to_index:
                notes.append("待新增的键已存在，已跳过：%r" % key)
                continue
            if not isinstance(value, str):
                notes.append("译文必须是字符串，已跳过：%r" % key)
                continue
            index = bisect.bisect_left(order, key)
            body.insert(index, (key, dumps_line(key, value)))
            order.insert(index, key)
            changed.add(key)

        # 逗号归一：正文每行都要有尾逗号，只有最后一行没有。
        # 这一趟只可能改动「成为末行」或「不再是末行」的那一行 ——
        # 其余行的字符串本来就以逗号结尾，赋值是空操作（不会污染最小 diff）。
        last = len(body) - 1
        for i in range(len(body)):
            line = body[i][1]
            want, has = i != last, line.rstrip().endswith(",")
            if want and not has:
                body[i] = (body[i][0], line.rstrip() + ",")
            elif not want and has:
                body[i] = (body[i][0], strip_comma(line))

        new_lines = [self.lines[0]] + [line for _, line in body] + self.lines[-2:]
        return new_lines, changed, dropped, notes

    def preview(self, edits, adds, deletes):
        """不落盘的 diff 预览（unified，行号在左）。"""
        new_lines, changed, dropped, notes = self._build_edits(edits, adds, deletes)
        diff = list(difflib.unified_diff(
            self.lines, new_lines,
            fromfile="a/translation.json", tofile="b/translation.json",
            lineterm="", n=1))
        return {"diff": diff, "notes": notes,
                "touched": sorted(changed), "dropped": sorted(dropped),
                "old_count": len(self.pairs),
                "new_count": count_entries(new_lines)}

    def save(self, edits, adds, deletes, backup_dir, keep_backups=20):
        """备份 -> 逐行写回 -> 自检 -> 失败回滚。返回结果字典。"""
        new_lines, changed, dropped, notes = self._build_edits(edits, adds, deletes)
        if not changed and not dropped:
            return {"ok": False, "reason": "没有实际改动", "notes": notes}

        # 1) 备份
        backup = None
        if backup_dir:
            os.makedirs(backup_dir, exist_ok=True)
            stamp = datetime.datetime.now().strftime("%Y%m%d-%H%M%S")
            backup = os.path.join(backup_dir, "translation.json.%s" % stamp)
            shutil.copy2(self.path, backup)
            self._prune_backups(backup_dir, keep_backups)

        # 2) 写回
        text = "\n".join(new_lines)
        with io.open(self.path, "w", encoding="utf-8", newline="") as fh:
            fh.write(text)

        # 3) 自检
        problems = self._verify(new_lines, changed, dropped, adds)
        if problems:
            if backup and os.path.isfile(backup):
                shutil.copy2(backup, self.path)
            self.load()
            return {"ok": False, "reason": "自检未通过，已从备份回滚",
                    "problems": problems, "backup": backup, "notes": notes}

        diff = list(difflib.unified_diff(
            self.lines, new_lines,
            fromfile="a/translation.json", tofile="b/translation.json",
            lineterm="", n=1))
        self.load()
        return {"ok": True, "backup": backup, "notes": notes,
                "diff": diff, "touched": sorted(changed), "dropped": sorted(dropped),
                "count": len(self.pairs)}

    def _verify(self, new_lines, changed, dropped, adds):
        """落盘后的不变量复核。任何一条不成立都说明手工行替换出了岔子。"""
        problems = []
        text = "\n".join(new_lines)
        if text[:1] == "\ufeff":
            problems.append("写出的文件带 BOM")
        if "\r" in text:
            problems.append("写出的文件含 CR")
        try:
            new_pairs = json.loads(text, object_pairs_hook=lambda kv: kv)
        except Exception as exc:                     # noqa: BLE001
            return ["写出的文件不是合法 JSON：%s" % exc]

        keys = [k for k, _ in new_pairs]
        if len(keys) != len(set(keys)):
            problems.append("出现字面重复键")
        bad = [(keys[i], keys[i + 1]) for i in range(len(keys) - 1) if keys[i] > keys[i + 1]]
        if bad:
            problems.append("键序被破坏，%d 处：%r" % (len(bad), bad[:3]))
        if len(new_lines) != len(new_pairs) + LINE_OVERHEAD:
            problems.append("行数 %d 与条目数 %d 不匹配" % (len(new_lines), len(new_pairs)))
        if any(not isinstance(v, str) for _, v in new_pairs):
            problems.append("有译文不是字符串")
        body = new_lines[1:-2]
        for i, line in enumerate(body):
            want_comma = i != len(body) - 1
            if line.rstrip().endswith(",") != want_comma:
                problems.append("第 %d 行逗号位不对" % (i + 2))
                break

        # 未触碰的键：那一行必须逐字节不变（这是「最小 diff」的硬保证）
        new_line_of = {k: line for k, line in zip(keys, body)}
        for key, old_index in self.line_of_key.items():
            if key in changed or key in dropped or key in adds:
                continue
            if key not in new_line_of:
                problems.append("键凭空消失：%r" % key)
                break
            if strip_comma(new_line_of[key]) != strip_comma(self.lines[old_index]):
                problems.append("未编辑的键却变了：%r" % key)
                break
        return problems

    @staticmethod
    def _prune_backups(backup_dir, keep):
        try:
            files = sorted(f for f in os.listdir(backup_dir)
                           if f.startswith("translation.json."))
        except OSError:
            return
        for name in files[:-keep]:
            try:
                os.remove(os.path.join(backup_dir, name))
            except OSError:
                pass


# --------------------------------------------------------------------------
# 附带数据：排除名单 / 作用域 / 运行期清单
# --------------------------------------------------------------------------
def load_json(path, default=None):
    try:
        return json.loads(io.open(path, encoding="utf-8").read())
    except Exception:                                # noqa: BLE001
        return default


class Workspace(object):
    """把「仓库数据 + 运行期产物」统一成一份可查询的状态。"""

    def __init__(self, data_dir, plugin_dir):
        self.data_dir = data_dir
        self.plugin_dir = plugin_dir
        self.table = Table(os.path.join(data_dir, "translation.json"))
        self.exclusions = load_json(os.path.join(data_dir, "exclusions.json"), {}) or {}
        self.force_scopes = load_json(os.path.join(data_dir, "force_scopes.json"), []) or []
        self.scopes_files = {}
        scopes_dir = os.path.join(data_dir, "scopes")
        if os.path.isdir(scopes_dir):
            for name in sorted(os.listdir(scopes_dir)):
                if name.endswith(".json"):
                    self.scopes_files[name] = load_json(os.path.join(scopes_dir, name), {})
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
    def meta(self):
        files = []
        for label, path in (("仓库词表", os.path.join(self.data_dir, "translation.json")),
                            ("仓库排除名单", os.path.join(self.data_dir, "exclusions.json")),
                            ("仓库 force_scopes", os.path.join(self.data_dir, "force_scopes.json")),
                            ("运行目录词表", os.path.join(self.plugin_dir, "translation.json")),
                            ("运行目录排除名单", os.path.join(self.plugin_dir, "exclusions.json"))):
            if os.path.isfile(path):
                raw = open(path, "rb").read()
                files.append({"label": label, "path": path, "size": len(raw),
                              "md5": hashlib.md5(raw).hexdigest(),
                              "mtime": datetime.datetime.fromtimestamp(
                                  os.path.getmtime(path)).strftime("%Y-%m-%d %H:%M:%S")})
            else:
                files.append({"label": label, "path": path, "size": None,
                              "md5": None, "mtime": None})

        counts = collections.Counter()
        flag_counts = collections.Counter()
        for key, _ in self.table.pairs:
            counts[self.table.kind_of(key)] += 1
            for flag in self.table.flags.get(key, []):
                flag_counts[flag] += 1

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
            "table_md5": self.table.md5,
            "table_mtime": datetime.datetime.fromtimestamp(
                self.table.mtime).strftime("%Y-%m-%d %H:%M:%S"),
            "load_problems": self.table.load_problems,
            "exclusions": {
                "scopes": self.exclusions.get("scopes", []),
                "texts": self.exclusions.get("texts", []),
                "terms": self.exclusions.get("terms", []),
                "useDefaults": self.exclusions.get("useDefaults"),
                "longGuard": self.exclusions.get("longGuard"),
            },
            "force_scopes": self.force_scopes,
            "scopes_files": {name: len(data) for name, data in self.scopes_files.items()},
            "runtime": {
                "missing": None if self.missing is None else len(self.missing),
                "missing_mtime": self.missing_mtime and datetime.datetime.fromtimestamp(
                    self.missing_mtime).strftime("%Y-%m-%d %H:%M:%S"),
                "untranslated": None if self.untranslated is None else len(self.untranslated),
                "untranslated_mtime": self.untranslated_mtime and datetime.datetime.fromtimestamp(
                    self.untranslated_mtime).strftime("%Y-%m-%d %H:%M:%S"),
            },
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
        if t.crlf:
            pass
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
        """
        if not os.path.isdir(self.plugin_dir):
            return {"ok": False, "reason": "运行目录不存在：%s" % self.plugin_dir}
        results = []
        pairs = []
        for name in ("translation.json", "exclusions.json", "force_scopes.json"):
            src = os.path.join(self.data_dir, name)
            if os.path.isfile(src):
                pairs.append((src, os.path.join(self.plugin_dir, name), name))
        pairs.append((os.path.join(self.data_dir, "fonts", "font.ttf"),
                      os.path.join(self.plugin_dir, "font.ttf"), "font.ttf"))
        scopes_dir = os.path.join(self.data_dir, "scopes")
        if os.path.isdir(scopes_dir):
            os.makedirs(os.path.join(self.plugin_dir, "scopes"), exist_ok=True)
            for name in sorted(os.listdir(scopes_dir)):
                if name.endswith(".json"):
                    pairs.append((os.path.join(scopes_dir, name),
                                  os.path.join(self.plugin_dir, "scopes", name),
                                  "scopes/" + name))

        for src, dst, label in pairs:
            if not os.path.isfile(src):
                continue
            before = open(dst, "rb").read() if os.path.isfile(dst) else None
            shutil.copy2(src, dst)
            after = open(dst, "rb").read()
            results.append({"file": label,
                            "changed": before != after,
                            "md5": hashlib.md5(after).hexdigest()})
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
def selftest(data_dir, plugin_dir):
    """三件事：① 序列化风格没漂移；② 在**临时副本**上真跑一次保存；③ 真文件没被碰。"""
    print("仓库根目录 : %s" % REPO)
    print("数据目录   : %s" % data_dir)
    print("运行目录   : %s" % plugin_dir)

    table_path = os.path.join(data_dir, "translation.json")
    before = hashlib.md5(open(table_path, "rb").read()).hexdigest()

    ws = Workspace(data_dir, plugin_dir)
    t = ws.table
    print("词表       : %d 条，md5=%s" % (len(t.pairs), t.md5))
    print("结构问题   : %s" % (t.load_problems or "无"))
    problems = []

    # ---- ① 序列化往返：既有每一行都能被 dumps_line 逐字复现（风格未漂移的证明）----
    bad = [key for index, (key, value) in enumerate(t.pairs)
           if strip_comma(t.lines[index + 1]) != strip_comma(dumps_line(key, value))]
    print("① 序列化往返 : %d/%d 行逐字复现" % (len(t.pairs) - len(bad), len(t.pairs)))
    if bad:
        problems.append("序列化风格已漂移，%d 行不一致（例：%r）" % (len(bad), bad[:2]))

    # ---- ② 在临时副本上真跑一次保存 ----
    tmp = tempfile.mkdtemp(prefix="no_review_selftest_")
    try:
        shutil.copy2(table_path, os.path.join(tmp, "translation.json"))
        tt = Table(os.path.join(tmp, "translation.json"))

        target = tt.pairs[len(tt.pairs) // 3][0]
        old_value = tt.entry(target)["value"]
        fake = "zzz-selftest-\u4e2d\u6587\u952e"
        edit_value = old_value + "\u3002"

        # 空改动必须拒绝写盘
        empty = tt.save({}, {}, [], os.path.join(tmp, "backups"))
        print("②a 空改动    : %s（应拒绝）" % ("已拒绝" if not empty.get("ok") else "竟然写了盘！"))
        if empty.get("ok"):
            problems.append("空改动竟然落盘")

        # 真改动：编辑 1 条 + 新增 1 条
        result = tt.save({target: edit_value}, {fake: "\u81ea\u68c0\u5360\u4f4d"},
                         [], os.path.join(tmp, "backups"))
        print("②b 编辑+新增 : ok=%s%s" % (
            result.get("ok"), "" if result.get("ok")
            else "  ！%s %s" % (result.get("reason"), result.get("problems"))))
        if not result.get("ok"):
            problems.append("保存失败：%s %s" % (result.get("reason"),
                                                result.get("problems")))
        else:
            if result["count"] != len(t.pairs) + 1:
                problems.append("条目数应为 %d，实际 %d"
                                % (len(t.pairs) + 1, result["count"]))
            diff_lines = [l for l in result["diff"]
                          if l[:1] in "+-" and l[:3] not in ("+++", "---")]
            # 期望：改 1 行（-/+ 各一）+ 新增 1 行 = 3
            if len(diff_lines) != 3:
                problems.append("diff 应为 3 行（改 1 加 1），实际 %d 行：%r"
                                % (len(diff_lines), diff_lines[:6]))
            print("②c 最小 diff : %d 行改动（期望 3：改 1 -/+、新增 1 +）"
                  % len(diff_lines))
            if result.get("backup") and not os.path.isfile(result["backup"]):
                problems.append("备份文件没生成")

            # 落盘结果复核：未触碰的键，那一行必须逐字节不变。
            # 注意必须**按键**比对而不是按行号 —— 新增键会让它后面的行整体位移一位。
            # 也别再给 `line_of_key` 加偏移：它存的已经是行号（pairs 下标 + 1）。
            tt2 = Table(os.path.join(tmp, "translation.json"))
            mutated = []
            for key, _ in t.pairs:
                if key == target:
                    continue
                old_line = t.lines[t.line_of_key[key]]
                new_line = tt2.lines[tt2.line_of_key[key]]
                if strip_comma(new_line) != strip_comma(old_line):
                    mutated.append(key)
            print("②d 未触碰行  : %d 行被动了（应为 0）" % len(mutated))
            if mutated:
                problems.append("%d 行未触碰的表行被改写（例：%r）"
                                % (len(mutated), mutated[:3]))

        # ---- ③ 回滚路径：故意喂一个会破坏升序的「新增」之外的坏输入 ----
        bad_result = tt.save({"不存在的键": "x"}, {}, [], os.path.join(tmp, "backups"))
        print("③ 非法改动   : %s（应拒绝或跳过并提示）"
              % ("已跳过" if bad_result.get("notes") or not bad_result.get("ok") else "无提示"))
        if bad_result.get("ok") and not bad_result.get("notes"):
            problems.append("非法键既没被拒绝也没给提示")
    finally:
        shutil.rmtree(tmp, ignore_errors=True)

    # ---- ④ 真文件必须原封不动 ----
    after = hashlib.md5(open(table_path, "rb").read()).hexdigest()
    print("④ 真文件 md5 : %s（%s）" % (after[:12], "未改动" if after == before else "被改了！"))
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

    if not os.path.isfile(os.path.join(data_dir, "translation.json")):
        print("找不到词表：%s" % os.path.join(data_dir, "translation.json"))
        return 2

    Handler.workspace = Workspace(data_dir, plugin_dir)
    meta = Handler.workspace.meta()
    httpd, port, shifted = bind_with_fallback(args.port, Handler)

    print("=" * 66)
    print("  Nuclear Option 汉化词表审核台")
    print("=" * 66)
    print("  词表       : %s" % os.path.join(data_dir, "translation.json"))
    print("  条目       : %d 条（md5 %s）" % (meta["total"], meta["table_md5"][:12]))
    print("  运行目录   : %s%s" % (plugin_dir,
                                   "" if meta["plugin_dir_exists"] else "  ← 不存在（部署会失败）"))
    print("  地址       : http://127.0.0.1:%d/" % port
          + ("   （%d 不可用，已顺延）" % args.port if shifted and port else ""))
    print()
    print("  提示：工具只在本机监听；写盘只发生在点「保存」时，且会先备份到")
    print("        data/.backups/，保存后自动复核不变量，失败即回滚。")
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
