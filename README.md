# GPCK Toolsuite

**GPCK** is a high-performance asset management system (.NET 10.0) featuring **GDeflate** (DirectStorage) & **Zstd** compression, encryption, deduplication, and Virtual File System (VFS).

## 🚀 CLI Commands

```powershell
# Pack / Unpack
GPCK compress <in_dir> <out.gpck> [--level 9] [--mip-split] [--key <secret>]
GPCK decompress <in.gpck> <out_dir> [--key <secret>]

# Single File / Patching
GPCK extract-file <in.gpck> <file_path> <out_dir>
GPCK patch <base.gpck> <new_content_dir> <patch.gpck>

# Diagnostics
GPCK info <in.gpck>                # View alignment & compression stats
GPCK verify <in.gpck>              # CRC32 integrity check
GPCK mount <a.gpck> <b.gpck> ...   # Test VFS layering
```

## 🖥️ GUI

The **GPCK.GUI** (WPF) allows for:
*   Visual inspection of archive structure and compression ratios.
*   **Previews** for Textures, Text, and Hex.
*   Drag-and-drop Packing and Extraction.

## 🛠 Build

Requires **Windows x64** and **.NET 10.0**.

```bash
dotnet build -c Release
```