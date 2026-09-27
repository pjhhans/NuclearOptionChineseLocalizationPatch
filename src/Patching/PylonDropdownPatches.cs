using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace NuclearOptionChineseLocalizationPatch.Patching
{
    /// <summary>
    /// 挂架武器下拉框（<c>WeaponSelector</c>）的 UI 修整。
    ///
    /// <para><b>背景（IL 实证，见 tools/_il_ws*_20260927.py）：</b>
    /// <c>WeaponSelector.dropdown</c> 是标准的 <see cref="TMP_Dropdown"/>，
    /// <c>PopulateOptions</c> 只负责 <c>options.Add(new OptionData(名))</c>。
    /// 中文武器名（如「AGM-84H 斯拉姆-ER」）在弹出列表里被 TMP 溢出裁剪截断。</para>
    ///
    /// <para><b>关键实测（v4/v5 诊断日志）：</b>模板根 296px / 模板标签 262px，
    /// 但弹出列表实际只有 137px、条目标签实际 103px —— <b>模板几何并不传导到
    /// 弹出列表</b>（游戏预制体的 Viewport/Content 固定宽，且 Show 只改 sizeDelta
    /// 的 y 分量）。所以<strong>在模板上加宽是无效的</strong>，必须
    /// <see cref="PylonDropdownShowDiagnostics"/> 那样在 Show 之后对实际弹出的
    /// 列表动手。本类只负责：登记实例、修模板滚动条方向（会被克隆进弹出列表）、
    /// 输出量测侧诊断。</para>
    ///
    /// <para><b>滚动条反向（游戏预制体 bug）：</b>这一版 TMP 的 Show 完全不触碰滚动条
    /// （没有旧版的 m_ListScrollbar 逻辑），条目由 Show 统一按「0 号在最上」重排，
    /// 所以正确的滚动条方向恒为 <see cref="Scrollbar.Direction.BottomToTop"/>
    /// （1 = 在顶端 = 看着列表顶）。游戏模板若是 TopToBottom，拖动与位置就整组反向。
    /// 这里直接把模板上的竖向滚动条规范化为 BottomToTop——本来就是的话零影响。
    /// 实机已验证有效。</para>
    /// </summary>
    [HarmonyPatch]
    internal static class PylonDropdownPatches
    {
        /// <summary>配置开关（启动时由 ModSettings 接线）。</summary>
        internal static bool Enabled = true;

        internal const float MinDelta = 1f;   // 差值小于 1px 不动，避免浮点噪声反复加宽
        internal const float MaxTemplateWidth = 800f; // 保险上限，防病态长串把列表撑满全屏
        internal const float ExtraPadding = 8f;       // 文字与边缘之间的呼吸空间

        private static FieldInfo _dropdownField;
        internal static readonly HashSet<TMP_Dropdown> _tracked = new HashSet<TMP_Dropdown>();

        /// <summary>
        /// 目标 = WeaponSelector.PopulateOptions 的全部重载。
        /// 用 TargetMethods 而不是按名打点，因为有两个重载且未来可能增减；
        /// 找不到类型/方法时返回空集，补丁退化为无操作（与逐类安装的容错哲学一致）。
        /// </summary>
        private static IEnumerable<MethodBase> TargetMethods()
        {
            var type = AccessTools.TypeByName("WeaponSelector");
            if (type == null) yield break;

            if (_dropdownField == null)
                _dropdownField = AccessTools.Field(type, "dropdown");

            foreach (MethodInfo m in AccessTools.GetDeclaredMethods(type))
                if (m.Name == "PopulateOptions") yield return m;
        }

        [HarmonyPostfix]
        internal static void Postfix(object __instance)
        {
            if (!Enabled || _dropdownField == null) return;

            try
            {
                var dd = _dropdownField.GetValue(__instance) as TMP_Dropdown;
                if (dd == null || dd.template == null || dd.itemText == null) return;

                FixScrollbarDirection(dd.template);

                _tracked.Add(dd);
                Diagnostics.Log.Info(string.Format(
                    "[挂架下拉·量测] 模板 {0:F0}px，标签 {1:F0}px，最长译文 {2:F0}px（按自适应上限字号），层级 {3}",
                    dd.template.rect.width, dd.itemText.rectTransform.rect.width,
                    MeasureMaxOptionWidth(dd),
                    PathOf(dd.itemText.transform, dd.template)));
            }
            catch (System.Exception ex)
            {
                // UI 修整失败不该连累翻译本身；Warn 默认可见，便于实机定位。
                Diagnostics.Log.Warn("挂架下拉框修整失败：" + ex.GetType().Name + ": " + ex.Message);
            }
        }

        /// <summary>
        /// 量出所有选项译文里最宽的那条首选宽。
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

        /// <summary>模板上的竖向滚动条规范化为 BottomToTop（见类注释）。</summary>
        private static void FixScrollbarDirection(RectTransform template)
        {
            Scrollbar bar = template.GetComponentInChildren<Scrollbar>(true);
            if (bar == null) return;
            if (bar.direction == Scrollbar.Direction.BottomToTop) return;
            if (bar.direction != Scrollbar.Direction.TopToBottom) return; // 只修竖向的反向
            bar.direction = Scrollbar.Direction.BottomToTop; // 属性 setter 会重映射当前取值
        }

        /// <summary>诊断用：从 node 到 root 的层级路径。</summary>
        private static string PathOf(Transform startNode, Transform root)
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
    }

    /// <summary>
    /// 挂架下拉弹出列表的加宽（v5，治本版）：在 <see cref="TMP_Dropdown.Show"/> 之后
    /// 对<strong>实际弹出的列表</strong>动手。
    ///
    /// <para><b>为什么必须是 Show 之后 + 独立补丁类：</b></para>
    /// <list type="number">
    /// <item>实测模板几何不传导到弹出列表（模板根 296px，弹出根 137px），
    ///       只有弹出列表自己的 rect 才是渲染真相；</item>
    /// <item>Harmony 对含 <c>TargetMethods</c> 的补丁类，会把类内所有补丁方法套到
    ///       每个目标上 —— v4 曾因 <c>__instance</c> 类型不兼容导致整类安装失败
    ///       （滚动条修复一起失效），所以 TargetMethods 与按方法标注的补丁必须分家。</item>
    /// </list>
    ///
    /// <para><b>做法：</b>沿「弹出条目标签 → 列表顶层（父为 dropdown 自身/Canvas 为止）」
    /// 的祖先链，把所有非拉伸锚 rect 一并加宽（拉伸锚自动随父变宽）；
    /// 选中高亮背景（Item Background）不在祖先链上，按名补一遍；最后把列表钳回
    /// 画布范围内（TMP 的翻转逻辑已处理展开方向，这里只兜底越界）。
    /// 弹出列表每次 Show 都从模板重建，天然幂等。</para>
    /// </summary>
    [HarmonyPatch(typeof(TMP_Dropdown), "Show")]
    internal static class PylonDropdownShowDiagnostics
    {
        private static FieldInfo _popupField;

        [HarmonyPostfix]
        internal static void Postfix(TMP_Dropdown __instance)
        {
            if (!PylonDropdownPatches.Enabled || !PylonDropdownPatches._tracked.Contains(__instance)) return;

            try
            {
                if (_popupField == null)
                    _popupField = AccessTools.Field(typeof(TMP_Dropdown), "m_Dropdown");
                object popupObj = _popupField?.GetValue(__instance);
                GameObject popup = popupObj as GameObject ?? (popupObj as Component)?.gameObject;
                if (popup == null) return;
                RectTransform popupRt = popup.transform as RectTransform;
                if (popupRt == null) return;

                // ---- 渲染侧诊断（保留：下轮若仍异常，两行日志对比可定位） ----
                TMP_Text firstLabel = null;
                string labelName = __instance.itemText != null ? __instance.itemText.name : "Item Label";
                TMP_Text[] texts = popup.GetComponentsInChildren<TMP_Text>(false);
                foreach (TMP_Text t in texts)
                {
                    if (t != null && t.name == labelName) { firstLabel = t; break; }
                }
                if (firstLabel == null) return;

                var sb = new System.Text.StringBuilder();
                sb.AppendFormat("[挂架下拉·Show] 列表 {0:F0}px，标签 {1:F0}px，条目文本 {2} 个",
                    popupRt.rect.width, firstLabel.rectTransform.rect.width, texts.Length);
                int shown = 0;
                for (int i = 0; i < texts.Length && shown < 3; i++)
                {
                    TMP_Text t = texts[i];
                    if (string.IsNullOrEmpty(t.text)) continue;
                    sb.AppendFormat(" | #{0} \"{1}\" 字号{2:F1} 自适应{3} 字体{4}",
                        shown, t.text, t.fontSize, t.enableAutoSizing,
                        t.font != null ? t.font.name : "null");
                    shown++;
                }
                Diagnostics.Log.Info(sb.ToString());

                // ---- 加宽（v8）----
                float required = PylonDropdownPatches.MeasureMaxOptionWidth(__instance)
                    + PylonDropdownPatches.ExtraPadding;
                float labelW = firstLabel.rectTransform.rect.width;
                float delta = required - labelW;
                if (delta >= PylonDropdownPatches.MinDelta)
                {
                    if (popupRt.rect.width + delta > PylonDropdownPatches.MaxTemplateWidth)
                        delta = PylonDropdownPatches.MaxTemplateWidth - popupRt.rect.width;
                }
                if (delta >= PylonDropdownPatches.MinDelta)
                {
                    // 链：标签 → … → popup 根（m_Dropdown），含两端。
                    // v7 转储发现 popup 根挂着 Canvas 组件导致向上走提前停、根没加宽
                    // 而子节点爆宽 —— 这里直接以 popupRt 为终点。
                    var chain = new List<RectTransform>();
                    for (Transform n = firstLabel.rectTransform; n != null; n = n.parent)
                    {
                        chain.Add(n as RectTransform);
                        if (n == popupRt) break;
                    }
                    if (chain[chain.Count - 1] != popupRt) chain.Add(popupRt); // 兜底

                    RectTransform item = firstLabel.rectTransform.parent as RectTransform;
                    RectTransform bg = item != null
                        ? item.Find("Item Background") as RectTransform : null;

                    // ① 目标宽全部按【修改前】rect.width 预计算，② 再自顶向下应用。
                    // v7 教训：应用时现读现算，拉伸锚节点被「父加宽 + 自身强制」
                    // 双重叠加（标签一路滚到 658px，把列表顶出屏幕左缘）。
                    var targets = new float[chain.Count];
                    for (int i = 0; i < chain.Count; i++)
                        targets[i] = (chain[i] != null ? chain[i].rect.width : 0f) + delta;
                    float bgTarget = (bg != null ? bg.rect.width : 0f) + delta;

                    for (int i = chain.Count - 1; i >= 0; i--)
                    {
                        RectTransform rt = chain[i];
                        if (rt == null) continue;
                        DisableWidthControl(rt);
                        rt.SetSizeWithCurrentAnchors(RectTransform.Axis.Horizontal, targets[i]);
                    }
                    if (bg != null)
                    {
                        DisableWidthControl(bg);
                        bg.SetSizeWithCurrentAnchors(RectTransform.Axis.Horizontal, bgTarget);
                    }

                    ClampIntoCanvas(popupRt);

                    // 读回验证：若仍等于旧值，说明宽度另有来源，转储整条链定位
                    Diagnostics.Log.Info(string.Format(
                        "[挂架下拉] 已加宽 +{0:F0}px，读回：根 {1:F0}px / 标签 {2:F0}px（目标 {3:F0}px）",
                        delta, popupRt.rect.width,
                        firstLabel.rectTransform.rect.width, required));
                    Diagnostics.Log.Info("[挂架下拉·链] " + DumpChain(chain));
                }
            }
            catch (System.Exception ex)
            {
                Diagnostics.Log.Warn("挂架下拉 Show 修整失败：" + ex.GetType().Name + ": " + ex.Message);
            }
        }

        /// <summary>横向拉伸锚的节点随父变宽，跳过；固定宽的节点补上增量。</summary>
        private static void StretchChild(RectTransform rt, float delta)
        {
            if (rt == null) return;
            if (rt.anchorMax.x - rt.anchorMin.x > 0.5f) return; // 拉伸锚，继承父宽
            rt.sizeDelta += new Vector2(delta, 0f);
        }

        /// <summary>组件名探测 Canvas（csproj 未引 UnityEngine.UIModule，不能用类型）。</summary>
        private static bool HasCanvasComponent(Transform t)
        {
            foreach (Component comp in t.GetComponents<Component>())
                if (comp != null && comp.GetType().Name == "Canvas") return true;
            return false;
        }

        /// <summary>
        /// 禁掉链上节点自身/子级的横向宽度控制（LayoutGroup 的 childControlWidth、
        /// ContentSizeFitter 的横向 fit），否则布局系统会在下一帧把强制宽度改回去。
        /// </summary>
        private static void DisableWidthControl(RectTransform rt)
        {
            var hv = rt.GetComponent<HorizontalOrVerticalLayoutGroup>();
            if (hv != null && hv.childControlWidth) hv.childControlWidth = false;

            var fitter = rt.GetComponent<ContentSizeFitter>();
            if (fitter != null
                && fitter.horizontalFit != ContentSizeFitter.FitMode.Unconstrained)
                fitter.horizontalFit = ContentSizeFitter.FitMode.Unconstrained;
        }

        /// <summary>诊断转储：链上每个节点的 锚跨度/宽/sizeDelta.x/宽度控制组件。</summary>
        private static string DumpChain(List<RectTransform> chain)
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
        /// 列表加宽后越出画布时平移回屏内（展开方向由 TMP 的翻转逻辑决定）。
        /// v7 修正：边界用<strong>真正的画布根</strong>（向上走到最顶层 RectTransform），
        /// 之前误用 popup 的直接父节点（= 下拉控件自身，仅 ~137px 宽），钳制时把
        /// 整个列表推进了屏幕左缘外。
        /// </summary>
        private static void ClampIntoCanvas(RectTransform popupRt)
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
