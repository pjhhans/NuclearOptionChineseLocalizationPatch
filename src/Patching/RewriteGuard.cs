using System;
using System.Collections.Generic;
using UnityEngine;

namespace NuclearOptionChineseLocalizationPatch.Patching
{
    /// <summary>
    /// 防回写守护。
    ///
    /// <para>有的 HUD 文本是游戏每帧重新赋值的（<c>Refresh()</c> 里的
    /// <c>if (x.text != target) x.text = target;</c>）。补丁层已经把「读回值还原成原文」
    /// 当作第一道防线，但仍有绕过 <c>set_text</c> 直接写字段的路径 —— 那些赋值我们收不到通知。</para>
    ///
    /// <para>于是这里做第二道防线：记下"哪个组件、原文是什么、我们写的中文是什么"，
    /// 每帧检查一次；发现被改回原文就补回中文。<b>但拉锯必须有个尽头</b> ——
    /// 若同一组件在 1 秒内被补回超过 5 次，说明它处在游戏的高频回写循环里，
    /// 此时主动放弃该组件，宁可显示英文，也不让每帧的读写拖垮帧率。</para>
    /// </summary>
    internal static class RewriteGuard
    {
        private sealed class Tracked
        {
            internal Component Component;
            internal string Original;
            internal string Translation;
            internal float WindowStart;
            internal int Rewrites;
        }

        private static readonly List<Tracked> Entries = new List<Tracked>(256);
        private static readonly HashSet<int> GivenUp = new HashSet<int>();

        /// <summary>跟踪条目上限。超出后淘汰最旧的，避免长时间游玩导致内存持续增长。</summary>
        private const int MaxEntries = 4000;

        /// <summary>同一窗口内允许的补回次数。超过即判定该组件不可控。</summary>
        private const int MaxRewritesPerWindow = 5;

        private const float WindowSeconds = 1f;

        /// <summary>全局开关，供排障时临时关闭防回写逻辑。</summary>
        internal static bool Disabled { get; set; }
        internal static int TrackedCount => Entries.Count;
        internal static int GivenUpCount => GivenUp.Count;

        internal static void Clear()
        {
            Entries.Clear();
            GivenUp.Clear();
        }

        /// <summary>记录一次成功的翻译，供后续回写检测。</summary>
        internal static void Track(Component comp, string original, string translation)
        {
            if (Disabled || comp == null || string.IsNullOrEmpty(translation)) return;
            if (!PatchHelpers.IsSupported(comp)) return;

            int id = comp.GetInstanceID();
            if (GivenUp.Contains(id)) return;

            for (int i = 0; i < Entries.Count; i++)
            {
                // Unity 的 == 重载会把已销毁对象判为 null，这里正好当作"还活着"的依据
                if (Entries[i].Component == comp)
                {
                    Entries[i].Original = original;
                    Entries[i].Translation = translation;
                    return;
                }
            }

            if (Entries.Count >= MaxEntries) Entries.RemoveAt(0);
            Entries.Add(new Tracked
            {
                Component = comp,
                Original = original,
                Translation = translation,
                WindowStart = Time.realtimeSinceStartup,
            });
        }

        /// <summary>每帧调用一次。</summary>
        internal static void Tick()
        {
            if (Disabled || Entries.Count == 0) return;
            float now = Time.realtimeSinceStartup;

            for (int i = Entries.Count - 1; i >= 0; i--)
            {
                Tracked entry = Entries[i];

                if (entry.Component == null)          // 组件已销毁
                {
                    Entries.RemoveAt(i);
                    continue;
                }

                string current = PatchHelpers.Read(entry.Component);
                if (current == null)
                {
                    Entries.RemoveAt(i);
                    continue;
                }

                if (string.Equals(current, entry.Translation, StringComparison.Ordinal)) continue;
                // 既不是我们的译文也不是原文 —— 说明游戏换了内容，让正常流程去处理。
                if (!string.Equals(current, entry.Original, StringComparison.Ordinal)) continue;

                if (now - entry.WindowStart > WindowSeconds)
                {
                    entry.WindowStart = now;
                    entry.Rewrites = 0;
                }
                entry.Rewrites++;

                if (entry.Rewrites > MaxRewritesPerWindow)
                {
                    Diagnostics.Log.Debug(
                        "放弃补译（回写过频）：" + PatchHelpers.ScopeOf(entry.Component));
                    GivenUp.Add(entry.Component.GetInstanceID());
                    Entries.RemoveAt(i);
                    continue;
                }

                PatchHelpers.Write(entry.Component, entry.Translation);
            }
        }
    }
}
