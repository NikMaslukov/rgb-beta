from __future__ import annotations

import copy
import json
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest
import warnings
import zipfile


REPO_ROOT = Path(__file__).resolve().parents[2]
VERIFIER = REPO_ROOT / "scripts" / "verify_plugin_artifact.py"
CONTRACT_PATH = REPO_ROOT / "scripts" / "plugin-artifact-contract.json"


class ArtifactFixture:
    def __init__(self, root: Path, strict_gate: bool = False):
        self.root = root
        self.publish = root / "publish"
        self.cache = root / "packages"
        self.publish.mkdir()
        self.contract = json.loads(CONTRACT_PATH.read_text(encoding="utf-8"))
        self.gate_path = "runtimes/linux-x64/native/librgbverifycffi.so"
        self.core_path = "runtimes/linux-x64/native/librgblibcffi.so"

        plain_files = {
            "btcpay.plugin.json": b"{}",
            "BTCPayServer.Plugins.RgbUtexo.dll": b"plugin",
            "RgbRestoreHelper.dll": b"helper",
            "RgbRestoreHelper.runtimeconfig.json": b"{}",
            "SharpCompress.dll": b"sharp",
            self.gate_path: b"gate",
            self.core_path: b"core",
        }
        for relative, data in plain_files.items():
            self.write(relative, data)

        plugin_packages = {
            "RgbLib/0.3.0-test": {
                "runtime": {"lib/net8.0/RgbLib.dll": {}},
                "runtimeTargets": {
                    self.core_path: {"rid": "linux-x64", "assetType": "native"}
                },
            },
            "SharpCompress/0.50.4-test": {
                "runtime": {"lib/net10.0/SharpCompress.dll": {}}
            },
        }
        if strict_gate:
            plugin_packages["RgbVerifyCffi/1.2.3-test"] = {
                "runtimeTargets": {
                    self.gate_path: {"rid": "linux-x64", "assetType": "native"}
                }
            }
        self.write_json(
            "BTCPayServer.Plugins.RgbUtexo.deps.json",
            {"targets": {"net10.0": plugin_packages}},
        )
        self.write_json(
            "RgbRestoreHelper.deps.json",
            {
                "targets": {
                    "net10.0": {
                        "RgbLib/0.3.0-test": {
                            "runtime": {"lib/net8.0/RgbLib.dll": {}},
                            "runtimeTargets": {
                                self.core_path: {"rid": "linux-x64", "assetType": "native"}
                            },
                        }
                    }
                }
            },
        )
        self.cache_write("RgbLib", "0.3.0-test", self.core_path, b"core")
        if strict_gate:
            self.cache_write("RgbVerifyCffi", "1.2.3-test", self.gate_path, b"gate")

    def write(self, relative: str, data: bytes) -> None:
        path = self.publish / relative
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_bytes(data)

    def write_json(self, relative: str, value: object) -> None:
        self.write(relative, json.dumps(value).encode())

    def cache_write(self, package: str, version: str, relative: str, data: bytes) -> None:
        path = self.cache / package.lower() / version / relative
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_bytes(data)

    def archive(self, name: str = "plugin.btcpay", prefix: str = "") -> Path:
        destination = self.root / name
        with zipfile.ZipFile(destination, "w") as archive:
            for path in sorted(self.publish.rglob("*")):
                if path.is_file():
                    relative = path.relative_to(self.publish).as_posix()
                    archive.write(path, prefix + relative)
        return destination

    def contract_file(self, contract: dict | None = None) -> Path:
        path = self.root / "contract.json"
        path.write_text(json.dumps(contract or self.contract), encoding="utf-8")
        return path


class VerifyPluginArtifactTests(unittest.TestCase):
    def fixture(self, strict_gate: bool = False):
        temporary = tempfile.TemporaryDirectory()
        self.addCleanup(temporary.cleanup)
        return ArtifactFixture(Path(temporary.name), strict_gate)

    def run_verify(
        self,
        fixture: ArtifactFixture,
        artifact: Path | None = None,
        *,
        strict: bool = False,
        contract: Path | None = None,
        gate_package: bool = False,
    ) -> subprocess.CompletedProcess[str]:
        command = [sys.executable, str(VERIFIER), str(artifact or fixture.publish)]
        command += ["--contract", str(contract or CONTRACT_PATH)]
        command += ["--provenance", "strict" if strict else "pre-package"]
        if strict and not gate_package:
            command += ["--package-cache", str(fixture.cache)]
        if gate_package:
            command.append("--gate-package")
        return subprocess.run(command, text=True, capture_output=True, check=False)

    def assert_failed(self, result: subprocess.CompletedProcess[str], message: str) -> None:
        self.assertNotEqual(0, result.returncode, result.stdout + result.stderr)
        self.assertIn(message, result.stderr)

    def test_accepts_complete_publish_tree_and_archive(self):
        fixture = self.fixture()
        for artifact in (fixture.publish, fixture.archive()):
            with self.subTest(artifact=artifact):
                result = self.run_verify(fixture, artifact)
                self.assertEqual(0, result.returncode, result.stdout + result.stderr)
                self.assertIn("PRE-PACKAGE MODE", result.stdout)
                self.assertIn("supported plugin RIDs verified: linux-x64", result.stdout)

    def test_rejects_removal_of_each_helper_file(self):
        for helper_file in (
            "RgbRestoreHelper.dll",
            "RgbRestoreHelper.deps.json",
            "RgbRestoreHelper.runtimeconfig.json",
        ):
            with self.subTest(helper_file=helper_file):
                fixture = self.fixture()
                (fixture.publish / helper_file).unlink()
                self.assert_failed(self.run_verify(fixture), f"missing required artifact path: {helper_file}")

    def test_rejects_missing_sharpcompress(self):
        fixture = self.fixture()
        (fixture.publish / "SharpCompress.dll").unlink()
        self.assert_failed(self.run_verify(fixture), "missing required artifact path: SharpCompress.dll")

    def test_rejects_missing_gate_for_claimed_rid(self):
        fixture = self.fixture()
        (fixture.publish / fixture.gate_path).unlink()
        self.assert_failed(self.run_verify(fixture), f"missing required artifact path: {fixture.gate_path}")

    def test_rejects_missing_core_for_claimed_rid(self):
        fixture = self.fixture()
        (fixture.publish / fixture.core_path).unlink()
        self.assert_failed(self.run_verify(fixture), f"missing required artifact path: {fixture.core_path}")

    def test_rejects_linux_arm64_claim_with_gate_but_no_core(self):
        fixture = self.fixture()
        arm_gate = "runtimes/linux-arm64/native/librgbverifycffi.so"
        arm_core = "runtimes/linux-arm64/native/librgblibcffi.so"
        fixture.write(arm_gate, b"extra gate")
        contract = copy.deepcopy(fixture.contract)
        contract["plugin"]["supported_rids"]["linux-arm64"] = {
            "gate": arm_gate,
            "core": arm_core,
        }
        self.assert_failed(
            self.run_verify(fixture, contract=fixture.contract_file(contract)),
            f"missing required artifact path: {arm_core}",
        )

    def test_rejects_required_archive_file_under_extra_prefix(self):
        fixture = self.fixture()
        archive_path = fixture.root / "prefixed-gate.btcpay"
        with zipfile.ZipFile(archive_path, "w") as archive:
            for path in sorted(fixture.publish.rglob("*")):
                if not path.is_file():
                    continue
                relative = path.relative_to(fixture.publish).as_posix()
                stored = "publish-out/" + relative if relative == fixture.gate_path else relative
                archive.write(path, stored)
        self.assert_failed(
            self.run_verify(fixture, archive_path),
            f"missing required artifact path: {fixture.gate_path}",
        )

    def test_rejects_duplicate_required_archive_entry(self):
        fixture = self.fixture()
        archive_path = fixture.archive("duplicate.btcpay")
        with warnings.catch_warnings():
            warnings.simplefilter("ignore", UserWarning)
            with zipfile.ZipFile(archive_path, "a") as archive:
                archive.writestr(fixture.gate_path, b"duplicate gate")
        self.assert_failed(
            self.run_verify(fixture, archive_path),
            f"required artifact path occurs 2 times; expected exactly one: {fixture.gate_path}",
        )

    def test_strict_mode_rejects_hand_staged_gate(self):
        fixture = self.fixture()
        self.assert_failed(
            self.run_verify(fixture, strict=True),
            f"{fixture.gate_path} is not declared as a native asset of RgbVerifyCffi",
        )

    def test_strict_mode_rejects_byte_mismatched_gate(self):
        fixture = self.fixture(strict_gate=True)
        fixture.cache_write("RgbVerifyCffi", "1.2.3-test", fixture.gate_path, b"different")
        self.assert_failed(
            self.run_verify(fixture, strict=True),
            f"{fixture.gate_path} is not byte-identical to the RgbVerifyCffi package-cache copy",
        )

    def test_extra_native_assets_do_not_create_support_claims(self):
        fixture = self.fixture()
        fixture.write("runtimes/linux-arm64/native/librgbverifycffi.so", b"extra gate")
        win_core = "runtimes/win-x64/native/rgblibcffi.dll"
        fixture.write(win_core, b"extra core")
        deps_path = fixture.publish / "BTCPayServer.Plugins.RgbUtexo.deps.json"
        deps = json.loads(deps_path.read_text(encoding="utf-8"))
        deps["targets"]["net10.0"]["RgbLib/0.3.0-test"]["runtimeTargets"][win_core] = {
            "rid": "win-x64",
            "assetType": "native",
        }
        fixture.write_json("BTCPayServer.Plugins.RgbUtexo.deps.json", deps)
        fixture.cache_write("RgbLib", "0.3.0-test", win_core, b"extra core")
        result = self.run_verify(fixture)
        self.assertEqual(0, result.returncode, result.stdout + result.stderr)
        self.assertIn("supported plugin RIDs verified: linux-x64", result.stdout)
        self.assertNotIn("supported plugin RIDs verified: linux-x64, linux-arm64", result.stdout)
        self.assertNotIn("win-x64", result.stdout)

    def test_strict_gate_package_inspection_accepts_three_rid_fixture(self):
        fixture = self.fixture()
        package = fixture.root / "RgbVerifyCffi.1.2.3-test.nupkg"
        gate_contract = fixture.contract["gate_package"]
        with zipfile.ZipFile(package, "w") as archive:
            archive.writestr("RgbVerifyCffi.nuspec", "<package><metadata><id>RgbVerifyCffi</id></metadata></package>")
            archive.writestr(gate_contract["placeholder"], b"")
            for relative in gate_contract["required_assets"].values():
                archive.writestr(relative, b"native")
        result = self.run_verify(fixture, package, strict=True, gate_package=True)
        self.assertEqual(0, result.returncode, result.stdout + result.stderr)
        self.assertIn("linux-x64, linux-arm64, osx-arm64", result.stdout)


if __name__ == "__main__":
    unittest.main()
