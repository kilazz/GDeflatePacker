using System;
using System.IO;
using System.Diagnostics;
using System.Collections.Generic;
using GDeflate.Core;

namespace GDeflateCLI
{
    class Program
    {
        static void Main(string[] args)
        {
            if (args.Length == 0)
            {
                ShowHelp();
                return;
            }

            // GDeflateCpuApi.IsAvailable check is not strictly needed for 'info' command if it just reads headers,
            // but the processor validates it generally. For Info we can skip it if we wanted, but keeping it for consistency.
            if (!GDeflateCpuApi.IsAvailable())
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine("Error: GDeflate.dll not found.");
                Console.WriteLine("Note: The standard Microsoft GDeflate build outputs a static library (.lib).");
                Console.WriteLine("      You must compile it as a Dynamic Link Library (.dll) for this tool.");
                Console.ResetColor();
                return;
            }

            try
            {
                string command = args[0].ToLower();
                switch (command)
                {
                    case "compress":
                    case "-c":
                        RunCompress(args);
                        break;
                    case "decompress":
                    case "-d":
                        RunDecompress(args);
                        break;
                    case "info":
                    case "-i":
                        RunInfo(args);
                        break;
                    case "help":
                    case "--help":
                    case "-h":
                        ShowHelp();
                        break;
                    default:
                        Console.WriteLine($"Unknown command: {command}");
                        ShowHelp();
                        break;
                }
            }
            catch (OutOfMemoryException oom)
            {
                 Console.ForegroundColor = ConsoleColor.Red;
                 Console.WriteLine($"\n[Memory Error] {oom.Message}");
                 Console.ResetColor();
            }
            catch (Exception ex)
            {
                PrintError(ex);
            }
        }

        static void RunCompress(string[] args)
        {
            if (args.Length < 2)
            {
                Console.WriteLine("Usage: GDeflateCLI compress <file/folder> [output_path]");
                return;
            }

            string inputPath = args[1];
            if (!ValidateInput(inputPath)) return;

            // Logic Fix: Check output extension to determine format, regardless of input being a file or folder.
            bool inputIsFolder = Directory.Exists(inputPath);
            string outputPath = args.Length > 2 ? args[2] : DeriveOutputPath(inputPath, true);

            bool outputIsGpck = outputPath.EndsWith(".gpck", StringComparison.OrdinalIgnoreCase);

            var processor = new GDeflateProcessor();

            Console.WriteLine($"Processing: {Path.GetFileName(inputPath)} -> {Path.GetFileName(outputPath)}");

            string modeStr = "Single File (.gdef)";
            if (outputIsGpck) modeStr = "Game Package (.gpck) [DirectStorage Aligned]";
            Console.WriteLine($"Mode: {modeStr}");

            var sw = Stopwatch.StartNew();

            Dictionary<string, string> map;

            if (inputIsFolder)
            {
                map = BuildDirectoryMap(inputPath);
                if (map.Count == 0)
                {
                    Console.WriteLine("Folder is empty.");
                    return;
                }

                // Folders MUST be Packages now.
                if (!outputIsGpck)
                {
                     Console.WriteLine("Info: Folder compression defaults to Game Package (.gpck).");
                     outputPath = Path.ChangeExtension(outputPath, ".gpck");
                     outputIsGpck = true;
                }
            }
            else
            {
                // Single file
                map = new Dictionary<string, string> { { inputPath, Path.GetFileName(inputPath) } };
            }

            Console.WriteLine($"Target files: {map.Count}");

            string format = outputIsGpck ? ".gpck" : ".gdef";
            processor.CompressFilesToArchive(map, outputPath, format);

            sw.Stop();
            PrintSuccess($"Operation completed in {sw.Elapsed.TotalSeconds:F2} seconds.");
        }

        static void RunDecompress(string[] args)
        {
            if (args.Length < 2)
            {
                Console.WriteLine("Usage: GDeflateCLI decompress <archive> [output_path]");
                return;
            }

            string inputPath = args[1];
            if (!ValidateInput(inputPath)) return;

            string outputDir = args.Length > 2
                ? args[2]
                : Path.Combine(Path.GetDirectoryName(Path.GetFullPath(inputPath)) ?? ".", Path.GetFileNameWithoutExtension(inputPath));

            if (!Directory.Exists(outputDir)) Directory.CreateDirectory(outputDir);

            Console.WriteLine($"Extracting to: {outputDir}");
            var sw = Stopwatch.StartNew();

            new GDeflateProcessor().DecompressArchive(inputPath, outputDir);

            sw.Stop();
            PrintSuccess($"Done in {sw.Elapsed.TotalSeconds:F2}s!");
        }

        static void RunInfo(string[] args)
        {
            if (args.Length < 2)
            {
                Console.WriteLine("Usage: GDeflateCLI info <package.gpck>");
                return;
            }

            string inputPath = args[1];
            if (!File.Exists(inputPath))
            {
                Console.WriteLine($"File not found: {inputPath}");
                return;
            }

            Console.WriteLine($"Inspecting: {Path.GetFileName(inputPath)}...");
            var processor = new GDeflateProcessor();

            try
            {
                var info = processor.InspectPackage(inputPath);

                if (info.Magic != "GPCK")
                {
                    Console.ForegroundColor = ConsoleColor.Yellow;
                    Console.WriteLine("Warning: File does not have GPCK signature.");
                    Console.ResetColor();
                    return;
                }

                Console.WriteLine($"Version: {info.Version}");
                Console.WriteLine($"File Count: {info.FileCount}");
                Console.WriteLine($"Total Size: {info.TotalSize / 1024.0 / 1024.0:F2} MB");
                Console.WriteLine(new string('-', 60));
                Console.WriteLine($"{"File",-30} | {"Compressed",-10} | {"Ratio",-8} | {"Aligned(4K)",-10}");
                Console.WriteLine(new string('-', 60));

                bool allAligned = true;

                foreach (var entry in info.Entries)
                {
                    double ratio = entry.OriginalSize > 0 ? (double)entry.CompressedSize / entry.OriginalSize * 100 : 0;
                    string ratioStr = $"{ratio:F1}%";
                    string alignedStr = entry.Is4KAligned ? "YES" : "NO";
                    if (!entry.Is4KAligned) allAligned = false;

                    string shortPath = entry.Path.Length > 28 ? "..." + entry.Path.Substring(entry.Path.Length - 25) : entry.Path;
                    Console.WriteLine($"{shortPath,-30} | {entry.CompressedSize / 1024.0:F1} KB   | {ratioStr,-8} | {alignedStr,-10}");
                }

                Console.WriteLine(new string('-', 60));
                if (allAligned)
                {
                     Console.ForegroundColor = ConsoleColor.Green;
                     Console.WriteLine("SUCCESS: All files are aligned to 4KB (DirectStorage Compatible).");
                }
                else
                {
                     Console.ForegroundColor = ConsoleColor.Red;
                     Console.WriteLine("WARNING: Some files are NOT 4KB aligned. DirectStorage IO performance may suffer.");
                }
                Console.ResetColor();

            }
            catch(Exception ex)
            {
                PrintError(ex);
            }
        }

        // --- Helpers ---

        static bool ValidateInput(string path)
        {
            if (File.Exists(path) || Directory.Exists(path)) return true;
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"Error: Input path not found: {path}");
            Console.ResetColor();
            return false;
        }

        static string DeriveOutputPath(string input, bool compress)
        {
            if (compress)
            {
                // Default logic: If folder -> gpck, if file -> gdef
                return Directory.Exists(input) ? input + ".gpck" : input + ".gdef";
            }
            return Path.ChangeExtension(input, null);
        }

        static Dictionary<string, string> BuildDirectoryMap(string dirPath)
        {
            var map = new Dictionary<string, string>();
            string rootDir = Path.GetFullPath(dirPath);
            if (!rootDir.EndsWith(Path.DirectorySeparatorChar.ToString()))
                rootDir += Path.DirectorySeparatorChar;

            foreach (var file in Directory.GetFiles(dirPath, "*.*", SearchOption.AllDirectories))
            {
                map[file] = Path.GetRelativePath(dirPath, file);
            }
            return map;
        }

        static void ShowHelp()
        {
            Console.WriteLine("GDeflate CLI Tool (CPU)");
            Console.WriteLine("Usage: GDeflateCLI <command> <input> [output]");
            Console.WriteLine("Commands:");
            Console.WriteLine("  compress (-c)   : Compress file or folder");
            Console.WriteLine("  decompress (-d) : Decompress archive");
            Console.WriteLine("  info (-i)       : Inspect archive structure & alignment");
            Console.WriteLine("Examples:");
            Console.WriteLine("  GDeflateCLI compress data/ levels.gpck");
            Console.WriteLine("  GDeflateCLI info levels.gpck");
        }

        static void PrintSuccess(string msg)
        {
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine(msg);
            Console.ResetColor();
        }

        static void PrintError(Exception ex)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"Critical Error: {ex.Message}");
            if (ex.InnerException != null) Console.WriteLine(ex.InnerException.Message);
            Console.ResetColor();
        }
    }
}
