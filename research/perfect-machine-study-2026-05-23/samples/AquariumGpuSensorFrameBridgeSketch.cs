// Sketch only. This is not wired to production contracts yet.
// Purpose: show the desired shape of the Mimir -> Fensalir camera lowering cut.

using System;
using System.Collections.Generic;

namespace Mimir.PerfectMachineStudy;

public readonly record struct MimirVideoObservation(
    string SourceId,
    long Sequence,
    double CanonicalTimeSeconds,
    double DeviceTimeSeconds,
    int Width,
    int Height,
    string PixelFormat,
    IntPtr NativeHandle,
    int NativeHandleKind,
    string CalibrationId,
    float Confidence);

public sealed class AquariumGpuSensorFrameBridgeSketch
{
    private readonly List<AquariumGpuSensorCameraSketch> _cameras = new(8);

    public AquariumGpuSensorFrameSketch Lower(
        ReadOnlySpan<MimirVideoObservation> observations,
        double frameCanonicalTimeSeconds)
    {
        _cameras.Clear();

        for (var i = 0; i < observations.Length; i++)
        {
            ref readonly var source = ref observations[i];
            if (source.NativeHandle == IntPtr.Zero || source.Confidence <= 0.0f)
            {
                continue;
            }

            _cameras.Add(new AquariumGpuSensorCameraSketch(
                source.SourceId,
                source.Sequence,
                source.CanonicalTimeSeconds,
                source.DeviceTimeSeconds,
                source.Width,
                source.Height,
                source.PixelFormat,
                source.NativeHandle,
                source.NativeHandleKind,
                source.CalibrationId,
                source.Confidence));
        }

        // Production version should fill a reusable contract-owned array or pooled
        // struct buffer instead of allocating here.
        return new AquariumGpuSensorFrameSketch(
            frameCanonicalTimeSeconds,
            _cameras.ToArray());
    }
}

public readonly record struct AquariumGpuSensorFrameSketch(
    double CanonicalTimeSeconds,
    AquariumGpuSensorCameraSketch[] Cameras);

public readonly record struct AquariumGpuSensorCameraSketch(
    string SourceId,
    long Sequence,
    double CanonicalTimeSeconds,
    double DeviceTimeSeconds,
    int Width,
    int Height,
    string PixelFormat,
    IntPtr NativeHandle,
    int NativeHandleKind,
    string CalibrationId,
    float Confidence);

