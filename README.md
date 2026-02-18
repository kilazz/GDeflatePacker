# GPCK (Game Package Construction Kit)

**Next-Gen Asset Management System for .NET 10**

High-performance archive format leveraging **DirectStorage**, **GDeflate** (GPU decompression), and NVMe optimizations.

## 🚀 Features
*   **Compression:** GDeflate (GPU optimized), Zstd (High Ratio), LZ4 (Low Latency).
*   **Architecture:** DirectStorage alignment (4KB/64KB), xxHash64 indexing, and AES-GCM encryption.
*   **Optimized:** Data locality for sequential reads and automatic texture streaming (mip-splitting).
*   **Smart Packing:** Content-addressable deduplication.

## 🛠️ Components
*   **GPCK.Core:** Core library with Virtual File System (VFS) and multithreaded I/O.
*   **GPCK.Avalonia:** Cross-platform GUI for packing, inspecting, and visualizing archives.
*   **GPCK.CLI:** Headless tool for build pipelines.
*   **GPCK.Benchmark:** Decompression throughput testing harness.

## 📦 Usage

### CLI
```bash
# Pack
GPCK.CLI.exe compress "C:\Assets" "Data.gpck" --method Auto --level 9

# Unpack
GPCK.CLI.exe decompress "Data.gpck" "Output"
```

### GUI
Run `GPCK.Avalonia.exe` to browse archives, view fragmentation maps, or pack folders via the UI.

## 🔧 Build
Requires **.NET 10 SDK**.

```bash
dotnet build -c Release
```
*Ensure native libraries (`dstorage.dll`, `GDeflate.dll`, `libzstd.dll`) are present in the output.*
