using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using GPCK.Core;
using Microsoft.Win32;

namespace GPCKGUI
{
    public class BlockItem
    {
        public double Width { get; set; }
        public required Brush Color { get; set; }
        public required string ToolTip { get; set; }
    }

    public partial class MainWindow : Window
    {
        private ObservableCollection<FileItem> _files = new ObservableCollection<FileItem>();
        private ICollectionView _fileView;
        private ObservableCollection<BlockItem> _blocks = new ObservableCollection<BlockItem>();
        private AssetPacker _processor;
        private CancellationTokenSource? _cts;
        private List<GameArchive> _openArchives = new List<GameArchive>();

        public MainWindow()
        {
            InitializeComponent();

            _fileView = CollectionViewSource.GetDefaultView(_files);
            _fileView.Filter = FileFilter;
            FileList.ItemsSource = _fileView;

            VisualizerItems.ItemsSource = _blocks;
            _processor = new AssetPacker();
            CheckBackend();
            UpdateStatus("Ready");
        }

        private bool FileFilter(object item)
        {
            if (string.IsNullOrWhiteSpace(TxtSearch.Text)) return true;
            if (item is FileItem f)
            {
                string search = TxtSearch.Text;
                return f.RelativePath.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                       f.AssetId.ToString().Contains(search, StringComparison.OrdinalIgnoreCase);
            }
            return false;
        }

        private void TxtSearch_TextChanged(object sender, TextChangedEventArgs e)
        {
            _fileView.Refresh();
            UpdateListCount();
        }

        private void UpdateListCount()
        {
            int count = _fileView.Cast<object>().Count();
            int total = _files.Count;

            if (count != total)
                TxtListCount.Text = $"{count} / {total} items";
            else
                TxtListCount.Text = $"{total} items";
        }

        protected override void OnClosed(EventArgs e)
        {
            CancelCurrentOperation();
            DisposeArchives();
            base.OnClosed(e);
        }

        private void DisposeArchives()
        {
            foreach (var arch in _openArchives) arch.Dispose();
            _openArchives.Clear();
        }

        private void CancelCurrentOperation()
        {
            if (_cts != null)
            {
                _cts.Cancel();
                _cts.Dispose();
                _cts = null;
            }
        }

        private void CheckBackend()
        {
            bool g = _processor.IsCpuLibraryAvailable();
            TxtBackendStatus.Text = $"GDeflate: {(g ? "Available ✓" : "Unavailable ❌")}";
        }

        private void BtnOpenArchive_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new OpenFileDialog { Filter = "GPCK Packages (*.gpck)|*.gpck", Title = "Open Archive" };
            if (dialog.ShowDialog() == true) LoadArchive(dialog.FileName);
        }

        private void LoadArchive(string path)
        {
            try
            {
                var archive = new GameArchive(path);
                _openArchives.Add(archive);
                var info = archive.GetPackageInfo();

                long totalBytes = info.TotalSize;
                double scale = 2000.0 / totalBytes; // Visual scale

                foreach (var entry in info.Entries)
                {
                    if (archive.TryGetEntry(entry.AssetId, out var rawEntry))
                    {
                        _files.Add(new FileItem
                        {
                            IsArchiveEntry = true,
                            SourceArchive = archive,
                            EntryInfo = rawEntry,
                            AssetId = entry.AssetId,
                            RelativePath = entry.Path,
                            Size = FormatSize(entry.OriginalSize),
                            FilePath = path,
                            CompressedSizeBytes = entry.CompressedSize,
                            ManifestInfo = entry.MetadataInfo
                        });

                        double w = Math.Max(1.0, entry.CompressedSize * scale);
                        if (w > 300) w = 300; // Cap width for wrap panel

                        var color = Brushes.LightGray;
                        if (entry.Method.Contains("GDeflate")) color = Brushes.LightGreen;
                        else if (entry.Method.Contains("Zstd")) color = Brushes.LightBlue;
                        else if (entry.Method.Contains("LZ4")) color = Brushes.Orange;

                        _blocks.Add(new BlockItem
                        {
                            Width = w,
                            Color = color,
                            ToolTip = $"{entry.Path}\nCompressed: {FormatSize(entry.CompressedSize)}\nOriginal: {FormatSize(entry.OriginalSize)}\nMethod: {entry.Method}"
                        });
                    }
                }
                UpdateStatus($"Loaded {info.FileCount} assets from {Path.GetFileName(path)}");
                UpdateListCount();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to load archive: {ex.Message}", "Load Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private async void FileList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            var item = FileList.SelectedItem as FileItem;
            ResetPreview();
            if (item == null) return;

            UpdateStatus($"Inspecting: {item.RelativePath}");

            try
            {
                using var stream = GetStreamForItem(item);
                if (stream == null) return;

                string ext = Path.GetExtension(item.RelativePath).ToLowerInvariant();
                if (IsImage(ext)) await ShowImagePreview(stream);
                else if (IsText(ext)) await ShowTextPreview(stream);
                else ShowHexPreview(stream, item.RelativePath);
            }
            catch (Exception ex)
            {
                TxtPreview.Visibility = Visibility.Visible;
                TxtPreview.Text = $"Error loading preview: {ex.Message}";
                LblNoPreview.Visibility = Visibility.Collapsed;
            }
        }

        private Stream? GetStreamForItem(FileItem item)
        {
            if (item.IsArchiveEntry && item.SourceArchive != null && item.EntryInfo.HasValue)
                return item.SourceArchive.OpenRead(item.EntryInfo.Value);
            else if (!string.IsNullOrEmpty(item.FilePath) && File.Exists(item.FilePath))
                return new FileStream(item.FilePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            return null;
        }

        private async Task ShowImagePreview(Stream source)
        {
            try
            {
                using var memory = new MemoryStream();
                await source.CopyToAsync(memory);
                memory.Position = 0;

                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.StreamSource = memory;
                bitmap.EndInit();
                bitmap.Freeze();

                ImgPreview.Source = bitmap;
                ImgPreview.Visibility = Visibility.Visible;
                LblNoPreview.Visibility = Visibility.Collapsed;
            }
            catch
            {
                source.Position = 0;
                ShowHexPreview(source, "Image decode failed. Showing hex instead.");
            }
        }

        private async Task ShowTextPreview(Stream source)
        {
            using var reader = new StreamReader(source, Encoding.UTF8, true, 4096, true);
            char[] buffer = new char[8192];
            int read = await reader.ReadAsync(buffer, 0, buffer.Length);

            TxtPreview.Text = new string(buffer, 0, read) + (read == buffer.Length ? "\n... [Preview Truncated]" : "");
            TxtPreview.Visibility = Visibility.Visible;
            LblNoPreview.Visibility = Visibility.Collapsed;
        }

        private void ShowHexPreview(Stream source, string title)
        {
            byte[] buffer = new byte[1024];
            int read = source.Read(buffer, 0, buffer.Length);
            var sb = new StringBuilder();
            sb.AppendLine($"HEX PREVIEW: {title}");
            sb.AppendLine(new string('-', 40));

            for (int i = 0; i < read; i++)
            {
                if (i % 16 == 0) sb.Append($"{i:X4}: ");
                sb.Append($"{buffer[i]:X2} ");
                if (i % 16 == 15) sb.AppendLine();
            }

            HexPreview.Text = sb.ToString();
            HexPreview.Visibility = Visibility.Visible;
            LblNoPreview.Visibility = Visibility.Collapsed;
        }

        private void ResetPreview()
        {
            ImgPreview.Source = null;
            ImgPreview.Visibility = Visibility.Collapsed;
            TxtPreview.Visibility = Visibility.Collapsed;
            HexPreview.Visibility = Visibility.Collapsed;
            LblNoPreview.Visibility = Visibility.Visible;
        }

        private bool IsImage(string ext) => ext == ".png" || ext == ".jpg" || ext == ".jpeg" || ext == ".bmp" || ext == ".ico";
        private bool IsText(string ext) => ext == ".txt" || ext == ".json" || ext == ".xml" || ext == ".lua" || ext == ".ini" || ext == ".yaml" || ext == ".xaml";

        private void BtnAddFiles_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new OpenFileDialog { Multiselect = true, Title = "Add Assets" };
            if (dialog.ShowDialog() == true) AddFiles(dialog.FileNames);
        }

        private void BtnAddFolder_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new OpenFolderDialog { Title = "Add Folder Content" };
            if (dialog.ShowDialog() == true) _ = AddFolderAsync(dialog.FolderName);
        }

        private void BtnClear_Click(object sender, RoutedEventArgs e)
        {
            _files.Clear();
            _blocks.Clear();
            DisposeArchives();
            ResetPreview();
            UpdateListCount();
            UpdateStatus("List cleared");
        }

        private async void BtnCompress_Click(object sender, RoutedEventArgs e)
        {
            if (_cts != null) { CancelCurrentOperation(); return; }

            var filesToPack = _files.Where(x => !x.IsArchiveEntry)
                                    .DistinctBy(x => x.FilePath)
                                    .ToDictionary(x => x.FilePath, x => x.RelativePath);

            if (filesToPack.Count == 0)
            {
                MessageBox.Show("No new files added to pack. Add files or folders first.", "Pack Info");
                return;
            }

            string sel = (CmbMethod.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "Auto";
            if (!Enum.TryParse<AssetPacker.CompressionMethod>(sel, out var method)) method = AssetPacker.CompressionMethod.Auto;

            var saveDialog = new SaveFileDialog { Filter = "GPCK Package (*.gpck)|*.gpck", FileName = "new_assets.gpck" };
            if (saveDialog.ShowDialog() == true)
            {
                await RunOperationAsync(async (token, progress) =>
                {
                    await _processor.CompressFilesToArchiveAsync(filesToPack, saveDialog.FileName, true, 9, null, false, progress, token, method);
                }, BtnCompress, "🚀 Pack .gpck");

                UpdateStatus("Archive created successfully.");
            }
        }

        private async void BtnExtractAll_Click(object sender, RoutedEventArgs e)
        {
             if (_cts != null) { CancelCurrentOperation(); return; }

             var selectedItems = FileList.SelectedItems.Cast<FileItem>().ToList();
             var items = selectedItems.Count > 0 ? selectedItems : _files.ToList();

             if (items.Count == 0) return;

             var dialog = new OpenFolderDialog { Title = "Select Extraction Folder" };
             if (dialog.ShowDialog() == true)
             {
                 await RunOperationAsync(async (token, progress) =>
                 {
                     int i = 0;
                     foreach(var item in items)
                     {
                         token.ThrowIfCancellationRequested();
                         string outPath = Path.Combine(dialog.FolderName, item.RelativePath);
                         Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);

                         using var s = GetStreamForItem(item);
                         if (s != null)
                         {
                             using var fs = new FileStream(outPath, FileMode.Create, FileAccess.Write, FileShare.None, 8192, FileOptions.Asynchronous);
                             await s.CopyToAsync(fs, token);
                         }

                         i++;
                         progress.Report((int)((float)i / items.Count * 100));
                     }
                 }, BtnExtractAll, "Extract Selected");

                 UpdateStatus($"Extracted {items.Count} files.");
             }
        }

        private void FileList_Drop(object sender, DragEventArgs e)
        {
             if (e.Data.GetDataPresent(DataFormats.FileDrop))
             {
                 string[]? f = (string[]?)e.Data.GetData(DataFormats.FileDrop);
                 if (f == null) return;

                 foreach(var path in f)
                 {
                     if (path.EndsWith(".gpck", StringComparison.OrdinalIgnoreCase)) LoadArchive(path);
                     else if (Directory.Exists(path)) _ = AddFolderAsync(path);
                     else AddFiles(new[] { path });
                 }
             }
        }

        private async Task RunOperationAsync(Func<CancellationToken, IProgress<int>, Task> action, Button btn, string defText)
        {
            _cts = new CancellationTokenSource();
            btn.Content = "Cancel";
            ProgressBar.Visibility = Visibility.Visible;
            ProgressBar.Value = 0;

            try
            {
                await action(_cts.Token, new Progress<int>(v => ProgressBar.Value = v));
                UpdateStatus("Operation completed.");
            }
            catch (OperationCanceledException)
            {
                UpdateStatus("Operation cancelled.");
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Operation failed: {ex.Message}", "Process Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                UpdateStatus("Operation failed.");
            }
            finally
            {
                _cts?.Dispose();
                _cts = null;
                btn.Content = defText;
                ProgressBar.Visibility = Visibility.Hidden;
            }
        }

        private void AddFiles(string[] paths)
        {
             foreach(var p in paths) {
                 if (File.Exists(p))
                 {
                     _files.Add(new FileItem
                     {
                         FilePath = p,
                         RelativePath = Path.GetFileName(p),
                         AssetId = AssetIdGenerator.Generate(Path.GetFileName(p)),
                         Size = FormatSize(new FileInfo(p).Length)
                     });
                 }
             }
             UpdateListCount();
        }

        private async Task AddFolderAsync(string f)
        {
            UpdateStatus($"Scanning folder: {f}");
            var map = await Task.Run(() => AssetPacker.BuildFileMap(f));

            Application.Current.Dispatcher.Invoke(() => {
                foreach(var kv in map)
                {
                    if (!_files.Any(x => x.FilePath == kv.Key))
                    {
                        _files.Add(new FileItem
                        {
                            FilePath = kv.Key,
                            RelativePath = kv.Value,
                            AssetId = AssetIdGenerator.Generate(kv.Value),
                            Size = FormatSize(new FileInfo(kv.Key).Length)
                        });
                    }
                }
                UpdateListCount();
                UpdateStatus($"Added folder content: {Path.GetFileName(f)}");
            });
        }

        private string FormatSize(long b)
        {
            if (b < 1024) return $"{b} B";
            if (b < 1024 * 1024) return $"{b/1024.0:F2} KB";
            return $"{b/(1024.0*1024.0):F2} MB";
        }

        private void UpdateStatus(string m) => TxtStatus.Text = m;
    }
}
