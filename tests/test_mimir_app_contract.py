from pathlib import Path
import unittest


ROOT = Path(__file__).resolve().parents[1]


class MimirAppContractTests(unittest.TestCase):
    def test_solution_owns_mimir_app_and_runtime_projects(self):
        solution = (ROOT / "Mimir.slnx").read_text(encoding="utf-8")

        self.assertIn("src/Mimir.App/Mimir.App.csproj", solution)
        self.assertIn("src/Mimir.Runtime/Mimir.Runtime.csproj", solution)

    def test_app_references_aquarium_engine_as_host_library(self):
        app_project = (ROOT / "src" / "Mimir.App" / "Mimir.App.csproj").read_text(encoding="utf-8-sig")
        program = (ROOT / "src" / "Mimir.App" / "Program.cs").read_text(encoding="utf-8")

        self.assertIn("Aquarium.Engine.csproj", app_project)
        self.assertIn("Mimir.Runtime.csproj", app_project)
        self.assertIn("AquariumHost.Run", program)
        self.assertIn("--client-assembly", program)

    def test_runtime_references_localcast_runtime_client(self):
        runtime_project = (ROOT / "src" / "Mimir.Runtime" / "Mimir.Runtime.csproj").read_text(encoding="utf-8-sig")
        factory = (ROOT / "src" / "Mimir.Runtime" / "MimirRuntimeFactory.cs").read_text(encoding="utf-8")
        runtime = (ROOT / "src" / "Mimir.Runtime" / "MimirRuntime.cs").read_text(encoding="utf-8")

        self.assertIn("Aquarium.Engine.Contracts.csproj", runtime_project)
        self.assertIn("Aquarium.LocalCast.csproj", runtime_project)
        self.assertIn("IAquariumRuntimeFactory", factory)
        self.assertIn("new MimirRuntime(options)", factory)
        self.assertIn("new LocalCastRuntime(options)", runtime)

    def test_runtime_owns_synchronization_buffers(self):
        runtime = (ROOT / "src" / "Mimir.Runtime" / "MimirRuntime.cs").read_text(encoding="utf-8")
        hub = (ROOT / "src" / "Mimir.Runtime" / "Synchronization" / "MimirSynchronizationHub.cs").read_text(encoding="utf-8")
        buffer = (ROOT / "src" / "Mimir.Runtime" / "Synchronization" / "MimirRollingStreamBuffer.cs").read_text(encoding="utf-8")
        settings = (ROOT / "src" / "Mimir.Runtime" / "Synchronization" / "MimirSynchronizationSettings.cs").read_text(encoding="utf-8")

        self.assertIn("MimirSynchronizationHub", runtime)
        self.assertIn("PollSources", runtime)
        self.assertIn("RegisterStreamSource", runtime)
        self.assertIn("MimirStreamBufferSet", hub)
        self.assertIn("TryRead(out var sample)", hub)
        self.assertIn("EvictExpired", buffer)
        self.assertIn("TimeSpan.FromSeconds(5)", settings)
        self.assertIn("MIMIR_NETWORK_VIDEO_STREAMS", settings)
        self.assertIn("MIMIR_LOCAL_AUDIO_STREAMS", settings)
        native_source = (ROOT / "src" / "Mimir.Runtime" / "Synchronization" / "MimirNativeIngestStreamSource.cs").read_text(encoding="utf-8")
        process_source = (ROOT / "src" / "Mimir.Runtime" / "Synchronization" / "MimirProcessStreamSource.cs").read_text(encoding="utf-8")
        frame_descriptor = (ROOT / "src" / "Mimir.Runtime" / "Synchronization" / "MimirVideoFrameDescriptor.cs").read_text(encoding="utf-8")

        self.assertIn("payloadHandle", native_source)
        self.assertIn("PushVideoFrame", native_source)
        self.assertIn("ReadOnlyMemory<byte>", native_source)
        self.assertIn("LeapStereoIr", frame_descriptor)
        self.assertIn("DeviceTimestampNs", frame_descriptor)
        self.assertIn("NativeHandle", frame_descriptor)
        self.assertIn("RedirectStandardOutput", process_source)


if __name__ == "__main__":
    unittest.main()
