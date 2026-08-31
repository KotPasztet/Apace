using System.CommandLine;
using Serilog;

namespace MCEPatcher.Core;

internal class Program
{
    static async Task<int> Main(string[] args)
    {
        var log = new LoggerConfiguration()
            .WriteTo.Console()
            .WriteTo.File("logs/debug.txt", rollingInterval: RollingInterval.Day, rollOnFileSizeLimit: true, fileSizeLimitBytes: 8338607, outputTemplate: "{Timestamp:HH:mm:ss.fff} [{Level:u3}] {Message:lj}{NewLine}{Exception}")
            .MinimumLevel.Debug()
            .CreateLogger();
        Log.Logger = log;

        AppDomain.CurrentDomain.UnhandledException += (object sender, UnhandledExceptionEventArgs e) =>
        {
            Log.Fatal($"Unhandled exception: {e.ExceptionObject}");
            Log.CloseAndFlush();
            U.PAKE();
            Environment.Exit(1);
        };

        var inApkArgument = new Argument<FileInfo>("in-apk")
        {
            Description = "Path to the minecraft earth apk file",
            Arity = ArgumentArity.ExactlyOne,
        };

        var outApkArgument = new Argument<FileInfo>("out-apk")
        {
            Description = "Path to the output patched apk file",
            Arity = ArgumentArity.ZeroOrOne,
            DefaultValueFactory = _ => new FileInfo("Minecraft_Earth_patched.apk"),
        };

        var resourcePackOption = new Option<FileInfo?>("resource-pack")
        {
            Description = "Path to the minecraft earth resource pack file",
            Required = false,
        };

        var iconOption = new Option<FileInfo?>("icon")
        {
            Description = "Path to a png image to use as the app icon (scaled to the original icon sizes)",
            Required = false,
        };

        var androidVersionOption = new Option<int>("android-version")
        {
            Description = "Android version of the phone the apk will run on (10+ recommended)",
            Required = false,
            DefaultValueFactory = _ => 14,
        };

        var decodedDirOption = new Option<DirectoryInfo?>("decoded-dir")
        {
            Description = "Path to the directory the input apk will be decoded to",
            Required = false,
        };

        var patchesOption = new Option<string[]>("--patches", "-p")
        {
            Description = "List of patches to apply, in format '[patch1 name] \"[patch2 name]\" ...'",
            Required = true,
            AllowMultipleArgumentsPerToken = true,
        };

        var variablesOption = new Option<string[]>("--variables", "-v")
        {
            Description = "Variables to use for patches, in format '[var1 name]=[var1 value] \"[var2 name]=[var2 value]\" ...'",
            Required = true,
            AllowMultipleArgumentsPerToken = true,
        };

        var nonInteractiveOption = new Option<bool>("non-interactive")
        {
            Required = false,
            DefaultValueFactory = _ => false,
        };

        var skipDecodeOption = new Option<bool>("skip-decode")
        {
            Description = "Skip decoding the apk",
            Required = false,
            DefaultValueFactory = _ => false,
        };

        var skipBuildOption = new Option<bool>("skip-build")
        {
            Description = "Skip building the apk",
            Required = false,
            DefaultValueFactory = _ => false,
        };

        var skipSignOption = new Option<bool>("skip-sign")
        {
            Description = "Skip signing the apk",
            Required = false,
            DefaultValueFactory = _ => false,
        };

        RootCommand rootCommand = new("Patch minecraft earth apk")
        {

        };

        rootCommand.Arguments.Add(inApkArgument);
        rootCommand.Arguments.Add(outApkArgument);

        rootCommand.Options.Add(resourcePackOption);
        rootCommand.Options.Add(iconOption);
        rootCommand.Options.Add(androidVersionOption);
        rootCommand.Options.Add(decodedDirOption);
        rootCommand.Options.Add(patchesOption);
        rootCommand.Options.Add(variablesOption);
        rootCommand.Options.Add(nonInteractiveOption);
        rootCommand.Options.Add(skipDecodeOption);
        rootCommand.Options.Add(skipBuildOption);
        rootCommand.Options.Add(skipSignOption);

        rootCommand.SetAction(async parseResult =>
        {
            var inApk = parseResult.GetValue(inApkArgument);
            if (inApk is null or { Exists: false })
            {
                Console.WriteLine("Input apk file does not exist.");
                U.PAKE();
                return 2;
            }

            using (var fs = inApk.OpenRead())
            {
                if (!ApkProcessor.VerifyApkHash(fs))
                {
                    Console.WriteLine("Warning: The .apk file hash does not match. Patching may fail.");
                    U.PAKE();
                }
            }

            var outApk = parseResult.GetValue(outApkArgument);
            if (outApk is null)
            {
                Console.WriteLine("Input apk file does not exist.");
                U.PAKE();
                return 2;
            }

            var options = new ApkProcessor.Options
            {
                InApk = inApk.FullName,
                OutApk = outApk.FullName,
                ResourcePack = parseResult.GetValue(resourcePackOption)?.FullName,
                IconPath = parseResult.GetValue(iconOption)?.FullName,
                AndroidOSVersion = parseResult.GetValue(androidVersionOption),
                DecodedDir = parseResult.GetValue(decodedDirOption)?.FullName ?? Path.GetFullPath(Path.Combine("tmp", "apk")),
                Patches = parseResult.GetValue(patchesOption) ?? [],
                Variables = parseResult.GetValue(variablesOption) ?? [],
                NonInteractive = parseResult.GetValue(nonInteractiveOption),
                SkipDecode = parseResult.GetValue(skipDecodeOption),
                SkipBuild = parseResult.GetValue(skipBuildOption),
                SkipSign = parseResult.GetValue(skipSignOption),
            };

            try
            {
                if (!await ApkProcessor.Run(options))
                {
                    U.PAKE();
                    Environment.Exit(1);
                }
            }
            catch (Exception exception)
            {
                Log.Fatal(exception.ToString());
                U.PAKE();
                Environment.Exit(1);
            }

            return 0;
        });

        var parseResult = rootCommand.Parse(args);
        return await parseResult.InvokeAsync();
    }
}
