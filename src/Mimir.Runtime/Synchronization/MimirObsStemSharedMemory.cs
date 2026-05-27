using System.IO.MemoryMappedFiles;
using System.Runtime.Versioning;
using System.Text;

namespace Mimir.Runtime.Synchronization;

public sealed class MimirObsStemSharedMemoryPublisher : IDisposable
{
    public const string DefaultMapName = "Local\\MimirObsStemBus";
    public const int MaxStems = 8;
    public const int MaxFramesPerStem = 4096;
    public const int StemIdBytes = 64;
    public const int DisplayNameBytes = 64;
    public const int SourceIdBytes = 64;
    public const int HeaderBytes = 64;
    public const int StemRecordBytes = 224;
    public const int MapBytes = HeaderBytes + (MaxStems * StemRecordBytes) + (MaxStems * MaxFramesPerStem * sizeof(float));
    private const ulong Magic = 0x4D545352494D494DUL;
    private const int Version = 1;

    private readonly MemoryMappedFile file;
    private readonly MemoryMappedViewAccessor view;
    private uint generation;
    private bool disposed;

    [SupportedOSPlatform("windows")]
    public MimirObsStemSharedMemoryPublisher(string mapName = DefaultMapName)
    {
        file = MemoryMappedFile.CreateOrOpen(mapName, MapBytes, MemoryMappedFileAccess.ReadWrite);
        view = file.CreateViewAccessor(0, MapBytes, MemoryMappedFileAccess.ReadWrite);
        view.Write(0, Magic);
        view.Write(8, Version);
        view.Write(20, MaxStems);
        view.Write(24, MaxFramesPerStem);
        view.Write(28, HeaderBytes);
        view.Write(32, StemRecordBytes);
        view.Write(36, MapBytes);
    }

    public void Publish(MimirObsStemPublicationSnapshot snapshot)
    {
        if (disposed)
        {
            return;
        }

        var stems = snapshot.ReadyStems
            .Where(stem => stem.FrameCount > 0 && stem.SampleRate > 0)
            .OrderBy(stem => stem.StemId, StringComparer.Ordinal)
            .Take(MaxStems)
            .ToArray();
        var beginGeneration = unchecked(++generation) | 1u;
        view.Write(12, beginGeneration);
        view.Write(16, stems.Length);
        var sampleOffset = HeaderBytes + (MaxStems * StemRecordBytes);
        for (var index = 0; index < MaxStems; index++)
        {
            var recordOffset = HeaderBytes + (index * StemRecordBytes);
            if (index >= stems.Length)
            {
                ClearRecord(recordOffset);
                continue;
            }

            var stem = stems[index];
            var frameCount = Math.Min(stem.FrameCount, MaxFramesPerStem);
            WriteRecord(recordOffset, stem, sampleOffset, frameCount);
            view.WriteArray(sampleOffset, stem.Samples, 0, Math.Min(frameCount, stem.Samples.Length));
            sampleOffset += MaxFramesPerStem * sizeof(float);
        }

        view.Write(12, unchecked(beginGeneration + 1u));
    }

    private void ClearRecord(int offset)
    {
        view.Write(offset, -1);
        view.Write(offset + 4, 0);
        view.Write(offset + 8, 0);
        view.Write(offset + 12, 0);
        view.Write(offset + 16, 0);
        WriteFixedString(offset + 32, StemIdBytes, "");
        WriteFixedString(offset + 96, DisplayNameBytes, "");
        WriteFixedString(offset + 160, SourceIdBytes, "");
    }

    private void WriteRecord(int offset, MimirObsPublishedAudioStem stem, int sampleOffset, int frameCount)
    {
        view.Write(offset, stem.ChannelIndex);
        view.Write(offset + 4, stem.Configured ? 1 : 0);
        view.Write(offset + 8, stem.SampleRate);
        view.Write(offset + 12, frameCount);
        view.Write(offset + 16, sampleOffset);
        WriteFixedString(offset + 32, StemIdBytes, stem.StemId);
        WriteFixedString(offset + 96, DisplayNameBytes, stem.DisplayName);
        WriteFixedString(offset + 160, SourceIdBytes, stem.SourceId);
    }

    private void WriteFixedString(int offset, int byteCount, string value)
    {
        var buffer = new byte[byteCount];
        var bytes = Encoding.UTF8.GetBytes(value);
        Array.Copy(bytes, buffer, Math.Min(bytes.Length, byteCount - 1));
        view.WriteArray(offset, buffer, 0, byteCount);
    }

    public void Dispose()
    {
        disposed = true;
        view.Dispose();
        file.Dispose();
    }
}
