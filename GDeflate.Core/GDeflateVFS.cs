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
    /// Example: Base.gpck (Layer 0) -> Patch01.gpck (Layer 1) -> Mod_4kTextures.gpck (Layer 2)
    /// </summary>
    public class GDeflateVFS : IDisposable
    {
        private readonly List<GDeflateArchive> _mountedArchives = new();

        // Maps AssetID (GUID) -> Index of the archive in _mountedArchives
        private readonly Dictionary<Guid, int> _virtualLookup = new();

        public int MountedCount => _mountedArchives.Count;

        /// <summary>
        /// Mounts an archive into the VFS.
        /// </summary>
        /// <param name="path">Path to .gpck file</param>
        public void Mount(string path)
        {
            var archive = new GDeflateArchive(path);
            _mountedArchives.Add(archive);
            int archiveIndex = _mountedArchives.Count - 1;

            // Register all files from this archive into the virtual lookup table
            for (int i = 0; i < archive.FileCount; i++)
            {
                var entry = archive.GetEntryByIndex(i);

                // Last Writer Wins (Modding behavior)
                // If an AssetID already exists, we update the index to point to THIS archive
                _virtualLookup[entry.AssetId] = archiveIndex;
            }
        }

        /// <summary>
        /// Checks if a file exists in the virtual file system.
        /// </summary>
        public bool FileExists(string virtualPath)
        {
            Guid id = AssetIdGenerator.Generate(virtualPath);
            return _virtualLookup.ContainsKey(id);
        }

        /// <summary>
        /// Opens a file stream from the highest priority archive containing the file.
        /// </summary>
        public Stream OpenRead(string virtualPath)
        {
            Guid id = AssetIdGenerator.Generate(virtualPath);

            if (_virtualLookup.TryGetValue(id, out int archiveIndex))
            {
                var archive = _mountedArchives[archiveIndex];
                // v8 TryGetEntry takes a GUID
                if (archive.TryGetEntry(id, out var entry))
                {
                    return archive.OpenRead(entry);
                }
            }

            throw new FileNotFoundException($"File not found in VFS: {virtualPath}");
        }

        /// <summary>
        /// Gets info about which archive is serving the file (Debug/Modding Tool helper).
        /// </summary>
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