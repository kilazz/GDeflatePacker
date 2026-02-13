using System;
using System.IO;
using System.Diagnostics;
using System.Collections.Generic;
using GDeflate.Core; // Updated namespace

namespace GDeflateCLI
{
    class Program
    {
        static void Main(string[] args)
        {
            // 1. Validate arguments
            if (args.Length == 0)
            {
                ShowHelp();
                return;
            }

            // 2. Ensure the core CPU library exists
            if (!GDeflateCpuApi.IsAvailable())
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine("Error: GDeflateCPU.dll not found in the application directory.");
                Console.ResetColor();
                return;
            }

            string command = args[0].ToLower();

            try
            {
                switch (command)
                {
                    case "compress":
                    case "-c":
                        HandleCompress(args);
                        break;
                    case "decompress":
                    case "-d":
                        HandleDecompress(args);
                        break;
                    case "help":
                    case "--help":
                    case "-h":
                    case "/?":
                        ShowHelp();
                        break;
                    default:
                        Console.WriteLine($"Unknown command: {command}");
                        ShowHelp();
                        break;
                }
            }
            catch (Exception ex)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"\nCritical Error: {ex.Message}");
                if (ex.InnerException != null)
                {
                    Console.WriteLine($"Details: {ex.InnerException.Message}");
                }
                Console.ResetColor();
            }
        }

        static void HandleCompress(string[] args)
        {
            if (args.Length < 2)
            {
                Console.WriteLine("Usage: GDeflateCLI compress <file/folder> [output_path]");
                return;
            }

            string inputPath = args[1];

            // Check if input exists
            if (!File.Exists(inputPath) && !Directory.Exists(inputPath))
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"Error: Input path not found: {inputPath}");
                Console.ResetColor();
                return;
            }

            // Determine output path if not provided
            string outputPath;
            if (args.Length > 2)
            {
                outputPath = args[2];
            }
            else
            {
                if (File.Exists(inputPath))
                {
                    // Single file: image.png -> image.png.gdef
                    // We use GetFileName (not WithoutExtension) to preserve the original format
                    outputPath = inputPath + ".gdef";
                }
                else
                {
                    // Folder: MyFolder -> MyFolder.zip
                    outputPath = Path.GetFileName(Path.TrimEndingDirectorySeparator(inputPath)) + ".zip";
                }
            }

            var processor = new GDeflateProcessor();
            Stopwatch sw = Stopwatch.StartNew();

            if (File.Exists(inputPath))
            {
                Console.WriteLine($"Compressing file: {Path.GetFileName(inputPath)}...");
                // Single file map
                var fileMap = new Dictionary<string, string>
                {
                    { inputPath, Path.GetFileName(inputPath) }
                };
                processor.CompressFilesToArchive(fileMap, outputPath, ".gdef");
                Console.WriteLine($"Saved to: {outputPath}");
            }
            else if (Directory.Exists(inputPath))
            {
                Console.WriteLine($"Scanning folder: {inputPath}...");
                string[] files = Directory.GetFiles(inputPath, "*.*", SearchOption.AllDirectories);

                if (files.Length == 0)
                {
                    Console.WriteLine("Folder is empty. Nothing to compress.");
                    return;
                }

                Console.WriteLine($"Found {files.Length} files. Creating archive...");

                // Build relative paths map
                var fileMap = new Dictionary<string, string>();
                string rootDir = Path.GetFullPath(inputPath);

                // Ensure rootDir ends with separator to avoid partial matches on names
                if (!rootDir.EndsWith(Path.DirectorySeparatorChar.ToString()))
                    rootDir += Path.DirectorySeparatorChar;

                foreach (var file in files)
                {
                    string fullPath = Path.GetFullPath(file);
                    // Create relative path for ZIP entry (e.g., SubFolder/Image.png)
                    string relativePath = Path.GetRelativePath(inputPath, fullPath);
                    fileMap[fullPath] = relativePath;
                }

                processor.CompressFilesToArchive(fileMap, outputPath, ".zip");
                Console.WriteLine($"Archive created: {outputPath}");
            }

            sw.Stop();
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine($"Operation completed in {sw.Elapsed.TotalSeconds:F2} seconds.");
            Console.ResetColor();
        }

        static void HandleDecompress(string[] args)
        {
            if (args.Length < 2)
            {
                Console.WriteLine("Usage: GDeflateCLI decompress <archive/file> [output_path]");
                return;
            }

            string inputPath = args[1];

            if (!File.Exists(inputPath))
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"Error: File not found: {inputPath}");
                Console.ResetColor();
                return;
            }

            var processor = new GDeflateProcessor();
            Stopwatch sw = Stopwatch.StartNew();
            string ext = Path.GetExtension(inputPath).ToLower();

            if (ext == ".zip")
            {
                // ZIP Mode
                // If output not provided, create a folder with the archive name
                string outputDir = args.Length > 2
                    ? args[2]
                    : Path.Combine(Path.GetDirectoryName(Path.GetFullPath(inputPath)) ?? ".", Path.GetFileNameWithoutExtension(inputPath));

                Console.WriteLine($"Extracting archive: {Path.GetFileName(inputPath)}...");
                processor.DecompressArchive(inputPath, outputDir);
                Console.WriteLine($"Extracted to: {outputDir}");
            }
            else if (ext == ".gdef")
            {
                // GDEF Mode (Single file)
                string outputPath;

                if (args.Length > 2)
                {
                    // User provided output path
                    // If it's a directory, put the file inside it
                    if (Directory.Exists(args[2]))
                    {
                        outputPath = Path.Combine(args[2], Path.GetFileNameWithoutExtension(inputPath));
                    }
                    else
                    {
                        outputPath = args[2];
                    }
                }
                else
                {
                    // Default: Remove .gdef extension
                    // image.png.gdef -> image.png
                    outputPath = Path.ChangeExtension(inputPath, null);
                }

                Console.WriteLine($"Decompressing file: {Path.GetFileName(inputPath)}...");
                processor.DecompressFile(inputPath, outputPath);
                Console.WriteLine($"Saved as: {outputPath}");
            }
            else
            {
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine($"Unknown extension: {ext}");
                Console.WriteLine("Supported formats: .zip (Archive), .gdef (Single File)");
                Console.ResetColor();
                return;
            }

            sw.Stop();
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine($"Done in {sw.Elapsed.TotalSeconds:F2}s!");
            Console.ResetColor();
        }

        static void ShowHelp()
        {
            Console.WriteLine("=========================================");
            Console.WriteLine("  GDeflate CLI Tool (CPU Version)        ");
            Console.WriteLine("=========================================");
            Console.WriteLine("Usage:");
            Console.WriteLine("Compress a file:");
            Console.WriteLine("GDeflateCLI compress <file> [output.gdef]");
            Console.WriteLine("");
            Console.WriteLine("Compress a folder (to ZIP):");
            Console.WriteLine("GDeflateCLI compress <folder> [output.zip]");
            Console.WriteLine("");
            Console.WriteLine("Decompress:");
            Console.WriteLine("GDeflateCLI decompress <file.gdef|archive.zip> [output_path]");
            Console.WriteLine("");
            Console.WriteLine("Examples:");
            Console.WriteLine("GDeflateCLI compress texture.png");
            Console.WriteLine("GDeflateCLI compress \"C:\\GameAssets\" assets.zip");
            Console.WriteLine("GDeflateCLI decompress texture.png.gdef");
        }
    }
}
