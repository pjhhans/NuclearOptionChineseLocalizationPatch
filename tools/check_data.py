#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""词表数据自检 —— 改动 data/ 之后跑一遍。

**读得多宽，查得就有多全**：所有词表文件（translation / templates / fragments /
scopes/*.json）都会被读进来，先查每个文件自身的格式，再把它们合并起来查语义。
布局规则本身由 `_table_layout.py` 定义，这里靠 `tools/split_table.py` 的漂移检测兜底。

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
 10. 布局漂移（硬失败）                   —— 词条是否待在它该待的分类文件里
 11. 作用域分类覆盖（提示级）             —— 新出现的作用域会落到 misc.json，提醒补分类
 12. 模板门禁（提示级）                   —— 最短模板钉死全体文本付不付一次归一化
 13. 模板归一化重复（硬失败）             —— `~Foo  Bar` 与 `~Foo Bar` 运行时会静默覆盖

用法:
    python tools/check_data.py [数据目录]
"""

import collections
import json
import os
import re
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)

from _table_layout import (  # noqa: E402
    FALLBACK_CATEGORY, KIND_FRAGMENT, KIND_PLAIN, KIND_TEMPLATE,
    SCOPE_CATEGORY, kind_of, read_all, scope_of, table_files,
)

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
#     Tranche Class 裁波级（注：「X 级」不留空格）
#   · 术语终裁（2026-09-26 用户裁决，以第三方词表为参照后定的口径）：
#     Boscali 波斯卡利（原博斯卡利）/ Andromeda 仙女座级（原安德洛墨达级）/
#     Piledriver 撼地者（原打桩机）/ SFB-81 Darkreach 暗域（原暗界）
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
    "Andromeda 级": "仙女座级",
    # 2026-09-26 「术语终裁」
    "博斯卡利": "波斯卡利",
    "安德洛墨达": "仙女座",
    "打桩机": "撼地者",
    "暗界": "暗域",
    # 2026-09-28 用户实机裁决：对抗措施名不再保留英文缩写
    #   · ECM = Electronic Countermeasures，与既有「ECM Pod 电子对抗吊舱」对齐；
    #     原文 ECM / RADAR ECM 都归一到「电子对抗」（教程叙述句里的 radar ECM 同步）
    "雷达 ECM": "电子对抗",
    # 2026-09-28 用户裁决：图标筛选槽位窄，LCV 译名两级缩短（5 字 → 最终 2 字）
    "轻型战斗车": "战车",
    "轻型战车": "战车",
    #   · 对抗措施名词一律「电子对抗」，不得再写「电子干扰」（2026-09-28 用户裁决）
    #     —— 上轮只扫了英文 ECM 侧，漏掉以 jammers 为原文的美杜莎吊舱描述
    "电子干扰": "电子对抗",
    #   · 美杜莎吊舱的**装备描述**（units.json 的 [Description]）与教程叙述句同一口径（2026-09-28 用户裁决）
    #     —— 同一具 pod 不能一处叫「电子对抗吊舱」、一处叫「干扰吊舱」。这里必须登记**完整旧译**：
    #        单登记「干扰吊舱」会误伤 `JAMMER POD` 的正译（那是另一条原文、译文本就该是「干扰吊舱」）
    "进攻性干扰吊舱": "进攻性电子对抗吊舱",
    # 2026-09-28 用户裁决：连级编制名统一（首版两套写法并存 —— 裸键「连战斗群 / 合成兵种连」
    #   与 scoped「连级战斗队 / 合成化步兵连」，同族混用）
    #   · Armoured Company Group   → 装甲连级战斗群
    #   · Combined Arms Company    → 合成化步兵连
    #   · Mechanized Company Group → 机械化连级战斗群
    "装甲连战斗群": "装甲连级战斗群",
    "装甲连级战斗队": "装甲连级战斗群",
    "合成兵种连": "合成化步兵连",
    "机械化连战斗群": "机械化连级战斗群",
    "机械化连级战斗队": "机械化连级战斗群",
    # 2026-10-18 用户裁决：地图/模式名 Escalation 不再译作「冲突升级」，改为「全面冲突」
    #   —— 只登记模式名旧译；小写的 general「escalation」（2082 年那段叙述）= 升级，不受影响
    #      （该条译文已同步改写为「一次重大升级」，避免撞上这条废弃口径）
    "冲突升级": "全面冲突",
    # 2026-10-19 挂点组名口径：Aryx 机型侧栏把 OUTER/INNER 作为独立行显示，
    #   旧的「左边外侧 / 右边外侧」与同屏的「内侧右 / 外侧左」不成体系，统一为「外侧左 / 外侧右」
    "左边外侧": "外侧左",
    "右边外侧": "外侧右",
}

# 「英文词 + 级」= 还没中文化的舰名（型号 / 数字单位不会被匹配：要求 ≥3 个字母起头）
EN_SHIP_RE = re.compile(r"([A-Za-z][A-Za-z\-]{2,})\s*级")

problems = []
notes = []


def fail(message):
    problems.append(message)


def note(message):
    notes.append(message)


# --------------------------------------------------------------- 逐文件格式检查

def check_file_format(rel, path):
    """BOM / 行尾 / 字面重复键 / 键序。**每个文件各查一遍**，任一文件坏都要报出来。"""
    raw = open(path, "rb").read()
    label = rel
    if raw[:3] == b"\xef\xbb\xbf":
        fail("%s 带 BOM，应去掉" % label)
    crlf = raw.count(b"\r\n")
    if crlf:
        fail("%s 含 %d 处 CRLF，应为纯 LF" % (label, crlf))
    try:
        pairs = json.loads(raw.decode("utf-8-sig"), object_pairs_hook=list)
    except Exception as exc:                     # noqa: BLE001
        fail("%s 解析失败: %s" % (label, exc))
        return None
    if not isinstance(pairs, list):
        fail("%s 顶层不是「键值对对象」" % label)
        return None

    keys = [k for k, _ in pairs]
    if len(keys) != len(set(keys)):
        seen, dup = set(), []
        for k in keys:
            if k in seen and k not in dup:
                dup.append(k)
            seen.add(k)
        fail("%s 存在字面重复键（json 装 dict 时会静默丢键）: %r" % (label, dup[:5]))

    violations = [(keys[i], keys[i + 1]) for i in range(len(keys) - 1)
                  if keys[i] > keys[i + 1]]
    if violations:
        fail("%s 键未按码点升序，%d 处（会破坏最小 diff）: %r"
             % (label, len(violations), violations[:3]))

    for key, value in pairs:
        if not isinstance(value, str):
            fail("%s 译文不是字符串: %r" % (label, key[:60]))
    return dict(pairs)


# --------------------------------------------------------------- 合并后的语义检查

def classify(table):
    counts = collections.Counter()
    empty_fragment = []
    for key, value in table.items():
        kind = kind_of(key)
        if kind == KIND_TEMPLATE:
            counts["模板"] += 1
        elif kind == KIND_FRAGMENT:
            counts["片段"] += 1
            if not key[2:].strip():
                empty_fragment.append(key)
        else:
            counts["普通"] += 1
    if empty_fragment:
        fail("片段键内容为空（会匹配一切）: %r" % empty_fragment[:5])
    return counts


def check_empty_values(table):
    """空译文是**硬错误**：命中即把整条文本替换成空串，界面上直接消失。

    片段（>> << ==）尤其危险：`<<NE` 值为空 ⇒ 任何以 NE 结尾的行都被抹掉。
    """
    empty = [k for k, v in table.items() if not str(v).strip()]
    if empty:
        fail("空译文 %d 条（命中即整条文本消失）: %s"
             % (len(empty), ", ".join(repr(k) for k in empty[:10])))


def check_generic_fragments(table):
    """通用短词片段是硬错误。

    片段匹配分三类，危险度完全不同：
      - `==` 中缀用 IndexOf 在**任意位置**命中 —— 最危险；
      - `>>` 前缀只在开头命中 —— 次之（`TO ` 会把 `TO WEAPONS` 改成 `至 WEAPONS`）；
      - `<<` 后缀只在结尾命中 —— 最安全（` PINGS`、` SORTIES` 这类单位词没问题）。

    事故（2026-10-29）：子代理产出的 `" IN "`、`" IS "`、`" TO "`、`" LEFT"`、`" SEC"`
    被自动提升成中缀片段后，正常句子被改写成半中半英（`TARGET IS INSIDE` → `TARGET 状况为 INSIDE`），
    文本宽度变化 → TMP 每帧重排（界面错位）+ 每帧大量字符串分配（卡顿）。

    判据按危险度分级：带**锚点**（分隔符号或数字）即可放行，否则要求足够长。
    """
    anchors = "·-:@%#/>()—›•×+&|$[\u2014"
    whitelist = {" S OLD", " VEH ", "CMD  ", "TWR  ", "Runway ", "runway ",
                 "TIED TO ", "Rearmed ",
                 # 动词词缀：只在**句首**命中（>> 前缀），且大写开头 ——
                 # `Sell RAH-72 Knockout` 是游戏用 "Sell " + 机型名实时拼出来的，
                 # 只登记整串永远追不上。整串键（"Sell aircraft"）优先级更高，不会被它抢。
                 "Sell "}
    limits = {"==": 12, ">>": 8, "<<": 5}
    bad = []
    for k in table:
        if not k.startswith((">>", "<<", "==")):
            continue
        body = k[2:]
        if body in whitelist:
            continue
        if any(ch in body for ch in anchors) or any(ch.isdigit() for ch in body):
            continue
        if len(body) >= limits[k[:2]]:
            continue
        bad.append(k)
    if bad:
        fail("通用短词片段 %d 条（会改写正常句子，界面错位 + 卡顿）: %s"
             % (len(bad), ", ".join(repr(k) for k in bad[:12])))


def check_case_duplicates(table):
    buckets = collections.defaultdict(list)
    for key in table:
        buckets[key.lower()].append(key)
    dups = {k: v for k, v in buckets.items() if len(v) > 1}
    if not dups:
        return
    dangerous = [v for v in dups.values() if len({table[k] for k in v}) > 1]
    note("忽略大小写后重复的键 %d 组（译文相同则无害）" % len(dups))
    for group in dangerous:
        fail("大小写重复且译文不同（会静默覆盖）: %r -> %r"
             % (group, [table[k] for k in group]))


def check_brackets(table):
    """译文不应引入原文没有的括号。原文有括号的忠实保留，不在此列。"""
    for key, value in table.items():
        if kind_of(key) != KIND_PLAIN:
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
        if kind_of(key) != KIND_PLAIN:
            continue
        plain = SCOPE_RE.sub("", key)
        norm = re.sub(r"\s+", " ", TAG_RE.sub("\u0001", plain)).strip().lower()
        groups[norm].add(value)
    multi = {k: v for k, v in groups.items() if len(v) > 1}
    if multi:
        note("同原文多译文 %d 组:" % len(multi))
        for norm, values in list(multi.items())[:10]:
            note("    %r -> %r" % (norm[:50], sorted(values)))


def check_retired_terms(table):
    """口径统一之后，旧的译法不许再被写回来。

    注意「转轴炮」而不是「转轴」：`Roll Axis -> 滚转轴` 是合法译文。
    """
    hits = collections.Counter()
    for key, value in table.items():
        for old in RETIRED_TERMS:
            if old in value:
                hits[old] += 1
                fail("译文里出现已废弃的译法 %r（现行 %r）: %r -> %r"
                     % (old, RETIRED_TERMS[old], key[:50], value[:70]))
    if not hits:
        note("废弃译法扫描：%d 项口径均无回流" % len(RETIRED_TERMS))


def check_ship_names(table):
    """舰名已统一中文（2026-09-26 用户裁决）—— 值里不该再出现「英文词 + 级」。

    只提示不报错：型号（`PAB-250LR`）与数字单位（`8t 级装甲车`）都不会命中
    （正则要求 ≥3 个字母起头、且紧跟「级」），命中即说明有新的英文舰名待中文化。
    """
    hits = collections.Counter()
    for key, value in table.items():
        for match in EN_SHIP_RE.finditer(value):
            hits[match.group(1)] += 1
    if hits:
        note("待中文化的英文舰名 %d 个: %s"
             % (len(hits), " / ".join(sorted(hits))))


def check_scope_categories(table):
    """作用域是否都有分类归属；没归类的会落到 <FALLBACK>.json，只是组织问题、不影响运行时。"""
    unmapped = sorted({s for s in (scope_of(k) for k in table) if s and s not in SCOPE_CATEGORY})
    if unmapped:
        note("未归类作用域 %d 个（落 %s.json，建议补进 _table_layout.SCOPE_GROUPS）: %s"
             % (len(unmapped), FALLBACK_CATEGORY, " / ".join(unmapped)))


PARAM_MARKER = "{#}"


def _template_canonical(key):
    """模板键的运行时归一化形式（复刻 TextCanonicalizer.Canonicalize）。"""
    s = TAG_RE.sub("\u0001", key[1:])
    s = s.replace("\r\n", "\n").replace("\r", "\n").replace("\u000b", "\n")
    return re.sub(r"[ \t]+", " ", s).strip()


def check_template_canonical_duplicates(table):
    """两条模板键**归一化后**相同 ⇒ 运行时后者静默覆盖前者。

    `check_case_duplicates` 只比 `key.lower()`，抓不到「仅空白不同」这一类
    —— `~Foo  Bar` 与 `~Foo Bar` 归一化后是同一个键，而 `_templates` 是
    `OrdinalIgnoreCase` 字典，写进去就是覆盖，没有日志。
    """
    groups = collections.defaultdict(list)
    for key in table:
        if kind_of(key) != KIND_TEMPLATE:
            continue
        groups[_template_canonical(key).lower()].append(key)
    for keys in groups.values():
        if len(keys) < 2:
            continue
        values = {table[k] for k in keys}
        if len(values) > 1:
            fail("模板键归一化后重复且译文不同（会静默覆盖）: %r -> %r"
                 % (sorted(keys), sorted(values)))
        else:
            note("模板键归一化后重复（译文相同，仅冗余）: %r" % sorted(keys))


WILD_MARKER = '{*}'


HUD_SCOPE = 'BS:HUD'


def hud_visual_em(s):
    """一段文字的**视觉宽度**，单位 em。

    与插件侧 TmpPatches.FitsHudGrid 是同一套口径（改一处必须改另一处）：
    汉字/全角 1 em，其余按 ScopeText.DesignAdvance = 0.65 em 的等宽拉丁算。
    """
    total = 0.0
    for ch in s:
        o = ord(ch)
        wide = (0x2E80 <= o <= 0x9FFF) or (0xF900 <= o <= 0xFAFF) or (0xFF00 <= o <= 0xFFEF)
        total += 1.0 if wide else 0.65
    return total


def check_hud_width(table):
    """`[BS:HUD]` 的译文必须放得进 mod 自绘 HUD 的字格子。

    mod 的 Hud 模块把每个词画在一个按**等宽拉丁**算好的格子里
    （ScopeText.Draw：`原文长度 × 0.65 em × 1.1 + 6 px`），外层还套了 RectMask2D，
    放不下就被裁掉半个字。插件侧的策略是「放不下就保持英文」（见
    TmpPatches.FitsHudGrid / PatchHelpers.ReportHudTooWide），
    所以这里超宽 = 这条词**永远不会显示中文**，必须报出来改短。
    """
    over, way = [], []
    for key, value in table.items():
        if not key.startswith('['):
            continue
        scope, _, src = key[1:].partition(']')
        if scope != HUD_SCOPE or not value:
            continue
        grid = len(src) * 0.715 + 0.5
        need = hud_visual_em(value)
        if need > grid:
            (way if need > grid * 1.3 else over).append((src, value, need, grid))

    for src, value, need, grid in over:
        note('HUD 略宽（实机会保持英文）：%s → %s（%.2f em > 格子 %.2f em）'
             % (src, value, need, grid))
    for src, value, need, grid in way:
        fail('HUD 明显放不下：%s → %s（%.2f em > 格子 %.2f em，超出 %.0f%%）'
             % (src, value, need, grid, (need / grid - 1) * 100))


def check_wild_templates(table):
    """`{*}` 文本通配模板的三条硬约束。

    运行时规则（LocalizationTable.AddWildTemplate / WildTemplate.Match）：
      * 每个 `{*}` 至少吃 1 个字符，所以最短长度 = 固定段总长 + 通配个数；
      * 译文必须**逐个**引用 `{0}`…`{n-1}` —— 少引用就等于把运行时值丢了，
        「半句中文 + 少了主角」比不翻更糟，运行时直接拒收该键；
      * 首段为空的键（`~{*}` 开头）无法按前缀分桶，只能逐条试。
        台词里「<who> is down.」这类**占位符在句首**的句子天然如此，压不到个位数，
        所以这里只管别失控：每条先比尾段（常数级，绝大多数当场退出），
        34 条实测 ~0.4 µs/条文本，只作用于「精确/指纹都没命中的那部分文本」。
    """
    headless = []
    bad = []
    for key, value in table.items():
        if kind_of(key) != KIND_TEMPLATE or WILD_MARKER not in key:
            continue
        raw = key[1:]
        n = raw.count(WILD_MARKER)
        used = set(int(x) for x in re.findall(r'\{(\d+)\}', value or ''))
        if used != set(range(n)):
            bad.append((key, sorted(used), n))
        for seg in raw.split(WILD_MARKER)[1:]:
            if seg.strip()[:1].isdigit():
                bad.append((key, 'fixed-segment-starts-with-digit', seg[:12]))
                break
        if raw.startswith(WILD_MARKER):
            headless.append(key)

    for key, used, n in bad:
        fail('%s：{*} 有 %d 个，译文引用 %s（必须是 0..%d）' % (key, n, used, n - 1))
    if len(headless) > 60:
        fail('首段为空的 {*} 模板 %d 条（上限 60）：%s' % (len(headless), headless[:5]))
    elif headless:
        note('{*} 模板共 %d 条，其中首段为空（无索引、逐条试）%d 条'
             % (sum(1 for k in table if kind_of(k) == KIND_TEMPLATE and WILD_MARKER in k), len(headless)))


def check_template_gate(table):
    """模板门禁 = **最短**那条模板键的归一化长度 —— 报出来，别让它被悄悄拖低。

    运行时的第一道闸是 `if (text.Length < _minTemplateKeyLength) return false;`，
    而它挡在 `Canonicalize` **之前**（LocalizationTable.TryGetTemplate）。所以门禁
    每低一档，所有长度 ≥ 门禁的文本（缓存命中也不例外）都要多付一次归一化
    —— 标签正则替换 + 空白折叠 + Trim。

    这条曾被 `~Taxi`（4 字符）钉在 4 上，直到 2026-09-28 把 9 条纯冗余短模板
    清掉才升到 11。**新增任何短模板都会立刻把全体文本的门槛拖回去**，故在此显式提示。
    """
    rows = []
    for key in table:
        if kind_of(key) != KIND_TEMPLATE:
            continue
        s = _template_canonical(key)
        if not s:
            continue
        # 参数化模板按最短展开（{#} = 1 位数字）计。
        rows.append((len(s) - s.count(PARAM_MARKER) * 2 - s.count(WILD_MARKER) * 2, key))
    if not rows:
        return
    gate, pin = min(rows)
    note("模板门禁 %d，由最短模板 %r 钉死 —— 长度 < %d 的文本可跳过 ④ 段"
         % (gate, pin, gate))


def check_layout(data_dir, table):
    """布局漂移：词条应当待在它该待的分类文件里（规则见 _table_layout.partition）。"""
    try:
        from split_table import collect_drift
    except Exception as exc:                     # noqa: BLE001
        note("跳过布局检查（%s）" % exc)
        return
    drift = collect_drift(data_dir, table)
    for rel, why in drift:
        fail("布局漂移 %s: %s（跑 python tools/split_table.py --apply 修正）" % (rel, why))
    if not drift:
        note("布局检查：磁盘文件与分类规则逐字节一致")


def main():
    data_dir = sys.argv[1] if len(sys.argv) > 1 else DEFAULT_DATA
    if not os.path.isdir(data_dir):
        print("找不到数据目录: %s" % data_dir)
        return 2

    files = table_files(data_dir)
    if not files:
        print("词表文件一个都没找到: %s" % data_dir)
        return 2

    table = {}
    for rel, path in files:
        obj = check_file_format(rel, path)
        if obj is None:
            continue
        for key, value in obj.items():
            if isinstance(value, str):
                table[key] = value

    print("数据目录: %s" % data_dir)
    print("词表文件 %d 个，合并后 %d 条：" % (len(files), len(table)))
    for rel, path in files:
        size = os.path.getsize(path)
        print("  %-26s %6d B" % (rel, size))

    counts = classify(table)
    print("  普通 %d / 模板 %d / 片段 %d"
          % (counts["普通"], counts["模板"], counts["片段"]))

    check_empty_values(table)
    check_generic_fragments(table)
    check_case_duplicates(table)
    check_brackets(table)
    check_same_text(table)
    check_retired_terms(table)
    check_ship_names(table)
    check_scope_categories(table)
    check_template_gate(table)
    check_wild_templates(table)
    check_hud_width(table)
    check_template_canonical_duplicates(table)
    check_layout(data_dir, table)

    for item in notes:
        print("提示: " + item)

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
