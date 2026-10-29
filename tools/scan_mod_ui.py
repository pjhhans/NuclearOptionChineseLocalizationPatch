"""按「调用点」判断字面量是否真的上屏，找出词表未覆盖的界面文案。

用法：
    python tools/scan_mod_ui.py [源码目录] [输出 json]

源码目录可以指向 GitHub 克隆，也可以指向反编译实机 DLL 出来的目录
（后者更权威 —— 那是玩家手上真正跑的那一版）。

上一版按内容启发式过滤，把日志、诊断、文档串一起算进来了（3463 条，噪声占多数）。
这一版改成看**调用点**：字面量所在语句出现了 UI 绘制 API 才算界面文案。
"""
import json
import os
import re
import sys

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
REPO = ROOT
sys.path.insert(0, os.path.join(REPO, "tools"))

# 要扫的 mod 源码目录：命令行第一个参数 > 环境变量 BS_SRC > 工作区里的 GitHub 克隆。
# 也可以指向**反编译实机 DLL** 出来的目录 —— 那才是玩家手上真正跑的那一版。
GH = (sys.argv[1] if len(sys.argv) > 1
      else os.environ.get("BS_SRC")
      or os.path.join(os.path.dirname(ROOT), "_bs_gh"))
OUT = (sys.argv[2] if len(sys.argv) > 2
       else os.path.join(ROOT, "tools", "_mod_ui_miss.json"))
from _table_layout import read_all  # noqa: E402

TABLE = read_all(os.path.join(REPO, "data"))
LOW = {k.lower() for k in TABLE}
CJK = re.compile(r"[\u4e00-\u9fff]")

# 出现这些 API 的语句 = 文本要上屏
UI_API = re.compile(
    r"\b(AvText|Make|Set|Label|Caption|Title|Text|Put|Tile|Tag|Say|Hint|Spec|"
    r"Note|Chip|Row|Readout|Banner|Head|Copy|Word|Pill|Plate|Marquee|"
    r"Tooltip|Popup|Notice|HintText|SetText|AppendText|Show)\b")
# 语句里出现这些 = 日志/诊断，不上屏
LOG_API = re.compile(r"\b(Log|Warn|Error|Debug|Trace|Console|Throw|Exception)\b")

BAD = re.compile(
    r"(^[a-z]+://)|(\.(png|jpg|jpeg|dds|wav|ogg|json|txt|cs|dll|avss|nobp|assetbundle|bundle)$)|"
    r"(^\s*[/\\])|(^[A-Za-z]:\\)|(^[a-z][a-zA-Z0-9]*$)")
LOGLIKE = re.compile(
    r"(bundle|failed|unreadable|unsupported|could not|cannot|invalid|"
    r"expected|missing|unknown|tick failed|TextMeshPro/|^NOA |^avionics|shader|prefab)", re.I)


def statements(src):
    """粗切语句：按 ; 和换行切，返回 [(text, start_offset)]。"""
    out = []
    start = 0
    for m in re.finditer(r"[;\n]", src):
        out.append((src[start:m.start()], start))
        start = m.end()
    return out


def scan(path):
    with open(path, encoding="utf-8", errors="ignore") as f:
        src = f.read()
    if CJK.search(src):
        pass
    found = []
    stmts = statements(src)
    # 为每个字面量找所属语句
    for m in re.finditer(r'"((?:[^"\\\n]|\\.)*)"', src):
        lit = m.group(1)
        if not lit:
            continue
        # 定位语句
        st = None
        for text, off in stmts:
            if off <= m.start() < off + len(text) + 1:
                st = text
                break
        if st is None:
            continue
        # 跨行调用：把前 3 行也算上（写法常是 AvText.Make(\n  parent, "Name", ...)）
        before = src[max(0, m.start() - 260):m.start()]
        window = before + st
        if LOG_API.search(window) and not UI_API.search(before[-120:]):
            continue
        if not UI_API.search(window):
            continue
        lit = (lit.replace('\\n', '\n').replace('\\t', '\t').replace('\\"', '"')
               .replace('\\\\', '\\').replace('\\u00B7', '·').replace('\\u25AA', '▪')
               .replace('\\u25B8', '▸').replace('\\u25B2', '▲').replace('\\u2715', '✕')
               .replace('\\u2212', '−').replace('\\u2192', '→').replace('\\u203A', '›'))
        line = src.count("\n", 0, m.start()) + 1
        found.append((lit, line))
    return found


def is_ui(s):
    if not s or len(s) < 2 or len(s) > 220:
        return False
    if CJK.search(s) or BAD.search(s) or LOGLIKE.search(s):
        return False
    if not re.search(r"[A-Za-z]{2}", s):
        return False
    if " " in s.strip():
        return True
    return bool(re.fullmatch(r"[A-Z][A-Z0-9'\-/.]{1,24}", s.strip()))


def main():
    files = []
    for root, _, names in os.walk(GH):
        if os.sep + ".git" in root:
            continue
        for n in names:
            if n.endswith(".cs"):
                files.append(os.path.join(root, n))

    seen = {}
    for f in files:
        rel = os.path.relpath(f, GH).replace("\\", "/")
        try:
            lits = scan(f)
        except Exception:
            continue
        for s, line in lits:
            if not is_ui(s):
                continue
            seen.setdefault(s, f"{rel}:{line}")

    miss = []
    for s, src in seen.items():
        key = s.strip()
        if s in TABLE or key in TABLE or s.lower() in LOW or key.lower() in LOW:
            continue
        miss.append({"t": s, "src": src})

    miss.sort(key=lambda x: x["src"])
    json.dump(miss, open(OUT, "w", encoding="utf-8"), ensure_ascii=False, indent=1)

    mods = {}
    for m in miss:
        mod = m["src"].split("/")[0]
        mods[mod] = mods.get(mod, 0) + 1
    print("上屏字面量", len(seen), "| 词表未覆盖", len(miss))
    print("--- 按模块 ---")
    for k, v in sorted(mods.items(), key=lambda x: -x[1])[:12]:
        print("  ", v, k)
    print("--- 样例 ---")
    for m in miss[:25]:
        print("  ", m["t"][:70].replace("\n", "\\n"), "|", m["src"])


if __name__ == "__main__":
    main()
