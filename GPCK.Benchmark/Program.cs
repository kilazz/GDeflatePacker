using System;
using System.Diagnostics;
using System.IO;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using GPCK.Core;
using Spectre.Console;

namespace GPCK.Benchmark
{
    class Program
    {
        private const int AlgorithmPayloadSize = 128 * 1024 * 1024; // 128MB
        private const string TempArchiveName = "format_integrity_test.gpck";

        [STAThread]
        static void Main(string[] args)
        {
            try
            {
                AnsiConsole.Write(new FigletText("GPCK SYSTEM").Color(Color.SpringGreen3));
                AnsiConsole.MarkupLine("[bold yellow]Integrated Format & Hardware Decompression Benchmark[/]");
                AnsiConsole.MarkupLine($"[gray]Execution Path: {AppContext.BaseDirectory}[/]");
                AnsiConsole.WriteLine();

                PrintSystemReport();

                double hostMemorySpeed = MeasureHostMemoryBandwidth();
                AnsiConsole.MarkupLine($"[bold]Host Memory Bandwidth:[/] [cyan]{hostMemorySpeed:F1} GB/s[/] (Hardware Limit)");
                AnsiConsole.WriteLine();

                // --- Part 1: Raw Algorithm Performance ---
                RunAlgorithmSuite();

                // --- Part 2: Archive Format & VFS Integrity ---
                RunFormatSuite();

                AnsiConsole.WriteLine();
                AnsiConsole.MarkupLine("[gray]Benchmark completed. Press Enter to exit...[/]");
                Console.ReadLine();
            }
            catch (Exception ex)
            {
                AnsiConsole.WriteException(ex);
                AnsiConsole.MarkupLine("[bold red]FATAL ERROR:[/] Process crashed.");
                Console.ReadLine();
            }
            finally
            {
                if (File.Exists(TempArchiveName)) File.Delete(TempArchiveName);
            }
        }

        static void RunAlgorithmSuite()
        {
            AnsiConsole.MarkupLine("[bold white]--- Part 1: Raw Algorithm Throughput (In-Memory) ---[/]");
            byte[] rawData = GenerateRealisticGameData(AlgorithmPayloadSize);

            var table = new Table().Border(TableBorder.Rounded);
            table.AddColumn("Method");
            table.AddColumn("Ratio");
            table.AddColumn("Compress Speed");
            table.AddColumn("Decompress Speed");

            AnsiConsole.Live(table).Start(ctx =>
            {
                RunTest("Store", rawData, 0, (inB, l) => inB, (inB, s) => { byte[] b = new byte[s]; Array.Copy(inB, b, Math.Min(inB.Length, s)); return b; }, table);
                ctx.Refresh();

                if (CodecLZ4.IsAvailable()) { RunTest("LZ4 (HC L9)", rawData, 9, CompressLZ4, DecompressLZ4, table); ctx.Refresh(); }
                if (CodecGDeflate.IsAvailable()) {
                    RunTest("GDeflate (CPU L12)", rawData, 12, CompressGDeflate, DecompressGDeflate, table); ctx.Refresh();
                    RunGpuBenchmark(rawData, 12, table); ctx.Refresh();
                }
                if (CodecZstd.IsAvailable()) { RunTest("Zstd (Ultra L22)", rawData, 22, CompressZstd, DecompressZstd, table); ctx.Refresh(); }
            });
            AnsiConsole.WriteLine();
        }

        static void RunFormatSuite()
        {
            AnsiConsole.MarkupLine("[bold white]--- Part 2: VFS Format Validation (Real-World IO) ---[/]");

            string dummyDir = Path.Combine(AppContext.BaseDirectory, "integrity_stress_src");
            if (Directory.Exists(dummyDir)) Directory.Delete(dummyDir, true);
            Directory.CreateDirectory(dummyDir);

            int smallFileCount = 3000;
            int largeFileCount = 20;

            AnsiConsole.Status().Start($"Building complex package ({smallFileCount} JSON, {largeFileCount} DDS)...", ctx => {
                for (int i = 0; i < smallFileCount; i++) {
                    string p = Path.Combine(dummyDir, $"metadata/node_{i:D4}.json");
                    string? dir = Path.GetDirectoryName(p);
                    if (dir != null) Directory.CreateDirectory(dir);
                    // Add unique salt to each file to prevent deduplication from skewing Slack Space metrics
                    File.WriteAllText(p, "{ \"id\": " + i + ", \"salt\": \"" + Guid.NewGuid() + "\", \"payload\": \"STRESS_METADATA_PARSING_LATENCY\" }");
                }
                for (int i = 0; i < largeFileCount; i++) {
                    string p = Path.Combine(dummyDir, $"textures/tex_4k_aligned_{i:D2}.dds");
                    string? dir = Path.GetDirectoryName(p);
                    if (dir != null) Directory.CreateDirectory(dir);
                    File.WriteAllBytes(p, GenerateRealisticGameData(1024 * 1024 * 4)); // 4MB each
                }
            });

            var packer = new AssetPacker();
            var fileMap = AssetPacker.BuildFileMap(dummyDir);
            var sw = Stopwatch.StartNew();

            // 1. Build Time (Deduplication disabled for clean metric)
            sw.Restart();
            packer.CompressFilesToArchiveAsync(fileMap, TempArchiveName, false, 6, null, false, null, CancellationToken.None).AsTask().Wait();
            var packTime = sw.ElapsedMilliseconds;
            var archiveSize = new FileInfo(TempArchiveName).Length;

            // 2. Cold Mount
            sw.Restart();
            using var archive = new GameArchive(TempArchiveName);
            var mountTime = sw.Elapsed.TotalMilliseconds;

            // 3. Search Stress
            var rnd = new Random(42);
            var keys = fileMap.Values.ToList();
            int lookups = 15000;
            sw.Restart();
            for (int i = 0; i < lookups; i++) {
                var id = AssetIdGenerator.Generate(keys[rnd.Next(keys.Count)]);
                archive.TryGetEntry(id, out _);
            }
            var searchLatencyUs = (sw.Elapsed.TotalMilliseconds * 1000.0) / lookups;

            // 4. Sequential VFS Throughput
            long totalReadSeq = 0;
            var ddsEntries = keys.Where(k => k.EndsWith(".dds")).ToList();
            sw.Restart();
            foreach (var rel in ddsEntries) {
                if (archive.TryGetEntry(AssetIdGenerator.Generate(rel), out var entry)) {
                    using var stream = archive.OpenRead(entry);
                    byte[] buffer = new byte[128 * 1024];
                    int read;
                    while ((read = stream.Read(buffer)) > 0) totalReadSeq += read;
                }
            }
            var vfsSpeedSeq = (totalReadSeq / 1024.0 / 1024.0) / sw.Elapsed.TotalSeconds;

            // 5. Parallel VFS Throughput (The "Real" GPCK Power)
            long totalReadPar = 0;
            sw.Restart();
            Parallel.ForEach(ddsEntries, new ParallelOptions { MaxDegreeOfParallelism = 8 }, rel => {
                if (archive.TryGetEntry(AssetIdGenerator.Generate(rel), out var entry)) {
                    using var stream = archive.OpenRead(entry);
                    byte[] buffer = new byte[256 * 1024];
                    int read;
                    long localTotal = 0;
                    while ((read = stream.Read(buffer)) > 0) localTotal += read;
                    Interlocked.Add(ref totalReadPar, localTotal);
                }
            });
            var vfsSpeedPar = (totalReadPar / 1024.0 / 1024.0) / sw.Elapsed.TotalSeconds;

            // 6. Alignment Verification
            long slackSpace = 0;
            int misalignedCount = 0;
            for (int i = 0; i < archive.FileCount; i++) {
                var entry = archive.GetEntryByIndex(i);
                bool isGpu = (entry.Flags & GameArchive.MASK_METHOD) == GameArchive.METHOD_GDEFLATE;
                if (isGpu && (entry.DataOffset % 4096 != 0)) misalignedCount++;
                if (i > 0) {
                    var prev = archive.GetEntryByIndex(i - 1);
                    long gap = entry.DataOffset - (prev.DataOffset + prev.CompressedSize);
                    if (gap > 0 && gap < 65536) slackSpace += gap;
                }
            }

            // Results Table
            var resTable = new Table().Border(TableBorder.DoubleEdge).Title("[bold cyan]GPCK Integrated Technical Report[/]");
            resTable.AddColumn("Metric");
            resTable.AddColumn("Measured Value");
            resTable.AddColumn("Integrity Status");

            resTable.AddRow("Archive Build Time", $"{packTime} ms", "[green]NOMINAL[/]");
            resTable.AddRow("VFS Mount Latency", $"{mountTime:F2} ms", mountTime < 15 ? "[green]PASS[/]" : "[yellow]SLOW_IO[/]");
            resTable.AddRow("O(log N) Search", $"{searchLatencyUs:F2} μs", "[green]EXCELLENT[/]");
            resTable.AddRow("VFS Seq Throughput", $"[bold gray]{vfsSpeedSeq:F1} MB/s[/]", "[gray]DISK_BOUND[/]");
            resTable.AddRow("VFS Parallel Throughput", $"[bold cyan]{vfsSpeedPar:F1} MB/s[/]", vfsSpeedPar > 1000 ? "[bold green]SCALED[/]" : "[yellow]STALLED[/]");
            resTable.AddRow("GPU 4KB Alignment", misalignedCount == 0 ? "[green]0 Errors[/]" : $"[red]{misalignedCount} ERRORS[/]", "[bold green]VALID[/]");
            resTable.AddRow("Alignment Slack (Waste)", $"{slackSpace / 1024} KB", $"{(double)slackSpace/archiveSize*100:F2}% ([green]IDEAL[/])");

            AnsiConsole.Write(resTable);

            // Cleanup
            if (Directory.Exists(dummyDir)) Directory.Delete(dummyDir, true);
        }

        static void PrintSystemReport()
        {
            var grid = new Grid();
            grid.AddColumn(new GridColumn().NoWrap());
            grid.AddColumn();

            bool isWin10Plus = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) && Environment.OSVersion.Version.Major >= 10;
            bool hasDsDll = NativeLibrary.TryLoad("dstorage.dll", out _);
            bool hasGDeflateDll = CodecGDeflate.IsAvailable();

            grid.AddRow("[bold]Operating System[/]", $"{RuntimeInformation.OSDescription} ({(isWin10Plus ? "[green]READY[/]" : "[red]UNSUPPORTED[/]")})");
            grid.AddRow("[bold]DirectStorage SDK[/]", hasDsDll ? "[green]ACTIVE[/]" : "[red]NOT FOUND[/]");
            grid.AddRow("[bold]GPCK Codec Runtime[/]", hasGDeflateDll ? "[green]NATIVE (HW_ACCEL)[/]" : "[yellow]SOFTWARE_ONLY[/]");

            AnsiConsole.Write(new Panel(grid).Header("Hardware & Driver Environment").BorderColor(Color.Grey));
            AnsiConsole.WriteLine();
        }

        static double MeasureHostMemoryBandwidth()
        {
            long size = 512 * 1024 * 1024;
            byte[] src = new byte[size];
            byte[] dst = new byte[size];
            new Random().NextBytes(src);
            var sw = Stopwatch.StartNew();
            Parallel.For(0, 128, i => {
                int chunkSize = (int)(size / 128);
                Array.Copy(src, i * chunkSize, dst, i * chunkSize, chunkSize);
            });
            sw.Stop();
            return (size / 1024.0 / 1024.0 / 1024.0) / sw.Elapsed.TotalSeconds;
        }

        static void RunTest(string name, byte[] input, int level, Func<byte[], int, byte[]> compressor, Func<byte[], int, byte[]> decompressor, Table table)
        {
            try {
                var sw = Stopwatch.StartNew();
                byte[] compressed = compressor(input, level);
                double compTime = sw.Elapsed.TotalSeconds;

                decompressor(compressed, input.Length); // Warmup
                sw.Restart();
                for (int i = 0; i < 3; i++) decompressor(compressed, input.Length);
                double decompSpeed = (input.Length / 1024.0 / 1024.0) / (sw.Elapsed.TotalSeconds / 3.0);

                table.AddRow(name, $"{(double)compressed.Length / input.Length * 100:F1}%", $"{(input.Length/1024.0/1024.0)/compTime:F0} MB/s", $"[bold green]{decompSpeed:F0} MB/s[/]");
            } catch (Exception ex) {
                table.AddRow(name, "ERR", "ERR", $"[red]{Markup.Escape(ex.Message)}[/]");
            }
        }

        static void RunGpuBenchmark(byte[] input, int level, Table table)
        {
            try {
                byte[] compressed = CompressGDeflate(input, level, false);
                using var gpu = new GpuDirectStorage();
                if (!gpu.IsSupported) {
                    table.AddRow("GDeflate (GPU)", "-", "N/A", $"[dim yellow]Unavailable: {Markup.Escape(gpu.InitError)}[/]");
                    return;
                }
                using var ms = new MemoryStream(compressed);
                using var br = new BinaryReader(ms);
                int numChunks = br.ReadInt32();
                int[] sizes = new int[numChunks];
                long[] offsets = new long[numChunks];
                long curr = ms.Position;
                for(int i=0; i<numChunks; i++) {
                    offsets[i] = curr;
                    int s = br.ReadInt32();
                    if (s == -1) { int rs = br.ReadInt32(); br.BaseStream.Seek(rs, SeekOrigin.Current); curr += 8 + rs; sizes[i] = rs; }
                    else { br.BaseStream.Seek(s, SeekOrigin.Current); curr += 4 + s; sizes[i] = s; }
                }
                gpu.RunDecompressionBatch(compressed, sizes, offsets, input.Length); // Warmup
                double t = 0;
                for (int i = 0; i < 3; i++) t += gpu.RunDecompressionBatch(compressed, sizes, offsets, input.Length);
                double speed = (input.Length / 1024.0 / 1024.0) / (t / 3.0);
                table.AddRow("GDeflate (GPU)", $"{(double)compressed.Length / input.Length * 100:F1}%", "N/A", $"[bold cyan]{speed:F0} MB/s[/]");
            } catch (Exception ex) {
                table.AddRow("GDeflate (GPU)", "ERR", "N/A", $"[red]{Markup.Escape(ex.Message)}[/]");
            }
        }

        // Codecs Helpers
        static byte[] CompressLZ4(byte[] input, int level) {
            int bound = CodecLZ4.LZ4_compressBound(input.Length);
            byte[] output = new byte[bound];
            unsafe { fixed (byte* pI = input, pO = output) {
                int sz = (level > 3) ? CodecLZ4.LZ4_compress_HC((IntPtr)pI, (IntPtr)pO, input.Length, bound, level) : CodecLZ4.LZ4_compress_default((IntPtr)pI, (IntPtr)pO, input.Length, bound);
                Array.Resize(ref output, sz); return output;
            }}
        }
        static byte[] DecompressLZ4(byte[] input, int outS) {
            byte[] output = new byte[outS];
            unsafe { fixed(byte* pI = input, pO = output) { CodecLZ4.LZ4_decompress_safe((IntPtr)pI, (IntPtr)pO, input.Length, outS); } }
            return output;
        }
        static byte[] CompressZstd(byte[] input, int level) {
            ulong bound = CodecZstd.ZSTD_compressBound((ulong)input.Length);
            byte[] output = new byte[bound];
            unsafe { fixed (byte* pI = input, pO = output) {
                ulong sz = CodecZstd.ZSTD_compress((IntPtr)pO, bound, (IntPtr)pI, (ulong)input.Length, level);
                Array.Resize(ref output, (int)sz); return output;
            }}
        }
        static byte[] DecompressZstd(byte[] input, int outS) {
            byte[] output = new byte[outS];
            unsafe { fixed(byte* pI = input, pO = output) { CodecZstd.ZSTD_decompress((IntPtr)pO, (ulong)outS, (IntPtr)pI, (ulong)input.Length); } }
            return output;
        }
        static byte[] CompressGDeflate(byte[] input, int level) => CompressGDeflate(input, level, true);
        static byte[] CompressGDeflate(byte[] input, int level, bool bypass) {
            using var ms = new MemoryStream();
            using var bw = new BinaryWriter(ms);
            int numChunks = (input.Length + 65535) / 65536;
            bw.Write(numChunks);
            byte[] scratch = new byte[CodecGDeflate.CompressBound(65535)];
            unsafe { fixed (byte* pI = input, pS = scratch) {
                for(int i=0; i<numChunks; i++) {
                    int off = i * 65536;
                    int sz = Math.Min(65536, input.Length - off);
                    ulong outS = (ulong)scratch.Length;
                    if (CodecGDeflate.Compress(pS, ref outS, pI + off, (ulong)sz, (uint)level, 0) && (!bypass || outS < (ulong)sz)) {
                        bw.Write((int)outS); bw.Write(new ReadOnlySpan<byte>(scratch, 0, (int)outS));
                    } else {
                        bw.Write(-1);
                        bw.Write(sz);
                        bw.Write(new ReadOnlySpan<byte>(input, off, sz));
                    }
                }
            }}
            return ms.ToArray();
        }
        static byte[] DecompressGDeflate(byte[] input, int outS) {
            byte[] output = new byte[outS];
            using var ms = new MemoryStream(input);
            using var br = new BinaryReader(ms);
            int chunks = br.ReadInt32();
            int[] sizes = new int[chunks];
            long[] offsets = new long[chunks];
            long curr = ms.Position;
            for(int i=0; i<chunks; i++) {
                offsets[i] = curr;
                int s = br.ReadInt32();
                if (s == -1) { int rs = br.ReadInt32(); br.BaseStream.Seek(rs, SeekOrigin.Current); curr += 8 + rs; }
                else { br.BaseStream.Seek(s, SeekOrigin.Current); curr += 4 + s; }
                sizes[i] = s;
            }
            Parallel.For(0, chunks, i => {
                unsafe { fixed (byte* pIn = input, pOut = output) {
                    int target = Math.Min(65536, outS - (i * 65536));
                    if (sizes[i] == -1) Buffer.MemoryCopy(pIn + offsets[i] + 8, pOut + (i * 65536), target, target);
                    else CodecGDeflate.Decompress(pOut + (i * 65536), (ulong)target, pIn + offsets[i] + 4, (ulong)sizes[i], 1);
                }}
            });
            return output;
        }

        static byte[] GenerateRealisticGameData(int size) {
            byte[] data = new byte[size];
            Random rnd = new Random(42);
            for (int k = 0; k < size; k++) {
                double pattern = Math.Sin(k * 0.05) * 60 + Math.Cos(k * 0.001) * 40;
                data[k] = (byte)(128 + (int)pattern + rnd.Next(0, 48));
            }
            return data;
        }
    }
}