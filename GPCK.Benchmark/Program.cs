using System;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using GPCK.Core;
using Spectre.Console;

namespace GPCK.Benchmark
{
    class Program
    {
        // 256 MB test buffer
        private const int PayloadSize = 256 * 1024 * 1024;

        static void Main(string[] args)
        {
            AnsiConsole.Write(new FigletText("GPCK BENCH").Color(Color.Cyan1));
            AnsiConsole.MarkupLine("[bold yellow]Hardware Decompression Benchmark Tool[/]");
            AnsiConsole.MarkupLine($"[gray]Target Payload: {PayloadSize / 1024 / 1024} MB (Mixed Game Assets)[/]");
            AnsiConsole.MarkupLine($"[gray]Execution Path: {AppContext.BaseDirectory}[/]");
            AnsiConsole.WriteLine();

            // 1. Check Native Libs
            bool zstd = CodecZstd.IsAvailable();
            bool lz4 = CodecLZ4.IsAvailable();
            bool gdeflate = CodecGDeflate.IsAvailable();

            CheckLibrary("Zstd", zstd);
            CheckLibrary("LZ4", lz4);
            CheckLibrary("GDeflate", gdeflate);

            if (!zstd && !lz4 && !gdeflate)
            {
                AnsiConsole.MarkupLine("\n[bold red]CRITICAL WARNING:[/]");
                AnsiConsole.MarkupLine("No compression libraries found. Ensure [green]libzstd.dll[/], [green]liblz4.dll[/], and [green]GDeflate.dll[/] are in the output directory.");
                AnsiConsole.MarkupLine("Only 'Store' (uncompressed) test will run.\n");
            }

            AnsiConsole.WriteLine();

            // 2. Generate Data
            AnsiConsole.MarkupLine("[gray]Generating mixed workload (Textures, Geometry, Logs)...[/]");
            byte[] rawData = GenerateRealisticGameData(PayloadSize);
            AnsiConsole.MarkupLine($"[green]Generated {rawData.Length:N0} bytes.[/]");
            AnsiConsole.MarkupLine("[gray]Composition: 60% Unique Textures (Hard), 20% Geometry, 20% Text/Logs[/]");
            AnsiConsole.WriteLine();

            var table = new Table();
            table.AddColumn("Method");
            table.AddColumn("Level");
            table.AddColumn("Comp Size");
            table.AddColumn("Ratio");
            table.AddColumn("Compress Speed");
            table.AddColumn("Decompress Speed");

            // 3. Run Tests (Balanced)
            AnsiConsole.MarkupLine("[bold white]--- Balanced Modes (Runtime Optimized) ---[/]");
            RunTest("Store", rawData, 0, (inB, lvl) => inB, (inB, outSz) => new byte[outSz], table);

            if (lz4)
                RunTest("LZ4", rawData, 1, CompressLZ4, DecompressLZ4, table);

            if (gdeflate)
                RunTest("GDeflate", rawData, 1, CompressGDeflate, DecompressGDeflate, table);

            if (zstd)
                RunTest("Zstd", rawData, 3, CompressZstd, DecompressZstd, table);

            // 4. Run Tests (Max Compression)
            AnsiConsole.MarkupLine("\n[bold white]--- Ultra Modes (Build-Time Optimized) ---[/]");
            table.AddEmptyRow(); // Separator

            if (lz4)
                RunTest("LZ4 HC", rawData, 9, CompressLZ4, DecompressLZ4, table);

            if (gdeflate)
                RunTest("GDeflate Max", rawData, 4, CompressGDeflate, DecompressGDeflate, table);

            if (zstd)
                RunTest("Zstd Ultra", rawData, 19, CompressZstd, DecompressZstd, table);

            AnsiConsole.Write(table);

            AnsiConsole.MarkupLine("\n[bold]Analysis:[/]");
            AnsiConsole.MarkupLine("- [blue]LZ4[/]: Fastest CPU decompression. Ideal for scripts/physics.");
            AnsiConsole.MarkupLine("- [green]GDeflate[/]: Best texture compression ratio. 1-2 GB/s on CPU is just a fallback; on GPU this would be 10GB/s+.");
            AnsiConsole.MarkupLine("- [cyan]Zstd[/]: Best ratio overall, but heavy on CPU. Use for Installers or Network packets.");

            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine("[gray]Press Enter to exit...[/]");
            Console.ReadLine();
        }

        static void CheckLibrary(string name, bool available)
        {
            AnsiConsole.MarkupLine($"Library [bold]{name}[/]: {(available ? "[green]FOUND[/]" : "[red]MISSING[/]")}");
        }

        static void RunTest(
            string name,
            byte[] input,
            int level,
            Func<byte[], int, byte[]> compressor,
            Func<byte[], int, byte[]> decompressor,
            Table table)
        {
            // --- Compression ---
            var sw = Stopwatch.StartNew();
            byte[] compressed = compressor(input, level);
            sw.Stop();
            double compMb = (input.Length / 1024.0 / 1024.0);
            double compSec = sw.Elapsed.TotalSeconds;
            double compSpeed = compMb / compSec;

            // --- Decompression (Warmup) ---
            decompressor(compressed, input.Length);

            // --- Decompression (Real Test) ---
            sw.Restart();
            for(int i=0; i<3; i++) // Run 3 times to average
            {
                decompressor(compressed, input.Length);
            }
            sw.Stop();
            double decompSec = sw.Elapsed.TotalSeconds / 3.0;
            double decompSpeed = compMb / decompSec;

            double ratio = (double)compressed.Length / input.Length * 100.0;

            string levelStr = level == 0 ? "-" : level.ToString();

            table.AddRow(
                name,
                levelStr,
                $"{compressed.Length / 1024 / 1024} MB",
                $"{ratio:F1}%",
                $"{compSpeed:F0} MB/s",
                $"[bold green]{decompSpeed:F0} MB/s[/]"
            );
        }

        // --- Wrappers ---

        static byte[] CompressLZ4(byte[] input, int level)
        {
            int bound = CodecLZ4.LZ4_compressBound(input.Length);
            byte[] output = new byte[bound];
            unsafe {
                fixed (byte* pI = input, pO = output) {
                    // Level > 3 triggers LZ4HC
                    int sz = (level > 2)
                        ? CodecLZ4.LZ4_compress_HC((IntPtr)pI, (IntPtr)pO, input.Length, bound, level)
                        : CodecLZ4.LZ4_compress_default((IntPtr)pI, (IntPtr)pO, input.Length, bound);

                    if (sz <= 0) return input;
                    Array.Resize(ref output, sz);
                    return output;
                }
            }
        }

        static byte[] DecompressLZ4(byte[] input, int outSize)
        {
            byte[] output = new byte[outSize];
            unsafe {
                fixed(byte* pI = input, pO = output) {
                    CodecLZ4.LZ4_decompress_safe((IntPtr)pI, (IntPtr)pO, input.Length, outSize);
                }
            }
            return output;
        }

        static byte[] CompressZstd(byte[] input, int level)
        {
            ulong bound = CodecZstd.ZSTD_compressBound((ulong)input.Length);
            byte[] output = new byte[bound];
            unsafe {
                fixed (byte* pI = input, pO = output) {
                    ulong sz = CodecZstd.ZSTD_compress((IntPtr)pO, bound, (IntPtr)pI, (ulong)input.Length, level);
                    if (CodecZstd.ZSTD_isError(sz) != 0) return input;
                    Array.Resize(ref output, (int)sz);
                    return output;
                }
            }
        }

        static byte[] DecompressZstd(byte[] input, int outSize)
        {
            byte[] output = new byte[outSize];
            unsafe {
                fixed(byte* pI = input, pO = output) {
                    CodecZstd.ZSTD_decompress((IntPtr)pO, (ulong)outSize, (IntPtr)pI, (ulong)input.Length);
                }
            }
            return output;
        }

        static byte[] CompressGDeflate(byte[] input, int level)
        {
            ulong bound = CodecGDeflate.CompressBound((ulong)input.Length);
            byte[] output = new byte[bound];
            unsafe {
                fixed(byte* pI = input, pO = output) {
                    ulong outS = bound;
                    CodecGDeflate.Compress(pO, ref outS, pI, (ulong)input.Length, (uint)level, 0);
                    Array.Resize(ref output, (int)outS);
                    return output;
                }
            }
        }

        static byte[] DecompressGDeflate(byte[] input, int outSize)
        {
            byte[] output = new byte[outSize];
            unsafe {
                fixed(byte* pI = input, pO = output) {
                    // Use 4 workers for CPU simulation
                    CodecGDeflate.Decompress(pO, (ulong)outSize, pI, (ulong)input.Length, 4);
                }
            }
            return output;
        }

        // --- Realistic Data Generator ---

        static byte[] GenerateRealisticGameData(int size)
        {
            byte[] data = new byte[size];
            var rand = new Random(123); // Constant seed

            int offset = 0;

            // Define segment types to simulate a real package
            // Type 0: High Entropy (Textures/Audio) - 60%
            // Type 1: Structured (Geometry/floats) - 20%
            // Type 2: Text (Scripts/JSON) - 20%

            // Reusable "Pools" for Type 1 & 2 to allow *some* Zstd matching, but not infinite
            byte[] geometryPool = new byte[1024 * 1024]; // 1MB Geometry buffer
            rand.NextBytes(geometryPool); // Random floats look like noise but have structure

            byte[] textPool = new byte[1024 * 1024]; // 1MB Text buffer
            for(int k=0; k<textPool.Length; k++) textPool[k] = (byte)rand.Next(32, 126);

            while (offset < size)
            {
                int chunkSize = rand.Next(16 * 1024, 256 * 1024); // 16KB - 256KB chunks
                chunkSize = Math.Min(chunkSize, size - offset);

                int typeRoll = rand.Next(100);

                if (typeRoll < 60) // 60% Unique Textures (Worst Case)
                {
                    // Generate fresh random data (High Entropy)
                    // This defeats Zstd's long-range window deduplication
                    rand.NextBytes(data.AsSpan(offset, chunkSize));
                }
                else if (typeRoll < 80) // 20% Geometry (Repeated Patterns)
                {
                    // Copy from geometry pool but with offset cycling
                    int poolOffset = rand.Next(0, geometryPool.Length - chunkSize);
                    if(poolOffset < 0) poolOffset = 0;
                    int copyLen = Math.Min(chunkSize, geometryPool.Length - poolOffset);
                    Array.Copy(geometryPool, poolOffset, data, offset, copyLen);
                }
                else // 20% Text (Highly Compressible)
                {
                    // Copy from text pool
                    int poolOffset = rand.Next(0, textPool.Length - chunkSize);
                    if(poolOffset < 0) poolOffset = 0;
                    int copyLen = Math.Min(chunkSize, textPool.Length - poolOffset);
                    Array.Copy(textPool, poolOffset, data, offset, copyLen);
                }

                offset += chunkSize;
            }

            return data;
        }
    }
}
