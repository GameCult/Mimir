using Aquarium.Engine;

namespace Mimir.Runtime;

public sealed class MimirRuntimeFactory : IAquariumRuntimeFactory
{
    public IAquariumRuntime Create(AquariumRuntimeOptions options)
    {
        return new MimirRuntime(options);
    }
}
