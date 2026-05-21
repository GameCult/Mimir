using Aquarium.Engine;

var hostArgs = args.ToList();
if (!HasOption(hostArgs, "--client-assembly") && !HasOption(hostArgs, "--live-assembly"))
{
    hostArgs.Add("--client-assembly");
    hostArgs.Add(ResolveRuntimeAssemblyPath());
}

return AquariumHost.Run(hostArgs.ToArray());

static bool HasOption(IReadOnlyList<string> args, string option)
{
    return args.Any(arg => string.Equals(arg, option, StringComparison.OrdinalIgnoreCase));
}

static string ResolveRuntimeAssemblyPath()
{
    var configured = Environment.GetEnvironmentVariable("MIMIR_RUNTIME_ASSEMBLY");
    if (!string.IsNullOrWhiteSpace(configured))
    {
        return configured;
    }

    return Path.Combine(AppContext.BaseDirectory, "Mimir.Runtime.dll");
}
