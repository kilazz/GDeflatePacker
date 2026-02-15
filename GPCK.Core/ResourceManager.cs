using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace GPCK.Core
{
    /// <summary>
    /// High-Level Asset Manager (Graph Loader).
    /// Mimics UE5/Frostbite loader.
    /// </summary>
    public class ResourceManager
    {
        private readonly VirtualFileSystem _vfs;
        private readonly ConcurrentDictionary<Guid, Stream> _loadedResources = new();

        public ResourceManager(VirtualFileSystem vfs)
        {
            _vfs = vfs;
        }

        public async Task<Stream> LoadAssetWithDependenciesAsync(string virtualPath, CancellationToken ct = default)
        {
            Guid rootId = AssetIdGenerator.Generate(virtualPath);
            return await LoadAssetRecursive(rootId, ct);
        }

        private async Task<Stream> LoadAssetRecursive(Guid assetId, CancellationToken ct)
        {
            if (_loadedResources.TryGetValue(assetId, out var cachedStream))
            {
                cachedStream.Position = 0;
                return cachedStream;
            }

            if (!_vfs.TryGetEntryForId(assetId, out var archive, out var entry))
            {
                throw new FileNotFoundException($"Asset {assetId} not found in any mounted archive.");
            }

            var dependencies = archive.GetDependenciesForAsset(assetId);
            
            if (dependencies.Count > 0)
            {
                var tasks = new List<Task>();
                foreach(var dep in dependencies)
                {
                    if (dep.Type == GameArchive.DependencyType.HardReference)
                    {
                        tasks.Add(LoadAssetRecursive(dep.TargetAssetId, ct));
                    }
                }
                
                if (tasks.Count > 0)
                {
                    await Task.WhenAll(tasks);
                }
            }

            Stream stream = await Task.Run(() => archive.OpenRead(entry), ct);
            _loadedResources.TryAdd(assetId, stream);
            return stream;
        }

        public void UnloadAll()
        {
            foreach(var s in _loadedResources.Values) s.Dispose();
            _loadedResources.Clear();
        }
    }
}