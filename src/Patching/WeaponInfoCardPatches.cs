using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace NuclearOptionChineseLocalizationPatch.Patching
{
    /// <summary>
    /// 基地武器信息卡（<c>AircraftSelectionMenu.DisplayInfo</c>）的几何稳定化 v5。
    ///
    /// <para><b>实证结构：</b><c>Darkener</c>（LayoutGroup）下三个子项：
    /// <c>[0] WeaponImage</c>（图片区）、<c>[1] WeaponInfo</c>（参数区，宽度恒为 0 是本体设计，
    /// 行容器自管宽度）、<c>[2] Description</c>（= <c>info</c> TMP）。
    /// 每次打开菜单游戏都会新建 <c>SelectionMenu(Clone)</c>（LogOutput 多实例实证）。</para>
    ///
    /// <para><b>v4 失败根因：</b>全局几何表对「新建实例」盲目回放 —— 新实例首帧 rect
    /// 还是预制体默认值就被 detach + 钉死，钉在错误状态（第二次打开图片与参数消失）。</para>
    ///
    /// <para><b>v5 策略：按实例钉死。</b>uGUI 布局重排在渲染期，Update 时 rect 为上一帧
    /// 布局最终结果。每个新实例先完全不碰几何（原生布局），直到其自身满足有效性判据
    /// （三者激活 + 图片宽≥1 + 参数/描述位于图片右侧），同帧读快照 → detach + 钉死。
    /// 与既有全局几何对照，不一致则告警并自适应采用新值。
    /// 看门狗：已钉实例激活却持续退化（图片/描述宽&lt;1）超过 60 帧 → 解除钉死重记。</para>
    /// </summary>
    [HarmonyPatch]
    internal static class WeaponInfoCardPatches
    {
        /// <summary>配置开关（启动时由 ModSettings 接线）。</summary>
        internal static bool Enabled = true;

        private static readonly string[] StatFieldNames =
        {
            "weaponSeeker", "weaponRange", "weaponAP", "weaponHE", "weaponRCS", "weaponCost",
        };

        internal static FieldInfo _infoField;        // TMP_Text：描述控件（= Darkener 子项 Description）
        internal static FieldInfo _infoAreaField;    // GameObject：weaponInfoArea（参数区容器，宽恒 0）
        internal static FieldInfo _imageAreaField;   // GameObject：weaponImageArea（图片区）
        internal static FieldInfo _weaponImageField; // Image：weaponImage（图标本体）
        internal static FieldInfo[] _statFields;

        /// <summary>最近一次钉死的几何（对照与回放基准）：键 → {x, y, w, h}。</summary>
        internal static readonly Dictionary<string, float[]> Pins = new Dictionary<string, float[]>();

        private static IEnumerable<MethodBase> TargetMethods()
        {
            var type = AccessTools.TypeByName("AircraftSelectionMenu");
            if (type == null)
            {
                Diagnostics.Log.Info("[信息卡] 未找到 AircraftSelectionMenu，补丁空转");
                yield break;
            }

            ResolveFields(type);

            foreach (MethodInfo m in AccessTools.GetDeclaredMethods(type))
                if (m.Name == "DisplayInfo")
                    yield return m;
        }

        internal static void ResolveFields(System.Type type)
        {
            if (_statFields != null)
                return;
            _infoField = AccessTools.Field(type, "info");
            _infoAreaField = AccessTools.Field(type, "weaponInfoArea");
            _imageAreaField = AccessTools.Field(type, "weaponImageArea");
            _weaponImageField = AccessTools.Field(type, "weaponImage");
            _statFields = new FieldInfo[StatFieldNames.Length];
            for (int i = 0; i < StatFieldNames.Length; i++)
                _statFields[i] = AccessTools.Field(type, StatFieldNames[i]);
        }

        /// <summary>
        /// 表格几何（<b>按实例</b>）：[0]=左格数值列位 posLeft，[1]=右格数值列位 posRight，
        /// [2]=右格起点 colC（相对行左缘）。同一列的所有行共享同一列位 →
        /// 数值跨行垂直对齐（表格效果）。不同信息卡（基地菜单 / 挂架面板）字号不同，
        /// 各自实测，互不污染（v6 教训）。
        /// </summary>
        internal static readonly Dictionary<int, float[]> TableGeom = new Dictionary<int, float[]>();

        /// <summary>已做过原文转储的实例（诊断，防刷屏）。</summary>
        private static readonly HashSet<int> _dumped = new HashSet<int>();

        /// <summary>
        /// 单元格文本宽下界：GetPreferredValues 实测与逐字符估计取较大者。
        /// 实测依赖当前字体度量 —— 新克隆菜单首刷时 CJK 可能仍走回退字体
        /// （每字 ≈0.5em），估出的列位会把中文标签压住（v8 实测「穿深:0」粘连）。
        /// 汉字/全角字符在真实字体里恰为 1em，以 fontSize 逐字符估计兜底。
        /// </summary>
        private static float CellWidth(TMP_Text tmp, string s, float fontSize)
        {
            float est = 0f;
            for (int i = 0; i < s.Length; i++)
                est += s[i] >= 0x2E80 ? fontSize * 1.1f : fontSize * 0.62f; // CJK 按 1.1em 估（部分字体 advance > 1em）
            float measured = 0f;
            try { measured = tmp.GetPreferredValues(s).x; } catch { /* 字体未就绪时用估计值 */ }
            return Mathf.Max(measured, est);
        }

        [HarmonyPostfix]
        private static void Postfix(object __instance)
        {
            if (!Enabled)
                return;

            try
            {
                ProcessStats(__instance);
            }
            catch (System.Exception ex)
            {
                Diagnostics.Log.Debug("[信息卡] postfix 异常（忽略）: " + ex.Message);
            }
        }

        /// <summary>
        /// 表格化（两遍处理）：① 禁自动换行（行数恒定 = 几何恒定的治本修复）；
        /// ② 第一遍实测各列标签/内容宽度，得出<b>每列共享</b>的列几何
        /// （posLeft / posRight / colC），第二遍在「标签：」后注入 &lt;pos=N&gt;
        /// 把数值推到固定列位 —— 同列跨行数值垂直对齐，即电子表格式四列布局：
        /// A 参数 | B 数值 | C 参数 | D 数值。
        /// </summary>
        private static void ProcessStats(object __instance)
        {
            if (_statFields == null)
                return;
            int id = RuntimeHelpers.GetHashCode(__instance);

            // 收集六个值文本：按父行分组、行内按 x 排序 → 0=左格（参数+数值），1=右格
            var byParent = new Dictionary<Transform, List<TMP_Text>>();
            foreach (FieldInfo f in _statFields)
            {
                if (!(f?.GetValue(__instance) is TMP_Text tmp) || tmp.rectTransform == null || tmp.rectTransform.parent == null)
                    continue;
                if (tmp.enableWordWrapping)
                {
                    tmp.enableWordWrapping = false;
                    tmp.overflowMode = TextOverflowModes.Overflow;
                    Diagnostics.Log.Info("[信息卡] 禁换行: " + tmp.name);
                }
                // 左对齐：单元格矩形只有 ~100px 而内容更宽，右/居中对齐会把文本
                // 原点向左挤出矩形，<pos> 列位的参照系随之漂移；左对齐保证
                // 文本原点 = 矩形左缘，与 <pos>（自原点起算）一致。
                tmp.alignment = TextAlignmentOptions.Left;
                Transform parent = tmp.rectTransform.parent;
                if (!byParent.TryGetValue(parent, out List<TMP_Text> list))
                    byParent[parent] = list = new List<TMP_Text>();
                list.Add(tmp);
            }

            var cells = new List<KeyValuePair<TMP_Text, int>>();
            foreach (List<TMP_Text> row in byParent.Values)
            {
                row.Sort((a, b) => a.rectTransform.localPosition.x.CompareTo(b.rectTransform.localPosition.x));
                for (int i = 0; i < row.Count; i++)
                    cells.Add(new KeyValuePair<TMP_Text, int>(row[i], i > 0 ? 1 : 0));
            }
            if (cells.Count == 0)
                return;

            // —— 诊断（每实例一次）：转储六格进入本方法时的真实文本；
            //     对仍无中文的格子用同 scope 探针重查词表 —— 若探针翻得出而实机没翻，
            //     说明文本写入路径没进管线（R/C 未翻问题 2026-09-27 待定论）。
            if (_dumped.Count > 64) _dumped.Clear();
            var localizer = LocalizationPlugin.Localizer;
            if (_dumped.Add(id))
            {
                var sb = new System.Text.StringBuilder(192);
                foreach (KeyValuePair<TMP_Text, int> kv in cells)
                {
                    string cur = kv.Key.text ?? string.Empty;
                    sb.Append(kv.Key.name).Append("=\"").Append(cur.Replace("\n", "\\n")).Append("\"");
                    if (localizer != null && cur.Length > 0 && !Core.TextLocalizer.HasChinese(cur))
                    {
                        string re = localizer.Localize(cur, PatchHelpers.ScopeOf(kv.Key));
                        sb.Append("→探针\"").Append(re.Replace("\n", "\\n")).Append("\"");
                    }
                    sb.Append(' ');
                }
                // 描述的换行/溢出模式与矩形现状——间歇溢出的机制留证
                if (WeaponInfoCardPatches._infoField?.GetValue(__instance) is TMP_Text di && di.rectTransform != null)
                    sb.Append("|| desc=").Append(di.name)
                      .Append(" wrap=").Append(di.enableWordWrapping)
                      .Append(" mode=").Append(di.overflowMode)
                      .AppendFormat(" rect={0:F0}x{1:F0}", di.rectTransform.rect.width, di.rectTransform.rect.height);
                Diagnostics.Log.Info("[信息卡·原文] " + sb.ToString());
            }

            // —— 第零遍：求每格的「显示文本」 ——
            // 关键事实（v9 日志实证）：这六格的翻译发生在 TMP 渲染管线内，
            // tmp.text 里存的始终是英文原文；v9 用英文标签量列位（posR=44 量的是
            // "HE:"），实际渲染的「装药:」更宽 → <pos> 落进中文标签内部、
            // 数值压住标签（用户实测「攻击距离不能正常显示」）。
            // 因此列位必须在 Localize 之后的显示文本上测量与注入。
            var display = new Dictionary<TMP_Text, string>();
            foreach (KeyValuePair<TMP_Text, int> kv in cells)
            {
                string m = kv.Key.text ?? string.Empty;
                if (string.IsNullOrEmpty(m))
                    continue;
                if (m.Contains("<pos="))
                {
                    int p = m.IndexOf("<pos=");
                    int q = m.IndexOf('>', p);
                    if (q > p)
                        m = m.Substring(0, p) + m.Substring(q + 1);
                }
                string d = m;
                if (localizer != null && !Core.TextLocalizer.HasChinese(m))
                {
                    try { d = localizer.Localize(m, PatchHelpers.ScopeOf(kv.Key)) ?? m; }
                    catch { /* 本地化器异常时按原文处理 */ }
                }
                display[kv.Key] = d;
            }

            // 表格几何<b>按实例冻结</b>（首个武器定版，之后不重算）：colC 计入数值宽，
            // 数值随武器变化曾让右列起点每换一把武器抖一次（2026-09-27 用户实测）。
            // 标签文本跨武器恒定（穿深:/RCS:/攻击距离:…），冻结不影响对齐。
            float posLeft, posRight, colC;
            if (TableGeom.TryGetValue(id, out float[] tgF))
            {
                posLeft = tgF[0];
                posRight = tgF[1];
                colC = tgF[2];
            }
            else
            {
                posLeft = 0f;
                posRight = 0f;
                colC = 0f;
                foreach (KeyValuePair<TMP_Text, int> kv in cells)
                {
                    if (!display.TryGetValue(kv.Key, out string s))
                        continue;
                    float fs = kv.Key.fontSize > 0f ? kv.Key.fontSize : 18f;
                    float pad = Mathf.Max(8f, fs * 0.5f); // 内边距收紧（缓解短标签行冒号后空白）
                    int idx = IndexOfColon(s);
                    if (idx < 0)
                    {
                        // 无标签的纯值文本（如制导）：计入左格内容宽度（决定右列起点）
                        if (kv.Value == 0)
                            colC = Mathf.Max(colC, CellWidth(kv.Key, s, fs) + 8f);
                        continue;
                    }
                    float labelW = CellWidth(kv.Key, s.Substring(0, idx + 1), fs) + pad;
                    if (kv.Value == 0)
                    {
                        posLeft = Mathf.Max(posLeft, labelW);
                        colC = Mathf.Max(colC, CellWidth(kv.Key, s, fs) + 8f);
                    }
                    else
                    {
                        posRight = Mathf.Max(posRight, labelW);
                    }
                }

                posLeft = Mathf.Round(posLeft);
                posRight = Mathf.Round(posRight);
                colC = Mathf.Clamp(Mathf.Round(colC), 116f, 224f); // 参数区再收 1/5（2026-09-27 用户裁决）
            }

            // 右格内容宽（含数值）<b>每次实测</b>：极端数值（如 15000000kg，本体疑似数据错误）
            // 会插进描述 —— 超出基线时由 FitDescription 让位；正常数值与基线相近不触发
            // 移动（防抖死区），参数区依旧固定。
            float rightContent = 0f;
            foreach (KeyValuePair<TMP_Text, int> kv in cells)
            {
                if (kv.Value != 1 || !display.TryGetValue(kv.Key, out string s))
                    continue;
                float fs = kv.Key.fontSize > 0f ? kv.Key.fontSize : 18f;
                rightContent = Mathf.Max(rightContent, CellWidth(kv.Key, s, fs));
            }
            TableGeom[id] = new[] { posLeft, posRight, colC, rightContent };

            // 第二遍：把显示文本（含每列共享列位标签）写回 —— 已是中文，
            // 翻译管线对其恒等；下次 DisplayInfo 会被游戏重写为新原文，无残留。
            foreach (KeyValuePair<TMP_Text, int> kv in cells)
            {
                if (!display.TryGetValue(kv.Key, out string s))
                    continue;
                int idx = IndexOfColon(s);
                if (idx < 0)
                    continue;
                float pos = kv.Value == 0 ? posLeft : posRight;
                kv.Key.text = s.Substring(0, idx + 1) + "<pos=" + Mathf.RoundToInt(pos) + ">"
                    + s.Substring(idx + 1).TrimStart();
            }
        }

        private static int IndexOfColon(string s)
        {
            for (int i = 0; i < s.Length; i++)
                if (s[i] == ':' || s[i] == '：')
                    return i;
            return -1;
        }

        internal static RectTransform ToRect(object value)
        {
            if (value is RectTransform rt)
                return rt;
            if (value is GameObject go)
                return go.GetComponent<RectTransform>();
            if (value is Component comp)
                return comp.GetComponent<RectTransform>();
            return null;
        }

        /// <summary>控件脱离父级布局引擎（父级 LayoutGroup 跳过它）。</summary>
        internal static void DetachFromLayout(RectTransform rt)
        {
            LayoutElement le = rt.GetComponent<LayoutElement>();
            if (le == null)
                le = rt.gameObject.AddComponent<LayoutElement>();
            if (!le.ignoreLayout)
                le.ignoreLayout = true;
        }

        /// <summary>归还父级布局引擎（看门狗/重置用）。</summary>
        internal static void UndetachFromLayout(RectTransform rt)
        {
            if (rt != null && rt.GetComponent<LayoutElement>() is LayoutElement le)
                le.ignoreLayout = false;
        }
    }

    /// <summary>
    /// 几何稳定执行器：钩 <c>AircraftSelectionMenu.Update</c>（每帧，rect 为上一帧
    /// 布局的最终结果）。按实例「等待布局稳定 → 快照 → detach + 钉死」，见 v5 类注释。
    /// </summary>
    [HarmonyPatch]
    internal static class WeaponInfoCardStabilizer
    {
        /// <summary>无表格几何时的右列起点回落值（局部 px，相对行左缘）。</summary>
        private const float FallbackColC = 128f;

        private sealed class StatCell
        {
            public RectTransform Rt;
            public Vector2 Orig;     // 原生 anchoredPosition
            public int Col;          // 0=左格（参数+数值），1=右格
            public float LeftEdge;   // 原生布局下矩形左缘的局部 x
            public float RowLeft;    // 所在行左缘的局部 x
        }

        private sealed class InstState
        {
            public readonly List<StatCell> Cells = new List<StatCell>();
            public float ParamShift; // 参数块整体平移量（世界 px，负=向左），供 FitDescription 修正
            public bool NoWeapon;    // 无武器模式：只钉描述（居中全宽）
            public float FittedRightContent = -1f; // 上次描述让位所依据的右格内容宽（变更才重算）
        }

        private static readonly HashSet<int> _pinnedInstances = new HashSet<int>();
        private static readonly Dictionary<int, InstState> _instStates = new Dictionary<int, InstState>();
        private static readonly HashSet<int> _widthDrift = new HashSet<int>(); // 宽度漂移告警（每实例一次）
        private static int _degenerateFrames;
        private static int _screenW, _screenH;

        private static IEnumerable<MethodBase> TargetMethods()
        {
            var type = AccessTools.TypeByName("AircraftSelectionMenu");
            if (type == null)
                yield break;
            WeaponInfoCardPatches.ResolveFields(type);
            foreach (MethodInfo m in AccessTools.GetDeclaredMethods(type))
                if (m.Name == "Update")
                    yield return m;
        }

        [HarmonyPostfix]
        private static void Postfix(object __instance)
        {
            if (!WeaponInfoCardPatches.Enabled)
                return;

            try
            {
                Stabilize(__instance);
            }
            catch (System.Exception ex)
            {
                Diagnostics.Log.Debug("[信息卡] stabilize 异常（忽略）: " + ex.Message);
            }
        }

        private static void Stabilize(object instance)
        {
            int id = RuntimeHelpers.GetHashCode(instance);

            // 分辨率变化 → 全部重来（锚定坐标是父级局部像素值）
            if (Screen.width != _screenW || Screen.height != _screenH)
            {
                if (_screenW != 0)
                    Diagnostics.Log.Info("[信息卡] 分辨率变化，解除钉死重记");
                _screenW = Screen.width;
                _screenH = Screen.height;
                ResetAll(instance);
                return;
            }

            RectTransform imageArea = WeaponInfoCardPatches.ToRect(
                WeaponInfoCardPatches._imageAreaField?.GetValue(instance));
            RectTransform infoArea = WeaponInfoCardPatches.ToRect(
                WeaponInfoCardPatches._infoAreaField?.GetValue(instance));
            RectTransform desc = WeaponInfoCardPatches._infoField?.GetValue(instance) is TMP_Text t
                ? t.rectTransform
                : null;

            // —— 同帧先读快照（此刻三者都还是布局引擎给出的完好几何） ——
            float[] img = Snapshot(imageArea);
            float[] inf = Snapshot(infoArea);
            float[] des = Snapshot(desc);
            bool imgValid = img != null && img[2] >= 1f;
            bool infValid = inf != null && imgValid && inf[0] >= img[0] + img[2] - 5f; // 参数须在图片右侧
            bool desBase = des != null && des[2] >= 1f;
            bool desValid = desBase && imgValid && des[0] >= img[0] + img[2] - 5f;

            if (!_pinnedInstances.Contains(id))
            {
                // —— 无武器分支（本体 SetActive(false) 掉图片/参数区）：
                //     描述独占卡片全宽并居中，右缘仍钳到右侧面板（长描述不再越界）——
                if (!(imgValid && infValid))
                {
                    if (!desBase)
                        return;
                    WeaponInfoCardPatches.Pins["description"] = des;
                    WeaponInfoCardPatches.DetachFromLayout(desc);
                    _pinnedInstances.Add(id);
                    _instStates[id] = new InstState { NoWeapon = true };
                    _degenerateFrames = 0;
                    PinDescriptionCentered(desc);
                    Diagnostics.Log.Info("[信息卡·钉死] 实例 " + id + "（无武器，描述居中全宽）");
                    return;
                }

                // 新实例：布局稳定前完全不碰几何（原生布局，绝不产生「钉错」）
                if (!desValid)
                    return;

                bool adapted = WeaponInfoCardPatches.Pins.Count == 3 && (
                    Differs(WeaponInfoCardPatches.Pins["imageArea"], img) ||
                    Differs(WeaponInfoCardPatches.Pins["infoArea"], new[] { inf[0], inf[1] - 8f, 0f, 0f }) ||
                    Differs(WeaponInfoCardPatches.Pins["description"], des));

                WeaponInfoCardPatches.Pins["imageArea"] = img;
                // 参数区相对图片下移 8px（垂直居中对齐，2026-09-27 用户裁决）
                WeaponInfoCardPatches.Pins["infoArea"] = new[] { inf[0], inf[1] - 8f, 0f, 0f };
                WeaponInfoCardPatches.Pins["description"] = des;

                WeaponInfoCardPatches.DetachFromLayout(imageArea);
                WeaponInfoCardPatches.DetachFromLayout(infoArea);
                WeaponInfoCardPatches.DetachFromLayout(desc);

                _pinnedInstances.Add(id);
                if (_pinnedInstances.Count > 32)
                {
                    _pinnedInstances.RemoveWhere(x => x != id);
                    _instStates.Remove(id);
                    // 清理已消亡实例的表格几何（菜单 Clone 销毁后键残留）
                    var stale = new List<int>();
                    foreach (int k in WeaponInfoCardPatches.TableGeom.Keys)
                        if (k != id && !_pinnedInstances.Contains(k))
                            stale.Add(k);
                    foreach (int k in stale)
                        WeaponInfoCardPatches.TableGeom.Remove(k);
                }
                _degenerateFrames = 0;

                ArrangeStatCells(id, instance);
                AlignParamBlock(id, imageArea, infoArea);
                FitDescription(id, desc);

                float[] dp = WeaponInfoCardPatches.Pins["description"];
                Diagnostics.Log.Info(string.Format(
                    "[信息卡·钉死] 实例 {0}{1}: image {2:F0}x{3:F0}@({4:F0},{5:F0}) info @({6:F0},{7:F0}) desc {8:F0}x{9:F0}@({10:F0},{11:F0})",
                    id, adapted ? "（与既有几何不一致，已自适应）" : "",
                    img[2], img[3], img[0], img[1], inf[0], inf[1], dp[2], dp[3], dp[0], dp[1]));
                return;
            }

            // —— 已钉实例：模式切换检测 + 每帧回放 ——
            bool nowNoWeapon = !imgValid || !infValid;
            _instStates.TryGetValue(id, out InstState cur);
            if (cur != null && cur.NoWeapon != nowNoWeapon)
            {
                ResetAll(instance); // 无武器↔有武器切换：归还布局，按新模式重新钉死
                return;
            }
            if (cur != null && cur.NoWeapon)
            {
                if (desc != null)
                {
                    ForceDescWrap(desc);
                    WeaponInfoCardPatches.DetachFromLayout(desc);
                    ApplyRect(desc, WeaponInfoCardPatches.Pins["description"], setHeight: false);
                }
                return;
            }
            if (imageArea != null)
            {
                if (WeaponInfoCardPatches._weaponImageField?.GetValue(instance) is Image wimg && !wimg.preserveAspect)
                    wimg.preserveAspect = true;
                WeaponInfoCardPatches.DetachFromLayout(imageArea);
                ApplyRect(imageArea, WeaponInfoCardPatches.Pins["imageArea"]);
            }
            if (infoArea != null)
            {
                WeaponInfoCardPatches.DetachFromLayout(infoArea);
                ApplyPos(infoArea, WeaponInfoCardPatches.Pins["infoArea"]); // 只钳位置（0 宽是本体设计）
            }
            if (desc != null)
            {
                ForceDescWrap(desc);
                WeaponInfoCardPatches.DetachFromLayout(desc);
                ApplyRect(desc, WeaponInfoCardPatches.Pins["description"], setHeight: false);
            }
            if (_instStates.TryGetValue(id, out InstState st))
            {
                ApplyCellLayout(id, st);
                // 右格内容宽变化（极端数值如 15000000kg）→ 描述让位重算；正常数值不动
                if (desc != null
                    && WeaponInfoCardPatches.TableGeom.TryGetValue(id, out float[] tgx)
                    && tgx.Length > 3 && tgx[3] != st.FittedRightContent)
                {
                    st.FittedRightContent = tgx[3];
                    FitDescription(id, desc);
                }
                // 宽度漂移巡检：钉值与实际矩形不一致 = 渲染期有其它机制在改宽度（留证）
                if (desc != null && WeaponInfoCardPatches.Pins.TryGetValue("description", out float[] dpw)
                    && Mathf.Abs(desc.rect.width - dpw[2]) > 2f && _widthDrift.Add(id))
                    Diagnostics.Log.Info(string.Format(
                        "[信息卡·表格] 描述宽度漂移：实际 {0:F0} ≠ 钉值 {1:F0}（渲染期被改写）",
                        desc.rect.width, dpw[2]));
            }

            // —— 看门狗：激活却持续退化（图片/描述宽<1）→ 解除钉死重记 ——
            bool degenerate =
                (imageArea != null && imageArea.gameObject.activeInHierarchy && imageArea.rect.width < 1f) ||
                (desc != null && desc.gameObject.activeInHierarchy && desc.rect.width < 1f);
            _degenerateFrames = degenerate ? _degenerateFrames + 1 : 0;
            if (_degenerateFrames > 60)
            {
                Diagnostics.Log.Info("[信息卡] 几何持续退化，解除钉死重记");
                ResetAll(instance);
            }
        }

        /// <summary>
        /// 表格化排布：六个值文本按父行分组，行内按 x 排序；记录每格的原生位置、
        /// 左缘与行左缘。右格实际位置由 <see cref="ApplyCellLayout"/> 按表格几何
        /// （行左缘 + colC）回放，与 &lt;pos&gt; 列位共同构成四列表格。
        /// 原位按实例记录，回放/重置均可逆。
        /// </summary>
        private static void ArrangeStatCells(int id, object instance)
        {
            FieldInfo[] fields = WeaponInfoCardPatches._statFields;
            if (fields == null)
                return;

            var byParent = new Dictionary<Transform, List<RectTransform>>();
            foreach (FieldInfo f in fields)
            {
                if (!(f?.GetValue(instance) is TMP_Text tmp) || tmp.rectTransform == null || tmp.rectTransform.parent == null)
                    continue;
                Transform parent = tmp.rectTransform.parent;
                if (!byParent.TryGetValue(parent, out List<RectTransform> list))
                    byParent[parent] = list = new List<RectTransform>();
                list.Add(tmp.rectTransform);
            }

            var state = new InstState();
            foreach (KeyValuePair<Transform, List<RectTransform>> kv in byParent)
            {
                List<RectTransform> cells = kv.Value;
                cells.Sort((a, b) => a.localPosition.x.CompareTo(b.localPosition.x));
                float rowLeft = float.MaxValue;
                foreach (RectTransform rt in cells)
                    rowLeft = Mathf.Min(rowLeft, rt.localPosition.x - rt.pivot.x * rt.rect.width);
                for (int i = 0; i < cells.Count; i++)
                {
                    RectTransform rt = cells[i];
                    state.Cells.Add(new StatCell
                    {
                        Rt = rt,
                        Orig = rt.anchoredPosition,
                        Col = i > 0 ? 1 : 0,
                        LeftEdge = rt.localPosition.x - rt.pivot.x * rt.rect.width,
                        RowLeft = rowLeft,
                    });
                }
            }
            _instStates[id] = state;
            ApplyCellLayout(id, state);

            string geom = WeaponInfoCardPatches.TableGeom.TryGetValue(id, out float[] tg)
                ? string.Format("posL={0:F0} posR={1:F0} colC={2:F0}", tg[0], tg[1], tg[2])
                : "无表格几何（回落）";
            Diagnostics.Log.Info("[信息卡·表格] 值单元格 " + state.Cells.Count + " 个，" + geom);
        }

        /// <summary>按表格几何回放单元格位置：右格左缘 = 行左缘 + colC，左格归原位。</summary>
        private static void ApplyCellLayout(int id, InstState st)
        {
            float colC = WeaponInfoCardPatches.TableGeom.TryGetValue(id, out float[] tg)
                ? tg[2]
                : FallbackColC;
            foreach (StatCell c in st.Cells)
            {
                Vector2 target = c.Col == 1
                    ? c.Orig + new Vector2((c.RowLeft + colC) - c.LeftEdge, 0f)
                    : c.Orig;
                if (c.Rt.anchoredPosition != target)
                    c.Rt.anchoredPosition = target;
            }
        }

        /// <summary>参数列与图片右缘的目标间距（infoArea 局部 px）。</summary>
        private const float ParamGap = 18f;

        /// <summary>
        /// 参数块整体平移贴向图片（2026-09-27 用户裁决「参数左边空得有点大」）：
        /// infoArea 原生 x 距图片右缘仅 ~10px，但其行/单元格内部还有居中偏移
        /// （v10 实机实测左列标签距图片右缘 ~158px）。按「左列左缘 = 图片右缘 +
        /// <see cref="ParamGap"/>」求世界位移，加到 infoArea 钉死 x 上（所有行/格
        /// 均为 infoArea 子孙，整体平移，下一帧回放生效）。平移量记入
        /// <see cref="InstState.ParamShift"/>，供 <see cref="FitDescription"/> 修正。
        /// </summary>
        private static void AlignParamBlock(int id, RectTransform imageArea, RectTransform infoArea)
        {
            if (imageArea == null || infoArea == null
                || !_instStates.TryGetValue(id, out InstState st)
                || !WeaponInfoCardPatches.Pins.TryGetValue("infoArea", out float[] pin))
                return;

            float minLeft = float.MaxValue;
            foreach (StatCell c in st.Cells)
                if (c.Col == 0)
                    minLeft = Mathf.Min(minLeft, LeftWorldX(c.Rt));
            if (minLeft == float.MaxValue)
                return;

            float imgRight = imageArea.position.x
                + (1f - imageArea.pivot.x) * imageArea.rect.width * imageArea.lossyScale.x;
            float deltaWorld = imgRight + ParamGap * infoArea.lossyScale.x - minLeft;
            deltaWorld = Mathf.Clamp(deltaWorld, -300f, 60f);
            if (Mathf.Abs(deltaWorld) < 2f)
                return;

            st.ParamShift = deltaWorld;
            pin[0] += deltaWorld / infoArea.lossyScale.x;
            Diagnostics.Log.Info(string.Format("[信息卡·表格] 参数块平移 {0:F0}px 贴近图片（目标间距 {1:F0}px）",
                deltaWorld / infoArea.lossyScale.x, ParamGap));
        }

        /// <summary>参数表内容预留宽度（世界尺度，含最长参数串 + 余量）。随参数区收窄同步 -20%。</summary>
        private const float ContentAllowance = 128f;

        /// <summary>
        /// 描述右缘世界上限 = min(原生右缘, 父容器右缘 − 边距, 右侧相邻面板左缘 − 边距)。
        /// v15 实证：Darkener 同级<b>没有任何其它子项</b>（候选列表为空）——飞机统计面板
        /// 在更高层级，故从 Darkener 父级起<b>逐级向上扫最多 3 级</b>（跳过包含本段的子树）。
        /// 候选须在描述左缘右侧 120px 之外、与卡片垂直重叠、且高度 ≥ 卡片一半
        /// （排除上方的武器槽位短条），落选候选全量进日志。
        /// </summary>
        private static float DescriptionRightLimitWorld(
            RectTransform desc, float nativeRightWorld, float scale, float minLeftWorld, out string boundaryLog)
        {
            float rightWorld = nativeRightWorld;
            boundaryLog = null;
            if (!(desc.parent is RectTransform pr))
                return rightWorld;

            Rect prW = WorldRect(pr);
            rightWorld = Mathf.Min(rightWorld, prW.xMax - 18f * scale);

            string bestName = null;
            float bestLeft = float.PositiveInfinity;
            var candidates = new List<string>();

            Transform level = pr.parent;
            for (int depth = 0; depth < 3 && level != null; depth++)
            {
                foreach (Transform sib in level)
                {
                    if (!(sib is RectTransform sr) || !sr.gameObject.activeInHierarchy)
                        continue;
                    if (sr == pr || IsAncestorOf(sr, pr))
                        continue; // 跳过包含武器卡段的子树
                    Rect sw = WorldRect(sr);
                    candidates.Add(string.Format("d{0}:{1}[{2:F0}-{3:F0},y{4:F0}-{5:F0}]",
                        depth, sr.name, sw.xMin, sw.xMax, sw.yMin, sw.yMax));
                    bool rightOf = sw.xMin > minLeftWorld + 120f * scale;
                    bool vOverlap = sw.yMin < prW.yMax - 2f && sw.yMax > prW.yMin + 2f;
                    bool tallEnough = sw.height >= prW.height * 0.5f;
                    if (rightOf && vOverlap && tallEnough && sw.xMin < bestLeft)
                    {
                        bestLeft = sw.xMin;
                        bestName = sr.name;
                    }
                }
                if (bestName != null)
                    break;
                level = level.parent;
            }
            if (bestName != null)
            {
                rightWorld = Mathf.Min(rightWorld, bestLeft - 26f * scale);
                boundaryLog = string.Format("右侧面板 {0} 左缘 {1:F0}", bestName, bestLeft);
            }
            else
            {
                boundaryLog = "无右侧面板（候选: " + string.Join(" | ", candidates) + "）";
            }
            return rightWorld;
        }

        /// <summary>maybeAncestor 是否为 startTransform 的祖先。</summary>
        private static bool IsAncestorOf(Transform maybeAncestor, Transform startTransform)
        {
            while (startTransform != null)
            {
                if (startTransform.parent == maybeAncestor)
                    return true;
                startTransform = startTransform.parent;
            }
            return false;
        }

        /// <summary>
        /// 每帧巡检：描述换行若被运行时关闭（切武器 DisplayInfo 重跑的嫌疑）立即恢复。
        /// v15 实证：钉死时刻换行是开的（无「强制」日志），实机仍间歇溢出 → 状态事后被改。
        /// </summary>
        private static void ForceDescWrap(RectTransform desc)
        {
            if (desc != null && desc.GetComponent<TMP_Text>() is TMP_Text t && !t.enableWordWrapping)
            {
                t.enableWordWrapping = true;
                Diagnostics.Log.Info("[信息卡·表格] 描述换行被运行时关闭，已恢复（每帧巡检）");
            }
        }

        /// <summary>
        /// 描述区适配：① 强制自动换行（描述若 Overflow/禁换行，长行会在矩形右缘
        /// 被裁字）；② 左缘让位参数区（max(原生左缘, 参数右缘+间距)）；
        /// ③ 右缘钳到 <see cref="DescriptionRightLimitWorld"/>。世界坐标运算。
        /// </summary>
        private static void FitDescription(int id, RectTransform desc)
        {
            if (desc == null
                || !_instStates.TryGetValue(id, out InstState st)
                || !WeaponInfoCardPatches.Pins.TryGetValue("description", out float[] dpin))
                return;

            float scale = desc.lossyScale.x;

            // ① 禁自身 ContentSizeFitter —— v14 实测证据：钉死位置生效（文字起点对）
            //    但钉死宽度失效（文本折行点在 ~2 倍钉宽处）：布局引擎在渲染期按文本
            //    把矩形重新撑大，我们的宽度每帧被覆盖。fitter 不禁，钉宽全是空话。
            // ② 强制自动换行 —— 长行必须在矩形右缘折行，否则溢出被裁。
            ContentSizeFitter fitter = desc.GetComponent<ContentSizeFitter>();
            if (fitter != null && (fitter.horizontalFit != ContentSizeFitter.FitMode.Unconstrained
                || fitter.verticalFit != ContentSizeFitter.FitMode.Unconstrained))
            {
                fitter.horizontalFit = ContentSizeFitter.FitMode.Unconstrained;
                fitter.verticalFit = ContentSizeFitter.FitMode.Unconstrained;
                Diagnostics.Log.Info("[信息卡·表格] 描述自身 ContentSizeFitter 已禁用（矩形曾被文本反向撑大，钉宽失效）");
            }

            TMP_Text dtmp = desc.GetComponent<TMP_Text>();
            if (dtmp != null && (!dtmp.enableWordWrapping || dtmp.overflowMode != TextOverflowModes.Overflow))
            {
                dtmp.enableWordWrapping = true;
                dtmp.overflowMode = TextOverflowModes.Overflow;
                Diagnostics.Log.Info("[信息卡·表格] 描述强制自动换行（修右缘溢出/裁字）");
            }

            // 参数区右缘（世界）：格左缘最大值 + 参数块整体平移 + 内容预留。
            // 基线 = tg[1]+40（固定，正常武器不抖动）；右格内容宽 tg[3] 超出基线时
            // 让位（极端数值如 15000000kg），上限 320 防超长值吃满描述区。
            float allowance = WeaponInfoCardPatches.TableGeom.TryGetValue(id, out float[] tg)
                ? Mathf.Max(tg[1] + 40f, Mathf.Min((tg.Length > 3 ? tg[3] : 0f) + 8f, 320f))
                : ContentAllowance;
            float paramsRight = float.MinValue;
            foreach (StatCell c in st.Cells)
                paramsRight = Mathf.Max(paramsRight, LeftWorldX(c.Rt));
            paramsRight += st.ParamShift;
            paramsRight += allowance * scale;

            // 左缘 = max(原生, 参数右缘 + 间距)
            float nativeLeftWorld = LeftWorldX(desc);
            float leftWorld = Mathf.Max(nativeLeftWorld, paramsRight + 10f * scale);
            float deltaLocal = (leftWorld - nativeLeftWorld) / scale;

            float nativeRightWorld = nativeLeftWorld + dpin[2] * scale;
            float rightWorld = DescriptionRightLimitWorld(desc, nativeRightWorld, scale, leftWorld, out string boundaryLog);
            float newWidth = Mathf.Max(160f, (rightWorld - leftWorld) / scale);
            WeaponInfoCardPatches.Pins["description"] = new[] { dpin[0] + deltaLocal, dpin[1], newWidth, dpin[3] };

            Diagnostics.Log.Info(string.Format(
                "[信息卡·表格] 描述适配：{0}；参数右缘 {1:F0}，左缘 {2:F0}→{3:F0}，宽 {4:F0}→{5:F0}",
                boundaryLog ?? "无右侧面板", paramsRight, nativeLeftWorld, leftWorld, dpin[2], newWidth));
        }

        /// <summary>
        /// 无武器分支：描述独占卡片全宽（左缘 = 卡片左缘 + 边距），右缘仍钳到
        /// 右侧相邻面板；描述文本本身居中对齐 ⇒ 视觉居中，长描述有最大宽度可用。
        /// </summary>
        private static void PinDescriptionCentered(RectTransform desc)
        {
            if (desc == null
                || !WeaponInfoCardPatches.Pins.TryGetValue("description", out float[] dpin))
                return;

            float scale = desc.lossyScale.x;
            ContentSizeFitter fitter0 = desc.GetComponent<ContentSizeFitter>();
            if (fitter0 != null && fitter0.horizontalFit != ContentSizeFitter.FitMode.Unconstrained)
            {
                fitter0.horizontalFit = ContentSizeFitter.FitMode.Unconstrained;
                Diagnostics.Log.Info("[信息卡·表格] 无武器：描述 ContentSizeFitter 已禁用");
            }
            float nativeLeftWorld = LeftWorldX(desc);
            float leftWorld = desc.parent is RectTransform pr
                ? WorldRect(pr).xMin + 12f * scale
                : nativeLeftWorld;
            float nativeRightWorld = nativeLeftWorld + dpin[2] * scale;
            float rightWorld = DescriptionRightLimitWorld(desc, nativeRightWorld, scale, leftWorld, out string boundaryLog);
            float newWidth = Mathf.Max(160f, (rightWorld - leftWorld) / scale);
            float deltaLocal = (leftWorld - nativeLeftWorld) / scale;
            WeaponInfoCardPatches.Pins["description"] = new[] { dpin[0] + deltaLocal, dpin[1], newWidth, dpin[3] };

            Diagnostics.Log.Info(string.Format(
                "[信息卡·表格] 无武器：描述居中全宽，左缘 {0:F0}→{1:F0}，宽 {2:F0}→{3:F0} {4}",
                nativeLeftWorld, leftWorld, dpin[2], newWidth, boundaryLog ?? ""));
        }

        /// <summary>矩形左缘的世界 x（position 是 pivot 世界坐标，需回退 pivot 占比）。</summary>
        private static float LeftWorldX(RectTransform rt)
            => rt.position.x - rt.pivot.x * rt.rect.width * rt.lossyScale.x;

        /// <summary>矩形的世界空间包围盒（轴对齐，含缩放）。</summary>
        private static Rect WorldRect(RectTransform rt)
        {
            Vector2 size = new Vector2(rt.rect.width * rt.lossyScale.x, rt.rect.height * rt.lossyScale.y);
            return new Rect((Vector2)rt.position - rt.pivot * size, size);
        }

        private static void ResetAll(object instance)
        {
            int id = RuntimeHelpers.GetHashCode(instance);
            if (_instStates.TryGetValue(id, out InstState st))
            {
                foreach (StatCell c in st.Cells)
                    c.Rt.anchoredPosition = c.Orig; // 归还原位
                _instStates.Remove(id);
            }
            // 表格几何保留（按实例冻结，跨 无武器↔有武器 模式抖动重钉不丢；实例销毁由
            // 钉死路径的 stale 清理回收）—— 清掉会让右列起点随武器重算而抖动
            WeaponInfoCardPatches.Pins.Clear();
            _pinnedInstances.Clear();
            _degenerateFrames = 0;
            // 本实例控件归还布局引擎（其它实例随销毁消亡）
            WeaponInfoCardPatches.UndetachFromLayout(WeaponInfoCardPatches.ToRect(
                WeaponInfoCardPatches._imageAreaField?.GetValue(instance)));
            WeaponInfoCardPatches.UndetachFromLayout(WeaponInfoCardPatches.ToRect(
                WeaponInfoCardPatches._infoAreaField?.GetValue(instance)));
            if (WeaponInfoCardPatches._infoField?.GetValue(instance) is TMP_Text t)
                WeaponInfoCardPatches.UndetachFromLayout(t.rectTransform);
        }

        private static bool Differs(float[] a, float[] b)
        {
            for (int i = 0; i < 4; i++)
                if (Mathf.Abs(a[i] - b[i]) > 1f)
                    return true;
            return false;
        }

        private static float[] Snapshot(RectTransform rt)
        {
            if (rt == null || !rt.gameObject.activeInHierarchy)
                return null;
            return new[]
            {
                rt.anchoredPosition.x, rt.anchoredPosition.y, rt.rect.width, rt.rect.height,
            };
        }

        private static void ApplyRect(RectTransform rt, float[] pin, bool setHeight = true)
        {
            const float eps = 0.5f;
            if (Mathf.Abs(rt.rect.width - pin[2]) > eps)
                rt.SetSizeWithCurrentAnchors(RectTransform.Axis.Horizontal, pin[2]);
            if (setHeight && Mathf.Abs(rt.rect.height - pin[3]) > eps)
                rt.SetSizeWithCurrentAnchors(RectTransform.Axis.Vertical, pin[3]);
            ApplyPos(rt, pin);
        }

        private static void ApplyPos(RectTransform rt, float[] pin)
        {
            const float eps = 0.5f;
            float dx = rt.anchoredPosition.x - pin[0];
            float dy = rt.anchoredPosition.y - pin[1];
            if (dx * dx + dy * dy > eps * eps)
                rt.anchoredPosition = new Vector2(pin[0], pin[1]);
        }
    }
}
