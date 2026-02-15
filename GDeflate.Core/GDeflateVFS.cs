using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace GDeflate.Core
{
    /// <summary>
    /// Virtual File System (VFS) for AAAA Modding Support.
    /// Allows mounting multiple .gpck archives as layers.
    /// Priority: Last mounted archive overrides files from previous ones.
    /// Example: Base.gpck (Layer 0) -> Patch01.gpck (Layer 1) -> Mod_4kTextures.gpck (Layer 2)
    /// </summary>
    public class GDeflateVFS : IDisposable
    {
        private readonly List<GDeflateArchive> _mountedArchives = new();

        // Maps a FilePath Hash -> Index of the archive in _mountedArchives that holds the latest version
        private readonly Dictionary<ulong, int> _virtualLookup = new();

        public int MountedCount => _mountedArchives.Count;

        /// <summary>
        /// Mounts an archive into the VFS.
        /// </summary>
        /// <param name="path">Path to .gpck file</param>
        /// <param name="priority">If true, this archive overrides existing files. Usually true for mods/patches.</param>
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
                // If a hash already exists, we update the index to point to THIS archive
                _virtualLookup[entry.PathHash] = archiveIndex;
            }
        }

        /// <summary>
        /// Checks if a file exists in the virtual file system.
        /// </summary>
        public bool FileExists(string virtualPath)
        {
            ulong hash = PathHasher.Hash(virtualPath);
            return _virtualLookup.ContainsKey(hash);
        }

        /// <summary>
        /// Opens a file stream from the highest priority archive containing the file.
        /// </summary>
        public Stream OpenRead(string virtualPath)
        {
            ulong hash = PathHasher.Hash(virtualPath);

            if (_virtualLookup.TryGetValue(hash, out int archiveIndex))
            {
                var archive = _mountedArchives[archiveIndex];
                if (archive.TryGetEntry(virtualPath, out var entry))
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
            ulong hash = PathHasher.Hash(virtualPath);
            if (_virtualLookup.TryGetValue(hash, out int archiveIndex))
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
