using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Lqb.Tool;

// dotnet-lqb works like dotnet-ef:
//   1. build the developer's startup project
//   2. read its output paths from MSBuild
//   3. start lqb-host with `dotnet exec`, using the APP's deps.json and runtimeconfig.json,
//      so EF Core, the database provider and Lqb.Design load in the exact versions the app uses.
internal static class Program
{
    private static int Main(string[] args)
    {
        if (args.Length == 0 || args[0] is "-h" or "--help")
        {
            PrintUsage();
            return 0;
        }

        try
        {
            var options = AddOptions.Parse(args);

            var projectFile = ResolveProjectFile(options.Project);
            var startupFile = options.StartupProject is null ? projectFile : ResolveProjectFile(options.StartupProject);

            if (!options.NoBuild)
            {
                Console.WriteLine($"Building {Path.GetFileName(startupFile)}...");
                var buildArgs = new List<string> { "build", startupFile, "--nologo", "-v", "q", "-c", options.Configuration };
                if (options.Framework is not null) buildArgs.AddRange(["-f", options.Framework]);
                if (ProcessRunner.Run("dotnet", buildArgs) != 0) throw new ToolException("Build failed.");
            }

            var target = ProjectInfo.Read(projectFile, options.Configuration, options.Framework);
            var startup = startupFile == projectFile ? target : ProjectInfo.Read(startupFile, options.Configuration, options.Framework);

            var depsFile = Path.ChangeExtension(startup.TargetPath, ".deps.json");
            var runtimeConfig = Path.ChangeExtension(startup.TargetPath, ".runtimeconfig.json");
            if (!File.Exists(runtimeConfig))
            {
                throw new ToolException(
                    $"{Path.GetFileName(startupFile)} isn't an executable project. " +
                    "Use --startup-project to point at your app (console or web project).");
            }

            var execArgs = new List<string> { "exec", "--depsfile", depsFile, "--runtimeconfig", runtimeConfig };
            if (startup.NuGetPackageRoot is not null)
            {
                execArgs.AddRange(["--additionalprobingpath", startup.NuGetPackageRoot.TrimEnd('/', '\\')]);
            }

            execArgs.AddRange(
            [
                Path.Combine(AppContext.BaseDirectory, "lqb-host.dll"),
                "add",
                "--name", options.Name,
                "--target-dir", startup.TargetDir,
                "--assembly", Path.GetFileNameWithoutExtension(target.TargetPath),
                "--startup-assembly", Path.GetFileNameWithoutExtension(startup.TargetPath),
                "--project-dir", target.ProjectDir,
                "--root-namespace", target.RootNamespace,
                "--changelog-dir", options.ChangelogDir is null
                    ? Path.Combine(target.ProjectDir, "db", "changelog")
                    : Path.GetFullPath(options.ChangelogDir),
                "--author", options.Author ?? Environment.UserName,
            ]);
            if (options.Context is not null) execArgs.AddRange(["--context", options.Context]);
            if (options.MigrationsDir is not null)
            {
                // Relative to the project folder, like dotnet-ef's --output-dir.
                execArgs.AddRange(["--migrations-dir", Path.GetFullPath(options.MigrationsDir, target.ProjectDir)]);
            }

            // Run from the startup project folder so appsettings.json etc. are found, like dotnet-ef.
            return ProcessRunner.Run("dotnet", execArgs, Path.GetDirectoryName(startupFile));
        }
        catch (ToolException ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }
    }

    private static string ResolveProjectFile(string? path)
    {
        var full = Path.GetFullPath(path ?? Directory.GetCurrentDirectory());
        if (File.Exists(full)) return full;
        if (!Directory.Exists(full)) throw new ToolException($"Project path not found: {full}");

        var projects = Directory.GetFiles(full, "*.csproj");
        return projects.Length switch
        {
            1 => projects[0],
            0 => throw new ToolException($"No .csproj found in {full}."),
            _ => throw new ToolException($"More than one .csproj in {full}. Pass the file path instead."),
        };
    }

    private static void PrintUsage() => Console.WriteLine(
        """
        Usage: dotnet lqb add <Name> [options]

        Compares your EF Core model with the last snapshot and writes a Liquibase
        changeset (forward SQL + rollback) for the differences.

        Options:
          -p, --project <path>          Project containing the DbContext (default: current folder)
          -s, --startup-project <path>  Executable project used to run the tool (default: --project)
          -c, --context <name>          DbContext to use when there is more than one
              --author <name>           Changeset author (default: current user)
              --changelog-dir <path>    Changelog folder (default: <project>/db/changelog)
          -o, --migrations-dir <path>   Folder for the model snapshot, relative to the project
                                        (default: where the snapshot is now, or Migrations)
              --configuration <name>    Build configuration (default: Debug)
              --framework <tfm>         Target framework, for multi-targeting projects
              --no-build                Skip building the project
        """);
}

internal sealed class AddOptions
{
    public required string Name { get; init; }
    public string? Project { get; set; }
    public string? StartupProject { get; set; }
    public string? Context { get; set; }
    public string? Author { get; set; }
    public string? ChangelogDir { get; set; }
    public string? MigrationsDir { get; set; }
    public string Configuration { get; set; } = "Debug";
    public string? Framework { get; set; }
    public bool NoBuild { get; set; }

    public static AddOptions Parse(string[] args)
    {
        if (args[0] != "add") throw new ToolException($"Unknown command '{args[0]}'. Run 'dotnet lqb --help'.");
        if (args.Length < 2 || args[1].StartsWith('-'))
        {
            throw new ToolException("Missing changeset name. Example: dotnet lqb add AddCustomerEmail");
        }
        if (!Regex.IsMatch(args[1], "^[A-Za-z0-9_-]+$"))
        {
            throw new ToolException("The name may only contain letters, digits, '_' and '-'.");
        }

        var options = new AddOptions { Name = args[1] };
        for (var i = 2; i < args.Length; i++)
        {
            var key = args[i];
            if (key == "--no-build")
            {
                options.NoBuild = true;
                continue;
            }
            if (i + 1 >= args.Length) throw new ToolException($"Missing value for {key}.");

            var value = args[++i];
            switch (key)
            {
                case "-p" or "--project": options.Project = value; break;
                case "-s" or "--startup-project": options.StartupProject = value; break;
                case "-c" or "--context": options.Context = value; break;
                case "--author": options.Author = value; break;
                case "--changelog-dir": options.ChangelogDir = value; break;
                case "-o" or "--migrations-dir": options.MigrationsDir = value; break;
                case "--configuration": options.Configuration = value; break;
                case "--framework": options.Framework = value; break;
                default: throw new ToolException($"Unknown option '{key}'.");
            }
        }
        return options;
    }
}

internal sealed record ProjectInfo(string TargetPath, string ProjectDir, string RootNamespace, string? NuGetPackageRoot)
{
    public string TargetDir => Path.GetDirectoryName(TargetPath)!;

    // Requires the .NET 8+ SDK (`-getProperty`).
    public static ProjectInfo Read(string projectFile, string configuration, string? framework)
    {
        var args = new List<string>
        {
            "msbuild", projectFile, "-nologo",
            "-getProperty:TargetPath", "-getProperty:ProjectDir",
            "-getProperty:RootNamespace", "-getProperty:NuGetPackageRoot",
            $"-property:Configuration={configuration}",
        };
        if (framework is not null) args.Add($"-property:TargetFramework={framework}");

        var (exitCode, stdout, stderr) = ProcessRunner.Capture("dotnet", args);
        if (exitCode != 0) throw new ToolException($"Couldn't read properties of {projectFile}:{Environment.NewLine}{stdout}{stderr}");

        using var json = JsonDocument.Parse(stdout);
        var properties = json.RootElement.GetProperty("Properties");
        string? Get(string name) =>
            properties.TryGetProperty(name, out var value) && value.GetString() is { Length: > 0 } text ? text : null;

        return new ProjectInfo(
            TargetPath: Get("TargetPath")
                ?? throw new ToolException($"{Path.GetFileName(projectFile)} targets several frameworks. Pass --framework."),
            ProjectDir: Get("ProjectDir") ?? Path.GetDirectoryName(projectFile)!,
            RootNamespace: Get("RootNamespace") ?? Path.GetFileNameWithoutExtension(projectFile),
            NuGetPackageRoot: Get("NuGetPackageRoot"));
    }
}

internal static class ProcessRunner
{
    public static int Run(string fileName, IEnumerable<string> args, string? workingDirectory = null)
    {
        using var process = Process.Start(CreateStartInfo(fileName, args, workingDirectory))!;
        process.WaitForExit();
        return process.ExitCode;
    }

    public static (int ExitCode, string Stdout, string Stderr) Capture(string fileName, IEnumerable<string> args)
    {
        var startInfo = CreateStartInfo(fileName, args, workingDirectory: null);
        startInfo.RedirectStandardOutput = true;
        startInfo.RedirectStandardError = true;

        using var process = Process.Start(startInfo)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        return (process.ExitCode, stdout.Result, stderr);
    }

    private static ProcessStartInfo CreateStartInfo(string fileName, IEnumerable<string> args, string? workingDirectory)
    {
        var startInfo = new ProcessStartInfo(fileName) { UseShellExecute = false, WorkingDirectory = workingDirectory ?? "" };
        foreach (var arg in args) startInfo.ArgumentList.Add(arg);
        return startInfo;
    }
}

internal sealed class ToolException(string message) : Exception(message);
