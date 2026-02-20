# GPCK (Game Package)

**High-performance asset management for .NET 10 & DirectStorage.**

GPCK is a next-gen archive format and toolset designed for AAA game engines. It leverages **DirectStorage** and **GDeflate** to eliminate loading bottlenecks and maximize NVMe throughput.

---

## 🚀 Key Features

*   **Hybrid Compression:** **Zstd** for logic/data, **GDeflate** for textures (GPU Path B), and **LZ4** for real-time streaming.
*   **Virtual File System (VFS):** Layer-based mounting with $O(\log N)$ lookup; native support for mods and delta patches.
*   **Hardware Ready:** 4KB/64KB block alignment for DirectStorage and Vulkan Compute pipelines.
*   **Smart Dedup:** Integrated **xxHash64** fingerprinting removes redundant assets.
*   **Data Locality:** Physical sorting by path ensures sequential read patterns.

---

## 🛠 Components

| Name | Role |
| :--- | :--- |
| **GPCK.Core** | Core logic, VFS, and native library interop. |
| **GPCK.Avalonia** | GUI with **Archive Fragmentation Map** visualizer. |
| **GPCK.CLI** | Headless tool for CI/CD build pipelines. |

---

## 📦 Quick Start

```bash
# Pack folder into an optimized archive
gpck pack "C:\Source\Assets" "Data.gpck" --method Auto --level 9

# Unpack archive
gpck unpack "Data.gpck" "C:\Output"

# Technical inspection
gpck info "Data.gpck"
```

---

## 🔧 Building

*   **SDK:** .NET 10.0
*   **Platform:** x64 (Required for GDeflate/DirectStorage)
*   **Deps:** Included in `runtimes/` (`dstorage.dll`, `libzstd.dll`, etc.)

```bash
dotnet build -c Release
```

## 📜 License
MIT License. NVIDIA GDeflate components subject to Apache-2.0.
