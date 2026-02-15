
using System;
using System.IO;
using System.Diagnostics;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using GDeflate.Core;
using System.Threading;

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
                    case "patch": // New
                    case "-delta":
                        RunPatch(args);
                        break;
                    case "decompress":
                    case "-d":
                        RunDecompress(args);
                        break;
                    case "extract-file":
                    case "-e":
                        RunExtractFile(args);
                        break;
                    case "cat":
                    case "-p":
                        RunCat(args);
                        break;
                    case "verify":
                    case "-v":
                        RunVerify(args);
                        break;
                    case "info":
                    case "-i":
                        RunInfo(args);
                        break;
                    case "mount":
                    case "-m":
                        RunMount(args);
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
            catch (Exception ex)
            {
                PrintError(ex);
            }
        }

        static byte[]? ParseKey(string keyString)
        {
            if (string.IsNullOrEmpty(keyString)) return null;
            using var sha = System.Security.Cryptography.SHA256.Create();
            return sha.ComputeHash(Encoding.UTF8.GetBytes(keyString));
        }

        static void RunCompress(string[] args)
        {
            if (args.Length < 2)
            {
                Console.WriteLine("Usage: GDeflateCLI compress <file/folder> [output] [-l level] [--no-dedup] [--mip-split] [--key secret]");
                return;
            }

            string inputPath = args[1];
            if (!ValidateInput(inputPath)) return;
            string outputPath = DeriveOutputPath(inputPath);
            bool dedup = true;
            bool mipSplit = false;
            int level = 9;
            byte[]? key = null;

            for (int i = 2; i < args.Length; i++)
            {
                if (args[i] == "--no-dedup") dedup = false;
                else if (args[i] == "--mip-split") mipSplit = true;
                else if (args[i] == "-l" || args[i] == "--level")
                {
                    if (i + 1 < args.Length && int.TryParse(args[i + 1], out int l))
                    {
                        level = l;
                        i++;
                    }
                }
                else if (args[i] == "--key" && i+1 < args.Length)
                {
                    key = ParseKey(args[i+1]);
                    i++;
                }
                else if (!args[i].StartsWith("-"))
                {
                    outputPath = args[i];
                }
            }

            if (!outputPath.EndsWith(".gpck", StringComparison.OrdinalIgnoreCase)) outputPath += ".gpck";

            var processor = new GDeflateProcessor();
            Console.WriteLine($"Processing: {Path.GetFileName(inputPath)}");
            Console.WriteLine($"Mode: Full Archive");
            Console.WriteLine($"Options: Dedup={dedup}, MipSplit={mipSplit}, Level={level}");
            
            var sw = Stopwatch.StartNew();
            Dictionary<string, string> map;

            if (Directory.Exists(inputPath))
                map = GDeflateProcessor.BuildFileMap(inputPath);
            else
                map = new Dictionary<string, string> { { inputPath, Path.GetFileName(inputPath) } };

            Console.WriteLine($"Target files: {map.Count}");
            processor.CompressFilesToArchive(map, outputPath, dedup, level, encryptionKey: key, enableMipSplitting: mipSplit);

            sw.Stop();
            PrintSuccess($"Operation completed in {sw.Elapsed.TotalSeconds:F2} seconds.");
        }

        static void RunPatch(string[] args)
        {
            if (args.Length < 3)
            {
                Console.WriteLine("Usage: GDeflateCLI patch <base.gpck> <new_content_folder> <output_patch.gpck>");
                return;
            }

            string basePath = args[1];
            string contentPath = args[2];
            string outPath = args[3];

            if (!File.Exists(basePath)) { Console.WriteLine("Base archive not found."); return; }
            if (!Directory.Exists(contentPath)) { Console.WriteLine("Content folder not found."); return; }

            Console.WriteLine($"Creating Delta Patch for: {Path.GetFileName(basePath)}");
            Console.WriteLine($"Source Content: {contentPath}");
            
            var sw = Stopwatch.StartNew();
            var map = GDeflateProcessor.BuildFileMap(contentPath);
            
            var proc = new GDeflateProcessor();
            // Note: This is an async method in Core, calling via task.run/wait
            proc.CreatePatchArchiveAsync(basePath, map, outPath, 9, null, null, CancellationToken.None).GetAwaiter().GetResult();
            
            sw.Stop();
            PrintSuccess($"Patch created: {outPath} ({sw.Elapsed.TotalSeconds:F2}s)");
        }

        static void RunDecompress(string[] args)
        {
            if (args.Length < 2)
            {
                Console.WriteLine("Usage: GDeflateCLI decompress <archive.gpck> [output_path] [--key secret]");
                return;
            }

            string inputPath = args[1];
            if (!ValidateInput(inputPath)) return;
            string outputDir = "."; 
            byte[]? key = null;

            int outputIdx = 2;
            if (args.Length > 2 && !args[2].StartsWith("-")) { outputDir = args[2]; outputIdx = 3; }
            else { outputDir = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(inputPath)) ?? ".", Path.GetFileNameWithoutExtension(inputPath)); }

            for(int i=outputIdx; i<args.Length; i++)
            {
                if (args[i] == "--key" && i+1 < args.Length) { key = ParseKey(args[i+1]); i++; }
            }

            Console.WriteLine($"Extracting All to: {outputDir}");
            var sw = Stopwatch.StartNew();
            new GDeflateProcessor().DecompressArchive(inputPath, outputDir, key);
            sw.Stop();
            PrintSuccess($"Done in {sw.Elapsed.TotalSeconds:F2}s!");
        }

        static void RunExtractFile(string[] args)
        {
            if (args.Length < 3)
            {
                Console.WriteLine("Usage: GDeflateCLI extract-file <archive.gpck> <filename> [output_path] [--key secret]");
                return;
            }

            string inputPath = args[1];
            string target = args[2];
            byte[]? key = null;
            if (!ValidateInput(inputPath)) return;
            string outputDir = ".";
            
            int argIdx = 3;
            if (args.Length > 3 && !args[3].StartsWith("-")) { outputDir = args[3]; argIdx = 4; }

            for(int i=argIdx; i<args.Length; i++)
            {
                if (args[i] == "--key" && i+1 < args.Length) { key = ParseKey(args[i+1]); i++; }
            }

            Console.WriteLine($"Extracting '{target}' from {Path.GetFileName(inputPath)}...");
            try 
            {
                new GDeflateProcessor().ExtractSingleFile(inputPath, outputDir, target, key);
                PrintSuccess($"Extracted successfully.");
            }
            catch(Exception e) { PrintError(e); }
        }

        static void RunCat(string[] args)
        {
            if (args.Length < 3)
            {
                Console.WriteLine("Usage: GDeflateCLI cat <archive.gpck> <file_inside> [--key secret]");
                return;
            }
            string inputPath = args[1];
            string target = args[2];
            byte[]? key = null;
             for(int i=3; i<args.Length; i++)
            {
                if (args[i] == "--key" && i+1 < args.Length) { key = ParseKey(args[i+1]); i++; }
            }

            try 
            {
                using var archive = new GDeflateArchive(inputPath);
                if (key != null) archive.DecryptionKey = key;

                if (archive.TryGetEntry(target, out var entry))
                {
                   using var s = archive.OpenRead(entry);
                   using var sr = new StreamReader(s);
                   Console.WriteLine(sr.ReadToEnd());
                }
                else
                {
                   Console.WriteLine("File not found.");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error: {ex.Message}");
            }
        }

        static void RunVerify(string[] args)
        {
            if (args.Length < 2)
            {
                Console.WriteLine("Usage: GDeflateCLI verify <archive.gpck> [--key secret]");
                return;
            }

            string inputPath = args[1];
            byte[]? key = null;
            if (args.Length > 2 && args[2] == "--key" && args.Length > 3) key = ParseKey(args[3]);

            if (!ValidateInput(inputPath)) return;

            Console.WriteLine($"Verifying integrity of: {Path.GetFileName(inputPath)}...");
            var sw = Stopwatch.StartNew();
            bool valid = new GDeflateProcessor().VerifyArchive(inputPath, key);
            sw.Stop();

            if (valid) PrintSuccess($"Verification Passed ({sw.Elapsed.TotalSeconds:F2}s). Archive is healthy.");
            else 
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine("VERIFICATION FAILED.");
                Console.ResetColor();
            }
        }

        static void RunInfo(string[] args)
        {
            if (args.Length < 2)
            {
                Console.WriteLine("Usage: GDeflateCLI info <package.gpck>");
                return;
            }

            string inputPath = args[1];
            if (!ValidateInput(inputPath)) return;

            Console.WriteLine($"Inspecting: {Path.GetFileName(inputPath)}...");
            try
            {
                using var archive = new GDeflateArchive(inputPath);
                var info = archive.GetPackageInfo();
                
                if (info.Magic != "GPCK") { Console.WriteLine("Invalid File"); return; }
                
                Console.WriteLine($"Version: {info.Version}");
                Console.WriteLine($"File Count: {info.FileCount}");
                Console.WriteLine($"Dependencies: {info.DependencyCount}");
                Console.WriteLine(new string('-', 115));
                Console.WriteLine($"{"File/Hash",-50} | {"Size",-8} | {"Align",-6} | {"Method",-15}");
                Console.WriteLine(new string('-', 115));

                int printed = 0;
                foreach (var entry in info.Entries)
                {
                    if (printed++ > 50) 
                    {
                        Console.WriteLine($"... and {info.Entries.Count - 50} more files.");
                        break;
                    }

                    string displayName = entry.Path;
                    if (displayName.Length > 48)
                    {
                        displayName = "..." + displayName.Substring(displayName.Length - 45);
                    }
                    
                    // Show 4K or 64K alignment info
                    string align = entry.Alignment >= 1024 ? (entry.Alignment/1024) + "K" : entry.Alignment + "B";

                    Console.WriteLine($"{displayName,-50} | {entry.OriginalSize / 1024.0:F1}KB | {align,-6} | {entry.Method,-15}");
                }
            }
            catch(Exception ex) { PrintError(ex); }
        }

        static void RunMount(string[] args)
        {
            if (args.Length < 3)
            {
                Console.WriteLine("Usage: GDeflateCLI mount <base.gpck> <mod.gpck> ... --check <file_to_check>");
                return;
            }

            var archives = new List<string>();
            string checkFile = null;

            for (int i = 1; i < args.Length; i++)
            {
                if (args[i] == "--check" && i + 1 < args.Length)
                {
                    checkFile = args[i + 1];
                    i++;
                }
                else
                {
                    archives.Add(args[i]);
                }
            }

            Console.WriteLine("Initializing VFS...");
            using var vfs = new GDeflateVFS();

            foreach (var arch in archives)
            {
                if (File.Exists(arch))
                {
                    Console.WriteLine($"Mounting: {Path.GetFileName(arch)}");
                    vfs.Mount(arch);
                }
                else
                {
                    Console.ForegroundColor = ConsoleColor.Yellow;
                    Console.WriteLine($"Warning: Archive not found {arch}");
                    Console.ResetColor();
                }
            }

            if (checkFile != null)
            {
                Console.WriteLine($"\nResolving file: '{checkFile}'");
                if (vfs.FileExists(checkFile))
                {
                    string source = vfs.GetSourceArchiveName(checkFile);
                    PrintSuccess($"FOUND in: {source}");
                }
                else
                {
                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.WriteLine("File NOT FOUND in any mounted archive.");
                    Console.ResetColor();
                }
            }
        }

        static bool ValidateInput(string path)
        {
            if (File.Exists(path) || Directory.Exists(path)) return true;
            Console.WriteLine($"Error: Input not found: {path}");
            return false;
        }

        static string DeriveOutputPath(string input) => Directory.Exists(input) ? input + ".gpck" : Path.ChangeExtension(input, ".gpck");

        static void ShowHelp()
        {
            Console.WriteLine("GDeflate CLI Tool");
            Console.WriteLine("Commands:");
            Console.WriteLine("  compress <in> [out] [opts]       : Standard Pack");
            Console.WriteLine("  patch <base> <new_dir> <out>     : Create Delta Patch Archive");
            Console.WriteLine("  decompress <gpck> [outDir]       : Unpack All");
            Console.WriteLine("  extract-file <gpck> <file>       : Unpack Single File");
            Console.WriteLine("  verify <gpck>                    : Check integrity");
            Console.WriteLine("  info <gpck>                      : Inspect Alignment & Deps");
            Console.WriteLine("  mount <base> <mod> --check <f>   : Test VFS Layering");
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
            Console.WriteLine($"Error: {ex.Message}");
            Console.ResetColor();
        }
    }
}