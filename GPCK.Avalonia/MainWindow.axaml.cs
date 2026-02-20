using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using GPCK.Core;
using GPCK.Core.Vulkan;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace GPCK.Avalonia;

// Helper classes for UI binding
public class FileItem
{
    public string RelativePath { get; set; } = string.Empty;
    public Guid AssetId { get; set; }
    public string Size { get; set; } = string.Empty;
    public long SizeBytes { get; set; }
    public string FilePath { get; set; } = string.Empty;
    public bool IsArchiveEntry { get; set; } = false;
    public GameArchive? SourceArchive { get; set; }
    public GameArchive.FileEntry? EntryInfo { get; set; }
    public long CompressedSizeBytes { get; set; }
    public string ManifestInfo { get; set; } = "";
    public string TypeIcon => IsArchiveEntry ? "📦" : "📄";
    public string DisplayName => IsArchiveEntry ? $"{RelativePath}\n[{AssetId}]" : RelativePath;
    public string CompressionInfo
    {
        get
        {
            if (!IsArchiveEntry || !EntryInfo.HasValue) return "Pending";
            var e = EntryInfo.Value;
            uint method = e.Flags & GameArchive.MASK_METHOD;
            string m = method switch
            {
                GameArchive.METHOD_GDEFLATE => "GDeflate",
                GameArchive.METHOD_ZSTD => "Zstd",
                GameArchive.METHOD_LZ4 => "LZ4",
                _ => "Store"
            };
            if ((e.Flags & GameArchive.FLAG_ENCRYPTED_META) != 0) m += " [Enc]";
            if (e.OriginalSize == 0) return m;
            double ratio = (double)CompressedSizeBytes / e.OriginalSize * 100.0;
            return $"{m} ({ratio:F0}%)";
        }
    }
}

public class BlockItem
{
    public double Width { get; set; }
    public IBrush? Color { get; set; }
    public string ToolTip { get; set; } = "";
}

public partial class MainWindow : Window
{
    private ObservableCollection<FileItem> _files = new();
    private ObservableCollection<FileItem> _filteredFiles = new();
    private ObservableCollection<BlockItem> _blocks = new();
    private AssetPacker _processor;
    private CancellationTokenSource? _cts;
    private List<GameArchive> _openArchives = new();
    private VulkanDecompressor? _gpu;

    // Benchmark-derived constants for simulator
    private const double SpeedCpuMbps = 910.2;
    private const double SpeedGpuMbps = 5772.0;

    public MainWindow()
    {
        InitializeComponent();

        _processor = new AssetPacker();
        FileList.ItemsSource = _filteredFiles;
        VisualizerItems.ItemsSource = _blocks;

        CheckBackend();

        // Subscribe to slider changes to update stats in real-time
        SliderSimLevel.PropertyChanged += (s, e) => {
             if (e.Property.Name == "Value" && FileList.SelectedItem is FileItem item)
                 UpdateSimStats(item);
        };
    }

    private void CheckBackend()
    {
        bool cpuAvailable = _processor.IsCpuLibraryAvailable();
        string status = $"CPU: {(cpuAvailable ? "Native" : ".NET")}";

        try
        {
            _gpu = new VulkanDecompressor();
            status += $" | GPU: {_gpu.DeviceName} (Vulkan)";
        }
        catch (Exception ex)
        {
            status += $" | GPU: Disabled ({ex.Message})";
        }

        TxtBackendStatus.Text = status;
    }

    protected override void OnClosed(EventArgs e)
    {
        _gpu?.Dispose();
        foreach (var a in _openArchives) a.Dispose();
        base.OnClosed(e);
    }

    private async void BtnOpenArchive_Click(object sender, RoutedEventArgs e)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Open Archive",
            AllowMultiple = false,
            FileTypeFilter = new[] { new FilePickerFileType("GPCK Archive") { Patterns = new[] { "*.gpck" } } }
        });

        if (files.Count > 0)
        {
            LoadArchive(files[0].Path.LocalPath);
        }
    }

    private void LoadArchive(string path)
    {
        try
        {
            var archive = new GameArchive(path);
            _openArchives.Add(archive);
            var info = archive.GetPackageInfo();
            double scale = 2000.0 / Math.Max(1, info.TotalSize);

            foreach (var entry in info.Entries)
            {
                if (archive.TryGetEntry(entry.AssetId, out var rawEntry))
                {
                    var item = new FileItem
                    {
                        IsArchiveEntry = true,
                        SourceArchive = archive,
                        EntryInfo = rawEntry,
                        AssetId = entry.AssetId,
                        RelativePath = entry.Path,
                        Size = FormatSize(entry.OriginalSize),
                        SizeBytes = entry.OriginalSize,
                        FilePath = path,
                        CompressedSizeBytes = entry.CompressedSize,
                        ManifestInfo = entry.MetadataInfo
                    };
                    _files.Add(item);

                    double w = Math.Max(2.0, entry.CompressedSize * scale);
                    if (w > 100) w = 100;

                    IBrush color = Brushes.LightGray;
                    if (entry.Method.Contains("GDeflate")) color = Brushes.LightGreen;
                    else if (entry.Method.Contains("Zstd")) color = Brushes.LightBlue;
                    else if (entry.Method.Contains("LZ4")) color = Brushes.Orange;

                    _blocks.Add(new BlockItem
                    {
                        Width = w,
                        Color = color,
                        ToolTip = $"{entry.Path}\n{entry.Method}"
                    });
                }
            }
            RefreshFilter();
            UpdateStatus($"Loaded {info.FileCount} files from {Path.GetFileName(path)}");
        }
        catch (Exception ex) { UpdateStatus($"Error: {ex.Message}"); }
    }

    private async void BtnAddFiles_Click(object sender, RoutedEventArgs e)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions { AllowMultiple = true });
        foreach (var file in files)
        {
            AddFileItem(file.Path.LocalPath);
        }
        RefreshFilter();
    }

    private async void BtnAddFolder_Click(object sender, RoutedEventArgs e)
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "Select Folder" });
        if (folders.Count > 0)
        {
            string path = folders[0].Path.LocalPath;
            UpdateStatus("Scanning folder...");
            await Task.Run(() => {
                var map = AssetPacker.BuildFileMap(path);
                Dispatcher.UIThread.Post(() => {
                    foreach (var kv in map) AddFileItem(kv.Key, kv.Value);
                    RefreshFilter();
                    UpdateStatus("Folder added.");
                });
            });
        }
    }

    private void AddFileItem(string path, string? relativePath = null)
    {
        string rel = relativePath ?? Path.GetFileName(path);
        long sz = File.Exists(path) ? new FileInfo(path).Length : 0;
        _files.Add(new FileItem
        {
            FilePath = path,
            RelativePath = rel,
            AssetId = AssetIdGenerator.Generate(rel),
            Size = FormatSize(sz),
            SizeBytes = sz
        });
    }

    private void BtnClear_Click(object sender, RoutedEventArgs e)
    {
        _files.Clear();
        _filteredFiles.Clear();
        _blocks.Clear();
        foreach(var a in _openArchives) a.Dispose();
        _openArchives.Clear();
        ResetPreview();
    }

    private void Tab_Clicked(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn) return;
        bool isExplorer = btn == TabExplorer;
        bool isVisualizer = btn == TabVisualizer;
        bool isSim = btn == TabSim;

        ExplorerView.IsVisible = isExplorer;
        VisualizerView.IsVisible = isVisualizer;
        SimView.IsVisible = isSim;

        TabExplorer.Classes.Set("Selected", isExplorer);
        TabVisualizer.Classes.Set("Selected", isVisualizer);
        TabSim.Classes.Set("Selected", isSim);
    }

    private void TxtSearch_TextChanged(object sender, TextChangedEventArgs e) => RefreshFilter();

    private void RefreshFilter()
    {
        _filteredFiles.Clear();
        string q = TxtSearch.Text ?? "";
        foreach (var f in _files.Where(x => x.RelativePath.Contains(q, StringComparison.OrdinalIgnoreCase)))
            _filteredFiles.Add(f);
        TxtListCount.Text = $"{_filteredFiles.Count} items";
    }

    private async void FileList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (FileList.SelectedItem is not FileItem item) return;
        ResetPreview();
        UpdateSimStats(item);

        try
        {
            Stream? s = GetStream(item);
            if (s != null)
            {
                using (s)
                {
                    string ext = Path.GetExtension(item.RelativePath).ToLower();
                    if (ext == ".png" || ext == ".jpg")
                    {
                        var ms = new MemoryStream();
                        await s.CopyToAsync(ms);
                        ms.Position = 0;
                        ImgPreview.Source = new Bitmap(ms);
                        ImgPreview.IsVisible = true;
                        LblNoPreview.IsVisible = false;
                    }
                    else
                    {
                        using var r = new StreamReader(s);
                        char[] buf = new char[1024];
                        int read = await r.ReadAsync(buf, 0, buf.Length);
                        TxtPreview.Text = new string(buf, 0, read);
                        TxtPreview.IsVisible = true;
                        LblNoPreview.IsVisible = false;
                    }
                }
            }
        }
        catch { /* Ignore preview errors */ }
    }

    private void UpdateSimStats(FileItem item)
    {
        double sizeMb = item.SizeBytes / 1024.0 / 1024.0;

        // Simulation logic: Higher compression level adds a CPU penalty (exponential complexity)
        double currentLevel = SliderSimLevel.Value;
        double cpuPenalty = 1.0 + (Math.Pow(currentLevel / 6.0, 2.0) - 1.0) * 0.5;

        double cpuTime = (sizeMb / SpeedCpuMbps) * cpuPenalty;
        double gpuTime = sizeMb / SpeedGpuMbps; // GPU is mostly invariant to level due to HW decompressors
        double gain = cpuTime / gpuTime;

        TxtSimAssetName.Text = item.RelativePath;
        TxtSimAssetSize.Text = item.Size;
        TxtCpuTime.Text = $"{cpuTime:F3}s";
        TxtGpuTime.Text = $"{gpuTime:F3}s";
        TxtEfficiencyGain.Text = $"{gain:F1}x Faster";

        BarCpu.Value = 0;
        BarGpu.Value = 0;
    }

    private async void BtnRunSim_Click(object sender, RoutedEventArgs e)
    {
        if (FileList.SelectedItem is not FileItem item) return;

        double sizeMb = item.SizeBytes / 1024.0 / 1024.0;
        double currentLevel = SliderSimLevel.Value;
        double cpuPenalty = 1.0 + (Math.Pow(currentLevel / 6.0, 2.0) - 1.0) * 0.5;

        double cpuTime = (sizeMb / SpeedCpuMbps) * cpuPenalty;

        BarCpu.Value = 0;
        BarGpu.Value = 0;

        // Animate GPU (instantly basically)
        await Task.Delay(10);
        BarGpu.Value = 100;

        // Animate CPU based on "Complexity"
        int steps = 20;
        int msPerStep = (int)(cpuTime * 1000 / steps);
        for (int i = 1; i <= steps; i++)
        {
            await Task.Delay(Math.Max(10, msPerStep));
            BarCpu.Value = (i * 100) / steps;
        }
    }

    private Stream? GetStream(FileItem item)
    {
        if (item.IsArchiveEntry && item.SourceArchive != null && item.EntryInfo.HasValue)
            return item.SourceArchive.OpenRead(item.EntryInfo.Value);
        if (File.Exists(item.FilePath))
            return File.OpenRead(item.FilePath);
        return null;
    }

    private void ResetPreview()
    {
        ImgPreview.IsVisible = false;
        TxtPreview.IsVisible = false;
        LblNoPreview.IsVisible = true;
    }

    private void CmbMethod_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (NumLevel == null || SliderSimLevel == null) return;

        string method = (CmbMethod.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "Auto";

        // Define limits per algorithm
        double maxLevel = 12;
        bool isEnabled = true;

        switch (method)
        {
            case "Zstd":
                maxLevel = 22;
                break;
            case "LZ4":
                maxLevel = 12;
                break;
            case "GDeflate":
                maxLevel = 12;
                break;
            case "Store":
                maxLevel = 1;
                isEnabled = false;
                break;
            default: // Auto
                maxLevel = 12;
                break;
        }

        NumLevel.IsEnabled = isEnabled;
        NumLevel.Maximum = (decimal)maxLevel;
        if (NumLevel.Value > (decimal)maxLevel) NumLevel.Value = (decimal)maxLevel;

        // Sync Simulator limits too
        SliderSimLevel.Maximum = maxLevel;
        if (SliderSimLevel.Value > maxLevel) SliderSimLevel.Value = maxLevel;
    }

    private async void BtnCompress_Click(object sender, RoutedEventArgs e)
    {
        var saveFile = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions { DefaultExtension = "gpck", Title = "Save Package" });
        if (saveFile == null) return;

        var map = _files.Where(x => !x.IsArchiveEntry).DistinctBy(x => x.FilePath).ToDictionary(x => x.FilePath, x => x.RelativePath);

        string methodStr = (CmbMethod.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "Auto";
        Enum.TryParse(methodStr, out AssetPacker.CompressionMethod method);

        // Use user selected level
        int level = (int)(NumLevel.Value ?? 9);

        await RunOp(async (ct, prog) => {
            await _processor.CompressFilesToArchiveAsync(map, saveFile.Path.LocalPath, true, level, null, false, prog, ct, method);
        });
        UpdateStatus("Packed successfully.");
    }

    private async void BtnExtractAll_Click(object sender, RoutedEventArgs e)
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "Extract To" });
        if (folders.Count == 0) return;
        string dest = folders[0].Path.LocalPath;

        await RunOp(async (ct, prog) => {
            int i = 0;
            foreach (var item in _files)
            {
                if (item.IsArchiveEntry)
                {
                    using var s = GetStream(item);
                    if (s != null)
                    {
                        string p = Path.Combine(dest, item.RelativePath);
                        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
                        using var fs = File.Create(p);
                        await s.CopyToAsync(fs, ct);
                    }
                }
                prog.Report(++i * 100 / _files.Count);
            }
        });
        UpdateStatus("Extraction complete.");
    }

    private async Task RunOp(Func<CancellationToken, IProgress<int>, Task> action)
    {
        _cts = new CancellationTokenSource();
        ProgressBar.IsVisible = true;
        ProgressBar.Value = 0;
        BtnCompress.IsEnabled = false;

        try
        {
            await action(_cts.Token, new Progress<int>(v => Dispatcher.UIThread.Post(() => ProgressBar.Value = v)));
        }
        catch (Exception ex)
        {
            UpdateStatus($"Error: {ex.Message}");
        }
        finally
        {
            ProgressBar.IsVisible = false;
            BtnCompress.IsEnabled = true;
            _cts = null;
        }
    }

    private void UpdateStatus(string msg) => TxtStatus.Text = msg;
    private string FormatSize(long b) => b < 1024 ? $"{b} B" : $"{b/1024.0/1024.0:F2} MB";
}
