using Aquarium.Engine;
using Aquarium.LocalCast;

namespace Mimir.Runtime;

public sealed class MimirRuntimeFactory : IAquariumRuntimeFactory
{
    public IAquariumRuntime Create(AquariumRuntimeOptions options)
    {
        return new LocalCastRuntime(options);
    }
}
