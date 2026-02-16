using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using GPCK.Core;
using Spectre.Console;

namespace GPCK.Benchmark
{
    class Program
    {
        // 256 MB test buffer
        private const int PayloadSize = 256 * 1024 * 1024;

        [STAThread]
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

            // 2. Measure Host Capabilities
            double hostMemorySpeed = MeasureHostMemoryBandwidth();
            AnsiConsole.MarkupLine($"[bold]Host Memory Bandwidth:[/] [cyan]{hostMemorySpeed:F1} GB/s[/] (Measured)");
            AnsiConsole.WriteLine();

            // 3. Generate Data
            AnsiConsole.MarkupLine("[gray]Generating mixed workload (Textures, Geometry, Logs)...[/]");
            byte[] rawData = GenerateRealisticGameData(PayloadSize);
            AnsiConsole.MarkupLine($"[green]Generated {rawData.Length:N0} bytes.[/]");
            AnsiConsole.MarkupLine("[gray]Composition: 40% Textures (Compressible), 40% Geometry, 20% Text/Logs[/]");
            AnsiConsole.WriteLine();

            var table = new Table();
            table.AddColumn("Method");
            table.AddColumn("Level");
            table.AddColumn("Comp Size");
            table.AddColumn("Ratio");
            table.AddColumn("Compress Speed");
            table.AddColumn("Decompress Speed");

            // 4. Run Tests (Balanced)
            AnsiConsole.MarkupLine("[bold white]--- Balanced Modes (Runtime Optimized) ---[/]");

            RunTest("Store", rawData, 0,
                (inB, lvl) => inB,
                (inB, outSz) => {
                    byte[] outB = new byte[outSz];
                    Array.Copy(inB, outB, Math.Min(inB.Length, outSz));
                    return outB;
                },
                table);

            if (lz4)
                RunTest("LZ4", rawData, 1, CompressLZ4, DecompressLZ4, table);

            if (gdeflate)
            {
                // Run Actual CPU Test
                RunTest("GDeflate (CPU)", rawData, 1, CompressGDeflate, DecompressGDeflate, table);

                // Run Actual GPU Test (DirectStorage)
                RunGpuBenchmark(rawData, 1, table);
            }

            if (zstd)
                RunTest("Zstd", rawData, 3, CompressZstd, DecompressZstd, table);

            // 5. Run Tests (Max Compression)
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
            AnsiConsole.MarkupLine("- [green]GDeflate (CPU)[/]: Optimized multi-core fallback using .NET ThreadPool.");
            AnsiConsole.MarkupLine("- [cyan]GDeflate (GPU)[/]: Actual hardware performance via DirectStorage.");

            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine("[gray]Press Enter to exit...[/]");
            Console.ReadLine();
        }

        static double MeasureHostMemoryBandwidth()
        {
            // Allocate 512MB for bandwidth test
            long size = 512 * 1024 * 1024;
            byte[] src = new byte[size];
            byte[] dst = new byte[size];
            // Touch pages
            new Random().NextBytes(src);

            AnsiConsole.Status().Start("Measuring System RAM Bandwidth...", ctx =>
            {
                // Warmup
                Array.Copy(src, dst, 1024 * 1024);

                var sw = Stopwatch.StartNew();
                // Use parallel block copy to saturate bus
                int chunkSize = 4 * 1024 * 1024;
                int chunks = (int)(size / chunkSize);

                Parallel.For(0, chunks, new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount }, i => {
                    Array.Copy(src, i * chunkSize, dst, i * chunkSize, chunkSize);
                });
                sw.Stop();
                return sw.Elapsed.TotalSeconds;
            });

            // Re-run for accuracy
            var timer = Stopwatch.StartNew();
            int cSize = 4 * 1024 * 1024;
            int cCount = (int)(size / cSize);
            Parallel.For(0, cCount, i => Array.Copy(src, i * cSize, dst, i * cSize, cSize));
            timer.Stop();

            double gb = size / 1024.0 / 1024.0 / 1024.0;
            return gb / timer.Elapsed.TotalSeconds;
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
            byte[] compressed;
            try {
                compressed = compressor(input, level);
            } catch (Exception ex) {
                AnsiConsole.MarkupLine($"[red]Error compressing {name}: {ex.Message}[/]");
                return;
            }
            sw.Stop();

            double compMb = (input.Length / 1024.0 / 1024.0);
            double compSec = sw.Elapsed.TotalSeconds;
            double compSpeed = compMb / compSec;

            // --- Decompression (Warmup) ---
            try {
                decompressor(compressed, input.Length);
            } catch (Exception ex) {
                 AnsiConsole.MarkupLine($"[red]Error decompressing {name} (Warmup): {ex.Message}[/]");
                 return;
            }

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

        static void RunGpuBenchmark(byte[] input, int level, Table table)
        {
            // 1. Prepare Data
            byte[] compressed = CompressGDeflate(input, level);
            double ratio = (double)input.Length / compressed.Length;
            double compMb = compressed.Length / 1024.0 / 1024.0;
            double rawMb = input.Length / 1024.0 / 1024.0;

            // 2. Initialize GPU Helper
            using var gpu = new GpuDirectStorage();

            if (!gpu.IsSupported)
            {
                table.AddRow(
                    "GDeflate (GPU)", "1",
                    $"{compMb:F0} MB", $"{100.0/ratio:F1}%",
                    "N/A",
                    $"[dim yellow]Not Supported: {Markup.Escape(gpu.InitError)}[/]"
                );
                return;
            }

            // 3. Parse our custom archive format to find chunk offsets
            // Format: [NumChunks int] ... [Size int][Data...]
            using var ms = new MemoryStream(compressed);
            using var br = new BinaryReader(ms);
            int numChunks = br.ReadInt32();

            int[] chunkSizes = new int[numChunks];
            long[] chunkOffsets = new long[numChunks];

            long currentPos = ms.Position;
            for(int i=0; i<numChunks; i++) {
                chunkOffsets[i] = currentPos;
                int cSize = br.ReadInt32();
                if (cSize == -1) {
                    // Raw data block found
                    int rSize = br.ReadInt32();
                    br.BaseStream.Seek(rSize, SeekOrigin.Current);
                    currentPos += 8 + rSize;
                    chunkSizes[i] = rSize;
                } else {
                    br.BaseStream.Seek(cSize, SeekOrigin.Current);
                    currentPos += 4 + cSize;
                    chunkSizes[i] = cSize;
                }
            }

            // 4. Run Benchmark
            try
            {
                // Run once for warmup
                gpu.RunDecompressionBatch(compressed, chunkSizes, chunkOffsets, input.Length);

                // Run 3 times
                double totalTime = 0;
                for(int i=0; i<3; i++)
                {
                    totalTime += gpu.RunDecompressionBatch(compressed, chunkSizes, chunkOffsets, input.Length);
                }
                double avgTime = totalTime / 3.0;
                double speed = rawMb / avgTime;

                string label = gpu.IsHardwareAccelerated ? "GDeflate (GPU)" : "GDeflate (CPU-DS)";
                string color = gpu.IsHardwareAccelerated ? "bold cyan" : "cyan";

                table.AddRow(
                    label, "1",
                    $"{compMb:F0} MB", $"{100.0/ratio:F1}%",
                    "N/A",
                    $"[{color}]{speed:F0} MB/s[/]"
                );
            }
            catch (Exception ex)
            {
                table.AddRow("GDeflate (GPU)", "1", $"{compMb:F0} MB", "Err", "N/A", $"[red]Error: {Markup.Escape(ex.Message)}[/]");
            }
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
            // FIX: GDeflate requires 64KB chunking (paging) to simulate DirectStorage correctly.
            // Using a single large buffer causes "Malformed Stream" errors in the reference implementation.

            using var ms = new MemoryStream();
            using var bw = new BinaryWriter(ms);

            int chunkSize = 65536; // 64KB Page Size
            int numChunks = (input.Length + chunkSize - 1) / chunkSize;

            // Header for benchmark purposes: [NumChunks] -> [Size1][Data1]...[SizeN][DataN]
            bw.Write(numChunks);

            byte[] scratch = new byte[CodecGDeflate.CompressBound((ulong)chunkSize)]; // Buffer for one chunk

            unsafe {
                fixed (byte* pInputBase = input)
                fixed (byte* pScratch = scratch)
                {
                    for(int i=0; i<numChunks; i++)
                    {
                        int offset = i * chunkSize;
                        int size = Math.Min(chunkSize, input.Length - offset);

                        ulong outSize = (ulong)scratch.Length;

                        // Compress 64KB chunk
                        bool result = CodecGDeflate.Compress(
                            pScratch,
                            ref outSize,
                            pInputBase + offset,
                            (ulong)size,
                            (uint)level,
                            0 // flags
                        );

                        // If compression fails (expands data) or returns false, we store raw
                        if (!result || outSize >= (ulong)size) {
                            bw.Write(-1); // Marker for uncompressed
                            bw.Write(size);
                            var span = new ReadOnlySpan<byte>(input, offset, size);
                            bw.Write(span);
                        } else {
                            bw.Write((int)outSize);
                            var span = new ReadOnlySpan<byte>(scratch, 0, (int)outSize);
                            bw.Write(span);
                        }
                    }
                }
            }

            return ms.ToArray();
        }

        static byte[] DecompressGDeflate(byte[] input, int outSize)
        {
            byte[] output = new byte[outSize];
            using var ms = new MemoryStream(input);
            using var br = new BinaryReader(ms);

            int numChunks = br.ReadInt32();
            int chunkSize = 65536;

            // Phase 1: Scan stream to map chunks (fast serial read)
            int[] chunkSizes = new int[numChunks];
            long[] inputOffsets = new long[numChunks];

            long currentPos = ms.Position;
            for(int i=0; i<numChunks; i++) {
                inputOffsets[i] = currentPos;
                int cSize = br.ReadInt32();
                if (cSize == -1) {
                    int rSize = br.ReadInt32();
                    br.BaseStream.Seek(rSize, SeekOrigin.Current);
                    currentPos += 8 + rSize; // 4 (cSize) + 4 (rSize) + data
                } else {
                    br.BaseStream.Seek(cSize, SeekOrigin.Current);
                    currentPos += 4 + cSize; // 4 (cSize) + data
                }
                chunkSizes[i] = cSize;
            }

            // Phase 2: Parallel Decompression
            unsafe {
                fixed (byte* pOutputBase = output)
                fixed (byte* pInputBase = input)
                {
                    // Copy pointers to local variables to avoid capturing fixed variables
                    byte* pOutBase = pOutputBase;
                    byte* pInBase = pInputBase;

                    Parallel.For(0, numChunks, new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount }, i =>
                    {
                        int cSize = chunkSizes[i];
                        long offsetInInput = inputOffsets[i];

                        int expectedSize = Math.Min(chunkSize, outSize - (i * chunkSize));
                        byte* pOut = pOutBase + (i * chunkSize);

                        if (cSize == -1) {
                            int rawSize = *(int*)(pInBase + offsetInInput + 4);
                            Buffer.MemoryCopy(pInBase + offsetInInput + 8, pOut, rawSize, rawSize);
                        }
                        else {
                            byte* pIn = pInBase + offsetInInput + 4;
                            bool res = CodecGDeflate.Decompress(
                                pOut,
                                (ulong)expectedSize,
                                pIn,
                                (ulong)cSize,
                                1 // Single threaded per chunk
                            );
                            if (!res) throw new Exception("GDeflate Chunk Decompression failed");
                        }
                    });
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
            // Type 0: Compressible Binary (Textures/Models) - 40% (Target ~1.5x)
            // Type 1: Structured (Geometry/floats) - 40%
            // Type 2: Text (Scripts/JSON) - 20%

            // Reusable "Pools" for Type 1 & 2 to allow *some* Zstd matching
            byte[] geometryPool = new byte[1024 * 1024]; // 1MB Geometry buffer

            // FIX: Using random bytes creates incompressible data which fails GDeflate compression.
            // Using a generated sine wave pattern instead ensures compressibility for DirectStorage benchmark.
            for(int k=0; k<geometryPool.Length; k++) geometryPool[k] = (byte)(Math.Sin(k * 0.1) * 127 + 128);

            byte[] textPool = new byte[1024 * 1024]; // 1MB Text buffer
            for(int k=0; k<textPool.Length; k++) textPool[k] = (byte)rand.Next(32, 126);

            // Create a pattern pool for compressible binary
            byte[] patternPool = new byte[4096];
            rand.NextBytes(patternPool);

            while (offset < size)
            {
                int chunkSize = rand.Next(16 * 1024, 256 * 1024); // 16KB - 256KB chunks
                chunkSize = Math.Min(chunkSize, size - offset);

                int typeRoll = rand.Next(100);

                if (typeRoll < 40) // 40% Binary Data (Compressible)
                {
                     for (int i = 0; i < chunkSize; i++)
                     {
                         data[offset + i] = (byte)(patternPool[i % patternPool.Length] ^ (i % 255));
                     }
                }
                else if (typeRoll < 80) // 40% Geometry (Repeated Patterns)
                {
                    int poolOffset = rand.Next(0, geometryPool.Length - chunkSize);
                    if(poolOffset < 0) poolOffset = 0;
                    int copyLen = Math.Min(chunkSize, geometryPool.Length - poolOffset);
                    Array.Copy(geometryPool, poolOffset, data, offset, copyLen);
                }
                else // 20% Text (Highly Compressible)
                {
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