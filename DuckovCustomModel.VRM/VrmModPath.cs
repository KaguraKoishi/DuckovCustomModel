using System.IO;
using DuckovCustomModel.Managers;

namespace DuckovCustomModel.VRM
{
    /// <summary>
    /// VRM 模组的目录约定：<c>ModConfigs/DuckovCustomModel.VRM/VRM</c>。
    /// 把 .vrm 文件丢进去，模组会自动把它们注册成 DCM 能识别的模型。
    /// </summary>
    public static class VrmModPath
    {
        private const string ModFolderName = "DuckovCustomModel.VRM";
        private const string VrmFolderName = "VRM";

        private static string? _modConfigDirectory;

        /// <summary>本模组的配置目录（放一个 verbose.txt 可开启细节日志）。</summary>
        public static string ModConfigDirectory
        {
            get
            {
                if (_modConfigDirectory == null)
                {
                    var dcmConfigRoot = ConfigManager.ConfigBaseDirectory;
                    var modConfigsRoot = Path.GetDirectoryName(dcmConfigRoot) ?? dcmConfigRoot;
                    _modConfigDirectory = Path.GetFullPath(Path.Combine(modConfigsRoot, ModFolderName));
                }

                return _modConfigDirectory;
            }
        }

        /// <summary>VRM 源文件目录。</summary>
        public static string SourceDirectory => Path.Combine(ModConfigDirectory, VrmFolderName);

        /// <summary>DCM 扫描模型包的根目录。</summary>
        public static string PackagesRoot => ModelManager.ModelsDirectory;
    }
}
