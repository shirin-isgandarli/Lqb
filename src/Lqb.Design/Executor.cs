using System.Reflection;

namespace Lqb.Design;

/// <summary>
/// Entry point called by lqb-host through reflection. Keep the type name and the
/// <c>Run(string[])</c> signature stable: the host only knows them by name.
/// </summary>
public static class Executor
{
    public static int Run(string[] args)
    {
        try
        {
            var arguments = HostArguments.Parse(args);
            return arguments.Command switch
            {
                "add" => Add(arguments),
                _ => throw new LqbException($"Unknown command '{arguments.Command}'."),
            };
        }
        catch (LqbException ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }
        catch (Exception ex)
        {
            // Unwrap reflection layers (e.g. an exception thrown inside the app's DbContext factory).
            var inner = ex;
            while (inner is TargetInvocationException { InnerException: { } next }) inner = next;
            Console.Error.WriteLine(inner);
            return 1;
        }
    }

    private static int Add(HostArguments arguments)
    {
        var targetAssembly = Assembly.Load(new AssemblyName(arguments.Required("assembly")));
        var startupAssembly = Assembly.Load(new AssemblyName(arguments.Required("startup-assembly")));

        using var context = ContextFactory.Create(targetAssembly, startupAssembly, arguments.Optional("context"));

        var result = ChangesetScaffolder.Scaffold(
            context,
            projectDir: arguments.Required("project-dir"),
            rootNamespace: arguments.Required("root-namespace"),
            migrationsDir: arguments.Optional("migrations-dir"));

        if (result is null)
        {
            Console.WriteLine("No model changes since the last snapshot. Nothing was generated.");
            return 0;
        }

        var id = $"{DateTime.UtcNow:yyyyMMddHHmmss}_{arguments.Required("name")}";
        var changesetPath = ChangelogWriter.Write(
            arguments.Required("changelog-dir"), id, arguments.Required("author"), result);

        Directory.CreateDirectory(Path.GetDirectoryName(result.SnapshotPath)!);
        File.WriteAllText(result.SnapshotPath, result.SnapshotCode);
        if (result.OldSnapshotPath is not null)
        {
            File.Delete(result.OldSnapshotPath);
            Console.WriteLine($"Moved snapshot from: {result.OldSnapshotPath}");
        }

        Console.WriteLine($"Created changeset: {changesetPath}");
        Console.WriteLine($"Updated snapshot:  {result.SnapshotPath}");
        return 0;
    }
}

internal sealed class HostArguments(string command, Dictionary<string, string> values)
{
    public string Command { get; } = command;

    public static HostArguments Parse(string[] args)
    {
        if (args.Length == 0) throw new LqbException("No command given.");

        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = 1; i + 1 < args.Length; i += 2)
        {
            if (!args[i].StartsWith("--", StringComparison.Ordinal)) throw new LqbException($"Unexpected argument '{args[i]}'.");
            values[args[i][2..]] = args[i + 1];
        }
        return new HostArguments(args[0], values);
    }

    public string Required(string key) =>
        values.TryGetValue(key, out var value) ? value : throw new LqbException($"Missing --{key}.");

    public string? Optional(string key) => values.GetValueOrDefault(key);
}

internal sealed class LqbException(string message) : Exception(message);
