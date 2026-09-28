using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace NuclearOptionChineseLocalizationPatch.UiMods
{
    /// <summary>
    /// 挂架下拉弹出列表的加宽（治本版）：在 <see cref="TMP_Dropdown.Show"/> 之后对
    /// <strong>实际弹出的列表</strong>动手 —— 这才是中文武器名被截断的修复点。
    ///
    /// <para><b>为什么必须在 Show 之后、且用独立补丁类：</b></para>
    /// <list type="number">
    /// <item>实测模板几何不传导到弹出列表（模板根 296px，弹出根 137px），
    ///       只有弹出列表自己的 rect 才是渲染真相；</item>
    /// <item>Harmony 对含 <c>TargetMethods</c> 的补丁类，会把类内所有补丁方法套到
    ///       每个目标上 —— 曾因 <c>__instance</c> 类型不兼容导致整类安装失败
    ///       （连滚动条修复一起失效），所以 TargetMethods 与按方法标注的补丁必须分家。</item>
    /// </list>
    ///
    /// <para><b>做法：</b>沿「弹出条目标签 → 列表顶层」的祖先链，把所有非拉伸锚 rect
    /// 一并加宽（拉伸锚自动随父变宽）；选中高亮背景（Item Background）不在祖先链上，
    /// 按名补一遍；最后把列表钳回画布范围内（TMP 的翻转逻辑已处理展开方向，这里只兜底越界）。
    /// 弹出列表每次 Show 都从模板重建，天然幂等。</para>
    ///
    /// <para><b>类名说明：</b>原名 <c>PylonDropdownShowDiagnostics</c> 与实际职责
    /// （加宽 + 滚轮挂载）不符，改名以名副其实；诊断日志保留，便于实机复核。
    /// <b>本类只含补丁方法</b>，度量/几何 helper 见 <see cref="DropdownGeom"/>。</para>
    /// </summary>
    [HarmonyPatch(typeof(TMP_Dropdown), "Show")]
    internal static class PylonDropdownPopupPatches
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

                // 滚轮驱动（用户需求「下拉列表只能手拉，加滚轮」）：
                // 必须在 firstLabel 早退之前挂 —— 列表结构异常时滚轮也要能装上。
                EnsureWheelDriver(popupRt);

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
                Diagnostics.Log.Debug(sb.ToString());

                // ---- 加宽 ----
                float required = DropdownGeom.MeasureMaxOptionWidth(__instance)
                    + DropdownGeom.ExtraPadding;
                float labelW = firstLabel.rectTransform.rect.width;
                float delta = required - labelW;
                if (delta >= DropdownGeom.MinDelta)
                {
                    if (popupRt.rect.width + delta > DropdownGeom.MaxTemplateWidth)
                        delta = DropdownGeom.MaxTemplateWidth - popupRt.rect.width;
                }
                if (delta >= DropdownGeom.MinDelta)
                {
                    // 链：标签 → … → popup 根（m_Dropdown），含两端。
                    // 转储发现 popup 根挂着 Canvas 组件会导致向上走提前停、根没加宽
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
                    // 应用时现读现算会让拉伸锚节点被「父加宽 + 自身强制」双重叠加
                    // （标签一路滚到 658px，把列表顶出屏幕左缘）。
                    var targets = new float[chain.Count];
                    for (int i = 0; i < chain.Count; i++)
                        targets[i] = (chain[i] != null ? chain[i].rect.width : 0f) + delta;
                    float bgTarget = (bg != null ? bg.rect.width : 0f) + delta;

                    for (int i = chain.Count - 1; i >= 0; i--)
                    {
                        RectTransform rt = chain[i];
                        if (rt == null) continue;
                        DropdownGeom.DisableWidthControl(rt);
                        rt.SetSizeWithCurrentAnchors(RectTransform.Axis.Horizontal, targets[i]);
                    }
                    if (bg != null)
                    {
                        DropdownGeom.DisableWidthControl(bg);
                        bg.SetSizeWithCurrentAnchors(RectTransform.Axis.Horizontal, bgTarget);
                    }

                    DropdownGeom.ClampIntoCanvas(popupRt);

                    // 读回验证：若仍等于旧值，说明宽度另有来源，转储整条链定位
                    Diagnostics.Log.Debug(string.Format(
                        "[挂架下拉] 已加宽 +{0:F0}px，读回：根 {1:F0}px / 标签 {2:F0}px（目标 {3:F0}px）",
                        delta, popupRt.rect.width,
                        firstLabel.rectTransform.rect.width, required));
                    Diagnostics.Log.Debug("[挂架下拉·链] " + DropdownGeom.DumpChain(chain));
                }
            }
            catch (System.Exception ex)
            {
                Diagnostics.Log.Warn("挂架下拉 Show 修整失败：" + ex.GetType().Name + ": " + ex.Message);
            }
        }

        /// <summary>在弹出根上装/刷新滚轮驱动组件（随弹出列表销毁自毁，天然幂等）。</summary>
        private static void EnsureWheelDriver(RectTransform popupRt)
        {
            var driver = popupRt.GetComponent<DropdownWheelDriver>();
            if (driver == null)
                driver = popupRt.gameObject.AddComponent<DropdownWheelDriver>();
            driver.Popup = popupRt;
            driver.Scroll = popupRt.GetComponentInChildren<ScrollRect>(false);
            driver.Bar = driver.Scroll != null ? null : popupRt.GetComponentInChildren<Scrollbar>(false);
            Diagnostics.Log.Debug(string.Format(
                "[挂架下拉·滚轮] 驱动已装：ScrollRect={0}，Scrollbar 回落={1}",
                driver.Scroll != null, driver.Bar != null));
        }
    }
}
