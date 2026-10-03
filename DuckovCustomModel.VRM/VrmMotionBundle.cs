using System;
using System.IO;
using UnityEngine;

namespace DuckovCustomModel.VRM
{
    /// <summary>
    /// vrmmotion 动画 AssetBundle 的唯一加载点（与 <see cref="VrmShaderBundle"/> 同一套模式）。
    ///
    /// AB 内含 AnimatorController + 动画片段，放在本 DLL 同目录即可启用动画。
    ///
    /// 用途：VRM 是运行时加载的，模型实例上的 Animator 没有 controller；
    /// 官方 DCM 的动画驱动（Running/Moving/Dashing/MoveSpeed 等参数）
    /// 只对「实例上挂了 controller 的 Animator」生效 —— 这里把包里的 controller 补上去。
    ///
    /// 动画是可选功能：包不存在或损坏时安静跳过（只报一次），不影响模型显示。
    /// </summary>
    internal static class VrmMotionBundle
    {
        public const string FileName = "vrmmotion";

        private static AssetBundle? _bundle;
        private static RuntimeAnimatorController? _controller;
        private static bool _controllerResolved;

        public static string BundlePath =>
            Path.Combine(
                Path.GetDirectoryName(typeof(VrmMotionBundle).Assembly.Location) ?? string.Empty,
                FileName);

        /// <summary>包里的第一个 AnimatorController；不存在 / 损坏时为 null（进程内只尝试一次）。</summary>
        public static RuntimeAnimatorController? Controller
        {
            get
            {
                if (_controllerResolved) return _controller;
                _controllerResolved = true;

                var path = BundlePath;
                if (!File.Exists(path))
                {
                    VrmLog.Info($"未找到动画包 {FileName}（动画功能跳过）。把 vrmmotion 动画包放到本模组目录即可启用。");
                    return null;
                }

                try
                {
                    _bundle = AssetBundle.LoadFromFile(path);
                }
                catch (Exception e)
                {
                    VrmLog.Error($"动画包加载异常: {e.Message}");
                    return null;
                }

                if (_bundle == null)
                {
                    VrmLog.Error($"动画包加载失败: {path}");
                    return null;
                }

                var controllers = _bundle.LoadAllAssets<RuntimeAnimatorController>();
                if (controllers.Length == 0)
                {
                    VrmLog.Error($"动画包里没有 AnimatorController（{path}），请重新导出。");
                    return null;
                }

                _controller = controllers[0];
                var clips = _bundle.LoadAllAssets<AnimationClip>().Length;
                VrmLog.Info($"动画包已就绪: {controllers[0].name}（{clips} 个动画片段）");
                return _controller;
            }
        }
    }
}
