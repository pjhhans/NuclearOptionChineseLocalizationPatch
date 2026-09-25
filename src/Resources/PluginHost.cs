using System;
using NuclearOptionChineseLocalizationPatch.Diagnostics;
using UnityEngine;

namespace NuclearOptionChineseLocalizationPatch.Resources
{
    /// <summary>
    /// 承载所有「必须每帧活着」的逻辑：热键、兜底扫描、防回写驱动、设置窗口。
    ///
    /// <para><b>为什么不直接写在插件类里。</b>BepInEx 在「首个真实场景就绪之前」就加载插件，
    /// 此时创建的一切 GameObject —— 包括 BepInEx 自己的管理器对象 —— 都会在首个真实场景
    /// 加载时被 Unity <b>一并销毁</b>，即使调用过 <c>DontDestroyOnLoad</c>。
    /// 挂在被销毁组件上的 <c>Update</c>/<c>OnGUI</c> 从此永不执行，于是热键、兜底扫描、
    /// 防回写补偿、设置窗口全部静默失效 —— 表现为「按键没反应 + 界面仍是英文」，
    /// 而日志里看不出任何异常（初始化那几行照常打印，因为它们在 <c>Awake</c> 里）。</para>
    ///
    /// <para><b>对策</b>：这些事情挪到这个延迟创建、可被反复重建的独立宿主上。
    /// 重建触发点三处互为备份：首次 <c>Awake</c> 末尾试建、每次场景加载事件、
    /// 以及每次翻译命中时的看门狗（见 <see cref="Patching.PatchHelpers"/>）。</para>
    ///
    /// <para><b>数据不放在本类。</b>宿主会被销毁重建，任何存在它字段里的状态都会丢；
    /// 词表等一律挂在 <see cref="LocalizationPlugin"/> 的静态属性上。</para>
    /// </summary>
    internal sealed class PluginHost : MonoBehaviour
    {
        private static PluginHost _instance;
        private static float _lastEnsureTime = -999f;

        /// <summary>重建防抖间隔。看门狗挂在高频路径上，未命中时必须只花一次静态比较。</summary>
        private const float EnsureDebounceSeconds = 1f;

        /// <summary>空闲退避的上限：间隔最多放大到基础的 2³ = 8 倍。</summary>
        private const int MaxBackoffShift = 3;

        /// <summary>鼠标按住期间轮询「松手了没」的间隔。</summary>
        private const float DragRecheckSeconds = 0.25f;

        private float _nextScanTime;

        /// <summary>连续「空手而归」的兜底扫描次数。用于空闲退避。</summary>
        private int _idleScans;

        /// <summary>上次读到的扫描间隔滑块值，用来检测用户手动改动（改动即复位退避）。</summary>
        private float _lastSliderValue = -1f;

        internal static bool Alive => _instance != null;

        /// <summary>场景加载 / 补丁路径翻到新东西时调用：退避立即复位，下次扫描按基础间隔来。</summary>
        internal static void ResetScanBackoff()
        {
            if (_instance != null) _instance._idleScans = 0;
        }

        /// <summary>
        /// 补丁路径真的翻译了什么（新内容出现了）时调用：退避复位，并让兜底扫描尽快跟上一遍
        /// —— 那些绕过 setter、只有扫描才够得着的文本不用等完整退避周期。
        /// </summary>
        internal static void NotifyTranslationActivity()
        {
            if (_instance == null) return;
            _instance._idleScans = 0;
            float baseInterval = SettingsWindow.ScanInterval;
            if (baseInterval <= 0f) baseInterval = 1f;
            float soon = Time.realtimeSinceStartup + baseInterval;
            if (soon < _instance._nextScanTime) _instance._nextScanTime = soon;
        }

        /// <summary>高频路径入口，带防抖。宿主已活着时只做一次存活判定。</summary>
        internal static void Ensure() => Ensure(false);

        /// <summary>
        /// 确保宿主存在：不存在就新建，存在时零成本返回。
        ///
        /// <paramref name="force"/> 为 true 时跳过防抖 —— 场景加载事件是宿主最重要的重建点，
        /// 那一次绝不能被防抖挡掉（挡掉之后就再没有别的东西会主动来重建它了）。
        /// </summary>
        internal static void Ensure(bool force)
        {
            // 必须走 UnityEngine.Object 重载的 ==（不要用 ReferenceEquals）：
            // 只比托管引用的话，「Unity 侧已销毁、而 OnDestroy 恰好没跑到」的实例会
            // 永远被判成"存在"，宿主再也重建不起来 —— 正是这次要修的那个故障的变体。
            // 该运算符同时覆盖 null / 存活 / 已销毁三种状态，且只做一次原生指针比较，开销可忽略。
            if (_instance != null) return;

            if (!force && Time.realtimeSinceStartup - _lastEnsureTime < EnsureDebounceSeconds) return;
            _lastEnsureTime = Time.realtimeSinceStartup;
            _instance = null; // 清掉可能残留的"已销毁"引用，避免下面 AddComponent 后仍被判成旧实例

            try
            {
                var go = new GameObject("NuclearOptionChinese_Host");
                UnityEngine.Object.DontDestroyOnLoad(go);
                _instance = go.AddComponent<PluginHost>();
                RuntimeStatus.HostBuilds++;
                RuntimeStatus.HostAlive = true;
                Log.Info("逐帧宿主已就绪（热重载 / 兜底扫描 / 防回写 / F11 设置窗口生效）。");
            }
            catch (Exception ex)
            {
                Log.Warn("逐帧宿主创建失败，热键与兜底扫描将不可用：" + ex.Message);
            }
        }

        /// <summary>
        /// 扫描间隔由窗口的滑块实时改写（<see cref="SettingsWindow.ScanInterval"/>），
        /// 每帧读取，所以改完立即生效，无需写回任何字段。
        /// </summary>
        private void Update()
        {
            RuntimeStatus.HostAlive = true;
            HandleHotkeys();
            HandleScan();
            LocalizationPlugin.MissLog?.FlushIfDue();
        }

        /// <summary>
        /// 防回写补偿必须等游戏本轮 Update 跑完再补，否则抢不过 HUD 自己的刷新循环
        /// （本帧补完、下帧又被覆盖，表现为闪烁）。
        /// </summary>
        private void LateUpdate()
        {
            Patching.RewriteGuard.Tick();
        }

        /// <summary>
        /// IMGUI 窗口。放在宿主上而不是插件对象上 —— 插件对象活不过第一个真实场景，
        /// 挂在那里的话窗口按一次就再也打不开了。
        /// </summary>
        private void OnGUI()
        {
            SettingsWindow.Draw();
        }

        private static void HandleHotkeys()
        {
            if (KeyDown(LocalizationPlugin.WindowKey)) SettingsWindow.Toggle();
            if (KeyDown(LocalizationPlugin.ReloadKey))
            {
                Log.Info("收到直接热重载请求。");
                LocalizationPlugin.ReloadFromDisk();
            }
        }

        private static bool KeyDown(KeyCode key)
        {
            return key != KeyCode.None && Input.GetKeyDown(key);
        }

        private void HandleScan()
        {
            float slider = SettingsWindow.ScanInterval;
            if (slider <= 0f) slider = 1f;
            if (_lastSliderValue != slider)
            {
                // 用户手动改了滑块：按他的意图来，退避立即清零。
                _lastSliderValue = slider;
                _idleScans = 0;
            }

            // ★ 拖动视角 / 拖拽 UI 时不扫描。兜底扫描用的是 FindObjectsOfTypeAll
            //   （场景 + 所有已加载资产的全内存枚举），一次几毫秒到几十毫秒；
            //   正在拖自由视角时，它表现为画面每隔一个扫描间隔跳一下。
            //   按住期间只做这三次廉价的按键轮询，松开后很快自动补扫，
            //   拖动期间新出现的文本也只延迟到松手那一刻。
            if (Input.GetMouseButton(0) || Input.GetMouseButton(1) || Input.GetMouseButton(2))
            {
                if (Time.realtimeSinceStartup < _nextScanTime) return;
                _nextScanTime = Time.realtimeSinceStartup + DragRecheckSeconds;
                return;
            }

            // 空闲退避：连续空手扫描（什么都没翻到）就把间隔逐次翻倍，上限 ×8。
            // 界面稳定时从「每秒一次全内存枚举」降到最多 8 秒一次；
            // 任何真实翻译（补丁路径 NotifyTranslationActivity / 扫描本身翻到东西 /
            // 场景加载）都会把退避清零，新文本 1 秒内变中文的语义不变。
            float interval = slider * (1 << Math.Min(_idleScans, MaxBackoffShift));
            if (Time.realtimeSinceStartup < _nextScanTime) return;
            _nextScanTime = Time.realtimeSinceStartup + interval;

            int changes = LocalizationPlugin.ScanScene();
            _idleScans = changes > 0 ? 0 : _idleScans + 1;
        }

        private void OnDestroy()
        {
            if (ReferenceEquals(_instance, this)) _instance = null;
            RuntimeStatus.HostAlive = false;
            SettingsWindow.Hide();
            Log.Debug("逐帧宿主被销毁，将在下次需要时重建。");
        }
    }
}
