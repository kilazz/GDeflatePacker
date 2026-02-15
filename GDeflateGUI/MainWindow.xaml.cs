using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using GDeflate.Core;
using Microsoft.Win32;

namespace GDeflateGUI
{
    public partial class MainWindow : Window
    {
        private ObservableCollection<FileItem> _files = new ObservableCollection<FileItem>();
        private GDeflateProcessor _processor;
        private CancellationTokenSource? _cts;
        private List<GDeflateArchive> _openArchives = new List<GDeflateArchive>();

        public MainWindow()
        {
            InitializeComponent();
            _files = new ObservableCollection<FileItem>();
            FileList.ItemsSource = _files;
            _processor = new GDeflateProcessor();
            CheckBackend();
            UpdateStatus($"Ready");
        }

        protected override void OnClosed(EventArgs e)
        {
            foreach (var arch in _openArchives) arch.Dispose();
            base.OnClosed(e);
        }

        private void CheckBackend()
        {
            var g = _processor.IsCpuLibraryAvailable();
            var z = ZstdCpuApi.IsAvailable();
            TxtBackendStatus.Text = $"GDeflate:{(g?"✓":"❌")} | Zstd:{(z?"✓":"❌")}";
        }

        private void BtnOpenArchive_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new OpenFileDialog { Filter = "Game Packages (*.gpck)|*.gpck", Title = "Inspect Archive" };
            if (dialog.ShowDialog() == true) LoadArchive(dialog.FileName);
        }

        private void LoadArchive(string path)
        {
            try
            {
                var archive = new GDeflateArchive(path);
                _openArchives.Add(archive);
                var info = archive.GetPackageInfo();
                
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
                    }
                }
                UpdateStatus($"Loaded {info.FileCount} assets from {Path.GetFileName(path)}");
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed: {ex.Message}", "Error");
            }
        }

        private async void FileList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            var item = FileList.SelectedItem as FileItem;
            ResetPreview();
            if (item == null) return;

            UpdateStatus($"Selected: {item.RelativePath} ({item.AssetId})");

            try
            {
                using var stream = GetStreamForItem(item);
                if (stream == null || stream.Length == 0) return;

                // Simple extension check for UI preview only
                string ext = Path.GetExtension(item.RelativePath).ToLowerInvariant();

                if (IsImage(ext)) await ShowImagePreview(stream);
                else if (IsText(ext)) await ShowTextPreview(stream);
                else ShowHexPreview(stream, item.RelativePath);
            }
            catch (Exception ex)
            {
                TxtPreview.Visibility = Visibility.Visible;
                TxtPreview.Text = $"Error: {ex.Message}";
                LblNoPreview.Visibility = Visibility.Collapsed;
            }
        }

        private Stream? GetStreamForItem(FileItem item)
        {
            if (item.IsArchiveEntry && item.SourceArchive != null && item.EntryInfo.HasValue)
                return item.SourceArchive.OpenRead(item.EntryInfo.Value);
            else if (File.Exists(item.FilePath))
                return File.OpenRead(item.FilePath);
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
                ShowHexPreview(source, "Image decode failed.");
            }
        }

        private async Task ShowTextPreview(Stream source)
        {
            using var reader = new StreamReader(source, Encoding.UTF8, true, 1024, true);
            char[] buffer = new char[5000];
            int read = await reader.ReadAsync(buffer, 0, buffer.Length);
            TxtPreview.Text = new string(buffer, 0, read);
            TxtPreview.Visibility = Visibility.Visible;
            LblNoPreview.Visibility = Visibility.Collapsed;
        }

        private void ShowHexPreview(Stream source, string title)
        {
            byte[] buffer = new byte[512];
            int read = source.Read(buffer, 0, buffer.Length);
            var sb = new StringBuilder();
            sb.AppendLine($"--- BINARY PREVIEW ---");
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

        private bool IsImage(string ext) => ext == ".png" || ext == ".jpg" || ext == ".jpeg" || ext == ".bmp";
        private bool IsText(string ext) => ext == ".txt" || ext == ".json" || ext == ".xml" || ext == ".lua" || ext == ".ini";

        private void BtnAddFiles_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new OpenFileDialog { Multiselect = true, Title = "Select Files" };
            if (dialog.ShowDialog() == true) AddFiles(dialog.FileNames);
        }

        private void BtnAddFolder_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new OpenFolderDialog();
            if (dialog.ShowDialog() == true) _ = AddFolderAsync(dialog.FolderName);
        }

        private void BtnClear_Click(object sender, RoutedEventArgs e)
        {
            _files.Clear();
            foreach (var a in _openArchives) a.Dispose();
            _openArchives.Clear();
            ResetPreview();
        }

        private async void BtnCompress_Click(object sender, RoutedEventArgs e)
        {
            if (_cts != null) { _cts.Cancel(); return; }
            var filesToPack = _files.Where(x => !x.IsArchiveEntry).DistinctBy(x => x.FilePath).ToDictionary(x => x.FilePath, x => x.RelativePath);
            if (filesToPack.Count == 0) return;

            var saveDialog = new SaveFileDialog { Filter = "Game Package (*.gpck)|*.gpck", FileName = "assets.gpck" };
            if (saveDialog.ShowDialog() == true)
            {
                await RunOperationAsync(async (token, progress) =>
                {
                    await Task.Run(() => _processor.CompressFilesToArchive(filesToPack, saveDialog.FileName, p: progress, t: token));
                }, BtnCompress, "🚀 Pack");
            }
        }

        private async void BtnExtractAll_Click(object sender, RoutedEventArgs e)
        {
            // Simplified Extraction logic for brevity, uses AssetID mapping internally
             if (_cts != null) { _cts.Cancel(); return; }
             var items = FileList.SelectedItems.Count > 0 ? FileList.SelectedItems.Cast<FileItem>().ToList() : _files.ToList();
             var dialog = new OpenFolderDialog();
             if (dialog.ShowDialog() == true)
             {
                 await RunOperationAsync(async (token, progress) =>
                 {
                     await Task.Run(() => {
                         int i=0;
                         foreach(var item in items)
                         {
                             token.ThrowIfCancellationRequested();
                             string outPath = Path.Combine(dialog.FolderName, item.RelativePath);
                             Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);
                             using var s = GetStreamForItem(item);
                             using var fs = File.Create(outPath);
                             s?.CopyTo(fs);
                             progress.Report((int)(++i / (float)items.Count * 100));
                         }
                     });
                 }, BtnExtractAll, "Extract");
             }
        }

        private void FileList_Drop(object sender, DragEventArgs e)
        {
             if (e.Data.GetDataPresent(DataFormats.FileDrop))
             {
                 var f = (string[])e.Data.GetData(DataFormats.FileDrop);
                 if (f[0].EndsWith(".gpck")) LoadArchive(f[0]);
                 else AddFiles(f);
             }
        }

        private async Task RunOperationAsync(Func<CancellationToken, IProgress<int>, Task> action, Button btn, string defText)
        {
            _cts = new CancellationTokenSource();
            btn.Content = "Cancel";
            ProgressBar.Visibility = Visibility.Visible;
            try { await action(_cts.Token, new Progress<int>(v => ProgressBar.Value = v)); }
            catch { }
            finally { _cts = null; btn.Content = defText; ProgressBar.Visibility = Visibility.Hidden; }
        }

        private void AddFiles(string[] paths)
        {
             foreach(var p in paths) {
                 if (File.Exists(p)) _files.Add(new FileItem { FilePath = p, RelativePath = Path.GetFileName(p), AssetId = AssetIdGenerator.Generate(Path.GetFileName(p)), Size = FormatSize(new FileInfo(p).Length)});
                 else if (Directory.Exists(p)) _ = AddFolderAsync(p);
             }
        }

        private async Task AddFolderAsync(string f)
        {
            var map = await Task.Run(() => GDeflateProcessor.BuildFileMap(f));
            foreach(var kv in map)
                if (!_files.Any(x=>x.FilePath == kv.Key))
                    _files.Add(new FileItem { FilePath = kv.Key, RelativePath = kv.Value, AssetId = AssetIdGenerator.Generate(kv.Value), Size = FormatSize(new FileInfo(kv.Key).Length)});
        }

        private string FormatSize(long b) => b < 1024 ? $"{b} B" : $"{b/1024.0:F2} KB";
        private void UpdateStatus(string m) => TxtStatus.Text = m;
    }
}