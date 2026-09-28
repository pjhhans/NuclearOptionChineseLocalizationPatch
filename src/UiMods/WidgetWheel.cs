using UnityEngine;

namespace NuclearOptionChineseLocalizationPatch.UiMods
{
    /// <summary>
    /// 滚轮轮询公共件：信息卡描述滚动视图与挂架下拉弹出列表共用。
    ///
    /// <para><b>为什么是轮询而不是监听事件。</b>游戏的输入模块不通过 EventSystem 派发
    /// <c>OnScroll</c>，这正是「原生滚轮失效、只能手拉」的根因假设。两处都改为每帧
    /// 直接读指针位置与 <c>Input.mouseScrollDelta</c>，绕开事件系统的派发差异。</para>
    ///
    /// <para><b>降级。</b>新 Input System 环境没有 legacy 轮询入口，首次调用会抛
    /// <see cref="System.InvalidOperationException"/>；届时置内部标志一次性停用，
    /// 之后恒返回 0（静默降级，不刷异常）。</para>
    /// </summary>
    internal static class WidgetWheel
    {
        private static bool _inputOk = true;

        /// <summary>
        /// 指针落在 <paramref name="rect"/> 内时返回滚轮方向：<c>+1</c>=向上滚、
        /// <c>-1</c>=向下滚、<c>0</c>=无输入或指针在控件之外。
        /// 指针不在控件上时恒返回 0 —— 保证不劫持其它 UI 的滚动。
        /// </summary>
        internal static int Poll(RectTransform rect)
        {
            if (rect == null || !_inputOk)
                return 0;
            try
            {
                var canvas = rect.GetComponentInParent<Canvas>();
                var cam = canvas != null && canvas.renderMode != RenderMode.ScreenSpaceOverlay
                    ? canvas.worldCamera
                    : null;
                if (!RectTransformUtility.RectangleContainsScreenPoint(rect, Input.mousePosition, cam))
                    return 0;

                float wheel = Input.mouseScrollDelta.y;
                if (wheel > 0.001f)
                    return 1;
                if (wheel < -0.001f)
                    return -1;
                return 0;
            }
            catch (System.InvalidOperationException)
            {
                _inputOk = false; // 新 Input System 环境无 legacy 轮询，静默停用
                return 0;
            }
        }
    }
}
