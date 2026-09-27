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
    /// 基地武器信息卡（<c>AircraftSelectionMenu.DisplayInfo</c>）的几何稳定化。
    ///
    /// <para><b>问题（IL 实证 + 实机截图）：</b>DisplayInfo 每次切换武器都全量重写
    /// 描述（<c>info.description</c>）与六个统计槽（weaponSeeker/Range/AP/HE/RCS/Cost，
    /// 无武器时 RCS 槽复用为 <c>M: </c> 单发质量、Cost 槽写 <c>C: </c> 花费）。
    /// 各武器描述行数与参数行数不同，布局系统随内容重排 —— 切换武器时参数
    /// 相对位置漂移、描述区高度跳变。</para>
    ///
    /// <para><b>修法（吸取 PylonDropdown v5-v8 教训）：</b>
    /// ① 菜单实例首次 DisplayInfo 时记录各控件的 anchoredPosition + 尺寸
    /// （此时为预制体设计位置；隐藏/零尺寸时不记录，防锚定到未布局状态）；
    /// ② 此后每次刷新把记录几何回放回去 —— 尺寸用
    /// <see cref="RectTransform.SetSizeWithCurrentAnchors"/>（拉伸锚链安全、幂等），
    /// 位置直接写 anchoredPosition；
    /// ③ 首次钳制时把描述与统计槽到最近公共祖先之间链上的
    /// LayoutGroup 内容控制与 ContentSizeFitter 禁用 —— 否则重排在下一帧
    /// 覆盖钳制（v5「读回不变」的教训）。禁用不重置子 rect 位置，故初始
    /// 布局保持原样。</para>
    ///
    /// <para>诊断日志全部 Debug 级；首轮实机以「回放是否被覆盖」读回值判断布局
    /// 驱动者，再决定后续迭代方向。</para>
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

        private static FieldInfo _infoField;
        private static FieldInfo[] _statFields;
        private static FieldInfo _descriptionField;

        /// <summary>菜单实例 → 控件键 → {x, y, w, h} 首见几何。</summary>
        private static readonly ConditionalWeakTable<object, Dictionary<string, float[]>> Pins =
            new ConditionalWeakTable<object, Dictionary<string, float[]>>();

        private static readonly ConditionalWeakTable<object, object> ReflowHandled =
            new ConditionalWeakTable<object, object>();

        private static IEnumerable<MethodBase> TargetMethods()
        {
            var type = AccessTools.TypeByName("AircraftSelectionMenu");
            if (type == null)
            {
                Diagnostics.Log.Debug("[信息卡] 未找到 AircraftSelectionMenu，补丁空转");
                yield break;
            }

            if (_statFields == null)
            {
                _infoField = AccessTools.Field(type, "info");
                _statFields = new FieldInfo[StatFieldNames.Length];
                for (int i = 0; i < StatFieldNames.Length; i++)
                    _statFields[i] = AccessTools.Field(type, StatFieldNames[i]);
            }

            foreach (MethodInfo m in AccessTools.GetDeclaredMethods(type))
                if (m.Name == "DisplayInfo")
                    yield return m;
        }

        [HarmonyPostfix]
        private static void Postfix(object __instance)
        {
            if (!Enabled)
                return;

            try
            {
                Run(__instance);
            }
            catch (System.Exception ex)
            {
                // 信息卡几何修整失败不应影响游戏本体刷新
                Diagnostics.Log.Debug("[信息卡] postfix 异常（忽略）: " + ex.Message);
            }
        }

        private static void Run(object __instance)
        {
            var targets = new List<KeyValuePair<string, RectTransform>>(8);

            if (_statFields != null)
            {
                foreach (FieldInfo f in _statFields)
                {
                    var tmp = f?.GetValue(__instance) as TMP_Text;
                    if (tmp != null)
                        targets.Add(new KeyValuePair<string, RectTransform>(f.Name, tmp.rectTransform));
                }
            }

            object info = _infoField?.GetValue(__instance);
            if (info != null)
            {
                if (_descriptionField == null)
                    _descriptionField = AccessTools.Field(info.GetType(), "description");
                var desc = _descriptionField?.GetValue(info) as TMP_Text;
                if (desc != null)
                    targets.Add(new KeyValuePair<string, RectTransform>("description", desc.rectTransform));
            }

            if (targets.Count == 0)
                return;

            Dictionary<string, float[]> pins = Pins.GetOrCreateValue(__instance);
            bool firstRun = false;

            foreach (KeyValuePair<string, RectTransform> kv in targets)
            {
                RectTransform rt = kv.Value;
                if (rt == null)
                    continue;

                if (!pins.TryGetValue(kv.Key, out float[] pin))
                {
                    // 防锚定到未布局状态：隐藏或零尺寸不记录
                    if (!rt.gameObject.activeInHierarchy || rt.rect.width < 1f)
                        continue;
                    pins[kv.Key] = new[]
                    {
                        rt.anchoredPosition.x, rt.anchoredPosition.y, rt.rect.width, rt.rect.height,
                    };
                    Diagnostics.Log.Debug(string.Format(
                        "[信息卡·首记] {0}: {1:F0}x{2:F0} @ ({3:F0},{4:F0})",
                        kv.Key, rt.rect.width, rt.rect.height,
                        rt.anchoredPosition.x, rt.anchoredPosition.y));
                    firstRun = true;
                    continue;
                }

                Apply(rt, kv.Key, pin);
            }

            if (firstRun && !ReflowHandled.TryGetValue(__instance, out _))
            {
                DisableReflow(targets);
                ReflowHandled.Add(__instance, null);
            }
        }

        private static void Apply(RectTransform rt, string key, float[] pin)
        {
            const float eps = 0.5f;
            bool changed = false;

            if (Mathf.Abs(rt.rect.width - pin[2]) > eps)
            {
                rt.SetSizeWithCurrentAnchors(RectTransform.Axis.Horizontal, pin[2]);
                changed = true;
            }
            if (Mathf.Abs(rt.rect.height - pin[3]) > eps)
            {
                rt.SetSizeWithCurrentAnchors(RectTransform.Axis.Vertical, pin[3]);
                changed = true;
            }

            float dx = rt.anchoredPosition.x - pin[0];
            float dy = rt.anchoredPosition.y - pin[1];
            if (dx * dx + dy * dy > eps * eps)
            {
                rt.anchoredPosition = new Vector2(pin[0], pin[1]);
                changed = true;
            }

            if (changed)
                Diagnostics.Log.Debug(string.Format(
                    "[信息卡·回放] {0}: 漂移 {1:F0}x{2:F0}@({3:F0},{4:F0}) → {5:F0}x{6:F0}@({7:F0},{8:F0})",
                    key, rt.rect.width, rt.rect.height,
                    rt.anchoredPosition.x, rt.anchoredPosition.y, pin[2], pin[3], pin[0], pin[1]));
        }

        /// <summary>
        /// 禁用描述与统计槽到最近公共祖先之间链上的布局重排，
        /// 范围外（面板公共父以上）一律不碰。
        /// </summary>
        private static void DisableReflow(List<KeyValuePair<string, RectTransform>> targets)
        {
            RectTransform desc = null;
            RectTransform stat = null;
            foreach (KeyValuePair<string, RectTransform> kv in targets)
            {
                if (kv.Value == null)
                    continue;
                if (kv.Key == "description" && desc == null)
                    desc = kv.Value;
                else if (stat == null)
                    stat = kv.Value;
            }
            if (desc == null || stat == null)
                return;

            Transform lca = CommonAncestor(desc, stat);
            if (lca == null)
                return;

            foreach (KeyValuePair<string, RectTransform> kv in targets)
            {
                Transform cur = kv.Value != null ? kv.Value.parent : null;
                int guard = 0;
                while (cur != null && cur != lca && guard++ < 6)
                {
                    GameObject go = cur.gameObject;
                    ContentSizeFitter fitter = go.GetComponent<ContentSizeFitter>();
                    if (fitter != null && fitter.enabled)
                    {
                        fitter.enabled = false;
                        Diagnostics.Log.Debug("[信息卡] 禁用 ContentSizeFitter: " + go.name);
                    }
                    var lg = go.GetComponent<HorizontalOrVerticalLayoutGroup>();
                    if (lg != null && lg.enabled)
                    {
                        lg.childControlWidth = false;
                        lg.childControlHeight = false;
                        lg.childForceExpandWidth = false;
                        lg.childForceExpandHeight = false;
                        Diagnostics.Log.Debug("[信息卡] 禁用 LayoutGroup 内容控制: " + go.name);
                    }
                    cur = cur.parent;
                }
            }
        }

        private static Transform CommonAncestor(Transform a, Transform b)
        {
            var chain = new HashSet<Transform>();
            for (Transform t = a.parent; t != null; t = t.parent)
                chain.Add(t);
            for (Transform t = b.parent; t != null; t = t.parent)
                if (chain.Contains(t))
                    return t;
            return null;
        }
    }
}
