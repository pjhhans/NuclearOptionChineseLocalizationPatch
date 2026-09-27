using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace NuclearOptionChineseLocalizationPatch.Patching
{
    /// <summary>
    /// 基地武器信息卡（<c>AircraftSelectionMenu.DisplayInfo</c>）的几何稳定化 v4。
    ///
    /// <para><b>实证结构（v3 首记父级转储）：</b><c>Darkener</c>（带 LayoutGroup）下三个子项：
    /// <c>[0] WeaponImage</c>（图片区）、<c>[1] WeaponInfo</c>（参数区，**宽度恒为 0**——
    /// 行容器 200px 自管宽度从容器向右伸出，钳制其尺寸必然压瘪/清零）、
    /// <c>[2] Description</c>（描述，即 <c>info</c> TMP）。</para>
    ///
    /// <para><b>v3 失败根因（LogOutput 实证）：</b>在 DisplayInfo 同帧（布局引擎尚未运行）
    /// 首记，<c>infoArea</c> 记到垃圾值 <c>0x110 @ (0,0)</c> → 位置钳到容器原点（参数块
    /// 跑到面板外压住「返回」）、宽度钳到 0。</para>
    ///
    /// <para><b>v4 关键时序：</b>uGUI 布局重排发生在渲染期（willRenderCanvases），
    /// <c>Update</c> 时 rect 反映上一帧的完整布局。因此全部几何操作移到
    /// <see cref="WeaponInfoCardStabilizer"/>（钩本体已有的 Update）：
    /// <b>同一帧先读三个子项的布局完好快照 → 有效性判定 → 统一 detach + 钉死</b>；
    /// 渲染期布局引擎重排时三个子项均已 ignoreLayout，互不挪动。</para>
    ///
    /// <para><b>有效性判据：</b>图片区须激活且宽≥1；参数区/描述须激活且位于图片右侧
    /// （防垃圾帧）；infoArea 只钳位置不碰尺寸；描述钳位置+宽度（高度交给本体）。
    /// 分辨率变化清空几何表重记。核心不变量：<b>未成功首记绝不 detach</b>。</para>
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

        /// <summary>全局共享几何：控件键 → {x, y, w, h}。跨实例回放同一份。</summary>
        internal static readonly Dictionary<string, float[]> Pins = new Dictionary<string, float[]>();

        internal static readonly HashSet<string> ParentDumped = new HashSet<string>();
        internal static int ScreenW, ScreenH;

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

        /// <summary>控件脱离父级布局引擎（父级 LayoutGroup 跳过它）。只挂 ignoreLayout，不动 fitter。</summary>
        internal static void DetachFromLayout(RectTransform rt)
        {
            LayoutElement le = rt.GetComponent<LayoutElement>();
            if (le == null)
                le = rt.gameObject.AddComponent<LayoutElement>();
            if (!le.ignoreLayout)
                le.ignoreLayout = true;
        }

        /// <summary>每键一次性输出父级与兄弟列表（Info 级），供后续迭代定位。</summary>
        internal static void DumpParentOnce(RectTransform rt, string key)
        {
            if (!ParentDumped.Add(key))
                return;
            Transform parent = rt.parent;
            if (parent == null)
                return;
            var sb = new System.Text.StringBuilder("[信息卡·父级] ").Append(key).Append(" -> ").Append(parent.name);
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

    /// <summary>
    /// 几何稳定执行器：钩 <c>AircraftSelectionMenu.Update</c>（每帧，rect 为上一帧
    /// 布局的最终结果）。同帧「读快照 → 判定 → detach + 钉死」，见 v4 类注释。
    /// </summary>
    [HarmonyPatch]
    internal static class WeaponInfoCardStabilizer
    {
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
            // 分辨率变化 → 几何表全部失效重记（锚定坐标是父级局部像素值）
            if (Screen.width != WeaponInfoCardPatches.ScreenW || Screen.height != WeaponInfoCardPatches.ScreenH)
            {
                if (WeaponInfoCardPatches.ScreenW != 0)
                    Diagnostics.Log.Info("[信息卡] 分辨率变化，清空几何表重记");
                WeaponInfoCardPatches.ScreenW = Screen.width;
                WeaponInfoCardPatches.ScreenH = Screen.height;
                WeaponInfoCardPatches.Pins.Clear();
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

            // —— 再统一 detach + 钉死 ——
            if (imageArea != null)
            {
                if (WeaponInfoCardPatches._weaponImageField?.GetValue(instance) is Image wimg && !wimg.preserveAspect)
                    wimg.preserveAspect = true; // 钳宽后防不同纵横比图标变形

                if (WeaponInfoCardPatches.Pins.TryGetValue("imageArea", out float[] pin))
                {
                    WeaponInfoCardPatches.DetachFromLayout(imageArea);
                    ApplyRect(imageArea, pin);
                }
                else if (imgValid)
                {
                    WeaponInfoCardPatches.Pins["imageArea"] = img;
                    WeaponInfoCardPatches.DetachFromLayout(imageArea);
                    WeaponInfoCardPatches.DumpParentOnce(imageArea, "imageArea");
                    Diagnostics.Log.Info(string.Format("[信息卡·首记] imageArea: {0:F0}x{1:F0} @ ({2:F0},{3:F0})",
                        img[2], img[3], img[0], img[1]));
                }
            }

            if (infoArea != null)
            {
                if (WeaponInfoCardPatches.Pins.TryGetValue("infoArea", out float[] pin))
                {
                    WeaponInfoCardPatches.DetachFromLayout(infoArea);
                    // 只钳位置：0 宽是本体设计（行容器自管宽度），碰尺寸必然压瘪
                    ApplyPos(infoArea, pin);
                }
                else if (infValid)
                {
                    WeaponInfoCardPatches.Pins["infoArea"] = new[] { inf[0], inf[1], 0f, 0f };
                    WeaponInfoCardPatches.DetachFromLayout(infoArea);
                    WeaponInfoCardPatches.DumpParentOnce(infoArea, "infoArea");
                    Diagnostics.Log.Info(string.Format("[信息卡·首记] infoArea 位置: @ ({0:F0},{1:F0})",
                        inf[0], inf[1]));
                }
            }

            if (desc != null)
            {
                if (WeaponInfoCardPatches.Pins.TryGetValue("description", out float[] pin))
                {
                    WeaponInfoCardPatches.DetachFromLayout(desc);
                    ApplyRect(desc, pin, setHeight: false); // 高度交给本体/自适应
                }
                else if (desValid)
                {
                    WeaponInfoCardPatches.Pins["description"] = des;
                    WeaponInfoCardPatches.DetachFromLayout(desc);
                    WeaponInfoCardPatches.DumpParentOnce(desc, "description");
                    Diagnostics.Log.Info(string.Format("[信息卡·首记] description: {0:F0}x{1:F0} @ ({2:F0},{3:F0})",
                        des[2], des[3], des[0], des[1]));
                }
            }
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
