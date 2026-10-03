using System;
using System.IO;
using System.Text;
using Newtonsoft.Json;

namespace DuckovCustomModel.VRM
{
    /// <summary>
    /// 角限位模式。数值即 <c>vrm.json</c> 里写的数字，改动数值等于改行为，别插新项。
    /// </summary>
    public enum VrmAngleLimitMode
    {
        /// <summary>0 = None：不加任何角限位，一切按模型原始参数摆。</summary>
        None = 0,

        /// <summary>1 = Breast：只给名字命中胸部关键词的动骨加限位，其它动骨不动。</summary>
        Breast = 1,

        /// <summary>2 = All：所有「模型自己没配过限位」的动骨都加限位。</summary>
        All = 2,
    }

    /// <summary>动骨（SpringBone）相关的可调项。</summary>
    public sealed class VrmSpringBoneOptions
    {
        /// <summary>
        /// 角限位模式（VRM 1.0 的 <c>VRMC_springBone_limit</c> Cone 圆锥限位）。
        /// 见 <see cref="VrmAngleLimitMode"/>。模型自己配过限位的关节一律跳过，不覆盖原作。
        /// </summary>
        public VrmAngleLimitMode AngleLimitMode { get; set; } = VrmAngleLimitMode.All;

        /// <summary>胸部动骨允许偏离静止方向的最大角度（度）。越小越"硬"。</summary>
        public float MaxSwingAngleDeg { get; set; } = 25f;

        /// <summary>
        /// 非胸部动骨（头发 / 裙子 / 尾巴 / 饰品…）在 <see cref="VrmAngleLimitMode.All"/> 下的最大偏角（度）。
        /// 头发一般要松一点，太小会显得僵硬。
        /// </summary>
        public float OtherMaxSwingAngleDeg { get; set; } = 60f;

        /// <summary>判定为「胸部动骨」的关键词（同时匹配弹簧名与关节骨骼名，忽略大小写）。</summary>
        public string[] BustKeywords { get; set; } =
        {
            "breast", "bust", "boob", "boobie", "tit", "nipple", "mune", "oppai", "chichi",
            "おっぱい", "ちち", "バスト", "胸", "乳", "欧派", "欧拜",
        };

        /// <summary>是否给胸部动骨自动补一个「胸口球体碰撞体」，避免胸骨向身体内侧穿模。</summary>
        public bool ChestColliderEnabled { get; set; } = true;

        /// <summary>胸口碰撞体半径 = （胸口骨骼 → 胸部动骨根）距离 × 该系数。</summary>
        public float ChestColliderRadiusScale { get; set; } = 0.5f;

        /// <summary>命中这些关键词的动骨会被完全冻结（等价于"不让它动"），用于个别模型兜底。</summary>
        public string[] FreezeKeywords { get; set; } = Array.Empty<string>();

        /// <summary>
        /// 旧版（配置 v1）字段：只控制胸部限位开/关。读取时用于迁移到 <see cref="AngleLimitMode"/>。
        /// </summary>
        [JsonProperty("AngleLimitEnabled", NullValueHandling = NullValueHandling.Ignore)]
        public bool? LegacyAngleLimitEnabled { get; set; }
    }

    /// <summary>定位锚点（挂点）相关可调项。</summary>
    public sealed class VrmLocatorOptions
    {
        /// <summary>是否自动生成 9 个内部 + 2 个外部定位锚点。</summary>
        public bool AutoGenerate { get; set; } = true;

        /// <summary>手工校正文件所在目录（相对模组配置目录）。</summary>
        public string OverrideDirectory { get; set; } = "Overrides";

        /// <summary>是否启用游戏内实时微调面板。</summary>
        public bool DebugPanelEnabled { get; set; } = true;

        /// <summary>微调面板热键。</summary>
        public string DebugPanelHotkey { get; set; } = "F9";

        /// <summary>头盔/面部锚点相对头顶的额外抬升比（占身高比例）。</summary>
        public float HeadTopPadding { get; set; } = 0f;
    }

    /// <summary>模组总配置，落在 <c>ModConfigs/DuckovCustomModel.VRM/vrm.json</c>。</summary>
    public sealed class VrmOptions
    {
        /// <summary>配置文件版本；由模组维护，升级时会按新模板重写一次。</summary>
        public int Version { get; set; } = VrmConfig.CurrentVersion;

        /// <summary>细节日志开关；留空则看 <c>verbose.txt</c> 是否存在。</summary>
        public bool? Verbose { get; set; }

        /// <summary>源目录里文件增删改时是否自动热重载（不重启游戏）。</summary>
        public bool HotReload { get; set; } = true;

        /// <summary>
        /// 变更检测方式：
        ///   * <c>meta</c>（默认）—— 比对文件大小 + 修改时间，秒级完成，适合几百 MB 的 VRM；
        ///   * <c>md5</c> —— 计算文件内容 MD5，最精确，但每次启动要完整读一遍所有模型文件。
        /// </summary>
        public string HashMode { get; set; } = "meta";

        /// <summary>最多同时缓存几个已加载的 VRM prefab（正在使用的不会被卸载）。</summary>
        public int MaxCachedModels { get; set; } = 2;

        /// <summary>启动后后台预加载当前已选中的 VRM 模型，避免切模型时等待。</summary>
        public bool PreloadSelected { get; set; } = true;

        public VrmLocatorOptions Locators { get; set; } = new();

        public VrmSpringBoneOptions SpringBone { get; set; } = new();

        public bool UseMd5 => string.Equals(HashMode, "md5", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 模组配置的唯一入口。首次运行会在模组目录写出一份<b>带注释</b>的模板，方便直接改。
    ///
    /// 文件是 JSONC（允许 <c>//</c> 与 <c>/* */</c> 注释）—— Newtonsoft 读取时会自动忽略注释，
    /// 所以可以放心在里面写说明。升级到新版本配置（<see cref="CurrentVersion"/>）时会按新模板重写一次，
    /// 已填的值会尽量带过去。
    /// </summary>
    internal static class VrmConfig
    {
        private const string FileName = "vrm.json";

        /// <summary>当前配置模板版本。加/改配置项时 +1，旧文件会在启动时自动重写成带注释的新模板。</summary>
        public const int CurrentVersion = 6;

        private static VrmOptions? _current;
        private static string? _path;

        public static string ConfigFilePath => _path ??= Path.Combine(VrmModPath.ModConfigDirectory, FileName);

        public static VrmOptions Current
        {
            get
            {
                if (_current != null) return _current;

                try
                {
                    var path = ConfigFilePath;
                    if (File.Exists(path))
                    {
                        var loaded = JsonConvert.DeserializeObject<VrmOptions>(File.ReadAllText(path));
                        _current = Normalize(loaded);

                        if (_current.Version < CurrentVersion)
                        {
                            var old = _current.Version;
                            _current.Version = CurrentVersion;
                            WriteTemplate(path, _current);
                            VrmLog.Info($"vrm.json 已从 v{old} 升级到 v{CurrentVersion}，" +
                                        "已重写为带注释的新模板（原有取值已尽量保留）");
                        }
                    }
                    else
                    {
                        _current = new VrmOptions();
                        WriteTemplate(path, _current);
                    }
                }
                catch (Exception e)
                {
                    VrmLog.Warn($"读取 {FileName} 失败，改用默认配置: {e.Message}");
                    _current = new VrmOptions();
                }

                return _current;
            }
        }

        /// <summary>配置有改动（热重载 / 用户手改）时调用，下次读取重新加载。</summary>
        public static void Reload()
        {
            _current = null;
        }

        private static VrmOptions Normalize(VrmOptions? options)
        {
            options ??= new VrmOptions();

            if (options.MaxCachedModels < 1) options.MaxCachedModels = 1;
            if (options.MaxCachedModels > 16) options.MaxCachedModels = 16;

            options.Locators ??= new VrmLocatorOptions();
            var lo = options.Locators;
            if (string.IsNullOrWhiteSpace(lo.OverrideDirectory)) lo.OverrideDirectory = "Overrides";
            if (string.IsNullOrWhiteSpace(lo.DebugPanelHotkey)) lo.DebugPanelHotkey = "F9";
            if (lo.HeadTopPadding < 0f || lo.HeadTopPadding > 0.5f) lo.HeadTopPadding = 0f;

            options.SpringBone ??= new VrmSpringBoneOptions();
            var sb = options.SpringBone;

            sb.BustKeywords ??= Array.Empty<string>();
            sb.FreezeKeywords ??= Array.Empty<string>();

            // 旧版只有「胸部限位开关」，迁移成模式：开 → Breast，关 → None。
            if (sb.LegacyAngleLimitEnabled.HasValue)
                sb.AngleLimitMode = sb.LegacyAngleLimitEnabled.Value
                    ? VrmAngleLimitMode.Breast
                    : VrmAngleLimitMode.None;

            if (!Enum.IsDefined(typeof(VrmAngleLimitMode), sb.AngleLimitMode))
            {
                VrmLog.Warn($"vrm.json 里的 AngleLimitMode={sb.AngleLimitMode} 不是 0/1/2，已按 2（All）处理");
                sb.AngleLimitMode = VrmAngleLimitMode.All;
            }

            if (sb.MaxSwingAngleDeg <= 0f || sb.MaxSwingAngleDeg > 180f) sb.MaxSwingAngleDeg = 25f;
            if (sb.OtherMaxSwingAngleDeg <= 0f || sb.OtherMaxSwingAngleDeg > 180f) sb.OtherMaxSwingAngleDeg = 60f;
            if (sb.ChestColliderRadiusScale <= 0f || sb.ChestColliderRadiusScale > 1.5f)
                sb.ChestColliderRadiusScale = 0.5f;

            return options;
        }

        #region 模板

        private static void WriteTemplate(string path, VrmOptions options)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path) ?? string.Empty);
                File.WriteAllText(path, BuildTemplateText(options), new UTF8Encoding(false));
                VrmLog.Detail($"已写出配置模板: {path}");
            }
            catch (Exception e)
            {
                VrmLog.Warn($"写出配置模板失败: {e.Message}");
            }
        }

        private static string BuildTemplateText(VrmOptions o)
        {
            var lo = o.Locators;
            var sb = o.SpringBone;

            var t = new StringBuilder();
            t.AppendLine("{");
            t.AppendLine("  // ==================== DuckovCustomModel.VRM 配置 ====================");
            t.AppendLine("  // 本文件支持 // 与 /* */ 注释，可以放心在下面写说明。");
            t.AppendLine("  // 删掉本文件，下次启动会重新生成一份默认模板。");
            t.AppendLine("  // VRM 源目录：ModConfigs/DuckovCustomModel.VRM/VRM —— 把 .vrm 丢进去即可自动识别。");
            t.AppendLine();
            t.AppendLine("  // 配置文件版本（由模组维护，勿手改；升级时会按新模板重写一次）。");
            t.AppendLine($"  \"Version\": {J(o.Version)},");
            t.AppendLine();
            t.AppendLine("  // 细节日志开关：true / false / null（null = 看同目录下有没有 verbose.txt）。");
            t.AppendLine($"  \"Verbose\": {J(o.Verbose)},");
            t.AppendLine();
            t.AppendLine("  // 源目录里文件增删改时是否自动热重载（不用重启游戏）。");
            t.AppendLine($"  \"HotReload\": {J(o.HotReload)},");
            t.AppendLine();
            t.AppendLine("  // 变更检测方式：\"meta\" = 文件大小 + 修改时间（快，推荐）；");
            t.AppendLine("  //               \"md5\" = 内容哈希（最准，但每次启动要完整读一遍所有模型文件）。");
            t.AppendLine($"  \"HashMode\": {J(o.HashMode)},");
            t.AppendLine();
            t.AppendLine("  // 最多同时缓存几个已加载的 VRM 模型（1~16）。正在被角色使用的那个永远不会被卸载。");
            t.AppendLine($"  \"MaxCachedModels\": {J(o.MaxCachedModels)},");
            t.AppendLine();
            t.AppendLine("  // 启动后在后台预热当前存档里已选中的 VRM 模型，切模型时就不用手等。");
            t.AppendLine($"  \"PreloadSelected\": {J(o.PreloadSelected)},");
            t.AppendLine();

            // ---------------- Locators ----------------
            t.AppendLine("  // ==================== 定位锚点（挂点） ====================");
            t.AppendLine("  // 枪 / 头盔 / 背包 / 弹字 等都挂在锚点上。锚点是 VRM 对应骨骼的子物体，");
            t.AppendLine("  // 跟随动画与身高一起缩放；游戏内按热键微调后点「保存」即固化为该校准。");
            t.AppendLine("  \"Locators\": {");
            t.AppendLine("    // 是否自动生成 9 个内部锚点 + 2 个外部锚点。");
            t.AppendLine($"    \"AutoGenerate\": {J(lo.AutoGenerate)},");
            t.AppendLine();
            t.AppendLine("    // 手工校正目录（相对本配置目录）。游戏内按热键调完点「保存」会写到");
            t.AppendLine("    // Overrides/<模型文件名>.json，只记录你改过的锚点。");
            t.AppendLine($"    \"OverrideDirectory\": {J(lo.OverrideDirectory)},");
            t.AppendLine();
            t.AppendLine("    // 游戏内实时微调面板（左右手 / 头部 / 背包等锚点边看边调）。");
            t.AppendLine($"    \"DebugPanelEnabled\": {J(lo.DebugPanelEnabled)},");
            t.AppendLine($"    \"DebugPanelHotkey\": {J(lo.DebugPanelHotkey)},");
            t.AppendLine();
            t.AppendLine("    // 头盔/面部锚点再往上抬多少（占身高的比例，0 = 正好在头顶）。");
            t.AppendLine($"    \"HeadTopPadding\": {J(lo.HeadTopPadding)}");
            t.AppendLine("  },");
            t.AppendLine();

            // ---------------- SpringBone ----------------
            t.AppendLine("  \"SpringBone\": {");
            t.AppendLine("    // ---------- 角限位（VRMC_springBone_limit 的 Cone 圆锥限位）----------");
            t.AppendLine("    // 把动骨方向夹在「静止方向」周围的圆锥里，是解决过度变形 / 穿模最有效的手段。");
            t.AppendLine("    // 只作用于「模型自己没配过限位」的关节；模型自带限位的原样保留。");
            t.AppendLine("    // VRM 0.x 模型同样生效（本项目统一走 VRM 1.0 运行时）。");
            t.AppendLine("    //   0 = None   —— 完全不加限位，一切按模型原始参数摆（最\"软\"）");
            t.AppendLine("    //   1 = Breast —— 只给名字命中 BustKeywords 的胸部动骨加限位，其它动骨不动（最保守）");
            t.AppendLine("    //   2 = All    —— 所有没配过限位的动骨都加：胸部用 MaxSwingAngleDeg，其余用 OtherMaxSwingAngleDeg");
            t.AppendLine($"    \"AngleLimitMode\": {J((int)sb.AngleLimitMode)},        // 0=None  1=Breast  2=All");
            t.AppendLine();
            t.AppendLine("    // 胸部动骨的最大偏角（度），用于 AngleLimitMode = 1 / 2。越小越\"硬\"，建议 15~35。");
            t.AppendLine($"    \"MaxSwingAngleDeg\": {J(sb.MaxSwingAngleDeg)},");
            t.AppendLine();
            t.AppendLine("    // 非胸部动骨（头发 / 裙子 / 尾巴 / 饰品…）在 AngleLimitMode = 2 时的最大偏角（度）。");
            t.AppendLine("    // 头发建议 45~80；嫌头发太僵硬就调大，或把 AngleLimitMode 改回 1。");
            t.AppendLine($"    \"OtherMaxSwingAngleDeg\": {J(sb.OtherMaxSwingAngleDeg)},");
            t.AppendLine();
            t.AppendLine("    // 判定为「胸部动骨」的关键词（同时匹配弹簧名与关节骨骼名，忽略大小写）。");
            t.AppendLine($"    \"BustKeywords\": {JArray(sb.BustKeywords, 4, 20)},");
            t.AppendLine();
            t.AppendLine("    // 是否给胸部动骨自动补一个「胸口球体碰撞体」，挡住往胸腔内部的穿模。");
            t.AppendLine($"    \"ChestColliderEnabled\": {J(sb.ChestColliderEnabled)},");
            t.AppendLine();
            t.AppendLine("    // 胸口碰撞体半径 =（胸口骨骼 → 胸部动骨根）距离 × 该系数。");
            t.AppendLine($"    \"ChestColliderRadiusScale\": {J(sb.ChestColliderRadiusScale)},");
            t.AppendLine();
            t.AppendLine("    // 命中这些关键词的动骨会被完全冻结（等价于\"不让它动\"），优先级高于上面的限位。默认空数组。");
            t.AppendLine($"    \"FreezeKeywords\": {JArray(sb.FreezeKeywords, 4, 22)}");
            t.AppendLine("  }");
            t.AppendLine("}");

            return t.ToString();
        }

        private static string J(object? value)
        {
            return JsonConvert.SerializeObject(value, Formatting.None);
        }

        /// <summary>把字符串数组写成一个 JSON 数组；一行放不下就折行，方便阅读与手改。</summary>
        private static string JArray(string[]? items, int indent, int startColumn)
        {
            if (items == null || items.Length == 0) return "[]";

            const int maxWidth = 100;
            var pad = new string(' ', indent);
            var sb = new StringBuilder();
            sb.Append('[');
            var column = startColumn + 1;

            for (var i = 0; i < items.Length; i++)
            {
                var element = J(items[i]);

                if (i > 0)
                {
                    sb.Append(',');
                    column++;
                }

                if (i > 0 && column + 1 + element.Length > maxWidth)
                {
                    sb.Append('\n').Append(pad);
                    column = indent;
                }
                else if (i > 0)
                {
                    sb.Append(' ');
                    column++;
                }

                sb.Append(element);
                column += element.Length;
            }

            sb.Append(']');
            return sb.ToString();
        }

        #endregion
    }
}
