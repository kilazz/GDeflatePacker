using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
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

            // Inject format options programmatically since XAML is static
            ComboFormat.Items.Clear();
            ComboFormat.Items.Add(new ComboBoxItem { Content = "Single File (.gdef)" });
            ComboFormat.Items.Add(new ComboBoxItem { Content = "Game Package (.gpck)" });
            ComboFormat.SelectedIndex = 1; // Default to Package

            _files = new ObservableCollection<FileItem>();
            FileList.ItemsSource = _files;
            _processor = new GDeflateProcessor();

            CheckBackend();
            UpdateStatus($"Ready - Running on WPF (.NET 10.0) x64");
        }

        private void CheckBackend()
        {
            if (_processor.IsCpuLibraryAvailable())
            {
                TxtBackendStatus.Text = "✓ Backend Active";
                TxtBackendStatus.Foreground = System.Windows.Media.Brushes.Green;
                TxtBackendStatus.ToolTip = "GDeflate.dll loaded successfully.";
            }
            else
            {
                TxtBackendStatus.Text = "❌ Backend Missing";
                TxtBackendStatus.Foreground = System.Windows.Media.Brushes.Red;
                TxtBackendStatus.ToolTip = "GDeflate.dll not found.";

                string msg = "GDeflate.dll was not found.\n\n" +
                             "The Microsoft GDeflate build produces a Static Library (.lib) by default, " +
                             "but this application requires a Dynamic Library (.dll).\n\n" +
                             "Please build GDeflate as a DLL and place it in the application folder or 'libs' subdirectory.";

                MessageBox.Show(msg, "Backend Configuration Error", MessageBoxButton.OK, MessageBoxImage.Warning);
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
            ComboFormat.SelectedIndex = 1;
            UpdateStatus("List cleared.");
        }

        private async void BtnCompress_Click(object sender, RoutedEventArgs e)
        {
            if (_cts != null) { CancelOperation(); return; }

            if (_files.Count == 0)
            {
                MessageBox.Show("Please add files to the list first.", "Info", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            if (!_processor.IsCpuLibraryAvailable())
            {
                CheckBackend(); // Show the detailed error again
                return;
            }

            int mode = ComboFormat.SelectedIndex;
            bool isGpck = mode == 1;
            string ext = isGpck ? ".gpck" : ".gdef";

            string filter = "All files (*.*)|*.*";
            if (isGpck) filter = "Game Package (*.gpck)|*.gpck";
            else filter = "GDeflate File (*.gdef)|*.gdef";

            // Auto-switch to package mode if multiple files and single mode is selected
            if (!isGpck && _files.Count > 1)
            {
                if (MessageBox.Show("Switch to Game Package (.gpck) for multiple files?", "Format Mismatch", MessageBoxButton.YesNo) == MessageBoxResult.Yes)
                {
                    ComboFormat.SelectedIndex = 1; // Index 1 is now GPCK
                    isGpck = true;
                    ext = ".gpck";
                    filter = "Game Package (*.gpck)|*.gpck";
                }
                else return;
            }

            string defaultName = _files.Count == 1 ? Path.GetFileName(_files[0].FilePath) + ext : "assets" + ext;
            var saveDialog = new SaveFileDialog { Filter = filter, FileName = defaultName };

            if (saveDialog.ShowDialog() == true)
            {
                await RunOperationAsync(async (token, progress) =>
                {
                    var fileMap = _files.DistinctBy(x => x.FilePath).ToDictionary(x => x.FilePath, x => x.RelativePath);
                    await Task.Run(() => _processor.CompressFilesToArchive(fileMap, saveDialog.FileName, ext, progress, token));
                }, BtnCompress, "Start Compression", "Compressing...", $"Saved to {Path.GetFileName(saveDialog.FileName)}");
            }
        }

        private async void BtnDecompress_Click(object sender, RoutedEventArgs e)
        {
            if (_cts != null) { CancelOperation(); return; }

            if (!_processor.IsCpuLibraryAvailable())
            {
                CheckBackend(); // Show the detailed error again
                return;
            }

            var openDialog = new OpenFileDialog { Multiselect = true, Filter = "GDeflate Archives (*.gdef, *.gpck)|*.gdef;*.gpck", Title = "Select Archives" };

            if (openDialog.ShowDialog() == true)
            {
                var folderDialog = new OpenFolderDialog { Title = "Select Output Folder" };
                if (folderDialog.ShowDialog() == true)
                {
                    await RunOperationAsync(async (token, progress) =>
                    {
                        foreach (var file in openDialog.FileNames)
                        {
                            token.ThrowIfCancellationRequested();
                            await Task.Run(() => _processor.DecompressArchive(file, folderDialog.FolderName, progress, token));
                        }
                    }, BtnDecompress, "Extract / Decompress", "Decompressing...", "Decompression complete.");
                }
            }
        }

        private void FileList_Drop(object sender, DragEventArgs e)
        {
            if (e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                AddFiles((string[])e.Data.GetData(DataFormats.FileDrop));
            }
        }

        // --- Core Logic Helpers ---

        private void CancelOperation()
        {
            _cts?.Cancel();
            UpdateStatus("Cancelling...");
        }

        private async Task RunOperationAsync(Func<CancellationToken, IProgress<int>, Task> action, System.Windows.Controls.Button triggerBtn, string defaultBtnText, string statusStart, string statusEnd)
        {
            ToggleUI(false);
            triggerBtn.IsEnabled = true;
            triggerBtn.Content = "Cancel";
            UpdateStatus(statusStart);
            SetProgress(0, true);

            _cts = new CancellationTokenSource();

            try
            {
                var progress = new Progress<int>(p => SetProgress(p, true));
                await action(_cts.Token, progress);
                UpdateStatus(statusEnd);
                MessageBox.Show(statusEnd, "Success", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (OperationCanceledException) { UpdateStatus("Operation cancelled."); }
            catch (OutOfMemoryException oom)
            {
                MessageBox.Show($"Not enough RAM to process this file.\nDetails: {oom.Message}", "Memory Error", MessageBoxButton.OK, MessageBoxImage.Error);
                UpdateStatus("Error: Out of Memory");
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error:\n{ex.Message}", "Failed", MessageBoxButton.OK, MessageBoxImage.Error);
                UpdateStatus("Error occurred.");
            }
            finally
            {
                _cts?.Dispose(); _cts = null;
                ToggleUI(true);
                triggerBtn.Content = defaultBtnText;
                SetProgress(0, false);
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
            // Auto switch to GPCK if multiple
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
                string root = Path.GetDirectoryName(folderPath) ?? folderPath;

                foreach (var file in files)
                {
                    if (!_files.Any(x => x.FilePath == file))
                    {
                        _files.Add(new FileItem { FilePath = file, RelativePath = Path.GetRelativePath(root, file), Size = FormatSize(new FileInfo(file).Length) });
                    }
                }
                if (_files.Count > 1 && ComboFormat.SelectedIndex == 0) ComboFormat.SelectedIndex = 1;
                UpdateStatus($"Total files: {_files.Count}");
            }
            finally { ToggleUI(true); }
        }

        private string FormatSize(long bytes) => $"{bytes / 1024.0:F2} KB";
        private void UpdateStatus(string msg) => TxtStatus.Text = msg;
        private void SetProgress(int value, bool visible) => Dispatcher.Invoke(() => { ProgressBar.Visibility = visible ? Visibility.Visible : Visibility.Hidden; ProgressBar.Value = value; });

        private void ToggleUI(bool enable)
        {
            BtnAddFiles.IsEnabled = BtnAddFolder.IsEnabled = BtnClear.IsEnabled = ComboFormat.IsEnabled = BtnCompress.IsEnabled = BtnDecompress.IsEnabled = enable;
        }
    }
}
