using System.Linq;
using DuckovCustomModel.Managers;
using HarmonyLib;

namespace DuckovCustomModel.VRM
{
    /// <summary>
    /// 模组入口。
    ///
    /// 启动顺序（每一步都不能乱）：
    ///   1. 加载 vrmshaders AssetBundle —— MToon10 等 Shader 运行时拿不到，必须靠 AB；
    ///   2. 打上 DCM 的注入补丁（把 .vrm 伪装成 DCM 能识别的模型包）；
    ///   3. <b>只读 GLB 头部</b>扫描 <c>ModConfigs/DuckovCustomModel.VRM/VRM</c>，
    ///      写出 bundleinfo.json + 预览图 —— 这一步不加载任何模型本体；
    ///   4. 打开源目录热重载（增删改自动生效，不用重启）；
    ///   5. 让 DCM 刷新一次模型列表（此时列表已经有名字和头像了）；
    ///   6. 后台预热当前存档里已经选中的模型。
    ///
    /// 模型本体只会在「用户选中它」或「上面第 6 步预热」时由 UniVRM 真正加载，
    /// 加载完缓存在内存里，换回同一个模型时直接命中。
    /// </summary>
    public class ModBehaviour : Duckov.Modding.ModBehaviour
    {
        private const string HarmonyId = "com.kagurakoishi.duckovcustommodel.vrm";

        /// <summary>VRM 扩展模组自身的版本号（独立于 DCM 本体的版本）。</summary>
        public const string ModVersion = "0.1.2";

        private void Awake()
        {
            VrmShaderLoader.Initialize();

            if (!Harmony.HasAnyPatches(HarmonyId))
            {
                var harmony = new Harmony(HarmonyId);
                harmony.PatchAll(typeof(ModBehaviour).Assembly);
            }

            VrmLog.Detail($"Harmony 补丁已应用；VRM 源目录: {VrmModPath.SourceDirectory}");

            // 配置先落地一份模板，用户才知道有哪些开关可以掰。
            var options = VrmConfig.Current;

            VrmCatalog.Refresh();
            var ids = VrmCatalog.Entries.Select(e => e.ModelId).ToList();
            VrmLog.Info($"VRM 模组就绪 v{ModVersion}：{ids.Count} 个模型（按需加载，HashMode={options.HashMode}）");

            VrmSourceWatcher.Ensure();

            if (ids.Count > 0) ModelListManager.RefreshModelList(ids);

            if (options.PreloadSelected) VrmModelRegistry.PreloadSelected();
        }
    }
}
