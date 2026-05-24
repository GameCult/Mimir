// Sketch only. Fensalir-side compute shape for updating simple live splat
// claims from synchronized feature observations.

struct FeatureObservation
{
    uint StableKeyLow;
    uint StableKeyHigh;
    float3 PositionMeters;
    float3 Normal;
    float4 Color;
    float Confidence;
    uint SourceIndex;
    uint TimestampLo;
    uint TimestampHi;
};

struct SplatClaim
{
    uint StableKeyLow;
    uint StableKeyHigh;
    float3 PositionMeters;
    float RadiusMeters;
    float4 Color;
    float Confidence;
    uint TimeMinLo;
    uint TimeMinHi;
    uint TimeMaxLo;
    uint TimeMaxHi;
};

cbuffer SplatUpdateConstants : register(b0)
{
    uint ObservationCount;
    float Decay;
    float Blend;
    float DefaultRadius;
};

StructuredBuffer<FeatureObservation> Observations : register(t0);
RWStructuredBuffer<SplatClaim> Claims : register(u0);

[numthreads(128, 1, 1)]
void main(uint3 id : SV_DispatchThreadID)
{
    uint index = id.x;
    if (index >= ObservationCount)
    {
        return;
    }

    FeatureObservation obs = Observations[index];

    // Sketch simplification: production needs a hash table or sorted stable-key
    // compaction pass. This writes one observation to one claim slot.
    SplatClaim claim;
    claim.StableKeyLow = obs.StableKeyLow;
    claim.StableKeyHigh = obs.StableKeyHigh;
    claim.PositionMeters = obs.PositionMeters;
    claim.RadiusMeters = DefaultRadius;
    claim.Color = obs.Color;
    claim.Confidence = saturate(obs.Confidence);
    claim.TimeMinLo = obs.TimestampLo;
    claim.TimeMinHi = obs.TimestampHi;
    claim.TimeMaxLo = obs.TimestampLo;
    claim.TimeMaxHi = obs.TimestampHi;
    Claims[index] = claim;
}

