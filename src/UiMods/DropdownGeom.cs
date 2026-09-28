using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using NuclearOptionChineseLocalizationPatch.Patching;

namespace NuclearOptionChineseLocalizationPatch.UiMods
{
    /// <summary>
    /// 挂架武器下拉框的度量与几何 helper（<b>非补丁类</b>，供两个下拉补丁共用）。
    ///
    /// <para>独立成类的理由同 <see cref="RectGeom"/>：helper 若留在 <c>[HarmonyPatch]</c>
    /// 类内，会被 Harmony 分析器当候选补丁方法检查，参数赋值会误报 <c>Harmony003</c>。</para>
    /// </summary>
    internal static class DropdownGeom
    {
        /// <summary>差值小于 1px 不动，避免浮点噪声反复加宽。</summary>
        internal const float MinDelta = 1f;

        /// <summary>保险上限，防病态长串把列表撑满全屏。</summary>
        internal const float MaxTemplateWidth = 800f;

        /// <summary>文字与边缘之间的呼吸空间。</summary>
        internal const float ExtraPadding = 8f;

        /// <summary>
        /// 量出所有选项译文里最宽的那条首选宽度。
        /// 标签开了自适应（实测 16~20）时按 <c>fontSizeMax</c> 探测 —— 加宽后 TMP 会
        /// 把字号涨到上限，按当前字号量会偏小。
        /// </summary>
        internal static float MeasureMaxOptionWidth(TMP_Dropdown dd)
        {
            TMP_Text label = dd.itemText;
            if (label == null || dd.options == null || dd.options.Count == 0) return 0f;

            // 这一版 TMP 里 GetPreferredWidth 两个重载都是 protected，公共入口是
            // GetPreferredValues(string)（返回宽高向量，纯布局演算，不写 m_text）。
            // 选项原始串是英文；实际显示的是译文，量之前先过一遍自家翻译管线。
            float saved = label.fontSize;
            float probe = label.enableAutoSizing ? label.fontSizeMax : label.fontSize;
            float max = 0f;
            try
            {
                label.fontSize = probe;
                for (int i = 0; i < dd.options.Count; i++)
                {
                    var opt = dd.options[i];
                    if (opt == null || string.IsNullOrEmpty(opt.text)) continue;

                    string shown = PatchHelpers.TryLocalize(opt.text, label, out string translated)
                        ? translated : opt.text;

                    float w = label.GetPreferredValues(shown).x;
                    if (w > max) max = w;
                }
            }
            finally
            {
                label.fontSize = saved;
            }
            return max;
        }

        /// <summary>模板上的竖向滚动条规范化为 BottomToTop（见 <see cref="PylonDropdownPatches"/> 类注释）。</summary>
        internal static void FixScrollbarDirection(RectTransform template)
        {
            Scrollbar bar = template.GetComponentInChildren<Scrollbar>(true);
            if (bar == null) return;
            if (bar.direction == Scrollbar.Direction.BottomToTop) return;
            if (bar.direction != Scrollbar.Direction.TopToBottom) return; // 只修竖向的反向
            bar.direction = Scrollbar.Direction.BottomToTop; // 属性 setter 会重映射当前取值
        }

        /// <summary>诊断用：从 node 到 root 的层级路径。</summary>
        internal static string PathOf(Transform startNode, Transform root)
        {
            var sb = new System.Text.StringBuilder();
            Transform cur = startNode;
            while (cur != null && cur != root)
            {
                if (sb.Length > 0) sb.Insert(0, '/');
                sb.Insert(0, cur.name);
                cur = cur.parent;
            }
            return sb.ToString();
        }

        /// <summary>诊断转储：链上每个节点的 锚跨度/宽/sizeDelta.x/宽度控制组件。</summary>
        internal static string DumpChain(List<RectTransform> chain)
        {
            var sb = new System.Text.StringBuilder();
            for (int i = chain.Count - 1; i >= 0; i--) // 顶层 → 标签
            {
                RectTransform rt = chain[i];
                if (rt == null) continue;
                if (sb.Length > 0) sb.Append(" <- ");
                string ctrl = "";
                if (rt.GetComponent<HorizontalOrVerticalLayoutGroup>() != null) ctrl += "+LG";
                if (rt.GetComponent<ContentSizeFitter>() != null) ctrl += "+Fitter";
                if (rt.GetComponent<RectMask2D>() != null) ctrl += "+Mask";
                sb.AppendFormat("{0}[锚{1:F1}-{2:F1} w{3:F0} sd{4:F0}{5}]",
                    rt.name, rt.anchorMin.x, rt.anchorMax.x, rt.rect.width,
                    rt.sizeDelta.x, ctrl);
            }
            return sb.ToString();
        }

        /// <summary>
        /// 禁掉链上节点自身/子级的横向宽度控制（LayoutGroup 的 childControlWidth、
        /// ContentSizeFitter 的横向 fit），否则布局系统会在下一帧把强制宽度改回去。
        /// </summary>
        internal static void DisableWidthControl(RectTransform rt)
        {
            var hv = rt.GetComponent<HorizontalOrVerticalLayoutGroup>();
            if (hv != null && hv.childControlWidth) hv.childControlWidth = false;

            var fitter = rt.GetComponent<ContentSizeFitter>();
            if (fitter != null
                && fitter.horizontalFit != ContentSizeFitter.FitMode.Unconstrained)
                fitter.horizontalFit = ContentSizeFitter.FitMode.Unconstrained;
        }

        /// <summary>
        /// 列表加宽后越出画布时平移回屏内（展开方向由 TMP 的翻转逻辑决定）。
        /// 修正记录：边界必须用<strong>真正的画布根</strong>（向上走到最顶层 RectTransform），
        /// 不能用 popup 的直接父节点（= 下拉控件自身，仅 ~137px 宽），否则钳制时会把
        /// 整个列表推进屏幕左缘外。
        /// </summary>
        internal static void ClampIntoCanvas(RectTransform popupRt)
        {
            Transform root = popupRt;
            while (root.parent != null && root.parent is RectTransform)
                root = root.parent;
            RectTransform boundsRt = root as RectTransform;
            if (boundsRt == null || boundsRt == popupRt) return;

            Vector3[] c = new Vector3[4];
            Vector3[] cc = new Vector3[4];
            popupRt.GetWorldCorners(c);
            boundsRt.GetWorldCorners(cc);

            float overflowR = c[2].x - cc[2].x;
            float overflowL = cc[0].x - c[0].x;
            if (overflowR > 0f)
                popupRt.position += new Vector3(-overflowR, 0f, 0f);
            else if (overflowL > 0f)
                popupRt.position += new Vector3(overflowL, 0f, 0f);
        }
    }
}
