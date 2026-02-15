using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace GDeflate.Core
{
    /// <summary>
    /// Virtual File System (VFS).
    /// Allows mounting multiple .gpck archives as layers.
    /// Priority: Last mounted archive overrides files from previous ones.
    /// </summary>
    public class VirtualFileSystem : IDisposable
    {
        private readonly List<GameArchive> _mountedArchives = new();
        private readonly Dictionary<Guid, int> _virtualLookup = new();

        public int MountedCount => _mountedArchives.Count;

        public void Mount(string path)
        {
            var archive = new GameArchive(path);
            _mountedArchives.Add(archive);
            int archiveIndex = _mountedArchives.Count - 1;

            for (int i = 0; i < archive.FileCount; i++)
            {
                var entry = archive.GetEntryByIndex(i);
                _virtualLookup[entry.AssetId] = archiveIndex;
            }
        }

        public bool FileExists(string virtualPath)
        {
            Guid id = AssetIdGenerator.Generate(virtualPath);
            return _virtualLookup.ContainsKey(id);
        }

        public Stream OpenRead(string virtualPath)
        {
            Guid id = AssetIdGenerator.Generate(virtualPath);

            if (TryGetEntryForId(id, out var archive, out var entry))
            {
                return archive.OpenRead(entry);
            }

            throw new FileNotFoundException($"File not found in VFS: {virtualPath}");
        }

        public bool TryGetEntryForId(Guid id, out GameArchive archive, out GameArchive.FileEntry entry)
        {
             if (_virtualLookup.TryGetValue(id, out int archiveIndex))
            {
                archive = _mountedArchives[archiveIndex];
                return archive.TryGetEntry(id, out entry);
            }
            archive = null;
            entry = default;
            return false;
        }

        public string GetSourceArchiveName(string virtualPath)
        {
            Guid id = AssetIdGenerator.Generate(virtualPath);
            if (_virtualLookup.TryGetValue(id, out int archiveIndex))
            {
                return Path.GetFileName(_mountedArchives[archiveIndex].FilePath);
            }
            return "None";
        }

        public void Dispose()
        {
            foreach (var archive in _mountedArchives)
            {
                archive.Dispose();
            }
            _mountedArchives.Clear();
            _virtualLookup.Clear();
        }
    }
}