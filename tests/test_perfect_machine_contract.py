import json
from pathlib import Path
import unittest


ROOT = Path(__file__).resolve().parents[1]


class PerfectMachineContractTests(unittest.TestCase):
    def test_contract_declares_native_reservoir_authorities(self):
        data = json.loads((ROOT / "config" / "perfect-machine.example.json").read_text(encoding="utf-8"))

        self.assertEqual("gamecult.localcast.perfect_machine.v1", data["schema"])
        self.assertEqual(5.0, data["reservoir"]["durationSeconds"])
        self.assertEqual("Aquarium", data["authorities"]["denseVisualFusion"])
        self.assertEqual("Faust", data["authorities"]["hotAudioDsp"])
        self.assertEqual("Aquarium", data["outputs"]["video"]["owner"])

    def test_contract_covers_six_cameras_six_mics_and_required_sample_views(self):
        data = json.loads((ROOT / "config" / "perfect-machine.example.json").read_text(encoding="utf-8"))

        self.assertEqual(6, len(data["visualSensors"]))
        self.assertEqual(6, len(data["audioSensors"]))
        self.assertEqual({"camera_frame", "surface_claim", "material_claim", "audio_block", "phase_claim"}, set(data["reservoirRings"]) & {
            "camera_frame",
            "surface_claim",
            "material_claim",
            "audio_block",
            "phase_claim",
        })
        self.assertIn("dense-feature-extraction", data["bridgeDemotion"]["notPython"])
        self.assertEqual("single-native-rolling-buffer-with-typed-views", data["reservoir"]["storageLayout"])

    def test_native_reservoir_header_exposes_small_c_abi(self):
        header = (ROOT / "native" / "reservoir" / "include" / "localcast_reservoir.h").read_text(encoding="utf-8")

        self.assertIn("typedef struct LocalcastReservoir LocalcastReservoir;", header)
        self.assertIn("typedef struct LocalcastSampleHandle", header)
        self.assertIn("uint64_t payload_handle;", header)
        self.assertIn("LocalcastReservoir *localcast_reservoir_create", header)
        self.assertIn("void localcast_reservoir_destroy", header)
        self.assertIn("bool localcast_reservoir_push", header)
        self.assertIn("uint64_t localcast_reservoir_edge_ns", header)
        self.assertIn("uint64_t localcast_reservoir_window_start_ns", header)
        self.assertIn("enum LocalcastSampleKind", header)
        self.assertIn("size_t localcast_reservoir_len", header)
        self.assertIn("size_t localcast_reservoir_view_len", header)
        self.assertIn("bool localcast_reservoir_sample_at", header)
        self.assertIn("bool localcast_reservoir_view_sample_at", header)
        self.assertIn("bool localcast_reservoir_latest_for_sensor", header)

    def test_native_runtime_header_exposes_typed_producer_spine(self):
        header = (ROOT / "native" / "reservoir" / "include" / "localcast_reservoir.h").read_text(encoding="utf-8")

        self.assertIn("typedef struct LocalcastRuntime LocalcastRuntime;", header)
        self.assertIn("typedef struct LocalcastRuntimeStatus", header)
        self.assertIn("uint64_t edge_ns;", header)
        self.assertIn("size_t total_sample_count;", header)
        self.assertIn("size_t camera_frame_count;", header)
        self.assertIn("LocalcastRuntime *localcast_runtime_create", header)
        self.assertIn("void localcast_runtime_destroy", header)
        for function in [
            "localcast_runtime_push_camera_frame",
            "localcast_runtime_push_camera_feature",
            "localcast_runtime_push_scene_ray",
            "localcast_runtime_push_surface_claim",
            "localcast_runtime_push_material_claim",
            "localcast_runtime_push_audio_block",
            "localcast_runtime_push_phase_claim",
            "localcast_runtime_push_event_claim",
            "localcast_runtime_push_render_packet",
            "localcast_runtime_status",
        ]:
            self.assertIn(function, header)


if __name__ == "__main__":
    unittest.main()
