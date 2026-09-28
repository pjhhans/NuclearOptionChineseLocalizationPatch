using System.Collections.Generic;
using System.Runtime.CompilerServices;
using TMPro;
using UnityEngine;
using NuclearOptionChineseLocalizationPatch.Patching;

namespace NuclearOptionChineseLocalizationPatch.UiMods
{
    /// <summary>
    /// 信息卡参数区的<b>表格化</b>：把六格参数文本排成四列 ——
    /// A 参数 | B 数值 | C 参数 | D 数值 —— 同列跨行数值垂直对齐。
    ///
    /// <para><b>两遍处理：</b>① 禁自动换行（行数恒定 = 几何恒定的治本修复）；
    /// ② 第一遍实测各列标签/内容宽度，得出<b>每列共享</b>的列几何
    /// （posLeft / posRight / colC），第二遍在「标签：」后注入 <c>&lt;pos=N&gt;</c>
    /// 把数值推到固定列位。</para>
    ///
    /// <para><b>量列位必须在翻译之后的显示文本上做。</b>这六格的翻译发生在 TMP 渲染管线内，
    /// <c>tmp.text</c> 里存的始终是英文原文；用英文标签量列位会把 <c>&lt;pos&gt;</c>
    /// 落进中文标签内部，数值压住标签（实测「攻击距离不能正常显示」）。</para>
    /// </summary>
    internal static class WeaponInfoCardTable
    {
        /// <summary>
        /// 表格几何（<b>按实例</b>）：[0]=左格数值列位 posLeft，[1]=右格数值列位 posRight，
        /// [2]=右格起点 colC（相对行左缘），[3]=右格内容宽。
        /// 同一列的所有行共享同一列位 → 数值跨行垂直对齐（表格效果）。
        /// 不同信息卡（基地菜单 / 挂架面板）字号不同，各自实测，互不污染。
        /// </summary>
        internal static readonly Dictionary<int, float[]> Geom = new Dictionary<int, float[]>();

        /// <summary>已做过原文转储的实例（诊断，防刷屏）。</summary>
        private static readonly HashSet<int> _dumped = new HashSet<int>();

        /// <summary>
        /// 表格化主流程：收集六格 → 求每格显示文本（翻译后）→ 实测列几何 → 注入 <c>&lt;pos&gt;</c>。
        /// </summary>
        internal static void ProcessStats(object __instance)
        {
            if (WeaponInfoCardPatches._statFields == null)
                return;
            int id = RuntimeHelpers.GetHashCode(__instance);

            // 收集六个值文本：按父行分组、行内按 x 排序 → 0=左格（参数+数值），1=右格
            var byParent = new Dictionary<Transform, List<TMP_Text>>();
            foreach (var f in WeaponInfoCardPatches._statFields)
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
            // 关键事实（日志实证）：这六格的翻译发生在 TMP 渲染管线内，
            // tmp.text 里存的始终是英文原文；用英文标签量列位 → <pos> 落进中文标签内部、
            // 数值压住标签。因此列位必须在 Localize 之后的显示文本上测量与注入。
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

            // posLeft/posRight <b>每次实测</b>：个别槽位标签的出现与否随面板/武器形态变化
            // （R: 仅部分形态有）—— 冻结列位会把短标签的列位套到长标签上，<pos>
            // 落进标签内部（实测「攻击距离:100km」数值贴标签）。
            // colC（右列起点，数值宽驱动）依旧按实例<b>单调冻结</b>（取 max，只增不减），
            // 数值宽变化不再抖动；标签本身宽度恒定，跨武器对齐不受影响。
            float posLeft = 0f, posRight = 0f, colC = 0f;
            var ownLabel = new Dictionary<TMP_Text, float>();
            foreach (KeyValuePair<TMP_Text, int> kv in cells)
            {
                if (!display.TryGetValue(kv.Key, out string s))
                    continue;
                float fs = kv.Key.fontSize > 0f ? kv.Key.fontSize : 18f;
                float pad = Mathf.Max(8f, fs * 0.5f); // 内边距收紧（缓解短标签行冒号后空白）
                int idx = RectGeom.IndexOfColon(s);
                if (idx < 0)
                {
                    // 无标签的纯值文本（如制导）：计入左格内容宽度（决定右列起点）
                    if (kv.Value == 0)
                        colC = Mathf.Max(colC, RectGeom.CellWidth(kv.Key, s, fs) + 8f);
                    continue;
                }
                float ownW = RectGeom.CellWidth(kv.Key, s.Substring(0, idx + 1), fs);
                ownLabel[kv.Key] = ownW;
                float labelW = ownW + pad;
                if (kv.Value == 0)
                {
                    posLeft = Mathf.Max(posLeft, labelW);
                    colC = Mathf.Max(colC, RectGeom.CellWidth(kv.Key, s, fs) + 8f);
                }
                else
                {
                    posRight = Mathf.Max(posRight, labelW);
                }
            }

            posLeft = Mathf.Round(posLeft);
            posRight = Mathf.Round(posRight);
            float colCMeas = Mathf.Clamp(Mathf.Round(colC), 116f, 224f); // 参数区收 1/5（用户裁决）
            if (Geom.TryGetValue(id, out float[] tgF) && tgF.Length > 2)
                colC = Mathf.Max(tgF[2], colCMeas);
            else
                colC = colCMeas;

            // 右格内容宽（含数值）<b>每次实测</b>：极端数值（如 15000000kg，本体疑似数据错误）
            // 会插进描述 —— 超出基线时由 FitDescription 让位；正常数值与基线相近不触发
            // 移动（防抖死区），参数区依旧固定。
            float rightContent = 0f;
            foreach (KeyValuePair<TMP_Text, int> kv in cells)
            {
                if (kv.Value != 1 || !display.TryGetValue(kv.Key, out string s))
                    continue;
                float fs = kv.Key.fontSize > 0f ? kv.Key.fontSize : 18f;
                rightContent = Mathf.Max(rightContent, RectGeom.CellWidth(kv.Key, s, fs));
            }
            Geom[id] = new[] { posLeft, posRight, colC, rightContent };

            // 第二遍：把显示文本（含每列共享列位标签）写回 —— 已是中文，
            // 翻译管线对其恒等；下次 DisplayInfo 会被游戏重写为新原文，无残留。
            foreach (KeyValuePair<TMP_Text, int> kv in cells)
            {
                if (!display.TryGetValue(kv.Key, out string s))
                    continue;
                int idx = RectGeom.IndexOfColon(s);
                if (idx < 0)
                    continue;
                // 列共享位与「自身标签 + 6px」取大者：列位永不落进本格标签内部
                float colPos = kv.Value == 0 ? posLeft : posRight;
                float own = ownLabel.TryGetValue(kv.Key, out float ow) ? ow : 0f;
                float pos = Mathf.Max(colPos, own + 6f);
                kv.Key.text = s.Substring(0, idx + 1) + "<pos=" + Mathf.RoundToInt(pos) + ">"
                    + s.Substring(idx + 1).TrimStart();
            }
        }
    }
}
