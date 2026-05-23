// Sketch only. D3D12 compute shape for batched chirp-bin scoring.
// One thread group scores one candidate window against one symbol/bin block.

cbuffer ChirpScoreConstants : register(b0)
{
    uint SampleCount;
    uint BinCount;
    uint CandidateCount;
    uint KernelStride;
};

StructuredBuffer<float> Samples : register(t0);
StructuredBuffer<uint> CandidateSampleOffsets : register(t1);
StructuredBuffer<float2> DechirpKernel : register(t2);
StructuredBuffer<float2> BinKernel : register(t3);
RWStructuredBuffer<float4> Scores : register(u0);

groupshared float2 Partial[256];

[numthreads(256, 1, 1)]
void main(uint3 groupId : SV_GroupID, uint groupIndex : SV_GroupIndex)
{
    uint candidate = groupId.x;
    uint bin = groupId.y;
    uint offset = CandidateSampleOffsets[candidate];

    float2 acc = float2(0.0, 0.0);
    for (uint i = groupIndex; i < SampleCount; i += 256)
    {
        float x = Samples[offset + i];
        float2 chirp = DechirpKernel[i];
        float2 tone = BinKernel[bin * KernelStride + i];

        float2 dechirped = float2(x * chirp.x, x * chirp.y);
        acc += float2(
            dechirped.x * tone.x - dechirped.y * tone.y,
            dechirped.x * tone.y + dechirped.y * tone.x);
    }

    Partial[groupIndex] = acc;
    GroupMemoryBarrierWithGroupSync();

    for (uint stride = 128; stride > 0; stride >>= 1)
    {
        if (groupIndex < stride)
        {
            Partial[groupIndex] += Partial[groupIndex + stride];
        }

        GroupMemoryBarrierWithGroupSync();
    }

    if (groupIndex == 0)
    {
        float2 z = Partial[0];
        float energy = dot(z, z);
        Scores[candidate * BinCount + bin] = float4(z.x, z.y, energy, 0.0);
    }
}

