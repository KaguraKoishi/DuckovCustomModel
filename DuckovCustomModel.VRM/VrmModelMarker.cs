using UnityEngine;

namespace DuckovCustomModel.VRM
{
    /// <summary>
    /// 打在「源 VRM prefab」上的小标记。DCM 用 <c>Instantiate</c> 克隆模型时它会被一并克隆，
    /// 于是克隆体也能知道自己来自哪个模型。
    ///
    /// 用途：SpringBone 修复时据此找到源 prefab，把克隆过程中丢失的弹簧数据
    /// （UniVRM 的运行时列表 / 初姿态字典不被 Unity 序列化，克隆体上是空的）
    /// 按层级路径重映射回来。
    /// </summary>
    public sealed class VrmModelMarker : MonoBehaviour
    {
        public string ModelId = "";
    }
}
