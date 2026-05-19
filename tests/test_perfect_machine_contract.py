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

    def test_contract_covers_six_cameras_six_mics_and_required_rings(self):
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


if __name__ == "__main__":
    unittest.main()
