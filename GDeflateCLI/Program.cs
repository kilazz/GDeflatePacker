using System;
using System.IO;
using System.Diagnostics;
using System.Collections.Generic;
using System.Linq;
using System.Text;
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
                    case "mount": // New Modding Command
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
            // Expecting 32-byte key in hex or base64. 
            // For simplicity in CLI, if length is 32 chars, take bytes, if 64 chars hex.
            // Simplified: SHA256 the string to get 32 byte key.
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
            Console.WriteLine($"Mode: Parallel Async (Scatter/Gather)");
            Console.WriteLine($"Layout: Heuristic (Hot/Cold Split)");

            Console.WriteLine($"Level: {level} (1-12)");
            Console.WriteLine($"Deduplication: {(dedup ? "ON (CAS)" : "OFF")}");
            Console.WriteLine($"Texture Streaming: {(mipSplit ? "ON (Mip Splitting)" : "OFF")}");
            if (key != null) Console.WriteLine($"Encryption: AES-GCM (Enabled)");
            
            var sw = Stopwatch.StartNew();
            Dictionary<string, string> map;

            if (Directory.Exists(inputPath))
                map = GDeflateProcessor.BuildFileMap(inputPath);
            else
                map = new Dictionary<string, string> { { inputPath, Path.GetFileName(inputPath) } };

            Console.WriteLine($"Target files: {map.Count}");
            processor.CompressFilesToArchiveAsync(map, outputPath, dedup, level, encryptionKey: key, enableMipSplitting: mipSplit).GetAwaiter().GetResult();

            sw.Stop();
            PrintSuccess($"Operation completed in {sw.Elapsed.TotalSeconds:F2} seconds.");
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
                var sw = Stopwatch.StartNew();
                new GDeflateProcessor().ExtractSingleFile(inputPath, outputDir, target, key);
                sw.Stop();
                PrintSuccess($"Extracted successfully in {sw.ElapsedMilliseconds} ms.");
            }
            catch (FileNotFoundException)
            {
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine($"File '{target}' not found in archive.");
                Console.ResetColor();
            }
            catch (UnauthorizedAccessException)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine("Error: File is encrypted. Provide valid --key.");
                Console.ResetColor();
            }
        }

        static void RunCat(string[] args)
        {
            // Cat (Print) not ideal for binary encrypted files, but we can try if key provided.
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
                Console.WriteLine("VERIFICATION FAILED. The archive contains corrupted data or key invalid.");
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
                if (info.Version != 5) { Console.WriteLine($"Unsupported Version: {info.Version} (Expected 5)"); return; }

                Console.WriteLine($"Version: {info.Version} (v5 AAAA)");
                Console.WriteLine($"File Count: {info.FileCount}");
                Console.WriteLine(new string('-', 105));
                Console.WriteLine($"{"File/Hash",-55} | {"Size",-10} | {"CRC32",-10} | {"Method",-15}");
                Console.WriteLine(new string('-', 105));

                int printed = 0;
                foreach (var entry in info.Entries)
                {
                    if (printed++ > 50) 
                    {
                        Console.WriteLine($"... and {info.Entries.Count - 50} more files.");
                        break;
                    }

                    string displayName = entry.Path;
                    if (displayName.Length > 53)
                    {
                        displayName = "..." + displayName.Substring(displayName.Length - 50);
                    }

                    Console.WriteLine($"{displayName,-55} | {entry.CompressedSize / 1024.0:F1} KB   | {entry.Crc32:X8}   | {entry.Method,-15}");
                }
            }
            catch(Exception ex) { PrintError(ex); }
        }

        static void RunMount(string[] args)
        {
            // Simulates game startup: loading Base -> Patch -> Mod
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
                    
                    // Actually try to read 4 bytes to prove it works
                    using var s = vfs.OpenRead(checkFile);
                    Console.WriteLine($"Stream Open OK. Size: {s.Length} bytes.");
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
            Console.WriteLine("GDeflate CLI Tool (v5)");
            Console.WriteLine("Commands:");
            Console.WriteLine("  compress <in> [out] [options]    : Pack (Async Scatter/Gather)");
            Console.WriteLine("    --key <secret>                 : Encryption key");
            Console.WriteLine("    --no-dedup                     : Disable CAS Deduplication");
            Console.WriteLine("    --mip-split                    : Enable Texture Streaming (Split High/Low Mips)");
            Console.WriteLine("  decompress <gpck> [outDir]       : Unpack All");
            Console.WriteLine("  extract-file <gpck> <file>       : Unpack Single File");
            Console.WriteLine("  cat <gpck> <file>                : Read file to stdout");
            Console.WriteLine("  verify <gpck>                    : Check integrity");
            Console.WriteLine("  info <gpck>                      : Inspect");
            Console.WriteLine("  mount <base> <mod> --check <f>   : Test Modding VFS");
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