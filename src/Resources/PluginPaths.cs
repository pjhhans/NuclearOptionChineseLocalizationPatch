using System;
using System.IO;
using System.Reflection;

namespace NuclearOptionChineseLocalizationPatch.Resources
{
    /// <summary>
    /// 插件数据文件的位置。
    ///
    /// <para>以 <b>DLL 自身所在目录</b>为基准而不是 BepInEx 的 <c>PluginPath</c>：
    /// 后者在某些加载顺序下尚未初始化，而且若用户把插件放在子目录里，
    /// 以 DLL 位置为准才能找到同目录的数据文件。</para>
    /// </summary>
    internal static class PluginPaths
    {
        private static string _baseDir;

        /// <summary>插件目录（DLL 所在目录，也是数据文件所在目录）。</summary>
        internal static string BaseDir
        {
            get
            {
                if (_baseDir != null) return _baseDir;
                try
                {
                    string location = Assembly.GetExecutingAssembly().Location;
                    _baseDir = string.IsNullOrEmpty(location)
                        ? Path.Combine(BepInEx.Paths.PluginPath, "NuclearOptionChineseLocalizationPatch")
                        : Path.GetDirectoryName(location);
                }
                catch
                {
                    _baseDir = Path.Combine(BepInEx.Paths.PluginPath, "NuclearOptionChineseLocalizationPatch");
                }
                return _baseDir;
            }
        }

        internal static string TableFile => Path.Combine(BaseDir, "translation.json");
        internal static string ExclusionsFile => Path.Combine(BaseDir, "exclusions.json");
        internal static string ScopesDir => Path.Combine(BaseDir, "scopes");
        internal static string FontFile => Path.Combine(BaseDir, "font.ttf");

        /// <summary>数据目录是否就绪。缺文件时给出可操作的提示，而不是抛异常。</summary>
        internal static bool IsReady(out string missing)
        {
            missing = null;
            if (!File.Exists(TableFile)) { missing = TableFile; return false; }
            return true;
        }

        internal static string Describe()
        {
            return "插件目录: " + BaseDir;
        }
    }
}
