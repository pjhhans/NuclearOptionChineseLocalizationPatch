#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""词表布局 —— 「读宽松、写确定」的单一事实来源。

布局（layout v2，2026-09-26 起）
-------------------------------
    data/translation.json   通用词条      无前缀键
    data/templates.json     整段模板      ``~`` 前缀
    data/fragments.json     拼接片段      ``>>`` / ``<<`` / ``==`` 前缀
    data/scopes/*.json      作用域词条    ``[Scope]`` 前缀，按**语义域**分类

为什么这样分（性能优先）
-------------------------------------------------------
1. **前三类与运行时的索引结构 1:1 对应**（``_global`` / ``_templates``+指纹 / 三张片段表）。
   拆开后每个文件只喂一种索引，解析完不必再逐键分流；模板与片段也不再混在
   ``_global`` 的关键字空间里。
2. **作用域词条独立成表**：最热的 ``_global`` 从 4600+ 条缩到约 2700 条，
   而 ``LookupScoped`` 的首次探测（每条要渲染的文本都会走一次）落在几十~几百条的
   小表上，不再是对着大表做哈希。
3. **按语义域分文件，而不是「一个作用域一个文件」**。后者在 Windows 上会直接丢数据：
   本仓库的作用域名里有 6 组只差大小写（``Text``/``text``、``Label``/``label``、
   ``Title``/``title``、``Header``/``header``、``CounterMeasureName``/``countermeasureName``、
   ``SellLabel``/``sellLabel``），而 NTFS 不区分大小写；此外还有带尾随空格的
   ``Text (TMP) ``、``header ``。分成 6 个语义域文件后，作用域名只出现在**键的前缀**里，
   与文件名彻底解耦，这两个坑一并消失。
4. **不按对方的主题名拆**：那是 XUnity.AutoTranslator 按作用域**懒加载**逼出来的形态。
   我们的加载器一次性建索引，文件数量的意义只是启动时的打开次数 —— 9 个文件已经够少。

读宽松
------
加载器（以及本模块的 :func:`read_all`）**按前缀分流，不关心键放在哪个文件**：
``~``/``>>``/``<<``/``==`` 归模板或片段；``[Scope]`` 归该作用域；其余归通用词条。
于是历史布局、手改、局部迁移都不会坏；``scopes/TypeText.json`` 那种「文件名即作用域名、
键不带前缀」的旧写法仍然兼容。

写确定
------
写出时一律走 :func:`partition`，同一份数据永远产出同一套文件（幂等），
``tools/split_table.py --check`` 靠这一条做漂移检测。

格式化契约
----------
无 BOM、纯 LF、``{`` + 每条一行（2 空格缩进）+ ``}`` **且最后一条不带逗号** ——
与既有文件逐字节一致（改这个会让整表变成假 diff）。
"""

import io
import json
import os
import re
import shutil

# --------------------------------------------------------------------------- 常量

MAIN_FILE = "translation.json"
TEMPLATES_FILE = "templates.json"
FRAGMENTS_FILE = "fragments.json"
SCOPES_DIRNAME = "scopes"

# 与 C# 侧 ExclusionRules.StripScopePrefix 完全一致：`[` + 1~40 字符 + `]`。
# 宽松一点会把正文里的方括号误当作用域，严格一点会让长作用域名漏掉。
SCOPE_RE = re.compile(r"^\[[^\]]{1,40}\]")

KIND_TEMPLATE = "template"
KIND_FRAGMENT = "fragment"
KIND_PLAIN = "plain"

# 语义域（文件顺序即加载顺序，也是审核台里的显示顺序）
CATEGORIES = [
    ("ui", "界面控件", "按钮 / 标签 / 标题 / 提示 / 买卖与设置项"),
    ("units", "单位兵器", "飞机、武器、挂载、涂装、单位简介"),
    ("mission", "任务目标", "任务标题、目标、简报、升级态势"),
    ("hud", "座舱读数", "HUD 标签、飞控配平、发动机告警、雷达读数"),
    ("world", "地图战报", "基地与地图标签、击杀战报、HQ 播报"),
    ("editor", "编辑器与多人", "任务/涂装编辑器、房间列表、输入设置"),
]
CATEGORY_NAMES = [name for name, _, _ in CATEGORIES]
FALLBACK_CATEGORY = "misc"

# 作用域 → 语义域。**只影响文件的组织方式，不影响运行时行为**
# （加载器按键上的 `[Scope]` 前缀分流，与它在哪个文件无关）。
SCOPE_GROUPS = {
    "ui": [
        "AltText", "Available", "Built in notice", "ButtonLabel", "CLS",
        "CategoryTitle", "Change Label", "Confirm Button Label", "Content Text",
        "ContributeLabel", "ContributeText", "Date Text (TMP)", "ELINT",
        "ExitButtonLabel", "FactionLabel", "FundsLabel", "GameTime_Label",
        "Header", "Header Text", "Header Text (TMP)", "header", "header ",
        "HintText", "HintText (TMP)", "hint", "place hint", "IDLabel",
        "InventoryLabel", "Label", "LabelText", "label", "LabelTimeFactor",
        "LabelTimeofDay", "LabelValue", "LastModifiedSort Text (TMP)",
        "MenuVolumeLabel", "MaxResponseAirspeedLabel", "MaxResponseRateLabel",
        "ModeText", "MuteButtonText", "NoFactionError", "NoLockText",
        "NoOptinosText", "Not found header", "Not found text", "Placeholder",
        "RemainLabel", "RemoveButton Text (TMP)", "RequestLabel", "ReserveLabel",
        "ReserveNoticeText", "SaveMessage", "SellLabel", "SellText", "sellLabel",
        "buyLabel", "Slider Text", "Sub header Text", "Subtitle", "TItle", "Text",
        "Text (Legacy)", "Text (TMP)", "Text (TMP) ", "text", "Theme Group Text",
        "Title", "Title Text", "Title Throttle", "TitleText", "TitleText (TMP)",
        "title", "ToggleLabel", "Totalcounttext", "UnavailableText", "Value",
        "VersionNumber", "WPN", "Warn", "error", "no faction warning",
        "exists warning text", "asymmetricLiftWarning", "error info Text (TMP)",
        "factionName", "h1", "h3", "load-message-text", "save info Text (TMP)",
        "selectedAction", "status", "subtext", "text-body", "visiblecounttext",
        "confirm Text (TMP)", "confirmLeaveText",
    ],
    "units": [
        "AircraftName", "AircraftRank", "CounterMeasureName", "countermeasureName",
        "CountermeasureType", "Desc", "Description", "HardpointSetName", "Item",
        "Item Label", "Item Name", "Item Type", "Livery faction warning", "Name",
        "pilotScore", "RankDisplay", "Type Text", "TypeText", "UnitDescription",
        "UnitName",
        "Weapon", "WeaponStation1_ammo", "WeaponStation2_ammo",
        "WeaponStation3_ammo", "WeaponStation4_ammo", "weaponName",
    ],
    "mission": [
        "CaptureText", "Mission Label", "MissionDescription",
        "MissionSubVersionTitle", "MissionTitle",
        "OBJ_Name", "OBJ_State", "ObjectiveInfo", "State", "TargetCode",
        "TargetUnit", "mission text", "missionEscalation", "missionTime",
    ],
    "hud": [
        "AOA", "AP_CombinedOverlay", "Airspeed", "Altitude Label", "AngVelText",
        "BasePitchDampingLabel", "Catapult Label", "CCIP_fallTime", "CCRP_fallTime",
        "Color_GLabel",
        "Color_RLabel", "DFactorFastText", "DFactorSlowText",
        "DirectControlFactorText", "DistanceText", "DynamicPressureLabel",
        "ENG 1 FIRE", "ENG 2 FIRE",
        "ENG 3 FIRE", "ENG 4 FIRE", "Engine FIre", "eng fire", "EngineText",
        "FLCSText", "FilteredPitchLabel", "GearTrimLabel", "HdgText",
        "HeadingText", "HookState", "LookAtText", "MFD_Label", "PFactorFastText",
        "PFactorSlowText", "PSMSpeedText", "PitchAdjustLimitFastText",
        "PitchAdjustLimitSlowText", "PitchAngVelLabel", "PitchTrimLabel",
        "PitchTrimLimitLabel", "PitchTrimRateLabel", "PowerTitle", "RCSLabel",
        "RMax_label", "RMin_label", "RNE_Label", "Radar", "Radar Label",
        "RadarText", "RelAltText", "RelSpeedText",
        "Revealed_Cam_Label", "Revealed_InfoPanel_Label", "Revealed_List_Label",
        "Revealed_Radar_Label", "Revealed_Status_Label",
        "Revealed_TargetList_Label", "Revealed_briefing_Label",
        "RollTightnessText",
        "RollTrimLimitText", "RollTrimRateText", "SlowFastText", "SpeedText",
        "ThreatTypeDisplay", "VelocityPitchDampingLabel", "WindRandomLabel",
        "YawTightnessText", "ap_value", "he_value", "rcs_value", "seeker_value",
        "average", "charge Label", "burnTime", "heat", "maxRange",
        "centerStabilizationSliderLabel", "cost_value", "infoAltitude", "infoHeading",
        "infoRange", "infoSpeed", "nozzleLabel", "range_value", "reverserActive",
        "reverserDisarmed", "reverserTitle", "targetDistLabel", "throttleLabel",
        "throttleReading",
    ],
    "world": [
        "AirbaseLabel", "AirbaseMapIcon", "AirbaseName", "AirbaseWarheads",
        "ActionReportText", "HQMessages", "HUDMessage", "KillFeed", "killText",
        "TeamkilledText", "MoonLabel", "NeighborsList", "NOXContactLabel",
        "NOXRespawnTimer", "NOXStatusHolder", "connections", "nodeName",
        "dropCountdown", "InfoText", "infoName",
    ],
    "editor": [
        "BanList Description", "BanList Header", "BlockButtonText",
        "ConditionsSliderLabel", "description value", "DisconnectReasonText",
        "GridAircraft",
        "GridText", "GridToolTip", "Hex Text", "InputManagerUpdateHeader",
        "InputManagerUpdateInfo", "KickButtonText", "LobbyName", "map Value",
        "MaxWrecksLabel", "mission Value",
        "Name Text (TMP)", "Name or Comment", "NameLabel", "Owner Label",
        "Owner Text", "Position different warning", "Preview Label",
        "SpawnTimingLabel", "SpawnTimingList", "StartingInventoryLabel",
        "TextMaxWreck", "TextWreckDespawn", "UniqueName Text (TMP)", "Uploading",
        "VoteNoButtonLabel", "VoteYesButtonLabel", "Voted Text",
        "WrecksDespawnTimeLabel",
        "eyeTrackingResponsivenessSliderLabel", "eyeVsHeadRatioSliderLabel",
        "headSensitivityPitchYawSliderLabel",
        "headSensitivityPositionSliderLabel", "headSensitivityRollSliderLabel",
        "interval Slider Text", "sendBytes",
    ],
}

SCOPE_CATEGORY = {}
for _cat, _items in SCOPE_GROUPS.items():
    for _s in _items:
        assert _s not in SCOPE_CATEGORY, "作用域重复归类: %r" % _s
        SCOPE_CATEGORY[_s] = _cat


# --------------------------------------------------------------------------- 键分类


def kind_of(key):
    """返回 ``template`` / ``fragment`` / ``plain``。

    顺序与 C# 侧 ``LocalizationTable.IngestMain`` 完全一致：``~`` 判在最前，
    所以 ``~[Scope]…`` 是模板而不是作用域词条。
    """
    if len(key) > 1 and key[0] == "~":
        return KIND_TEMPLATE
    if len(key) > 2 and key[0:2] in (">>", "<<", "=="):
        return KIND_FRAGMENT
    return KIND_PLAIN


def scope_of(key):
    """键上的 ``[Scope]`` 前缀；没有则 None。仅对普通词条有意义。"""
    match = SCOPE_RE.match(key)
    return match.group(0)[1:-1] if match else None


def category_of_scope(scope):
    return SCOPE_CATEGORY.get(scope, FALLBACK_CATEGORY)


def home_file(key):
    """键在写出时应该落到哪个文件（相对 data/ 的路径，统一用 ``/``）。"""
    kind = kind_of(key)
    if kind == KIND_TEMPLATE:
        return TEMPLATES_FILE
    if kind == KIND_FRAGMENT:
        return FRAGMENTS_FILE
    scope = scope_of(key)
    if scope is not None:
        return "%s/%s.json" % (SCOPES_DIRNAME, category_of_scope(scope))
    return MAIN_FILE


# --------------------------------------------------------------------------- 序列化


def dumps_line(key, value):
    """单条词条行（不含换行）。与既有文件的风格逐字节一致。"""
    return "  %s: %s," % (json.dumps(key, ensure_ascii=False),
                          json.dumps(value, ensure_ascii=False))


def serialize(entries):
    """把 ``{键: 译文}`` 序列化成整文件字节串（无 BOM、纯 LF、末条无逗号）。"""
    keys = sorted(entries)
    out = ["{"]
    for index, key in enumerate(keys):
        line = dumps_line(key, entries[key])
        if index == len(keys) - 1:
            line = line[:-1]          # 最后一条不带逗号
        out.append(line)
    out.append("}")
    return ("\n".join(out) + "\n").encode("utf-8")


# --------------------------------------------------------------------------- 读取


def table_files(data_dir):
    """按加载顺序返回 ``[(相对路径, 绝对路径), …]``。

    顺序必须与 C# 侧 ``LocalizationTable.Load`` 一致：主表 → 模板 → 片段 →
    ``scopes/*.json``（按名排序）。反向索引「先到先得」，顺序会决定同名中文归给谁。
    """
    files = []
    for name in (MAIN_FILE, TEMPLATES_FILE, FRAGMENTS_FILE):
        path = os.path.join(data_dir, name)
        if os.path.isfile(path):
            files.append((name, path))

    scopes_dir = os.path.join(data_dir, SCOPES_DIRNAME)
    if os.path.isdir(scopes_dir):
        names = sorted((n for n in os.listdir(scopes_dir)
                        if n.lower().endswith(".json")), key=str.lower)
        for name in names:
            files.append(("%s/%s" % (SCOPES_DIRNAME, name),
                          os.path.join(scopes_dir, name)))
    return files


def _read_object(path):
    raw = open(path, "rb").read()
    if not raw.strip():
        return {}
    # utf-8-sig：容忍历史文件残留的 BOM，读得进就比读不进强（写出时统一去掉）
    return json.loads(raw.decode("utf-8-sig"))


def read_all(data_dir):
    """读全部词表文件，合并成**规范形式**的 ``{键: 译文}``。

    规范形式 = 作用域词条一律写成 ``[Scope]原文``。
    ``scopes/`` 下的文件两种写法都认：

    * 键自带 ``[Scope]`` 前缀 → 用键里的作用域；
    * 键不带前缀 → 用**文件名**当作用域名（历史约定，如 ``TypeText.json``）。
    """
    merged = {}
    for rel, path in table_files(data_dir):
        obj = _read_object(path)
        if not isinstance(obj, dict):
            raise ValueError("不是键值对象: %s" % path)
        stem = os.path.splitext(os.path.basename(rel))[0]
        in_scopes = rel.startswith(SCOPES_DIRNAME + "/")
        for key, value in obj.items():
            if in_scopes and scope_of(key) is None:
                key = "[%s]%s" % (stem, key)
            merged[key] = value
    return merged


# --------------------------------------------------------------------------- 写出


def partition(merged):
    """``{键: 译文}`` → ``{相对路径: {键: 译文}}``，只含非空文件。"""
    buckets = {}
    for key, value in merged.items():
        buckets.setdefault(home_file(key), {})[key] = value
    return {rel: buckets[rel] for rel in sorted(buckets)}


def write_all(data_dir, merged, apply=True):
    """按 :func:`partition` 重写词表文件。

    返回 ``[(动作, 相对路径, 条数), …]``，动作 ∈ ``write`` / ``remove`` / ``keep``。
    ``apply=False`` 时只报告不落盘（漂移检测用）。
    """
    plan = partition(merged)
    actions = []

    for rel in sorted(plan):
        path = os.path.join(data_dir, rel.replace("/", os.sep))
        payload = serialize(plan[rel])
        current = None
        if os.path.isfile(path):
            current = open(path, "rb").read()
        if current == payload:
            actions.append(("keep", rel, len(plan[rel])))
            continue
        if apply:
            parent = os.path.dirname(path)
            if parent and not os.path.isdir(parent):
                os.makedirs(parent)
            with io.open(path, "wb") as handle:
                handle.write(payload)
        actions.append(("write", rel, len(plan[rel])))

    # 布局迁移：清掉已经被吸收的旧文件（scope 分表与上一版布局留下的文件）。
    # 只动 data/ 顶层的 templates/fragments 与 scopes/ 目录内部，且仅在目标计划里没有它时。
    stale = []
    for name in (TEMPLATES_FILE, FRAGMENTS_FILE):
        if name not in plan:
            stale.append(name)
    for rel, path in table_files(data_dir):
        if rel.startswith(SCOPES_DIRNAME + "/") and rel not in plan:
            stale.append(rel)
    for rel in stale:
        path = os.path.join(data_dir, rel.replace("/", os.sep))
        if os.path.isfile(path):
            if apply:
                os.remove(path)
            actions.append(("remove", rel, 0))

    return actions


def write_backup(data_dir, names, backup_dir=None, keep=20):
    """写盘前把原文件复制一份到 ``data/.backups/``（已 gitignore）。"""
    if backup_dir is None:
        backup_dir = os.path.join(data_dir, ".backups")
    if not os.path.isdir(backup_dir):
        os.makedirs(backup_dir)
    stamp = _timestamp()
    copied = []
    for rel in names:
        src = os.path.join(data_dir, rel.replace("/", os.sep))
        if not os.path.isfile(src):
            continue
        flat = rel.replace("/", "__")
        dst = os.path.join(backup_dir, "%s.%s" % (flat, stamp))
        shutil.copyfile(src, dst)
        copied.append(dst)
    _prune(backup_dir, keep)
    return copied


def _timestamp():
    import time
    return time.strftime("%Y%m%d-%H%M%S")


def _prune(backup_dir, keep):
    files = sorted((os.path.join(backup_dir, n) for n in os.listdir(backup_dir)),
                   key=os.path.getmtime)
    for path in files[:-keep] if keep > 0 else files:
        try:
            os.remove(path)
        except OSError:
            pass


# --------------------------------------------------------------------------- 统计


def stats(merged):
    counts = {"plain": 0, "template": 0, "fragment": 0}
    scopes = {}
    for key in merged:
        kind = kind_of(key)
        counts[kind] += 1
        if kind == KIND_PLAIN:
            scope = scope_of(key)
            if scope is not None:
                scopes[scope] = scopes.get(scope, 0) + 1
    counts["scoped"] = sum(scopes.values())
    counts["global"] = counts["plain"] - counts["scoped"]
    counts["scopes"] = len(scopes)
    counts["files"] = partition(merged)
    return counts


def report(merged, stream=None):
    import sys
    stream = stream or sys.stdout
    counts = stats(merged)
    total = len(merged)
    stream.write("词表共 %d 条：\n" % total)
    stream.write("  通用词条  %5d\n" % counts["global"])
    stream.write("  模板      %5d\n" % counts["template"])
    stream.write("  片段      %5d\n" % counts["fragment"])
    stream.write("  作用域词条 %5d（%d 个作用域）\n"
                 % (counts["scoped"], counts["scopes"]))
    stream.write("  —— 分类文件 ——\n")
    for rel in sorted(counts["files"]):
        stream.write("  %-24s %5d\n" % (rel, len(counts["files"][rel])))
    unmapped = sorted({s for s in _all_scopes(merged)
                       if s not in SCOPE_CATEGORY})
    if unmapped:
        stream.write("  未归类作用域 %d（落 %s.json）：%s\n"
                     % (len(unmapped), FALLBACK_CATEGORY, " / ".join(unmapped)))


def _all_scopes(merged):
    for key in merged:
        if kind_of(key) != KIND_PLAIN:
            continue
        scope = scope_of(key)
        if scope is not None:
            yield scope


if __name__ == "__main__":
    import sys
    root = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
    data = sys.argv[1] if len(sys.argv) > 1 else os.path.join(root, "data")
    report(read_all(data))
