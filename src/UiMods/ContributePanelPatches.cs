using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;

namespace NuclearOptionChineseLocalizationPatch.UiMods
{
    /// <summary>
    /// 捐款面板（<c>ContributeToFaction</c>，「为战争捐款 / Contribute to the war effort」）
    /// <b>选车队后金额不刷新</b>的游戏 bug 修正。
    ///
    /// <para><b>症状</b>：点选另一个车队行后，绿色高亮跟着走了，但「总价值」还停在上一个
    /// 车队的数字上，「剩余资金」也不扣减；而确认购买时扣的钱是对的。</para>
    ///
    /// <para><b>成因（IL 实证，0.34.2）</b>：面板把「选中的车队」和「贡献类型」分成两个字段维护 ——
    /// <c>selectedConvoy</c>（ConvoyGroup）与 <c>contributionType</c>（int，0=未选 / 1=资金 / 2=车辆）。</para>
    /// <list type="bullet">
    /// <item><c>ContributeToFaction::SelectConvoy</c> 只写 <c>selectedConvoy</c> 再调 <c>CheckValues</c>，
    /// <b>不会</b>把 <c>contributionType</c> 置成 2（置 2 的是 <c>SetContributeVehicles</c> 那条页签路径）。</item>
    /// <item><c>CheckValues</c> 的车辆分支被 <c>contributionType == 2</c> 守着：
    /// 只有等于 2 才写 <c>giveVehicleValue</c>（总价值），车价也只在那个分支里读。</item>
    /// <item><c>CalculateFunds</c> 同样只在 <c>contributionType == 2 &amp;&amp; selectedConvoy != null</c>
    /// 时才从余额里减车价，否则「剩余资金」就等于当前资金。</item>
    /// <item>而 <c>PurchaseConvoy</c> <b>只读 <c>selectedConvoy</c></b>，与 <c>contributionType</c> 无关。</item>
    /// </list>
    /// <para>于是只要 <c>contributionType</c> 因为 <c>ResetValues</c>（OnEnable）或 <c>CancelContribution</c>
    /// 被重置成 0/1，而车队行仍然可点，就会出现「显示不动、扣款正常」的错位 —— 正是实测截图里的状态
    /// （总价值仍是上一个车队的 -$9.96m，剩余资金等于当前资金）。</para>
    ///
    /// <para><b>实测复现（0.34.2 实机探针）</b>：面板<b>首次</b>打开时页签会自动走一遍
    /// <c>SetContributeVehicles</c>（type=2），第一次点行一切正常；<b>再次</b>打开时
    /// <c>OnEnable → ResetValues</c> 把 type 打回 0，而页签已在选中态、不会重发
    /// <c>onValueChanged</c>，于是 <c>SetContributeVehicles</c> 不再被调用 —— 此时点行就是
    /// type=0 进 <c>CheckValues</c>，总价值不动、剩余资金不减。这就是"第一次好、之后坏"的来源。</para>
    ///
    /// <para><b>对策</b>：<c>SelectConvoy</c> 前置把 <c>contributionType</c> 修正为 2。
    /// 随后的 <c>CheckValues</c> 就会走进车辆分支，总价值与剩余资金一次算对。
    /// 这是纯状态修正，不改任何算法；类型已经是 2 时零改动（一次字段读取即返回）。</para>
    ///
    /// <para><b>职责边界</b>：本类只含补丁方法，helper 一律不得留在这里
    /// （Harmony 分析器会把类内所有方法当候选补丁检查）。</para>
    /// </summary>
    [HarmonyPatch]
    internal static class ContributePanelPatches
    {
        /// <summary>配置开关（启动时由 ModSettings 接线）。</summary>
        internal static bool Enabled = true;

        /// <summary><c>contributionType</c> 的「车辆」取值（IL：<c>SetContributeVehicles</c> 里 <c>ldc.i4.2</c>）。</summary>
        private const int VehicleMode = 2;

        private static FieldInfo _typeField;   // int contributionType
        private static bool _reported;

        private static IEnumerable<MethodBase> TargetMethods()
        {
            Type type = AccessTools.TypeByName("ContributeToFaction");
            if (type == null)
            {
                Diagnostics.Log.Info("[捐款面板] 未找到 ContributeToFaction，补丁空转");
                yield break;
            }

            _typeField = AccessTools.Field(type, "contributionType");
            if (_typeField == null)
            {
                Diagnostics.Log.Info("[捐款面板] 未找到 contributionType 字段，补丁空转");
                yield break;
            }

            MethodInfo target = null;
            foreach (MethodInfo m in AccessTools.GetDeclaredMethods(type))
            {
                if (m.Name != "SelectConvoy" || m.GetParameters().Length != 1) continue;
                target = m;
                break;
            }

            if (target == null)
            {
                Diagnostics.Log.Info("[捐款面板] 未找到 SelectConvoy(ConvoyGroup)，补丁空转");
                yield break;
            }

            yield return target;
        }

        [HarmonyPrefix]
        private static void Prefix(object __instance)
        {
            if (!Enabled || _typeField == null) return;

            try
            {
                // 字段类型是枚举 ContributionType（{ None, Funds, Vehicles }，底层 int），
                // 取值必须走 Convert.ToInt32：装箱后的枚举值 `is int` 恒为 false，
                // 用过 `is int` 判断会让「已经是车辆模式」也照改一遍，日志还会误报成需要修正。
                object current = _typeField.GetValue(__instance);
                int mode;
                try { mode = Convert.ToInt32(current); }
                catch { mode = -1; }

                if (mode == VehicleMode) return;

                _typeField.SetValue(
                    __instance, Enum.ToObject(_typeField.FieldType, VehicleMode));

                // 只在第一次真正修正时留一行日志：这是诊断用证据，不是每点必刷的噪声。
                if (!_reported)
                {
                    _reported = true;
                    Diagnostics.Log.Info(
                        "[捐款面板] 选择车队时贡献类型为 " + mode + "（None=0 / Funds=1 / Vehicles=2），"
                        + "已修正为 " + VehicleMode + "：总价值与剩余资金将随选择刷新。");
                }
            }
            catch (Exception ex)
            {
                Diagnostics.Log.Debug("[捐款面板] prefix 异常（忽略）: " + ex.Message);
            }
        }
    }
}
