#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""打发布包：把「不该出门的东西」变成构建期断言，而不是靠人肉检查清单。

用法
----
    python tools/make_release_zip.py                 # 以仓库为源（推荐）
    python tools/make_release_zip.py --source plugin # 以游戏运行目录为源（等价于装完的样子）
    python tools/make_release_zip.py --out D:/tmp    # 指定输出目录

输出
----
    NuclearOptionChineseLocalizationPatch-v1.5.5.zip
      └─ NuclearOptionChineseLocalizationPatch/   ← 单一顶层目录，与 README 安装说明一致
           ├─ NuclearOptionChineseLocalizationPatch.dll
           ├─ Newtonsoft.Json.dll                  （分发第三方组件，需 LICENSE / THIRD-PARTY-NOTICES）
           ├─ translation.json / exclusions.json / force_scopes.json
           ├─ font.ttf
           ├─ scopes/*.json
           └─ README.md / LICENSE / THIRD-PARTY-NOTICES.md

为什么要有这个脚本
------------------
发布包一旦挂到 Release 上，错误就只能靠**改写历史**来挽回。所以这里把四类错误
做成硬失败（命中即不产出 zip、退出码非 0）：

  1. 必需文件缺失            —— 漏掉 LICENSE 是最常见的合规事故
  2. 混入运行期文件          —— missing.json / untranslated.json 是**用户游玩过程的逐条日志**，
                                里面必然混着玩家昵称、工坊物件名、上传者 handle，是最大的泄漏源；
                                *.pdb / bin/ / obj/ / .git/ 同理（PDB 还带源码绝对路径）
  3. 产物内嵌本机路径        —— 逐 entry 扫字节：Steam 库路径、Users 目录、盘符绝对路径，
                                同时对 .dll 做 UTF-16LE 扫描（.NET 字符串字面量是 UTF-16）
  4. 版本号三处不一致        —— csproj <Version> / 代码 PluginVersion / zip 文件名；tag 也要跟它们对齐

数据源为何默认取仓库而不是运行目录
----------------------------------
运行目录是「本机热改过」的现场（词表可能被 F11 编辑、还堆着诊断文件），
仓库 data/ 才是版本控制里的权威副本。--source plugin 只用于交叉核对。
"""

import argparse
import json
import os
import re
import sys
import zipfile
from pathlib import Path

REPO = Path(__file__).resolve().parent.parent
TOP = "NuclearOptionChineseLocalizationPatch"

# ---------------------------------------------------------------- 清单定义
# (zip 内相对路径, 仓库源, 运行目录源)；数据与文档取自仓库，DLL 取自构建输出
BUILD_DIR = REPO / "bin" / "Release" / "net472"

RUNTIME_FILES = [
    ("NuclearOptionChineseLocalizationPatch.dll", BUILD_DIR / "NuclearOptionChineseLocalizationPatch.dll"),
    ("Newtonsoft.Json.dll", BUILD_DIR / "Newtonsoft.Json.dll"),
    ("translation.json", REPO / "data" / "translation.json"),
    ("exclusions.json", REPO / "data" / "exclusions.json"),
    ("force_scopes.json", REPO / "data" / "force_scopes.json"),
    ("font.ttf", REPO / "data" / "fonts" / "font.ttf"),
    ("README.md", REPO / "README.md"),
    ("LICENSE", REPO / "LICENSE"),
    ("THIRD-PARTY-NOTICES.md", REPO / "THIRD-PARTY-NOTICES.md"),
]

SCOPES_DIR = REPO / "data" / "scopes"

# 绝不允许出现在发布包里的名字/后缀
FORBIDDEN_NAMES = {"missing.json", "untranslated.json"}
FORBIDDEN_PARTS = {".git", "bin", "obj", "__pycache__"}
FORBIDDEN_SUFFIX = (".pdb", ".pyc", ".bak")

# 本机路径特征。刻意用「具体词」而不是宽泛的盘符正则：二进制资源里
# 两字符的 `C:\` 会大量误报，而这些词一旦出现就必然是路径没脱敏。
PATH_MARKERS = [
    b"SteamLibrary",
    b"steamapps",
    b"\\Users\\",
    b"/Users/",
    b"\\AppData\\",
    b"/AppData/",
    b"\\Documents and Settings\\",
]
# 盘符绝对路径：`X:\dir` 或 `X:/dir`
DRIVE_ABS = re.compile(rb"[A-Za-z]:[\\/][A-Za-z_]")
# 只对本程序集的 DLL 启用盘符检查，理由：
#   · 文本文件（README / 词表 / json）里合法地存在示例路径与转义序列 ——
#     README 的 `-p:GameDir="D:\Games\Nuclear Option"` 是给人看的示例；
#     JSON 里的 `\n` 两个字节会被 `[A-Za-z]:[\\/][A-Za-z_]` 误判成 `X:\n`；
#   · 第三方二进制（Newtonsoft.Json.dll）里带的是 NuGet 的构建机路径（形如 D:\a\1\s\…），
#     那是上游既有的、与本机无关，拦它只会让发布永远失败。
# 本程序集的 DLL 则必须干净 —— PathMap + Deterministic 就是为它设的。
OWN_DLL = "NuclearOptionChineseLocalizationPatch.dll"


def read_version():
    """从 csproj 读 <Version>，从代码读 PluginVersion，并断言两者一致。"""
    csproj = (REPO / "NuclearOptionChineseLocalizationPatch.csproj").read_text(encoding="utf-8")
    m = re.search(r"<Version>\s*([0-9][^<\s]*)\s*</Version>", csproj)
    if not m:
        fail("csproj 里找不到 <Version>")
    csproj_version = m.group(1)

    src = (REPO / "src" / "LocalizationPlugin.cs").read_text(encoding="utf-8")
    m = re.search(r'PluginVersion\s*=\s*"([^"]+)"', src)
    if not m:
        fail("src/LocalizationPlugin.cs 里找不到 PluginVersion")
    code_version = m.group(1)

    if csproj_version != code_version:
        fail(
            "版本号不一致：csproj <Version> = %s，代码 PluginVersion = %s\n"
            "  发布 tag、发布包文件名都由 csproj 推导，两者必须先对齐。"
            % (csproj_version, code_version)
        )
    print("  版本号一致：%s（csproj 与 PluginVersion）" % csproj_version)
    return csproj_version


def collect(source):
    """返回 [(zip 内路径, 磁盘路径)]。"""
    entries = []
    build_root = BUILD_DIR
    plugin_root = Path(os.environ.get("NUCLEAR_OPTION_DIR", ""))
    if source == "plugin":
        if not str(plugin_root):
            fail("--source plugin 需要环境变量 NUCLEAR_OPTION_DIR 指向游戏根目录")
        plugin_dir = plugin_root / "BepInEx" / "plugins" / TOP
        if not plugin_dir.is_dir():
            fail("运行目录不存在：%s" % plugin_dir)
        for name in [r[0] for r in RUNTIME_FILES]:
            entries.append((name, plugin_dir / name))
        for p in sorted((plugin_dir / "scopes").glob("*.json")):
            entries.append(("scopes/" + p.name, p))
        return entries

    for name, path in RUNTIME_FILES:
        entries.append((name, path))
    if not SCOPES_DIR.is_dir():
        fail("scopes 目录不存在：%s" % SCOPES_DIR)
    for p in sorted(SCOPES_DIR.glob("*.json")):
        entries.append(("scopes/" + p.name, p))
    return entries


def audit_entry(arcname, path, problems):
    """对单个 entry 做存在性 / 禁止项 / 字节级审计。"""
    if not path.is_file():
        problems.append("%s：源文件不存在（%s）" % (arcname, path))
        return None
    parts = set(Path(arcname).parts)
    if parts & FORBIDDEN_PARTS:
        problems.append("%s：路径里含禁止目录（%s）" % (arcname, sorted(parts & FORBIDDEN_PARTS)))
        return None
    if Path(arcname).name in FORBIDDEN_NAMES:
        problems.append("%s：运行期记录文件，绝不可分发" % arcname)
        return None
    if arcname.lower().endswith(FORBIDDEN_SUFFIX):
        problems.append("%s：禁止的后缀" % arcname)
        return None

    data = path.read_bytes()
    for marker in PATH_MARKERS:
        if marker in data:
            problems.append("%s：字节中含本机路径标记 %r" % (arcname, marker))

    if arcname.lower().endswith(".dll"):
        # .NET 字符串字面量是 UTF-16LE —— PDB 路径残留就藏在这里
        try:
            wide = data.decode("utf-16-le", "ignore").encode("utf-8", "ignore")
        except Exception:
            wide = b""
        for marker in PATH_MARKERS:
            if marker in wide:
                problems.append("%s：UTF-16LE 段含本机路径标记 %r" % (arcname, marker))
        if Path(arcname).name == OWN_DLL:
            for label, buf in (("UTF-8", data), ("UTF-16LE", wide)):
                hit = DRIVE_ABS.search(buf)
                if hit:
                    problems.append(
                        "%s：%s 段含盘符绝对路径 %r（PathMap 没生效？）"
                        % (arcname, label, hit.group())
                    )
    return data


def fail(msg):
    print("✗ %s" % msg, file=sys.stderr)
    sys.exit(1)


def main():
    ap = argparse.ArgumentParser(description="打发布包（含强制审计）")
    ap.add_argument("--out", default=str(REPO), help="输出目录（默认仓库根）")
    ap.add_argument(
        "--source",
        choices=("repo", "plugin"),
        default="repo",
        help="repo=用仓库 data/ 与构建输出（默认）；plugin=用游戏运行目录",
    )
    args = ap.parse_args()

    print("== 1/4 版本号 ==")
    version = read_version()

    print("== 2/4 收集文件 ==")
    entries = collect(args.source)
    seen = set()
    for arcname, path in entries:
        if arcname in seen:
            fail("zip 内路径重复：%s" % arcname)
        seen.add(arcname)
        print("  + %-46s %s" % (arcname, path))

    print("== 3/4 审计 ==")
    problems = []
    payload = []
    for arcname, path in entries:
        data = audit_entry(arcname, path, problems)
        if data is not None:
            payload.append((arcname, path, data))
    # 反向断言：必需项一个都不能少（audit 已把缺失记进 problems，这里再确认必需名齐备）
    required_names = {r[0] for r in RUNTIME_FILES}
    got = {a for a, _, _ in payload}
    missing = required_names - got
    if missing:
        problems.append("缺少必需文件：%s" % sorted(missing))
    if problems:
        print("✗ 审计未通过，未产出 zip：", file=sys.stderr)
        for p in problems:
            print("   - %s" % p, file=sys.stderr)
        sys.exit(1)
    print("  审计通过：%d 个 entry，无本机路径、无运行期文件、无禁止后缀" % len(payload))

    print("== 4/4 写包 ==")
    out_path = Path(args.out) / ("%s-v%s.zip" % (TOP, version))
    out_path.parent.mkdir(parents=True, exist_ok=True)
    # 固定时间戳 → 同内容产出同样的字节（便于比对发布产物是否被替换）
    stamp = (2026, 1, 1, 0, 0, 0)
    with zipfile.ZipFile(out_path, "w", zipfile.ZIP_DEFLATED, compresslevel=9) as z:
        for arcname, _path, data in payload:
            info = zipfile.ZipInfo("%s/%s" % (TOP, arcname), date_time=stamp)
            info.compress_type = zipfile.ZIP_DEFLATED
            info.external_attr = 0o644 << 16
            z.writestr(info, data)

    size = out_path.stat().st_size
    print("  → %s（%.1f MB，%d 个 entry）" % (out_path, size / 1048576.0, len(payload)))
    print("== 完成 ==   tag 应为 v%s" % version)


if __name__ == "__main__":
    main()
