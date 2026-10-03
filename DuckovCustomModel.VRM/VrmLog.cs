using System.IO;
using UnityEngine;

namespace DuckovCustomModel.VRM
{
    /// <summary>
    /// 统一日志出口。
    ///
    /// 默认只输出「有信息量」的一行式日志；细节日志（逐材质、逐 Pass、逐关节、渲染管线诊断）
    /// 一律走 <see cref="Detail"/>，只有开启 verbosity 时才输出，避免正常游戏时刷屏。
    ///
    /// 开启方式（二选一）：
    ///   * <c>ModConfigs/DuckovCustomModel.VRM/verbose.txt</c> 存在；
    ///   * <c>vrm.json</c> 里 <c>"Verbose": true</c>。
    /// </summary>
    internal static class VrmLog
    {
        private static bool? _verbose;

        public static bool Verbose
        {
            get
            {
                if (_verbose == null) _verbose = Resolve();
                return _verbose.Value;
            }
        }

        private static bool Resolve()
        {
            try
            {
                if (VrmConfig.Current.Verbose == true) return true;
            }
            catch
            {
                // 配置读失败不影响日志判断
            }

            try
            {
                return File.Exists(Path.Combine(VrmModPath.ModConfigDirectory, "verbose.txt"));
            }
            catch
            {
                return false;
            }
        }

        public static void Info(string message) => Debug.Log($"[VRM] {message}");

        public static void Warn(string message) => Debug.LogWarning($"[VRM] {message}");

        public static void Error(string message) => Debug.LogError($"[VRM] {message}");

        public static void Detail(string message)
        {
            if (Verbose) Debug.Log($"[VRM] {message}");
        }
    }
}
