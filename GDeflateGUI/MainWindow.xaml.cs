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
        
        // Keep track of opened archives to Dispose them later
        private List<GDeflateArchive> _openArchives = new List<GDeflateArchive>();

        public MainWindow()
        {
            InitializeComponent();

            _files = new ObservableCollection<FileItem>();
            FileList.ItemsSource = _files;
            _processor = new GDeflateProcessor();

            CheckBackend();
            UpdateStatus($"Ready - .NET 10.0 Preview Mode");
        }

        protected override void OnClosed(EventArgs e)
        {
            // Cleanup memory mapped files
            foreach (var arch in _openArchives) arch.Dispose();
            base.OnClosed(e);
        }

        private void CheckBackend()
        {
            if (_processor.IsCpuLibraryAvailable())
            {
                TxtBackendStatus.Text = "✓ GDeflate Active";
            }
            else
            {
                TxtBackendStatus.Text = "❌ Backend Missing";
                MessageBox.Show("GDeflate.dll not found.", "Error", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        // --- 1. Archive Loading Logic ---

        private void BtnOpenArchive_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new OpenFileDialog { Filter = "Game Packages (*.gpck)|*.gpck", Title = "Inspect Archive" };
            if (dialog.ShowDialog() == true)
            {
                LoadArchive(dialog.FileName);
            }
        }

        private void LoadArchive(string path)
        {
            try
            {
                var archive = new GDeflateArchive(path);
                _openArchives.Add(archive); // Keep alive

                var info = archive.GetPackageInfo();
                
                // Add files to list
                foreach (var entry in info.Entries)
                {
                    // Find the raw entry to store for later extraction
                    archive.TryGetEntry(entry.PathHash.ToString(), out var rawEntry);
                    // Actually, we need to find by hash, let's use the helper
                    if (archive.TryGetEntry(entry.Path, out var correctEntry))
                    {
                        rawEntry = correctEntry;
                    }
                    else 
                    {
                        // Fallback if path lookup fails (shouldn't happen with valid NameTable)
                        continue;
                    }

                    _files.Add(new FileItem
                    {
                        IsArchiveEntry = true,
                        SourceArchive = archive,
                        EntryInfo = rawEntry,
                        RelativePath = entry.Path,
                        Size = FormatSize(entry.OriginalSize),
                        FilePath = path // Origin path
                    });
                }
                UpdateStatus($"Loaded {info.FileCount} files from {Path.GetFileName(path)}");
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to load archive: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        // --- 2. Preview Logic (The Asset Browser Part) ---

        private async void FileList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            var item = FileList.SelectedItem as FileItem;
            ResetPreview();

            if (item == null) return;

            UpdateStatus($"Selected: {item.RelativePath}");

            try
            {
                // Get a stream to the data (either from disk OR from inside the GDeflate archive)
                using var stream = GetStreamForItem(item);
                if (stream == null || stream.Length == 0) 
                {
                    TxtPreview.Visibility = Visibility.Visible;
                    TxtPreview.Text = "Empty File";
                    LblNoPreview.Visibility = Visibility.Collapsed;
                    return;
                }

                string ext = Path.GetExtension(item.RelativePath).ToLowerInvariant();

                if (IsImage(ext))
                {
                    await ShowImagePreview(stream);
                }
                else if (IsText(ext))
                {
                    await ShowTextPreview(stream);
                }
                else
                {
                    ShowHexPreview(stream, item.RelativePath);
                }
            }
            catch (Exception ex)
            {
                TxtPreview.Visibility = Visibility.Visible;
                TxtPreview.Text = $"Error previewing file:\n{ex.Message}";
                LblNoPreview.Visibility = Visibility.Collapsed;
            }
        }

        private Stream? GetStreamForItem(FileItem item)
        {
            if (item.IsArchiveEntry && item.SourceArchive != null && item.EntryInfo.HasValue)
            {
                // Zero-Copy extraction from archive (GDeflateStream)
                return item.SourceArchive.OpenRead(item.EntryInfo.Value);
            }
            else if (File.Exists(item.FilePath))
            {
                // Standard file read
                return File.OpenRead(item.FilePath);
            }
            return null;
        }

        private async Task ShowImagePreview(Stream source)
        {
            try
            {
                // WPF Images need MemoryStream.
                // This decompresses the GDeflate stream into RAM entirely for the UI to render.
                using var memory = new MemoryStream();
                await source.CopyToAsync(memory);
                memory.Position = 0;

                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.StreamSource = memory;
                bitmap.EndInit();
                bitmap.Freeze(); // Make thread safe

                ImgPreview.Source = bitmap;
                ImgPreview.Visibility = Visibility.Visible;
                LblNoPreview.Visibility = Visibility.Collapsed;
            }
            catch
            {
                // If not a standard image (e.g. DDS), fall back to HEX
                source.Position = 0;
                ShowHexPreview(source, "Image decode failed (Format not supported by WPF). Raw data:");
            }
        }

        private async Task ShowTextPreview(Stream source)
        {
            // Limit text preview to 10KB to prevent UI lag
            using var reader = new StreamReader(source, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, bufferSize: 1024, leaveOpen: true);
            char[] buffer = new char[10000];
            int read = await reader.ReadAsync(buffer, 0, buffer.Length);
            
            string text = new string(buffer, 0, read);
            if (source.Length > 10000) text += "\n\n[...Truncated (File too large)...]";

            TxtPreview.Text = text;
            TxtPreview.Visibility = Visibility.Visible;
            LblNoPreview.Visibility = Visibility.Collapsed;
        }

        private void ShowHexPreview(Stream source, string title)
        {
            // Read first 512 bytes for Hex Dump
            byte[] buffer = new byte[512];
            int read = source.Read(buffer, 0, buffer.Length);
            
            var sb = new StringBuilder();
            sb.AppendLine($"--- BINARY PREVIEW: {title} ---");
            sb.AppendLine($"Total Size: {source.Length} bytes");
            sb.AppendLine();

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
            TxtPreview.Text = "";
            HexPreview.Text = "";
        }

        private bool IsImage(string ext) => ext == ".png" || ext == ".jpg" || ext == ".jpeg" || ext == ".bmp" || ext == ".ico";
        private bool IsText(string ext) => ext == ".txt" || ext == ".json" || ext == ".xml" || ext == ".lua" || ext == ".ini" || ext == ".cs" || ext == ".js" || ext == ".log";


        // --- 3. Command Handlers ---

        private void BtnAddFiles_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new OpenFileDialog { Multiselect = true, Title = "Select Files to Pack" };
            if (dialog.ShowDialog() == true) AddFiles(dialog.FileNames);
        }

        private void BtnAddFolder_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new OpenFolderDialog { Title = "Select Folder to Pack" };
            if (dialog.ShowDialog() == true) _ = AddFolderAsync(dialog.FolderName);
        }

        private void BtnClear_Click(object sender, RoutedEventArgs e)
        {
            _files.Clear();
            foreach (var a in _openArchives) a.Dispose();
            _openArchives.Clear();
            ResetPreview();
            UpdateStatus("List cleared.");
        }

        private async void BtnCompress_Click(object sender, RoutedEventArgs e)
        {
            if (_cts != null) { CancelOperation(); return; }
            if (_files.Count == 0) return;

            // Filter: Can only pack "Loose" files, not existing archive entries
            var filesToPack = _files.Where(x => !x.IsArchiveEntry).DistinctBy(x => x.FilePath).ToDictionary(x => x.FilePath, x => x.RelativePath);

            if (filesToPack.Count == 0)
            {
                MessageBox.Show("No new files to pack. You cannot re-pack an opened archive directly (Extract first).", "Info");
                return;
            }

            var saveDialog = new SaveFileDialog { Filter = "Game Package (*.gpck)|*.gpck", FileName = "assets.gpck" };
            if (saveDialog.ShowDialog() == true)
            {
                await RunOperationAsync(async (token, progress) =>
                {
                    await Task.Run(() => _processor.CompressFilesToArchive(filesToPack, saveDialog.FileName, progress: progress, token: token));
                }, BtnCompress, "🚀 Pack to .gpck", "Compressing...", "Saved successfully.");
            }
        }

        private async void BtnExtractAll_Click(object sender, RoutedEventArgs e)
        {
            if (_cts != null) { CancelOperation(); return; }

            // Extract selected items, or all if nothing selected
            var itemsToExtract = FileList.SelectedItems.Count > 0 
                ? FileList.SelectedItems.Cast<FileItem>().ToList() 
                : _files.ToList();

            if (itemsToExtract.Count == 0) return;

            var folderDialog = new OpenFolderDialog { Title = "Select Output Folder" };
            if (folderDialog.ShowDialog() == true)
            {
                string outDir = folderDialog.FolderName;
                await RunOperationAsync(async (token, progress) =>
                {
                    await Task.Run(() =>
                    {
                        int total = itemsToExtract.Count;
                        int cur = 0;
                        byte[] buffer = new byte[65536]; // 64KB buffer

                        foreach (var item in itemsToExtract)
                        {
                            token.ThrowIfCancellationRequested();
                            
                            // Determine destination path
                            string destPath = Path.Combine(outDir, item.RelativePath.Replace('/', Path.DirectorySeparatorChar));
                            Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);

                            using var inStream = GetStreamForItem(item);
                            if (inStream != null)
                            {
                                using var outStream = File.Create(destPath);
                                inStream.CopyTo(outStream);
                            }
                            
                            cur++;
                            progress.Report((int)((cur / (float)total) * 100));
                        }
                    });
                }, BtnExtractAll, "⬇ Extract Selected", "Extracting...", "Extraction complete.");
            }
        }

        private void FileList_Drop(object sender, DragEventArgs e)
        {
            if (e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                var files = (string[])e.Data.GetData(DataFormats.FileDrop);
                foreach(var f in files)
                {
                    // If dropping a GPCK, open it in browser mode
                    if (Path.GetExtension(f).ToLower() == ".gpck") LoadArchive(f);
                    // Otherwise add to packing list
                    else AddFiles(new[] { f });
                }
            }
        }

        // --- Helpers ---

        private void CancelOperation()
        {
            _cts?.Cancel();
            UpdateStatus("Cancelling...");
        }

        private async Task RunOperationAsync(Func<CancellationToken, IProgress<int>, Task> action, Button triggerBtn, string defaultBtnText, string statusStart, string statusEnd)
        {
            ToggleUI(false);
            triggerBtn.IsEnabled = true;
            triggerBtn.Content = "Cancel";
            UpdateStatus(statusStart);
            ProgressBar.Visibility = Visibility.Visible;
            ProgressBar.Value = 0;

            _cts = new CancellationTokenSource();

            try
            {
                var progress = new Progress<int>(p => Dispatcher.Invoke(() => ProgressBar.Value = p));
                await action(_cts.Token, progress);
                UpdateStatus(statusEnd);
                MessageBox.Show(statusEnd, "Success", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (OperationCanceledException) { UpdateStatus("Operation cancelled."); }
            catch (Exception ex) { MessageBox.Show(ex.Message, "Error", MessageBoxButton.OK, MessageBoxImage.Error); }
            finally
            {
                _cts?.Dispose(); _cts = null;
                ToggleUI(true);
                triggerBtn.Content = defaultBtnText;
                ProgressBar.Visibility = Visibility.Hidden;
                GC.Collect();
            }
        }

        private void AddFiles(string[] paths)
        {
            foreach (var path in paths)
            {
                if (Directory.Exists(path)) _ = AddFolderAsync(path);
                else if (File.Exists(path) && !_files.Any(x => x.FilePath == path))
                {
                    _files.Add(new FileItem { FilePath = path, RelativePath = Path.GetFileName(path), Size = FormatSize(new FileInfo(path).Length) });
                }
            }
        }

        private async Task AddFolderAsync(string folderPath)
        {
            ToggleUI(false);
            UpdateStatus("Scanning folder...");
            try
            {
                var fileMap = await Task.Run(() => GDeflateProcessor.BuildFileMap(folderPath));
                foreach (var kvp in fileMap)
                {
                    if (!_files.Any(x => x.FilePath == kvp.Key))
                    {
                        string displayRelPath = kvp.Value.Replace('/', Path.DirectorySeparatorChar);
                        _files.Add(new FileItem { FilePath = kvp.Key, RelativePath = displayRelPath, Size = FormatSize(new FileInfo(kvp.Key).Length) });
                    }
                }
                UpdateStatus($"Total files: {_files.Count}");
            }
            finally { ToggleUI(true); }
        }

        private string FormatSize(long bytes) => bytes < 1024 ? $"{bytes} B" : $"{bytes / 1024.0:F2} KB";
        private void UpdateStatus(string msg) => TxtStatus.Text = msg;

        private void ToggleUI(bool enable)
        {
            // Simple toggle for main buttons, excluding Cancel button logic which is handled in RunOperation
            BtnAddFiles.IsEnabled = BtnAddFolder.IsEnabled = BtnClear.IsEnabled = BtnCompress.IsEnabled = BtnExtractAll.IsEnabled = BtnOpenArchive.IsEnabled = enable;
        }
    }
}
