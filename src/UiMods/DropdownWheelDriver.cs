using UnityEngine;
using UnityEngine.UI;

namespace NuclearOptionChineseLocalizationPatch.UiMods
{
    /// <summary>
    /// 下拉弹出列表的滚轮驱动（用户需求「武器选择下拉列表只能手拉」）：
    /// 挂在弹出列表根上，<c>Update</c> 通过 <see cref="WidgetWheel.Poll"/> 轮询指针位置与滚轮，
    /// <b>不依赖 EventSystem 派发 OnScroll</b>（游戏的输入模块不派发滚轮事件，这正是原生
    /// 滚轮失效的根因假设）。有 <see cref="ScrollRect"/> 走 <c>verticalNormalizedPosition</c>
    /// （拖动手感与滚动条联动），没有则回落到 <see cref="Scrollbar"/> 直接调 <c>value</c>。
    /// 弹出列表 Hide 时随根销毁，每次 Show 重新挂载，天然幂等。
    /// 指针不在列表上时不吃滚轮（不劫持其它 UI 的滚动）。
    /// </summary>
    internal sealed class DropdownWheelDriver : MonoBehaviour
    {
        internal RectTransform Popup;
        internal ScrollRect Scroll;
        internal Scrollbar Bar;      // ScrollRect 缺失时的回落

        private void Update()
        {
            // 步长按符号取固定比例（规避 mouseScrollDelta 量纲随平台/鼠标的不确定性）：
            // 每次滚轮事件走列表高的 ~12%，连滚累积。
            int wheel = WidgetWheel.Poll(Popup);
            if (wheel == 0)
                return;

            const float step = 0.12f;
            float delta = wheel > 0 ? step : -step;
            if (Scroll != null)
                Scroll.verticalNormalizedPosition = Mathf.Clamp01(Scroll.verticalNormalizedPosition + delta);
            else if (Bar != null)
                Bar.value = Mathf.Clamp01(Bar.value + delta); // 已规范化为 BottomToTop，value 大 = 顶端
        }
    }
}
