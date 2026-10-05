using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using DuckovCustomModel.Managers;
using UnityEngine;

namespace DuckovCustomModel.VRM
{
    /// <summary>
    /// VRM 源目录的热重载。
    ///
    /// 监视 <c>ModConfigs/DuckovCustomModel.VRM/VRM</c>（以及同级目录里的 <c>vrm.json</c>），
    /// 文件事件先在后台线程打标记，去抖若干秒后在主线程统一处理：
    ///   1. 重扫目录（只有指纹变化的文件才会重新读 GLB 头部）；
    ///   2. 变更 / 删除的模型丢弃 prefab 缓存（正在使用的等它被换下去再释放）；
    ///   3. 更新 bundleinfo.json + 预览图，并让 DCM 刷新模型列表。
    /// 全程不需要重启游戏，也不会把没动过的模型重新加载一遍。
    /// </summary>
    [DefaultExecutionOrder(-20000)]
    public sealed class VrmSourceWatcher : MonoBehaviour
    {
        /// <summary>去抖时长：写入一个大文件会持续触发事件，等目录安静下来再处理。</summary>
        private const float DebounceSeconds = 1.5f;

        private readonly object _gate = new();
        private readonly List<FileSystemWatcher> _watchers = new();

        private static VrmSourceWatcher? _instance;

        private bool _dirty;
        private bool _rescanAll;
        private float _lastEventTime;
        private long _configStamp;

        /// <summary>创建（或复用）热重载宿主。</summary>
        public static void Ensure()
        {
            if (_instance != null) return;

            var host = new GameObject("DuckovCustomModel.VRM.Watcher");
            DontDestroyOnLoad(host);
            _instance = host.AddComponent<VrmSourceWatcher>();
            _instance.Setup();
        }

        public static void Shutdown()
        {
            if (_instance == null) return;
            Destroy(_instance.gameObject);
            _instance = null;
        }

        private void Setup()
        {
            if (!VrmConfig.Current.HotReload)
            {
                VrmLog.Detail("热重载已关闭（vrm.json: HotReload=false）");
                return;
            }

            _configStamp = GetStamp(VrmConfig.ConfigFilePath);

            if (!AddDirectoryWatcher(VrmModPath.SourceDirectory, "*.vrm")) return;
            AddDirectoryWatcher(VrmModPath.ModConfigDirectory, "vrm.json");
            VrmLog.Detail("已开启源目录热重载");
        }

        private static long GetStamp(string path)
        {
            try
            {
                return File.Exists(path) ? File.GetLastWriteTimeUtc(path).Ticks : 0L;
            }
            catch
            {
                return 0L;
            }
        }

        private bool AddDirectoryWatcher(string directory, string filter)
        {
            try
            {
                Directory.CreateDirectory(directory);

                var watcher = new FileSystemWatcher(directory, filter)
                {
                    NotifyFilter = NotifyFilters.FileName
                                   | NotifyFilters.LastWrite
                                   | NotifyFilters.Size
                                   | NotifyFilters.CreationTime,
                    IncludeSubdirectories = false,
                    EnableRaisingEvents = true,
                };

                watcher.Created += (_, _) => MarkDirty();
                watcher.Changed += (_, _) => MarkDirty();
                watcher.Deleted += (_, _) => MarkDirty();
                watcher.Renamed += (_, _) => MarkDirty();
                watcher.Error += (_, e) =>
                {
                    // 事件缓冲区溢出会丢掉一批事件 —— 只能整体重扫。
                    lock (_gate)
                    {
                        _rescanAll = true;
                        _dirty = true;
                        _lastEventTime = Time.realtimeSinceStartup;
                    }

                    VrmLog.Warn($"目录监视出错（将整体重扫）: {e.GetException()?.Message}");
                };

                _watchers.Add(watcher);
                return true;
            }
            catch (Exception e)
            {
                VrmLog.Warn($"无法监视目录 {directory}: {e.Message}");
                return false;
            }
        }

        private void MarkDirty()
        {
            lock (_gate)
            {
                _dirty = true;
                _lastEventTime = Time.realtimeSinceStartup;
            }
        }

        private void Update()
        {
            bool rescanAll;
            lock (_gate)
            {
                if (!_dirty || Time.realtimeSinceStartup - _lastEventTime < DebounceSeconds) return;
                _dirty = false;
                rescanAll = _rescanAll;
                _rescanAll = false;
            }

            try
            {
                Flush(rescanAll);
            }
            catch (Exception e)
            {
                VrmLog.Error($"热重载处理失败: {e}");
            }
        }

        private void Flush(bool rescanAll)
        {
            ReloadConfigIfChanged();

            var before = Snapshot();
            VrmCatalog.Refresh();
            var after = Snapshot();

            var removed = new List<string>();
            var changed = new List<string>();

            foreach (var kv in before)
                if (!after.ContainsKey(kv.Key)) removed.Add(kv.Key);
                else if (!string.Equals(kv.Value, after[kv.Key], StringComparison.Ordinal)) changed.Add(kv.Key);

            foreach (var modelId in removed.Concat(changed)) VrmModelRegistry.Invalidate(modelId);

            if (removed.Count > 0) VrmLog.Info($"检测到 {removed.Count} 个模型被移除");
            if (changed.Count > 0) VrmLog.Info($"检测到 {changed.Count} 个模型发生变化，已重新注册");

            if (!rescanAll && removed.Count == 0 && changed.Count == 0 && before.Count == after.Count) return;

            VrmModelRegistry.TrimCache();
            ModelListManager.RefreshModelList(after.Keys.ToList());
        }

        private void ReloadConfigIfChanged()
        {
            try
            {
                var path = VrmConfig.ConfigFilePath;
                var stamp = GetStamp(path);
                if (stamp == 0L || stamp == _configStamp) return;

                _configStamp = stamp;
                VrmConfig.Reload();
                VrmLog.Info("已重新读取 vrm.json");
            }
            catch (Exception e)
            {
                VrmLog.Detail($"重新读取配置失败: {e.Message}");
            }
        }

        private static Dictionary<string, string> Snapshot()
        {
            var map = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var entry in VrmCatalog.Entries) map[entry.ModelId] = entry.Hash;
            return map;
        }

        private void OnDestroy()
        {
            foreach (var watcher in _watchers)
                try
                {
                    watcher.EnableRaisingEvents = false;
                    watcher.Dispose();
                }
                catch
                {
                    // ignored
                }

            _watchers.Clear();
            if (_instance == this) _instance = null;
        }
    }
}
