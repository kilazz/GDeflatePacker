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

            string outputPath = args.Length > 2 ? args[2] : DeriveOutputPath(inputPath);

            // Enforce .gpck extension
            if (!outputPath.EndsWith(".gpck", StringComparison.OrdinalIgnoreCase))
            {
                outputPath += ".gpck";
            }

            var processor = new GDeflateProcessor();

            Console.WriteLine($"Processing: {Path.GetFileName(inputPath)} -> {Path.GetFileName(outputPath)}");
            Console.WriteLine("Mode: Game Package (.gpck) [DirectStorage Aligned]");

            var sw = Stopwatch.StartNew();
            Dictionary<string, string> map;

            if (Directory.Exists(inputPath))
            {
                // Use the centralized method to avoid logic duplication
                map = GDeflateProcessor.BuildFileMap(inputPath);
                if (map.Count == 0)
                {
                    Console.WriteLine("Folder is empty.");
                    return;
                }
            }
            else
            {
                // Single file packed into a container
                map = new Dictionary<string, string> { { inputPath, Path.GetFileName(inputPath) } };
            }

            Console.WriteLine($"Target files: {map.Count}");

            processor.CompressFilesToArchive(map, outputPath);

            sw.Stop();
            PrintSuccess($"Operation completed in {sw.Elapsed.TotalSeconds:F2} seconds.");
        }

        static void RunDecompress(string[] args)
        {
            if (args.Length < 2)
            {
                Console.WriteLine("Usage: GDeflateCLI decompress <archive.gpck> [output_path]");
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
            // Use ValidateInput instead of manual checks to avoid duplication
            if (!ValidateInput(inputPath)) return;

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
                Console.WriteLine("Layout: Standard (Header-at-Front)");

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

        static string DeriveOutputPath(string input)
        {
            // Always .gpck now
            return Directory.Exists(input) ? input + ".gpck" : Path.ChangeExtension(input, ".gpck");
        }

        // Removed BuildDirectoryMap in favor of GDeflateProcessor.BuildFileMap

        static void ShowHelp()
        {
            Console.WriteLine("GDeflate CLI Tool (CPU)");
            Console.WriteLine("Usage: GDeflateCLI <command> <input> [output]");
            Console.WriteLine("Commands:");
            Console.WriteLine("compress (-c) : Compress file or folder into .gpck");
            Console.WriteLine("decompress (-d) : Decompress .gpck archive");
            Console.WriteLine("info (-i) : Inspect archive structure & alignment");
            Console.WriteLine("Examples:");
            Console.WriteLine("GDeflateCLI compress texture.png texture.gpck");
            Console.WriteLine("GDeflateCLI compress data/ levels.gpck");
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
