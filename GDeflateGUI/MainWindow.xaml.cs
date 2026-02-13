using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using GDeflate.Core;
using Microsoft.Win32;

namespace GDeflateGUI
{
    public partial class MainWindow : Window
    {
        private ObservableCollection<FileItem> _files = new ObservableCollection<FileItem>();
        private GDeflateProcessor _processor;
        private CancellationTokenSource? _cts;

        public MainWindow()
        {
            InitializeComponent();

            // Bind data
            FileList.ItemsSource = _files;
            _processor = new GDeflateProcessor();

            CheckBackend();
            UpdateStatus($"Ready - Running on WPF (.NET 10) x64");
        }

        private void CheckBackend()
        {
            if (_processor.IsCpuLibraryAvailable())
            {
                TxtBackendStatus.Text = "✓ Backend Active";
                TxtBackendStatus.Foreground = System.Windows.Media.Brushes.Green;
                TxtBackendStatus.ToolTip = "GDeflateCPU.dll loaded successfully.";
            }
            else
            {
                string baseDir = AppContext.BaseDirectory;
                string dllPath = Path.Combine(baseDir, "GDeflateCPU.dll");
                string libsDllPath = Path.Combine(baseDir, "libs", "GDeflateCPU.dll");

                string? targetPath = File.Exists(dllPath) ? dllPath : (File.Exists(libsDllPath) ? libsDllPath : null);
                string errorDetail = "Unknown error";

                if (targetPath != null)
                {
                    try { NativeLibrary.Load(targetPath); }
                    catch (Exception ex) { errorDetail = ex.Message; }
                }
                else
                {
                    errorDetail = $"File not found. Searched in:\n1. {dllPath}\n2. {libsDllPath}";
                }

                TxtBackendStatus.Text = "❌ Backend Error";
                TxtBackendStatus.Foreground = System.Windows.Media.Brushes.Red;
                TxtBackendStatus.ToolTip = errorDetail;

                MessageBox.Show($"Could not load GDeflateCPU.dll.\n\nDetails: {errorDetail}", "Backend Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        // --- Event Handlers ---

        private void BtnAddFiles_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new OpenFileDialog { Multiselect = true, Title = "Select Files" };
            if (dialog.ShowDialog() == true) AddFiles(dialog.FileNames);
        }

        private void BtnAddFolder_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new OpenFolderDialog { Title = "Select Folder to Add" };
            if (dialog.ShowDialog() == true) _ = AddFolderAsync(dialog.FolderName);
        }

        private void BtnClear_Click(object sender, RoutedEventArgs e)
        {
            _files.Clear();
            ComboFormat.SelectedIndex = 0;
            UpdateStatus("List cleared.");
        }

        private async void BtnCompress_Click(object sender, RoutedEventArgs e)
        {
            // If already running, treat as Cancel
            if (_cts != null)
            {
                _cts.Cancel();
                BtnCompress.Content = "Cancelling...";
                BtnCompress.IsEnabled = false; // Prevent double click
                return;
            }

            if (_files.Count == 0)
            {
                MessageBox.Show("Please add files to the list first.", "Info", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            bool isZip = ComboFormat.SelectedIndex == 1;
            string ext = isZip ? ".zip" : ".gdef";
            string filter = isZip ? "Zip Archive (*.zip)|*.zip" : "GDeflate File (*.gdef)|*.gdef";

            if (!isZip && _files.Count > 1)
            {
                var result = MessageBox.Show(
                    "Switch to Archive (.zip) mode automatically?", "Format Mismatch", MessageBoxButton.YesNo, MessageBoxImage.Question);
                if (result == MessageBoxResult.Yes) { ComboFormat.SelectedIndex = 1; isZip = true; ext = ".zip"; filter = "Zip Archive (*.zip)|*.zip"; }
                else return;
            }

            var saveDialog = new SaveFileDialog { Filter = filter, FileName = isZip ? "archive.zip" : Path.GetFileName(_files[0].FilePath) + ".gdef" };

            if (saveDialog.ShowDialog() == true)
            {
                ToggleUI(false);
                BtnCompress.Content = "Cancel"; // Switch button to Cancel mode
                BtnCompress.IsEnabled = true; // Keep enabled to allow cancelling

                UpdateStatus("Compressing...");
                SetProgress(0, true);

                _cts = new CancellationTokenSource();

                try
                {
                    // Create mapping: Full Path -> Relative Path
                    var fileMap = new Dictionary<string, string>();
                    foreach(var item in _files)
                    {
                        // Avoid duplicates if user added same file twice
                        if(!fileMap.ContainsKey(item.FilePath))
                        {
                            fileMap.Add(item.FilePath, item.RelativePath);
                        }
                    }

                    string outFile = saveDialog.FileName;
                    var progress = new Progress<int>(p => SetProgress(p, true));

                    await Task.Run(() => _processor.CompressFilesToArchive(fileMap, outFile, ext, progress, _cts.Token), _cts.Token);

                    UpdateStatus($"Saved to {Path.GetFileName(outFile)}");
                    MessageBox.Show("Compression finished successfully!", "Success", MessageBoxButton.OK, MessageBoxImage.Information);
                }
                catch (OperationCanceledException)
                {
                    UpdateStatus("Operation cancelled.");
                    // Clean up partial file if needed
                    try { if (File.Exists(saveDialog.FileName)) File.Delete(saveDialog.FileName); } catch { }
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"Compression Failed:\n{ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                    UpdateStatus("Error occurred.");
                }
                finally
                {
                    _cts.Dispose();
                    _cts = null;
                    ToggleUI(true);
                    BtnCompress.Content = "Start Compression"; // Reset text
                    SetProgress(0, false);
                    GC.Collect();
                    GC.WaitForPendingFinalizers();
                }
            }
        }

        private async void BtnDecompress_Click(object sender, RoutedEventArgs e)
        {
            // If already running, treat as Cancel
            if (_cts != null)
            {
                _cts.Cancel();
                BtnDecompress.Content = "Cancelling...";
                BtnDecompress.IsEnabled = false;
                return;
            }

            var openDialog = new OpenFileDialog
            {
                Multiselect = true,
                Filter = "Archives (*.gdef, *.zip)|*.gdef;*.zip|All Files|*.*",
                Title = "Select Archives"
            };

            if (openDialog.ShowDialog() == true)
            {
                var folderDialog = new OpenFolderDialog { Title = "Select Output Folder" };

                if (folderDialog.ShowDialog() == true)
                {
                    ToggleUI(false);
                    BtnDecompress.Content = "Cancel";
                    BtnDecompress.IsEnabled = true;

                    UpdateStatus("Decompressing...");
                    SetProgress(0, true);

                    _cts = new CancellationTokenSource();

                    try
                    {
                        string outDir = folderDialog.FolderName;
                        var progress = new Progress<int>(p => SetProgress(p, true));
                        var files = openDialog.FileNames;
                        int totalFiles = files.Length;

                        await Task.Run(() =>
                        {
                            for(int i = 0; i < totalFiles; i++)
                            {
                                _cts.Token.ThrowIfCancellationRequested();
                                _processor.DecompressArchive(files[i], outDir, progress, _cts.Token);
                            }
                        }, _cts.Token);

                        UpdateStatus("Decompression complete.");
                        MessageBox.Show($"Extraction complete to:\n{outDir}", "Success", MessageBoxButton.OK, MessageBoxImage.Information);
                    }
                    catch (OperationCanceledException)
                    {
                        UpdateStatus("Operation cancelled.");
                    }
                    catch (Exception ex)
                    {
                        MessageBox.Show($"Decompression Failed:\n{ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                        UpdateStatus("Error occurred.");
                    }
                    finally
                    {
                        _cts.Dispose();
                        _cts = null;
                        ToggleUI(true);
                        BtnDecompress.Content = "Extract / Decompress";
                        SetProgress(0, false);
                        GC.Collect();
                        GC.WaitForPendingFinalizers();
                    }
                }
            }
        }

        private void FileList_Drop(object sender, DragEventArgs e)
        {
            if (e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                string[] files = (string[])e.Data.GetData(DataFormats.FileDrop);
                AddFiles(files);
            }
        }

        // --- Helpers ---

        private void AddFiles(string[] paths)
        {
            foreach (var path in paths)
            {
                if (Directory.Exists(path))
                {
                    _ = AddFolderAsync(path);
                }
                else if (File.Exists(path))
                {
                    if (!_files.Any(x => x.FilePath == path))
                    {
                        long size = 0;
                        try { size = new FileInfo(path).Length; } catch { }
                        _files.Add(new FileItem { FilePath = path, RelativePath = Path.GetFileName(path), Size = FormatSize(size) });
                    }
                }
            }
            if (_files.Count > 1 && ComboFormat.SelectedIndex == 0) ComboFormat.SelectedIndex = 1;
            UpdateStatus($"Total files: {_files.Count}");
        }

        private async Task AddFolderAsync(string folderPath)
        {
            ToggleUI(false);
            UpdateStatus("Scanning folder...");
            try
            {
                var files = await Task.Run(() => Directory.GetFiles(folderPath, "*.*", SearchOption.AllDirectories));

                // When adding a folder, we want to preserve the folder structure.
                // We use the parent directory of the selected folder as the root for relative paths,
                // so the selected folder itself is included in the archive structure.
                string rootForRelative = Path.GetDirectoryName(folderPath) ?? folderPath;

                foreach (var file in files)
                {
                    if (!_files.Any(x => x.FilePath == file))
                    {
                        long size = 0;
                        try { size = new FileInfo(file).Length; } catch { }

                        string relativePath = Path.GetRelativePath(rootForRelative, file);
                        _files.Add(new FileItem { FilePath = file, RelativePath = relativePath, Size = FormatSize(size) });
                    }
                }

                if (_files.Count > 1 && ComboFormat.SelectedIndex == 0) ComboFormat.SelectedIndex = 1;
                UpdateStatus($"Total files: {_files.Count}");
            }
            finally { ToggleUI(true); }
        }

        private string FormatSize(long bytes)
        {
            string[] suffixes = { "B", "KB", "MB", "GB" };
            int counter = 0;
            decimal number = (decimal)bytes;
            while (Math.Round(number / 1024) >= 1 && counter < suffixes.Length - 1)
            {
                number = number / 1024;
                counter++;
            }
            return string.Format("{0:n1} {1}", number, suffixes[counter]);
        }

        private void UpdateStatus(string msg) => TxtStatus.Text = msg;

        private void SetProgress(int value, bool visible)
        {
            Dispatcher.Invoke(() =>
            {
                ProgressBar.Visibility = visible ? Visibility.Visible : Visibility.Hidden;
                ProgressBar.Value = value;
            });
        }

        private void ToggleUI(bool enable)
        {
            BtnAddFiles.IsEnabled = enable;
            BtnAddFolder.IsEnabled = enable;
            BtnClear.IsEnabled = enable;
            ComboFormat.IsEnabled = enable;

            // Note: Compress/Decompress buttons are managed individually during operations
            // to allow for cancellation interactions.
            if(enable)
            {
                BtnCompress.IsEnabled = true;
                BtnDecompress.IsEnabled = true;
            }
            else
            {
                // When UI is disabled (processing), we disable everything EXCEPT the active operation button
                // This logic is handled inside the specific click handlers
                BtnAddFiles.IsEnabled = false;
                BtnAddFolder.IsEnabled = false;
                BtnClear.IsEnabled = false;
                ComboFormat.IsEnabled = false;

                // If Compress is running, disable Decompress, and vice versa
                if (BtnCompress.Content.ToString() == "Cancel") BtnDecompress.IsEnabled = false;
                if (BtnDecompress.Content.ToString() == "Cancel") BtnCompress.IsEnabled = false;
            }
        }
    }
}
