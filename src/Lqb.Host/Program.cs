using System.Reflection;
using System.Runtime.Loader;

// lqb-host is started by dotnet-lqb with:
//   dotnet exec --depsfile <app>.deps.json --runtimeconfig <app>.runtimeconfig.json lqb-host.dll ...
// so this process resolves EF Core, the provider and Lqb.Design exactly as the developer's app would.
// It deliberately references nothing and calls into Lqb.Design by reflection.

var targetDir = GetOption(args, "--target-dir");
if (targetDir is null)
{
    Console.Error.WriteLine("lqb-host is started by dotnet-lqb; don't run it directly.");
    return 1;
}

// The app's own assemblies (and its referenced projects) live in its bin folder, not the NuGet cache.
AssemblyLoadContext.Default.Resolving += (context, name) =>
{
    var candidate = Path.Combine(targetDir, name.Name + ".dll");
    return File.Exists(candidate) ? context.LoadFromAssemblyPath(candidate) : null;
};

Assembly design;
try
{
    design = Assembly.Load(new AssemblyName("Lqb.Design"));
}
catch (FileNotFoundException)
{
    Console.Error.WriteLine("Your startup project doesn't reference Lqb.Design. Run: dotnet add package Lqb.Design");
    return 1;
}

var run = design.GetType("Lqb.Design.Executor", throwOnError: true)!
    .GetMethod("Run", BindingFlags.Public | BindingFlags.Static)!;

try
{
    return (int)run.Invoke(null, new object[] { args })!;
}
catch (TargetInvocationException ex) when (ex.InnerException is not null)
{
    Console.Error.WriteLine(ex.InnerException);
    return 1;
}

static string? GetOption(string[] args, string name)
{
    var index = Array.IndexOf(args, name);
    return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
}
