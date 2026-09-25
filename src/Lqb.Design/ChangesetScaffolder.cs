using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Design;
using Microsoft.Extensions.DependencyInjection;

namespace Lqb.Design;

internal sealed record ScaffoldResult(
    string UpSql,
    string DownSql,
    bool RunInTransaction,
    string SnapshotPath,
    string SnapshotCode,
    string? OldSnapshotPath);

/// <summary>
/// Does what `dotnet ef migrations add` does, minus the C# migration class:
/// diffs the last model snapshot against the current model, turns the differences into
/// SQL in both directions, and produces the updated snapshot for the next run.
/// </summary>
internal static class ChangesetScaffolder
{
    public static ScaffoldResult? Scaffold(DbContext context, string projectDir, string rootNamespace, string? migrationsDir)
    {
        if (migrationsDir is not null && !IsInside(projectDir, migrationsDir))
        {
            throw new LqbException($"The migrations folder must be inside the project so the snapshot gets compiled: {migrationsDir}");
        }

        var currentModel = context.GetService<IDesignTimeModel>().Model;
        var snapshot = context.GetService<IMigrationsAssembly>().ModelSnapshot;
        var previousModel = snapshot is null ? null : Initialize(context, snapshot.Model);

        // The same two diffs EF uses to fill a migration's Up() and Down().
        var differ = context.GetService<IMigrationsModelDiffer>();
        var upOperations = differ.GetDifferences(previousModel?.GetRelationalModel(), currentModel.GetRelationalModel());
        if (upOperations.Count == 0) return null;
        var downOperations = differ.GetDifferences(currentModel.GetRelationalModel(), previousModel?.GetRelationalModel());

        var sqlGenerator = context.GetService<IMigrationsSqlGenerator>();
        var upCommands = sqlGenerator.Generate(upOperations, currentModel, MigrationsSqlGenerationOptions.Script);
        var downCommands = sqlGenerator.Generate(downOperations, previousModel, MigrationsSqlGenerationOptions.Script);

        // Keep the existing snapshot's name. Its file stays where it is unless a
        // migrations folder is given, in which case it moves there.
        var contextType = context.GetType();
        var snapshotName = snapshot?.GetType().Name ?? contextType.Name + "ModelSnapshot";
        var existingPath = FindExistingSnapshot(projectDir, snapshotName);
        var snapshotPath = migrationsDir is not null
            ? Path.Combine(migrationsDir, snapshotName + ".cs")
            : existingPath ?? Path.Combine(projectDir, "Migrations", snapshotName + ".cs");

        // Like EF: an existing snapshot keeps its namespace; a new one gets one from its folder.
        var snapshotNamespace = snapshot?.GetType().Namespace
            ?? NamespaceForFolder(rootNamespace, projectDir, Path.GetDirectoryName(snapshotPath)!);
        var codeGenerator = BuildDesignTimeServices(context)
            .GetRequiredService<IMigrationsCodeGeneratorSelector>()
            .Select(null); // C#
        var snapshotCode = codeGenerator.GenerateSnapshot(snapshotNamespace, contextType, snapshotName, currentModel);

        return new ScaffoldResult(
            UpSql: ToSql(upCommands),
            DownSql: ToSql(downCommands),
            RunInTransaction: !upCommands.Concat(downCommands).Any(c => c.TransactionSuppressed),
            SnapshotPath: snapshotPath,
            SnapshotCode: snapshotCode,
            OldSnapshotPath: existingPath is not null && !SamePath(existingPath, snapshotPath) ? existingPath : null);
    }

    // A snapshot builds a mutable model; like EF, finalize and initialize it before diffing.
    private static IModel Initialize(DbContext context, IModel model)
    {
        if (model is IMutableModel mutable) model = mutable.FinalizeModel();
        return context.GetService<IModelRuntimeInitializer>().Initialize(model, designTime: true, validationLogger: null);
    }

    // Design-time services (snapshot code generator, provider annotation code generator, ...),
    // assembled the way EF's docs describe for using design-time services from code.
    private static IServiceProvider BuildDesignTimeServices(DbContext context)
    {
        var services = new ServiceCollection();
        services.AddEntityFrameworkDesignTimeServices();
        services.AddDbContextDesignTimeServices(context);

        // Provider services (e.g. SqlServerDesignTimeServices) are found like dotnet-ef finds them:
        // through [assembly: DesignTimeProviderServices] on the provider assembly.
        var providerAssembly = Assembly.Load(new AssemblyName(context.Database.ProviderName!));
        var attribute = providerAssembly.GetCustomAttribute<DesignTimeProviderServicesAttribute>();
        if (attribute is not null)
        {
            var type = providerAssembly.GetType(attribute.TypeName, throwOnError: true)!;
            ((IDesignTimeServices)Activator.CreateInstance(type)!).ConfigureDesignTimeServices(services);
        }

        return services.BuildServiceProvider();
    }

    private static string ToSql(IEnumerable<MigrationCommand> commands) =>
        string.Join(Environment.NewLine, commands.Select(c => c.CommandText.Trim()));

    private static string? FindExistingSnapshot(string projectDir, string snapshotName) =>
        Directory
            .EnumerateFiles(projectDir, snapshotName + ".cs", SearchOption.AllDirectories)
            .FirstOrDefault(path => !IsBuildOutput(projectDir, path));

    // "Data/Migrations" in project "MyApp" -> "MyApp.Data.Migrations", like EF's --output-dir.
    private static string NamespaceForFolder(string rootNamespace, string projectDir, string folder)
    {
        var relative = Path.GetRelativePath(projectDir, folder);
        if (relative == ".") return rootNamespace;

        var segments = relative
            .Split(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar }, StringSplitOptions.RemoveEmptyEntries)
            .Select(ToIdentifier);
        return string.Join('.', segments.Prepend(rootNamespace));
    }

    private static string ToIdentifier(string segment)
    {
        var identifier = new string(segment.Select(c => char.IsLetterOrDigit(c) || c == '_' ? c : '_').ToArray());
        return char.IsDigit(identifier[0]) ? "_" + identifier : identifier;
    }

    private static bool IsInside(string projectDir, string path)
    {
        var relative = Path.GetRelativePath(projectDir, path);
        return !Path.IsPathRooted(relative) && relative != ".." && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal);
    }

    private static bool SamePath(string a, string b) =>
        string.Equals(
            Path.GetFullPath(a),
            Path.GetFullPath(b),
            OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase);

    private static bool IsBuildOutput(string projectDir, string path)
    {
        var firstSegment = Path.GetRelativePath(projectDir, path)
            .Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)[0];
        return firstSegment is "bin" or "obj";
    }
}
