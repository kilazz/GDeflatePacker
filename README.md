# Game Asset Browser
**Game Package** built on **.NET 10.0**.
This project provides efficient tools for compressing and decompressing files using Microsoft's [GDeflate](https://github.com/microsoft/DirectStorage/tree/main/GDeflate) algorithm, utilizing direct native memory (`unsafe`) for maximum performance.

## 🚀 Components
*   **GPCK.Core**: Low-level engine handling P/Invoke to `GDeflate.dll`.
*   **GPCK.CLI**: Command-line tool for automation and batch processing.
*   **GPCK.GUI**: Modern WPF application with Drag & Drop support and structure preservation.

## 📦 Usage

### CLI
```powershell
# Compress file -> texture.png.gdef
GDeflateCLI compress texture.png

# Compress folder -> assets.gpck (DirectStorage aligned package)
GDeflateCLI compress "C:\Assets" assets.gpck

# Inspect package alignment (DirectStorage check)
GDeflateCLI info assets.gpck

# Decompress
GDeflateCLI decompress assets.gpck "C:\Output"
```

### GUI
1.  Launch `GDeflateGUI.exe`.
2.  **Drag & Drop** files or folders.
3.  Select **.gpck** (for folders) or **.gdef** (for single files).
4.  Click **Start Compression**.

## 🛠 Build & Requirements
*   **Requirements**: Windows x64, .NET 10.0, `GDeflate.dll` (must be in the execution folder).
*   **Build**:
    ```bash
    dotnet build -c Release
    ```