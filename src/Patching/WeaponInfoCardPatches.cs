using System.Collections.Generic;
using System.Reflection;
using System.Text;
using HarmonyLib;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace NuclearOptionChineseLocalizationPatch.Patching
{
    /// <summary>
    /// 基地武器信息卡（<c>AircraftSelectionMenu.DisplayInfo</c>）的几何稳定化 v2。
    ///
    /// <para><b>问题（IL 实证 + 实机截图）：</b>DisplayInfo 每次切换武器都全量重写
    /// 描述（<c>info.description</c>）与六个统计槽（weaponSeeker/Range/AP/HE/RCS/Cost，
    /// 无武器时 RCS 槽复用为 <c>M: </c> 单发质量、Cost 槽写 <c>C: </c> 花费）。
    /// 武器图片区随图标纵横比伸缩、描述随内容伸缩，布局引擎随之重排 ——
    /// 参数块与描述的位置、宽度随武器漂移。</para>
    ///
    /// <para><b>v2 语义（用户裁决）：</b>参数块始终靠近左侧武器图片且位置固定；
    /// 参数块与描述的宽度固定。</para>
    ///
    /// <para><b>实现（v1「按实例首记」缺陷的修正）：</b>
    /// ① <b>全局共享几何表</b> —— 首个有效实例记录一次，此后所有实例、所有武器、
    /// 所有打开菜单都回放同一份（v1 按实例 ConditionalWeakTable，每次重开菜单
    /// 重新首记，几何跨实例不一致）；
    /// ② <b>脱离布局引擎</b> —— 每个被钳控件挂 <see cref="LayoutElement"/>
    /// <c>ignoreLayout=true</c> 并禁用自身 <see cref="ContentSizeFitter"/>，
    /// 父级 LayoutGroup 跳过它（比禁父级 layout 安全，不波及兄弟控件），
    /// 否则回放在下一帧被覆盖；
    /// ③ <b>图片区宽度一并钳制</b>（weaponImageArea）—— 图片区伸缩是参数列
    /// 漂移源头之一；
    /// ④ 首记时一次性输出 Info 级层级转储（Debug 级默认静默，实机不可见）。</para>
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
        private static FieldInfo _imageAreaField;

        /// <summary>
        /// 全局共享几何：控件键 → {x, y, w, h}。首个有效实例记录，
        /// 之后所有实例回放同一份 —— 跨打开菜单、跨武器一致。
        /// </summary>
        private static readonly Dictionary<string, float[]> Pins = new Dictionary<string, float[]>();

        private static bool _hierarchyDumped;

        private static IEnumerable<MethodBase> TargetMethods()
        {
            var type = AccessTools.TypeByName("AircraftSelectionMenu");
            if (type == null)
            {
                Diagnostics.Log.Info("[信息卡] 未找到 AircraftSelectionMenu，补丁空转");
                yield break;
            }

            if (_statFields == null)
            {
                _infoField = AccessTools.Field(type, "info");
                _imageAreaField = AccessTools.Field(type, "weaponImageArea");
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
                    RectTransform rt = ToRect(f?.GetValue(__instance));
                    if (rt != null)
                        targets.Add(new KeyValuePair<string, RectTransform>(f.Name, rt));
                }
            }

            RectTransform imageArea = ToRect(_imageAreaField?.GetValue(__instance));
            if (imageArea != null)
                targets.Add(new KeyValuePair<string, RectTransform>("imageArea", imageArea));

            object info = _infoField?.GetValue(__instance);
            if (info != null)
            {
                if (_descriptionField == null)
                    _descriptionField = AccessTools.Field(info.GetType(), "description");
                var desc = _descriptionField?.GetValue(info) as TMP_Text;
                if (desc != null)
                    targets.Add(new KeyValuePair<string, RectTransform>("description", desc.rectTransform));
            }

            foreach (KeyValuePair<string, RectTransform> kv in targets)
            {
                RectTransform rt = kv.Value;
                if (rt == null)
                    continue;

                DetachFromLayout(rt);

                if (!Pins.TryGetValue(kv.Key, out float[] pin))
                {
                    // 防锚定到未布局状态：隐藏或零尺寸不记录
                    if (!rt.gameObject.activeInHierarchy || rt.rect.width < 1f)
                        continue;
                    Pins[kv.Key] = new[]
                    {
                        rt.anchoredPosition.x, rt.anchoredPosition.y, rt.rect.width, rt.rect.height,
                    };
                    Diagnostics.Log.Info(string.Format(
                        "[信息卡·首记] {0}: {1:F0}x{2:F0} @ ({3:F0},{4:F0})",
                        kv.Key, rt.rect.width, rt.rect.height,
                        rt.anchoredPosition.x, rt.anchoredPosition.y));
                    if (!_hierarchyDumped)
                    {
                        _hierarchyDumped = true;
                        DumpHierarchy(targets);
                    }
                    continue;
                }

                Apply(rt, kv.Key, pin);
            }
        }

        private static RectTransform ToRect(object value)
        {
            if (value is RectTransform rt)
                return rt;
            if (value is GameObject go)
                return go.GetComponent<RectTransform>();
            if (value is Component comp)
                return comp.GetComponent<RectTransform>();
            return null;
        }

        /// <summary>
        /// 让控件彻底脱离布局引擎：父级 LayoutGroup 跳过它（ignoreLayout），
        /// 自身不再随内容自适应（禁 fitter）。不改其它控件，比禁父级 layout 安全。
        /// </summary>
        private static void DetachFromLayout(RectTransform rt)
        {
            ContentSizeFitter fitter = rt.GetComponent<ContentSizeFitter>();
            if (fitter != null && fitter.enabled)
            {
                fitter.enabled = false;
                Diagnostics.Log.Debug("[信息卡] 禁用自身 ContentSizeFitter: " + rt.name);
            }

            LayoutElement le = rt.GetComponent<LayoutElement>();
            if (le == null)
                le = rt.gameObject.AddComponent<LayoutElement>();
            if (!le.ignoreLayout)
            {
                le.ignoreLayout = true;
                Diagnostics.Log.Debug("[信息卡] ignoreLayout = true: " + rt.name);
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

        /// <summary>首记时一次性输出层级转储（Info 级，实机可见），供后续迭代定位布局驱动者。</summary>
        private static void DumpHierarchy(List<KeyValuePair<string, RectTransform>> targets)
        {
            var sb = new StringBuilder("[信息卡·层级] ");
            foreach (KeyValuePair<string, RectTransform> kv in targets)
            {
                RectTransform rt = kv.Value;
                if (rt == null)
                    continue;
                sb.Append("\n  ").Append(kv.Key).Append(" = ").Append(PathOf(rt, 4));
            }
            Diagnostics.Log.Info(sb.ToString());
        }

        private static string PathOf(RectTransform rt, int up)
        {
            var sb = new StringBuilder();
            Transform cur = rt;
            int guard = 0;
            while (cur != null && guard <= up)
            {
                var crt = cur as RectTransform;
                string geo = crt != null
                    ? string.Format("{0}x{1}@({2:F0},{3:F0})", crt.rect.width, crt.rect.height,
                        crt.anchoredPosition.x, crt.anchoredPosition.y)
                    : "?";
                string comps = crt != null && crt.GetComponent<LayoutGroup>() != null ? "+LG" : "";
                comps += crt != null && crt.GetComponent<ContentSizeFitter>() != null ? "+Fitter" : "";
                sb.Insert(0, string.Format("{0}[{1}{2}] <- ", cur.name, geo, comps));
                cur = cur.parent;
                guard++;
            }
            return sb.ToString().TrimEnd('<', '-');
        }
    }
}
