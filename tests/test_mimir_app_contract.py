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

        self.assertIn("Aquarium.Engine.Contracts.csproj", runtime_project)
        self.assertIn("Aquarium.LocalCast.csproj", runtime_project)
        self.assertIn("IAquariumRuntimeFactory", factory)
        self.assertIn("new LocalCastRuntime(options)", factory)


if __name__ == "__main__":
    unittest.main()
