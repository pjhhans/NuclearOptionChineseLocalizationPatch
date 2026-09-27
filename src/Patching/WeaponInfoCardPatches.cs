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
    /// 基地武器信息卡（<c>AircraftSelectionMenu.DisplayInfo</c>）的几何稳定化 v3。
    ///
    /// <para><b>v2 实机两处失败（IL + LogOutput 实证）：</b>
    /// ① <c>info</c> 字段本身就是 <see cref="TMP_Text"/>（IL：<c>ldfld info</c> 直接接
    /// <c>TMP_Text::set_text</c>），v2 把它当嵌套对象找 <c>info.description</c>，反射永远失败；
    /// ② <c>weaponImageArea</c> 是 GameObject 且无武器时被本体 <c>SetActive(false)</c>，
    /// 首记时宽度 0 永远进不了几何表，但 v2 已先执行 <c>DetachFromLayout</c> ——
    /// 控件被拔出 <c>Darkener</c> 布局组却无几何回放，图片区坍缩消失。</para>
    ///
    /// <para><b>真实结构（首记层级转储）：</b><c>Darkener(+LG)</c> 下挂
    /// <c>WeaponImage</c>（图片区）与 <c>WeaponInfo(+LG)</c>（参数块，内含三行
    /// seeker|range / AP|HE / RCS|cost，行 200px、值文本仅 100px 宽）。
    /// 中文值串（「攻击距离：150km」约 175px）在 100px 单元格里必然换行，
    /// 行数随武器变化 = 参数块行数漂移的根源。</para>
    ///
    /// <para><b>v3 语义（用户裁决）：</b>参数块始终靠近左侧武器图片且位置固定；
    /// 参数块与描述的宽度固定；图片正常显示。</para>
    ///
    /// <para><b>v3 实现：</b>
    /// ① 六个参数值文本禁自动换行（<c>enableWordWrapping=false</c> + Overflow），
    /// 行数恒定 = 参数块几何恒定的治本修复；
    /// ② 钳 <c>weaponInfoArea</c>：首个激活状态首记位置+尺寸，脱离父级布局组并回放；
    /// ③ 钳描述宽度：关闭描述 TMP 的水平自适应（保留垂直），宽度首记后回放；
    /// ④ 图片区仅在「激活且宽度有效」时首记+接管，未接管前绝不触碰其布局
    /// （v2 图片消失根因的直接修正），并开 <c>preserveAspect</c> 防钳宽后图标变形。</para>
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

        private static FieldInfo _infoField;        // TMP_Text：描述控件（IL 实证 info 直接是 TMP_Text）
        private static FieldInfo _infoAreaField;    // GameObject：weaponInfoArea（参数块容器）
        private static FieldInfo _imageAreaField;   // GameObject：weaponImageArea（图片区容器）
        private static FieldInfo _weaponImageField; // Image：weaponImage（图标本体）
        private static FieldInfo[] _statFields;

        /// <summary>
        /// 全局共享几何：控件键 → {x, y, w, h}。首个有效状态记录一次，
        /// 之后所有实例回放同一份 —— 跨打开菜单、跨武器一致。
        /// </summary>
        private static readonly Dictionary<string, float[]> Pins = new Dictionary<string, float[]>();

        private static readonly HashSet<string> _parentDumped = new HashSet<string>();

        private static IEnumerable<MethodBase> TargetMethods()
        {
            var type = AccessTools.TypeByName("AircraftSelectionMenu");
            if (type == null)
            {
                Diagnostics.Log.Info("[信息卡] 未找到 AircraftSelectionMenu，补丁空转");
                yield break;
            }

            _infoField = AccessTools.Field(type, "info");
            _infoAreaField = AccessTools.Field(type, "weaponInfoArea");
            _imageAreaField = AccessTools.Field(type, "weaponImageArea");
            _weaponImageField = AccessTools.Field(type, "weaponImage");
            _statFields = new FieldInfo[StatFieldNames.Length];
            for (int i = 0; i < StatFieldNames.Length; i++)
                _statFields[i] = AccessTools.Field(type, StatFieldNames[i]);

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
            // ① 六个参数值文本：禁自动换行（治本：行数恒定 → 参数块几何恒定）
            if (_statFields != null)
            {
                foreach (FieldInfo f in _statFields)
                {
                    if (f?.GetValue(__instance) is TMP_Text tmp && tmp.enableWordWrapping)
                    {
                        tmp.enableWordWrapping = false;
                        tmp.overflowMode = TextOverflowModes.Overflow;
                        Diagnostics.Log.Info("[信息卡] 禁换行: " + tmp.name);
                    }
                }
            }

            // ② 描述宽度钳制（info 直接是 TMP_Text，v2 反射路径错误的修正）
            if (_infoField?.GetValue(__instance) is TMP_Text info)
                PinWidth(info.rectTransform, "description");

            // ③ 参数块容器：位置+尺寸钳制（贴左武器图片固定）
            RectTransform infoArea = ToRect(_infoAreaField?.GetValue(__instance));
            if (infoArea != null)
                PinAll(infoArea, "infoArea");

            // ④ 图片区：仅在有效状态首记后才接管；未接管绝不碰布局（v2 图片消失根因）
            RectTransform imageArea = ToRect(_imageAreaField?.GetValue(__instance));
            if (imageArea != null)
            {
                if (_weaponImageField?.GetValue(__instance) is Image img && !img.preserveAspect)
                    img.preserveAspect = true; // 钳宽后防不同纵横比图标变形
                PinAll(imageArea, "imageArea");
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
        /// 钳制位置+尺寸。核心不变量：<b>未成功首记绝不脱离布局引擎</b> ——
        /// v2 先 detach 后首记失败 = 控件失去布局又无回放（图片消失）。
        /// </summary>
        private static void PinAll(RectTransform rt, string key)
        {
            if (!rt.gameObject.activeInHierarchy)
                return; // 本体无武器分支会 SetActive(false)，未激活不记不碰

            if (!Pins.TryGetValue(key, out float[] pin))
            {
                // 图片区必须取到有效宽度才接管（首帧布局未跑时 rect 可能未刷新，等下次）
                if (key == "imageArea" && rt.rect.width < 1f)
                    return;

                Pins[key] = new[]
                {
                    rt.anchoredPosition.x, rt.anchoredPosition.y, rt.rect.width, rt.rect.height,
                };
                DetachFromLayout(rt);
                DumpParentOnce(rt, key);
                Diagnostics.Log.Info(string.Format(
                    "[信息卡·首记] {0}: {1:F0}x{2:F0} @ ({3:F0},{4:F0})",
                    key, rt.rect.width, rt.rect.height,
                    rt.anchoredPosition.x, rt.anchoredPosition.y));
                return;
            }

            DetachFromLayout(rt); // 已有 pin：确保持续脱离父级布局引擎，然后回放
            Apply(rt, key, pin);
        }

        /// <summary>仅钳宽度（描述）：关闭水平自适应（保留垂直），不动位置、不脱离父布局。</summary>
        private static void PinWidth(RectTransform rt, string key)
        {
            if (!rt.gameObject.activeInHierarchy)
                return;

            if (!Pins.TryGetValue(key, out float[] pin))
            {
                if (rt.rect.width < 1f)
                    return;

                ContentSizeFitter fitter = rt.GetComponent<ContentSizeFitter>();
                if (fitter != null && fitter.horizontalFit != ContentSizeFitter.FitMode.Unconstrained)
                {
                    fitter.horizontalFit = ContentSizeFitter.FitMode.Unconstrained;
                    Diagnostics.Log.Info("[信息卡] 描述关闭水平自适应: " + rt.name);
                }

                Pins[key] = new[] { 0f, 0f, rt.rect.width, rt.rect.height };
                DumpParentOnce(rt, key);
                Diagnostics.Log.Info(string.Format("[信息卡·首记] {0}: 宽 {1:F0}", key, rt.rect.width));
                return;
            }

            if (Mathf.Abs(rt.rect.width - pin[2]) > 0.5f)
            {
                rt.SetSizeWithCurrentAnchors(RectTransform.Axis.Horizontal, pin[2]);
                Diagnostics.Log.Debug(string.Format(
                    "[信息卡·回放] {0}: 宽 {1:F0} → {2:F0}", key, rt.rect.width, pin[2]));
            }
        }

        private static void Apply(RectTransform rt, string key, float[] pin)
        {
            const float eps = 0.5f;

            if (Mathf.Abs(rt.rect.width - pin[2]) > eps)
                rt.SetSizeWithCurrentAnchors(RectTransform.Axis.Horizontal, pin[2]);
            if (Mathf.Abs(rt.rect.height - pin[3]) > eps)
                rt.SetSizeWithCurrentAnchors(RectTransform.Axis.Vertical, pin[3]);

            float dx = rt.anchoredPosition.x - pin[0];
            float dy = rt.anchoredPosition.y - pin[1];
            if (dx * dx + dy * dy > eps * eps)
                rt.anchoredPosition = new Vector2(pin[0], pin[1]);
        }

        /// <summary>
        /// 让控件脱离父级布局引擎：父级 LayoutGroup 跳过它（ignoreLayout），
        /// 自身不再随内容自适应（禁 fitter）。
        /// </summary>
        private static void DetachFromLayout(RectTransform rt)
        {
            ContentSizeFitter fitter = rt.GetComponent<ContentSizeFitter>();
            if (fitter != null && fitter.enabled)
                fitter.enabled = false;

            LayoutElement le = rt.GetComponent<LayoutElement>();
            if (le == null)
                le = rt.gameObject.AddComponent<LayoutElement>();
            if (!le.ignoreLayout)
                le.ignoreLayout = true;
        }

        /// <summary>每键一次性输出父级与兄弟列表（Info 级），若出现兄弟位移可据此定位。</summary>
        private static void DumpParentOnce(RectTransform rt, string key)
        {
            if (!_parentDumped.Add(key))
                return;

            Transform parent = rt.parent;
            if (parent == null)
                return;

            var sb = new StringBuilder("[信息卡·父级] ").Append(key).Append(" -> ").Append(parent.name);
            int n = parent.childCount;
            for (int i = 0; i < n; i++)
            {
                Transform child = parent.GetChild(i);
                var crt = child as RectTransform;
                sb.Append("\n  [").Append(i).Append("] ").Append(child.name);
                if (crt != null)
                    sb.Append(string.Format(" {0:F0}x{1:F0} active={2}",
                        crt.rect.width, crt.rect.height, child.gameObject.activeSelf));
                if (child.GetComponent<LayoutElement>() is LayoutElement le && le.ignoreLayout)
                    sb.Append(" (ignored)");
            }
            Diagnostics.Log.Info(sb.ToString());
        }
    }
}
