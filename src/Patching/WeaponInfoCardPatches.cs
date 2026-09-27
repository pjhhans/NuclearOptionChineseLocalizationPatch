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

        [HarmonyPostfix]
        private static void Postfix(object __instance)
        {
            if (!Enabled)
                return;

            try
            {
                DisableWrap(__instance);
            }
            catch (System.Exception ex)
            {
                Diagnostics.Log.Debug("[信息卡] postfix 异常（忽略）: " + ex.Message);
            }
        }

        /// <summary>六个参数值文本禁自动换行（行数恒定 = 参数块几何恒定的治本修复）。</summary>
        private static void DisableWrap(object __instance)
        {
            if (_statFields == null)
                return;
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
        private static readonly HashSet<int> _pinnedInstances = new HashSet<int>();
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
            bool desValid = des != null && des[2] >= 1f && imgValid && des[0] >= img[0] + img[2] - 5f;

            if (!_pinnedInstances.Contains(id))
            {
                // 新实例：布局稳定前完全不碰几何（原生布局，绝不产生「钉错」）
                if (!(imgValid && infValid && desValid))
                    return;

                bool adapted = WeaponInfoCardPatches.Pins.Count == 3 && (
                    Differs(WeaponInfoCardPatches.Pins["imageArea"], img) ||
                    Differs(WeaponInfoCardPatches.Pins["infoArea"], new[] { inf[0], inf[1], 0f, 0f }) ||
                    Differs(WeaponInfoCardPatches.Pins["description"], des));

                WeaponInfoCardPatches.Pins["imageArea"] = img;
                WeaponInfoCardPatches.Pins["infoArea"] = new[] { inf[0], inf[1], 0f, 0f };
                WeaponInfoCardPatches.Pins["description"] = des;

                WeaponInfoCardPatches.DetachFromLayout(imageArea);
                WeaponInfoCardPatches.DetachFromLayout(infoArea);
                WeaponInfoCardPatches.DetachFromLayout(desc);

                _pinnedInstances.Add(id);
                if (_pinnedInstances.Count > 32)
                {
                    _pinnedInstances.Clear();
                    _pinnedInstances.Add(id);
                }
                _degenerateFrames = 0;

                Diagnostics.Log.Info(string.Format(
                    "[信息卡·钉死] 实例 {0}{1}: image {2:F0}x{3:F0}@({4:F0},{5:F0}) info @({6:F0},{7:F0}) desc {8:F0}x{9:F0}@({10:F0},{11:F0})",
                    id, adapted ? "（与既有几何不一致，已自适应）" : "",
                    img[2], img[3], img[0], img[1], inf[0], inf[1], des[2], des[3], des[0], des[1]));
                return;
            }

            // —— 已钉实例：确保脱离 + 每帧回放 ——
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
                WeaponInfoCardPatches.DetachFromLayout(desc);
                ApplyRect(desc, WeaponInfoCardPatches.Pins["description"], setHeight: false);
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

        private static void ResetAll(object instance)
        {
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
