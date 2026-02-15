using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using GPCK.Core;
using Spectre.Console;
using Spectre.Console.Cli;

namespace GPCK.CLI
{
    class Program
    {
        public static int Main(string[] args)
        {
            var app = new CommandApp();
            app.Configure(config =>
            {
                config.SetApplicationName("GPCK");
                config.AddCommand<CompressCommand>("compress")
                    .WithAlias("pack")
                    .WithDescription("Compress a folder into a .gpck archive.");

                config.AddCommand<DecompressCommand>("decompress")
                    .WithAlias("unpack")
                    .WithDescription("Decompress a .gpck archive.");

                config.AddCommand<VerifyCommand>("verify")
                    .WithDescription("Verify the integrity (CRC/Hash) of an archive.");

                config.AddCommand<InfoCommand>("info")
                    .WithDescription("Show technical details about an archive.");

                config.AddCommand<PatchCommand>("patch")
                    .WithDescription("Create a delta patch based on an existing archive.");
            });

            return app.Run(args);
        }
    }

    public class CompressCommand : AsyncCommand<CompressCommand.Settings>
    {
        public class Settings : CommandSettings
        {
            [CommandArgument(0, "<INPUT>")]
            [Description("Input folder or file.")]
            public string Input { get; set; } = "";

            [CommandArgument(1, "[OUTPUT]")]
            [Description("Output .gpck file path.")]
            public string? Output { get; set; }

            [CommandOption("-l|--level")]
            [DefaultValue(9)]
            public int Level { get; set; }

            [CommandOption("-m|--method")]
            [DefaultValue(AssetPacker.CompressionMethod.Auto)]
            [Description("Compression method: Auto, Store, GDeflate, Zstd, LZ4")]
            public AssetPacker.CompressionMethod Method { get; set; }

            [CommandOption("--mip-split")]
            public bool MipSplit { get; set; }

            [CommandOption("--key")]
            public string? Key { get; set; }
        }

        public override async Task<int> ExecuteAsync(CommandContext context, Settings settings)
        {
            string output = settings.Output ?? Path.ChangeExtension(settings.Input, ".gpck");
            byte[]? keyBytes = !string.IsNullOrEmpty(settings.Key) ? System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(settings.Key)) : null;

            AnsiConsole.MarkupLine($"[bold green]Packing:[/] {settings.Input} -> {output} (Method: {settings.Method})");

            var packer = new AssetPacker();
            var map = AssetPacker.BuildFileMap(settings.Input);

            await AnsiConsole.Progress()
                .StartAsync(async ctx =>
                {
                    var task = ctx.AddTask("[green]Compressing assets...[/]");
                    var progress = new Progress<int>(p => task.Value = p);

                    await packer.CompressFilesToArchiveAsync(
                        map, output, true, settings.Level, keyBytes, settings.MipSplit, progress, default, settings.Method);
                });

            AnsiConsole.MarkupLine("[bold green]Done![/]");
            return 0;
        }
    }

    public class DecompressCommand : AsyncCommand<DecompressCommand.Settings>
    {
        public class Settings : CommandSettings
        {
            [CommandArgument(0, "<ARCHIVE>")]
            public string Archive { get; set; } = "";

            [CommandArgument(1, "[OUTPUT]")]
            public string? Output { get; set; }

            [CommandOption("--key")]
            public string? Key { get; set; }
        }

        public override async Task<int> ExecuteAsync(CommandContext context, Settings settings)
        {
            string outDir = settings.Output ?? Path.GetFileNameWithoutExtension(settings.Archive);
            byte[]? keyBytes = !string.IsNullOrEmpty(settings.Key) ? System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(settings.Key)) : null;

            AnsiConsole.MarkupLine($"[bold blue]Unpacking:[/] {settings.Archive} -> {outDir}");

            var packer = new AssetPacker();

            await AnsiConsole.Progress()
                .StartAsync(async ctx =>
                {
                    var task = ctx.AddTask("[blue]Extracting...[/]");
                    var progress = new Progress<int>(p => task.Value = p);

                    await packer.DecompressArchiveAsync(settings.Archive, outDir, keyBytes, progress);
                });

            return 0;
        }
    }

    public class VerifyCommand : Command<VerifyCommand.Settings>
    {
        public class Settings : CommandSettings
        {
            [CommandArgument(0, "<ARCHIVE>")]
            public string Archive { get; set; } = "";
            [CommandOption("--key")]
            public string? Key { get; set; }
        }

        public override int Execute(CommandContext context, Settings settings)
        {
            if (Directory.Exists(settings.Archive))
            {
                AnsiConsole.MarkupLine("[bold red]ERROR:[/] Input is a directory. Please provide the path to a .gpck file.");
                return 1;
            }

            if (!File.Exists(settings.Archive))
            {
                AnsiConsole.MarkupLine($"[bold red]ERROR:[/] File '{settings.Archive}' not found.");
                return 1;
            }

            byte[]? keyBytes = !string.IsNullOrEmpty(settings.Key) ? System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(settings.Key)) : null;

            return AnsiConsole.Status()
                .Start("Verifying integrity...", ctx =>
                {
                    bool result = new AssetPacker().VerifyArchive(settings.Archive, keyBytes);
                    if (result)
                    {
                        AnsiConsole.MarkupLine("[bold green]VERIFICATION PASSED[/]");
                        return 0;
                    }
                    else
                    {
                        AnsiConsole.MarkupLine("[bold red]VERIFICATION FAILED[/]");
                        return 1;
                    }
                });
        }
    }

    public class InfoCommand : Command<InfoCommand.Settings>
    {
        public class Settings : CommandSettings { [CommandArgument(0, "<ARCHIVE>")] public string Archive { get; set; } = ""; }

        public override int Execute(CommandContext context, Settings settings)
        {
            var info = new AssetPacker().InspectPackage(settings.Archive);

            var grid = new Grid();
            grid.AddColumn();
            grid.AddColumn();
            grid.AddRow("Version", info.Version.ToString());
            grid.AddRow("Files", info.FileCount.ToString());
            grid.AddRow("Total Size", $"{info.TotalSize / 1024.0 / 1024.0:F2} MB");

            AnsiConsole.Write(new Panel(grid).Header("Archive Info"));

            var table = new Table();
            table.AddColumn("Path");
            table.AddColumn("Size");
            table.AddColumn("Comp %");
            table.AddColumn("Method");

            foreach(var e in info.Entries)
            {
                double ratio = e.OriginalSize > 0 ? (double)e.CompressedSize / e.OriginalSize * 100 : 0;
                table.AddRow(
                    e.Path.Length > 50 ? "..." + e.Path.Substring(e.Path.Length-47) : e.Path,
                    $"{e.OriginalSize/1024} KB",
                    $"{ratio:F0}%",
                    e.Method
                );
            }
            AnsiConsole.Write(table);
            return 0;
        }
    }

    public class PatchCommand : AsyncCommand<PatchCommand.Settings>
    {
        public class Settings : CommandSettings
        {
            [CommandArgument(0, "<BASE>")] public string Base { get; set; } = "";
            [CommandArgument(1, "<CONTENT>")] public string Content { get; set; } = "";
            [CommandArgument(2, "<OUTPUT>")] public string Output { get; set; } = "";
        }

        public override async Task<int> ExecuteAsync(CommandContext context, Settings settings)
        {
            AnsiConsole.MarkupLine($"Creating patch [bold]{settings.Output}[/] from [bold]{settings.Content}[/] against [bold]{settings.Base}[/]");

            var packer = new AssetPacker();
            var map = AssetPacker.BuildFileMap(settings.Content);

            await AnsiConsole.Progress().StartAsync(async ctx => {
                var t = ctx.AddTask("Computing Deltas & Packing...");
                await packer.CompressFilesToArchiveAsync(map, settings.Output, true, 9, null, false, null, default);
                t.Value = 100;
            });
            return 0;
        }
    }
}
