using System;
using System.IO;
using UnityEngine;

namespace DuckovCustomModel.VRM
{
    /// <summary>
    /// vrmshaders AssetBundle 的唯一加载点。
    ///
    /// 之前 VrmShaderLoader（初始化 Shader）和占位包（伪造 DCM 的模型包）各自
    /// <c>AssetBundle.LoadFromFile</c> 同一个文件。Unity 对「同一文件的重复加载」
    /// 会返回 null 并报错
    /// <c>The AssetBundle '...' can't be loaded because another AssetBundle with the
    /// same files is already loaded.</c>，而且失败方不会缓存结果 —— 于是 DCM 每次刷新
    /// 模型列表 / 目标设置面板都重试一次，每次都抛一整屏堆栈。
    ///
    /// 现在统一走这里：进程内只加载一次，谁用谁取。
    /// </summary>
    internal static class VrmShaderBundle
    {
        public const string FileName = "vrmshaders";

        private static AssetBundle? _bundle;
        private static bool _attempted;

        public static string BundlePath =>
            Path.Combine(
                Path.GetDirectoryName(typeof(VrmShaderBundle).Assembly.Location) ?? string.Empty,
                FileName);

        public static AssetBundle? Bundle
        {
            get
            {
                if (_attempted) return _bundle;
                _attempted = true;

                var path = BundlePath;
                if (!File.Exists(path))
                {
                    VrmLog.Error($"找不到 Shader AssetBundle: {path}");
                    return null;
                }

                try
                {
                    _bundle = AssetBundle.LoadFromFile(path);
                }
                catch (Exception e)
                {
                    VrmLog.Error($"Shader AssetBundle 加载异常: {e.Message}");
                    return null;
                }

                if (_bundle == null) VrmLog.Error($"Shader AssetBundle 加载失败: {path}");
                return _bundle;
            }
        }
    }
}
