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
        private const int PayloadSize = 256 * 1024 * 1024;

        [STAThread]
        static void Main(string[] args)
        {
            try
            {
                AnsiConsole.Write(new FigletText("GPCK BENCH").Color(Color.DeepPink1));
                AnsiConsole.MarkupLine("[bold yellow]Hardware Decompression Benchmark Tool - EXTREME MODE[/]");
                AnsiConsole.MarkupLine($"[gray]Execution Path: {AppContext.BaseDirectory}[/]");
                AnsiConsole.WriteLine();

                PrintSystemReport();

                double hostMemorySpeed = MeasureHostMemoryBandwidth();
                AnsiConsole.MarkupLine($"[bold]Host Memory Bandwidth:[/] [cyan]{hostMemorySpeed:F1} GB/s[/] (Ceiling)");
                AnsiConsole.WriteLine();

                AnsiConsole.MarkupLine("[gray]Generating semi-random workload (Entropy-simulated)...[/]");
                byte[] rawData = GenerateRealisticGameData(PayloadSize);
                AnsiConsole.MarkupLine($"[green]Generated {rawData.Length:N0} bytes.[/]");
                AnsiConsole.WriteLine();

                var table = new Table();
                table.AddColumn("Method");
                table.AddColumn("Level");
                table.AddColumn("Comp Size");
                table.AddColumn("Ratio");
                table.AddColumn("Compress Speed");
                table.AddColumn("Decompress Speed");

                AnsiConsole.MarkupLine("[bold white]--- Extreme Archival Modes (Max Ratio) ---[/]");

                AnsiConsole.Live(table)
                    .Start(ctx =>
                    {
                        RunTest("Store", rawData, 0,
                            (inB, lvl) => inB,
                            (inB, outSz) => {
                                byte[] outB = new byte[outSz];
                                Array.Copy(inB, outB, Math.Min(inB.Length, outSz));
                                return outB;
                            },
                            table);
                        ctx.Refresh();

                        if (CodecLZ4.IsAvailable())
                        {
                            // Max HC level is 9
                            RunTest("LZ4 (HC)", rawData, 9, CompressLZ4, DecompressLZ4, table);
                            ctx.Refresh();
                        }

                        if (CodecGDeflate.IsAvailable())
                        {
                            // Max GDeflate level is 12
                            RunTest("GDeflate (CPU)", rawData, 12, CompressGDeflate, DecompressGDeflate, table);
                            ctx.Refresh();
                            RunGpuBenchmark(rawData, 12, table);
                            ctx.Refresh();
                        }

                        if (CodecZstd.IsAvailable())
                        {
                            // Max Zstd level is 22 (Ultra)
                            RunTest("Zstd (Ultra)", rawData, 22, CompressZstd, DecompressZstd, table);
                            ctx.Refresh();
                        }
                    });

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
        }

        static void PrintSystemReport()
        {
            var grid = new Grid();
            grid.AddColumn(new GridColumn().NoWrap());
            grid.AddColumn();

            bool isWin10Plus = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) && Environment.OSVersion.Version.Major >= 10;

            bool hasDsDll = NativeLibrary.TryLoad("dstorage.dll", out _);
            bool hasCompilerDll = NativeLibrary.TryLoad("dxcompiler.dll", out _);
            bool hasLz4Dll = CodecLZ4.IsAvailable();
            bool hasZstdDll = CodecZstd.IsAvailable();
            bool hasGDeflateDll = CodecGDeflate.IsAvailable();

            grid.AddRow("[bold]Operating System[/]", $"{RuntimeInformation.OSDescription} ({(isWin10Plus ? "[green]Compatible[/]" : "[red]Legacy[/]")})");
            grid.AddRow("[bold]DirectStorage Runtime[/]", hasDsDll ? "[green]FOUND[/]" : "[red]MISSING[/]");
            grid.AddRow("[bold]GPU Compiler Stack[/]", hasCompilerDll ? "[green]FOUND[/]" : "[red]MISSING[/]");
            grid.AddRow("[bold]GDeflate Native[/]", hasGDeflateDll ? "[green]OK[/]" : "[red]MISSING[/]");
            grid.AddRow("[bold]LZ4 Native[/]", hasLz4Dll ? "[green]OK[/]" : "[red]MISSING[/]");
            grid.AddRow("[bold]Zstd Native[/]", hasZstdDll ? "[green]OK[/]" : "[red]MISSING[/]");

            AnsiConsole.Write(new Panel(grid).Header("System Capabilities Report").BorderColor(Color.Grey));
            AnsiConsole.WriteLine();
        }

        static double MeasureHostMemoryBandwidth()
        {
            long size = 512 * 1024 * 1024;
            byte[] src = new byte[size];
            byte[] dst = new byte[size];
            new Random().NextBytes(src);

            var sw = Stopwatch.StartNew();
            int chunkSize = 4 * 1024 * 1024;
            int chunks = (int)(size / chunkSize);

            Parallel.For(0, chunks, i => {
                Array.Copy(src, i * chunkSize, dst, i * chunkSize, chunkSize);
            });
            sw.Stop();

            double gb = size / 1024.0 / 1024.0 / 1024.0;
            return gb / sw.Elapsed.TotalSeconds;
        }

        static void RunTest(string name, byte[] input, int level, Func<byte[], int, byte[]> compressor, Func<byte[], int, byte[]> decompressor, Table table)
        {
            try
            {
                var sw = Stopwatch.StartNew();
                byte[] compressed = compressor(input, level);
                sw.Stop();
                double compMb = (input.Length / 1024.0 / 1024.0);
                double compSpeed = compMb / sw.Elapsed.TotalSeconds;

                decompressor(compressed, input.Length); // Warmup

                sw.Restart();
                for(int i=0; i<3; i++) decompressor(compressed, input.Length);
                sw.Stop();
                double decompSpeed = compMb / (sw.Elapsed.TotalSeconds / 3.0);

                table.AddRow(name, level == 0 ? "-" : level.ToString(), $"{compressed.Length / 1024 / 1024} MB", $"{(double)compressed.Length / input.Length * 100:F1}%", $"{compSpeed:F0} MB/s", $"[bold green]{decompSpeed:F0} MB/s[/]");
            }
            catch (Exception ex)
            {
                table.AddRow(name, level.ToString(), "ERR", "ERR", "ERR", $"[red]{Markup.Escape(ex.Message)}[/]");
            }
        }

        static void RunGpuBenchmark(byte[] input, int level, Table table)
        {
            try
            {
                byte[] compressed = CompressGDeflate(input, level, false);
                using var gpu = new GpuDirectStorage();

                if (!gpu.IsSupported)
                {
                    table.AddRow("GDeflate (GPU)", level.ToString(), "-", "-", "N/A", $"[dim yellow]Disabled: {Markup.Escape(gpu.InitError)}[/]");
                    return;
                }

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

                gpu.RunDecompressionBatch(compressed, chunkSizes, chunkOffsets, input.Length); // Warmup
                double totalTime = 0;
                for(int i=0; i<3; i++) totalTime += gpu.RunDecompressionBatch(compressed, chunkSizes, chunkOffsets, input.Length);
                double speed = (input.Length / 1024.0 / 1024.0) / (totalTime / 3.0);

                table.AddRow("GDeflate (GPU)", level.ToString(), $"{compressed.Length / 1024 / 1024} MB", $"{(double)compressed.Length / input.Length * 100:F1}%", "N/A", $"[bold cyan]{speed:F0} MB/s[/]");
            }
            catch (Exception ex)
            {
                table.AddRow("GDeflate (GPU)", level.ToString(), "ERR", "ERR", "N/A", $"[red]{Markup.Escape(ex.Message)}[/]");
            }
        }

        static byte[] CompressLZ4(byte[] input, int level) {
            int bound = CodecLZ4.LZ4_compressBound(input.Length);
            byte[] output = new byte[bound];
            unsafe { fixed (byte* pI = input, pO = output) {
                int sz = (level > 2) ? CodecLZ4.LZ4_compress_HC((IntPtr)pI, (IntPtr)pO, input.Length, bound, level) : CodecLZ4.LZ4_compress_default((IntPtr)pI, (IntPtr)pO, input.Length, bound);
                Array.Resize(ref output, sz); return output;
            }}
        }

        static byte[] DecompressLZ4(byte[] input, int outSize) {
            byte[] output = new byte[outSize];
            unsafe { fixed(byte* pI = input, pO = output) { CodecLZ4.LZ4_decompress_safe((IntPtr)pI, (IntPtr)pO, input.Length, outSize); } }
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

        static byte[] DecompressZstd(byte[] input, int outSize) {
            byte[] output = new byte[outSize];
            unsafe { fixed(byte* pI = input, pO = output) { CodecZstd.ZSTD_decompress((IntPtr)pO, (ulong)outSize, (IntPtr)pI, (ulong)input.Length); } }
            return output;
        }

        static byte[] CompressGDeflate(byte[] input, int level) => CompressGDeflate(input, level, true);

        static byte[] CompressGDeflate(byte[] input, int level, bool allowBypass) {
            using var ms = new MemoryStream();
            using var bw = new BinaryWriter(ms);
            int chunkSize = 65536;
            int numChunks = (input.Length + chunkSize - 1) / chunkSize;
            bw.Write(numChunks);
            byte[] scratch = new byte[CodecGDeflate.CompressBound((ulong)chunkSize)];
            unsafe { fixed (byte* pInputBase = input, pScratch = scratch) {
                for(int i=0; i<numChunks; i++) {
                    int offset = i * chunkSize;
                    int size = Math.Min(chunkSize, input.Length - offset);
                    ulong outSize = (ulong)scratch.Length;
                    if (CodecGDeflate.Compress(pScratch, ref outSize, pInputBase + offset, (ulong)size, (uint)level, 0) && (!allowBypass || outSize < (ulong)size)) {
                        bw.Write((int)outSize);
                        bw.Write(new ReadOnlySpan<byte>(scratch, 0, (int)outSize));
                    } else {
                        bw.Write(-1); bw.Write(size);
                        bw.Write(new ReadOnlySpan<byte>(input, offset, size));
                    }
                }
            }}
            return ms.ToArray();
        }

        static byte[] DecompressGDeflate(byte[] input, int outSize) {
            byte[] output = new byte[outSize];
            using var ms = new MemoryStream(input);
            using var br = new BinaryReader(ms);
            int numChunks = br.ReadInt32();
            int chunkSize = 65536;
            int[] sizes = new int[numChunks];
            long[] offsets = new long[numChunks];
            long curr = ms.Position;
            for(int i=0; i<numChunks; i++) {
                offsets[i] = curr;
                int s = br.ReadInt32();
                if (s == -1) { int rs = br.ReadInt32(); br.BaseStream.Seek(rs, SeekOrigin.Current); curr += 8 + rs; }
                else { br.BaseStream.Seek(s, SeekOrigin.Current); curr += 4 + s; }
                sizes[i] = s;
            }

            var hIn = GCHandle.Alloc(input, GCHandleType.Pinned);
            var hOut = GCHandle.Alloc(output, GCHandleType.Pinned);
            try {
                IntPtr pInputBase = hIn.AddrOfPinnedObject();
                IntPtr pOutputBase = hOut.AddrOfPinnedObject();

                Parallel.For(0, numChunks, i => {
                    int expected = Math.Min(chunkSize, outSize - (i * chunkSize));
                    unsafe {
                        byte* pIn = (byte*)pInputBase;
                        byte* pOut = (byte*)pOutputBase;
                        if (sizes[i] == -1) {
                            int rs = *(int*)(pIn + offsets[i] + 4);
                            Buffer.MemoryCopy(pIn + offsets[i] + 8, pOut + (i * chunkSize), rs, rs);
                        } else {
                            CodecGDeflate.Decompress(pOut + (i * chunkSize), (ulong)expected, pIn + offsets[i] + 4, (ulong)sizes[i], 1);
                        }
                    }
                });
            } finally {
                hIn.Free();
                hOut.Free();
            }
            return output;
        }

        static byte[] GenerateRealisticGameData(int size) {
            byte[] data = new byte[size];
            Random rnd = new Random(42);
            // Mix of gradients and noise to simulate texture/mesh entropy
            for (int k = 0; k < size; k++)
            {
                double pattern = Math.Sin(k * 0.05) * 60 + Math.Cos(k * 0.001) * 40;
                byte noise = (byte)rnd.Next(0, 32);
                data[k] = (byte)(128 + (int)pattern + noise);
            }
            return data;
        }
    }
}
