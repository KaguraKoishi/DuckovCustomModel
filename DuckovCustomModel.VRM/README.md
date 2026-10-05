# DuckovCustomModel.VRM

[Escape from Duckov](https://store.steampowered.com/app/2427700/) 模组
[DuckovCustomModel（DCM）](../README.md) 的扩展：让玩家直接使用 **VRM 格式**的角色模型。

DCM 本体的模型包需要用 Unity 打包成 AssetBundle；本扩展改为**运行时加载 `.vrm` 文件** ——
把文件丢进目录就能用，无需任何打包步骤。

## 功能

- **拖入即用**：`.vrm` 放到 `ModConfigs/DuckovCustomModel.VRM/VRM/` 下，下次启动自动出现在 DCM 模型列表（带模型名、作者、内嵌缩略图）；
- **按需加载 + LRU 缓存**：启动时只读 GLB 头部（毫秒级），选中模型才真正加载；加载结果缓存在内存里，换回同一模型不再等待；
- **MToon 渲染修复**：游戏使用 URP **Deferred** 渲染管线，官方 MToon10 shader 只有 `UniversalForward` 通道、完全不会被绘制。本扩展提供打过补丁的 shader（增加 `UniversalForwardOnly` / `UniversalGBuffer` 通道）并做材质规范化；
- **SpringBone（动骨）**：修复 UniVRM 运行时数据在 `Object.Instantiate` 克隆时丢失的问题（弹簧列表 / 静止姿态重映射）；可选的圆锥角限位防止胸部等动骨穿模；
- **定位锚点**：自动生成 DCM 需要的 11 个锚点（枪 / 头盔 / 背包 / 弹字等），挂在 VRM 对应的 Humanoid 骨骼下，随动画与身高一起缩放；
- **游戏内微调面板**：默认按 `F9` 打开，gizmo 手感对齐 Unity 场景视图（拖球移动 / 拖箭头轴向移动 / 拖圆弧旋转），调完保存为 `Overrides/<模型文件名>.json`，代码里另有一套出厂默认值兜底；
- **源目录热重载**：增删改 `.vrm` 文件自动生效，不用重启游戏；
- **内存保护**：加载前检查系统可用内存（VRM 解析峰值约为文件体积的 10 倍），不足时明确跳过而不是把游戏拖崩。

## 安装（玩家）

1. 安装 [DuckovCustomModel](../README.md) 本体；
2. 把 `Duckov Custom Model VRM` 目录放进游戏的 `Duckov_Data/Mods/`；
3. 把 `.vrm` 文件放进 `ModConfigs/DuckovCustomModel.VRM/VRM/`；
4. 游戏内 DCM 设置里选择你的模型。

可选文件（放在模组目录，即 DLL 所在处）：

| 文件 | 作用 |
| --- | --- |
| `vrmmotion` | 动画 AssetBundle（AnimatorController + 动画片段）。存在时自动挂载，让 DCM 的动画参数驱动 VRM 模型 |
| `vrm.json` | 配置文件（首次运行自动生成，支持注释） |
| `verbose.txt` | 空文件，存在时开启细节日志 |

## 配置（vrm.json）

主要开关（完整带注释模板见首次运行生成的文件）：

```jsonc
{
  "Verbose": null,              // 细节日志；null = 看 verbose.txt 是否存在
  "HotReload": true,            // 源目录热重载
  "HashMode": "meta",           // meta = 大小+时间（快）；md5 = 内容哈希（准）
  "MaxCachedModels": 2,         // 内存里最多缓存几个模型（LRU 淘汰）
  "PreloadSelected": true,      // 启动时后台预热当前选中的模型
  "SpringBone": {
    "AngleLimitMode": 2,        // 0 = 不限位 / 1 = 仅胸部 / 2 = 全身（模型自带限位不覆盖）
    "MaxSwingAngleDeg": 25,     // 胸部动骨最大偏角
    "OtherMaxSwingAngleDeg": 60 // 头发/裙子/尾巴等最大偏角
  },
  "Locators": {
    "AutoGenerate": true,       // 自动生成定位锚点
    "DebugPanelEnabled": true,  // F9 微调面板
    "DebugPanelHotkey": "F9"
  }
}
```

## 构建（开发者）

```bash
dotnet build DuckovCustomModel.VRM/DuckovCustomModel.VRM.csproj -c Release
```

- 本地构建通过 `<DuckovPath>`（csproj 里的游戏安装目录）引用游戏的托管程序集，可用 `-p:DuckovPath=...` 覆盖；
- CI 构建传 `-p:CI=true`，改用 NuGet 包 `DuckovGameLibs`；
- 编译产物 `DuckovCustomModel.VRM.dll` 连同 `Shaders/` 打包出的 `vrmshaders` AB 一起放进模组目录；
- **发布清单（放在 DLL 同目录，均不入 git，需从 Unity 工程产物复制）**：
  - `vrmshaders` — MToon shader AB（Unity 工程 `VRMShadersAB` 构建，约 0.7 MB，**必需**）；
  - `vrmmotion` — 动画 AB（含移动/攻击等状态动画，约 34 MB，可选；缺失时动画功能静默跳过）。

### 依赖

- [UniVRM](https://github.com/vrm-c/UniVRM)（MIT License）：运行时加载 VRM。`External/UniVRM/` 里是编译与运行所需的程序集，随本模组一起发布；
- `Shaders/vrmc_materials_mtoon_urp.shader` 基于 UniVRM 官方 MToon10 URP shader 修改（增加 Deferred 渲染管线所需的通道），遵循原 MIT 许可证；
- [Harmony](https://github.com/pardeike/Harmony)、Newtonsoft.Json、UniTask（随游戏或 DCM 本体提供）。

## 许可证

与 DCM 本体一致，见 [LICENSE](../LICENSE)。
