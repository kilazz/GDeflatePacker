using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace GPCK.Core
{
    /// <summary>
    /// High-Level Asset Manager.
    /// Supports Generic loading for specific asset types (Texture, Text, etc).
    /// </summary>
    public class ResourceManager
    {
        private readonly VirtualFileSystem _vfs;
        private readonly ConcurrentDictionary<Guid, object> _loadedAssets = new();

        public ResourceManager(VirtualFileSystem vfs)
        {
            _vfs = vfs;
        }

        public async Task<T> LoadAssetAsync<T>(string virtualPath, CancellationToken ct = default) where T : class
        {
            Guid assetId = AssetIdGenerator.Generate(virtualPath);
            return await LoadAssetRecursive<T>(assetId, ct);
        }

        private async Task<T> LoadAssetRecursive<T>(Guid assetId, CancellationToken ct) where T : class
        {
            if (_loadedAssets.TryGetValue(assetId, out var cached)) return (T)cached;

            if (!_vfs.TryGetEntryForId(assetId, out var archive, out var entry))
                throw new FileNotFoundException($"Asset {assetId} not found.");

            // Dependencies
            var deps = archive.GetDependenciesForAsset(assetId);
            foreach(var dep in deps)
            {
                // Recursive load of hard references (simplified)
                if (dep.Type == GameArchive.DependencyType.HardReference)
                {
                    // Recursively load generic object for deps
                    await LoadAssetRecursive<object>(dep.TargetAssetId, ct);
                }
            }

            // Deserialization (Mocking real engine logic)
            using var stream = archive.OpenRead(entry);
            object? result = null;

            if (typeof(T) == typeof(string))
            {
                using var reader = new StreamReader(stream);
                result = await reader.ReadToEndAsync(ct);
            }
            else if (typeof(T) == typeof(byte[]))
            {
                using var ms = new MemoryStream();
                await stream.CopyToAsync(ms, ct);
                result = ms.ToArray();
            }
            else
            {
                // Fallback for unknown types (return as byte array or stream wrapper)
                using var ms = new MemoryStream();
                await stream.CopyToAsync(ms, ct);
                result = ms.ToArray(); 
            }

            if (result == null) throw new InvalidOperationException($"Failed to load asset {assetId}");

            _loadedAssets.TryAdd(assetId, result);
            return (T)result;
        }

        public void UnloadAll() => _loadedAssets.Clear();
    }
}