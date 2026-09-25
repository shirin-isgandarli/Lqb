using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Lqb.Design;

/// <summary>
/// Creates the DbContext at design time, following dotnet-ef's conventions:
/// an <see cref="IDesignTimeDbContextFactory{TContext}"/> first, then a public
/// parameterless constructor. (Creating it through the app's host builder, as
/// dotnet-ef also can, is a later improvement.)
/// </summary>
internal static class ContextFactory
{
    public static DbContext Create(Assembly targetAssembly, Assembly startupAssembly, string? contextName)
    {
        var types = new[] { targetAssembly, startupAssembly }
            .Distinct()
            .SelectMany(GetLoadableTypes)
            .Where(t => t is { IsClass: true, IsAbstract: false, IsGenericTypeDefinition: false })
            .ToList();

        var factories = types
            .Select(t => (Factory: t, Context: GetFactoryContextType(t)))
            .Where(x => x.Context is not null && Matches(x.Context, contextName))
            .ToList();

        if (factories.Count == 1)
        {
            var (factoryType, contextType) = factories[0];
            var factory = Activator.CreateInstance(factoryType)!;
            var createMethod = typeof(IDesignTimeDbContextFactory<>)
                .MakeGenericType(contextType!)
                .GetMethod(nameof(IDesignTimeDbContextFactory<DbContext>.CreateDbContext))!;
            return (DbContext)createMethod.Invoke(factory, new object[] { Array.Empty<string>() })!;
        }

        if (factories.Count > 1) throw TooMany(factories.Select(f => f.Context!));

        var contexts = types
            .Where(t => typeof(DbContext).IsAssignableFrom(t)
                        && t.GetConstructor(Type.EmptyTypes) is not null
                        && Matches(t, contextName))
            .ToList();

        return contexts.Count switch
        {
            1 => (DbContext)Activator.CreateInstance(contexts[0])!,
            0 => throw new LqbException(
                "No DbContext could be created. Add an IDesignTimeDbContextFactory<TContext> " +
                "to your project, or give the context a public parameterless constructor."),
            _ => throw TooMany(contexts),
        };
    }

    private static Type? GetFactoryContextType(Type type) =>
        type.GetInterfaces()
            .FirstOrDefault(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IDesignTimeDbContextFactory<>))
            ?.GetGenericArguments()[0];

    private static bool Matches(Type contextType, string? name) =>
        name is null || contextType.Name == name || contextType.FullName == name;

    private static LqbException TooMany(IEnumerable<Type> contexts) =>
        new($"More than one DbContext was found. Choose one with --context: " +
            string.Join(", ", contexts.Select(c => c.Name).Distinct()));

    private static IEnumerable<Type> GetLoadableTypes(Assembly assembly)
    {
        try
        {
            return assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            return ex.Types.OfType<Type>();
        }
    }
}
