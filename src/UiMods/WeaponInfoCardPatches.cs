using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;

namespace NuclearOptionChineseLocalizationPatch.UiMods
{
    /// <summary>
    /// 基地武器信息卡（<c>AircraftSelectionMenu.DisplayInfo</c>）的<b>打点入口</b>：
    /// 解析控件字段引用，并在 DisplayInfo 之后触发表格化。
    ///
    /// <para><b>职责边界：</b>本类只做「字段解析 + 补丁方法」。表格几何见
    /// <see cref="WeaponInfoCardTable"/>，每帧几何稳定见 <see cref="WeaponInfoCardStabilizer"/>，
    /// 纯几何 helper 见 <see cref="RectGeom"/>。
    /// <b>任何 helper 都不得留在这里</b> —— Harmony 分析器会把补丁类中的所有方法
    /// 当候选补丁检查，helper 里对自身参数的赋值会误报 <c>Harmony003</c>。</para>
    ///
    /// <para><b>实证结构：</b><c>Darkener</c>（LayoutGroup）下三个子项：
    /// <c>[0] WeaponImage</c>（图片区）、<c>[1] WeaponInfo</c>（参数区，宽度恒为 0 是本体设计，
    /// 行容器自管宽度）、<c>[2] Description</c>（= <c>info</c> TMP）。
    /// 每次打开菜单游戏都会新建 <c>SelectionMenu(Clone)</c>（LogOutput 多实例实证）。</para>
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
                WeaponInfoCardTable.ProcessStats(__instance);
            }
            catch (System.Exception ex)
            {
                Diagnostics.Log.Debug("[信息卡] postfix 异常（忽略）: " + ex.Message);
            }
        }
    }
}
