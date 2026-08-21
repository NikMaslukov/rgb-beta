#!/usr/bin/env python3
"""Verify a published RGB plugin tree, its .btcpay ZIP, or a gate-native nupkg."""

from __future__ import annotations

import argparse
import hashlib
import json
import os
from pathlib import Path, PurePosixPath
import sys
import zipfile
import xml.etree.ElementTree as ET


SCRIPT_DIR = Path(__file__).resolve().parent
DEFAULT_CONTRACT = SCRIPT_DIR / "plugin-artifact-contract.json"


class VerificationError(Exception):
    pass


def fail(message: str) -> None:
    raise VerificationError(message)


def normalized_archive_name(name: str) -> str:
    if "\\" in name:
        fail(f"archive entry uses a backslash instead of '/': {name}")
    path = PurePosixPath(name)
    if path.is_absolute() or ".." in path.parts:
        fail(f"archive entry is not rooted safely at the plugin root: {name}")
    return path.as_posix().removeprefix("./")


class Artifact:
    def names(self) -> list[str]:
        raise NotImplementedError

    def read(self, relative: str) -> bytes:
        raise NotImplementedError

    def count(self, relative: str) -> int:
        return self.names().count(relative)


class DirectoryArtifact(Artifact):
    def __init__(self, root: Path):
        self.root = root
        if not root.is_dir():
            fail(f"publish directory does not exist: {root}")
        self._names = sorted(
            path.relative_to(root).as_posix()
            for path in root.rglob("*")
            if path.is_file()
        )

    def names(self) -> list[str]:
        return self._names

    def read(self, relative: str) -> bytes:
        return (self.root / relative).read_bytes()


class ZipArtifact(Artifact):
    def __init__(self, path: Path):
        if not path.is_file():
            fail(f"archive does not exist: {path}")
        try:
            self.archive = zipfile.ZipFile(path)
            self._entries = [
                (normalized_archive_name(info.filename), info)
                for info in self.archive.infolist()
                if not info.is_dir()
            ]
        except (OSError, zipfile.BadZipFile) as exc:
            fail(f"cannot read archive {path}: {exc}")

    def names(self) -> list[str]:
        return [name for name, _ in self._entries]

    def read(self, relative: str) -> bytes:
        matches = [info for name, info in self._entries if name == relative]
        if len(matches) != 1:
            fail(f"archive entry {relative} occurs {len(matches)} times; expected exactly one")
        return self.archive.read(matches[0])


def require_file(artifact: Artifact, relative: str) -> bytes:
    count = artifact.count(relative)
    if count != 1:
        if count == 0:
            fail(f"missing required artifact path: {relative}")
        fail(f"required artifact path occurs {count} times; expected exactly one: {relative}")
    data = artifact.read(relative)
    if not data:
        fail(f"required artifact path is empty: {relative}")
    return data


def load_json_bytes(data: bytes, relative: str) -> dict:
    try:
        value = json.loads(data.decode("utf-8"))
    except (UnicodeDecodeError, json.JSONDecodeError) as exc:
        fail(f"{relative} is not valid UTF-8 JSON: {exc}")
    if not isinstance(value, dict):
        fail(f"{relative} must contain a JSON object")
    return value


def package_entries(deps: dict, package_id: str) -> list[tuple[str, dict]]:
    matches: list[tuple[str, dict]] = []
    prefix = package_id.lower() + "/"
    targets = deps.get("targets", {})
    if not isinstance(targets, dict):
        return matches
    for target in targets.values():
        if not isinstance(target, dict):
            continue
        for key, value in target.items():
            if key.lower().startswith(prefix) and isinstance(value, dict):
                matches.append((key, value))
    return matches


def one_package_entry(deps: dict, package_id: str, deps_path: str) -> tuple[str, dict]:
    matches = package_entries(deps, package_id)
    identities = sorted({key for key, _ in matches})
    if not identities:
        fail(f"{deps_path} does not declare runtime dependency {package_id}")
    if len(identities) != 1:
        fail(f"{deps_path} declares multiple {package_id} versions: {', '.join(identities)}")
    return next((key, value) for key, value in matches if key == identities[0])


def declares_runtime_file(entry: dict, expected_name: str) -> bool:
    runtime = entry.get("runtime", {})
    return isinstance(runtime, dict) and any(
        PurePosixPath(path).name == expected_name for path in runtime
    )


def native_declarations(deps: dict, package_id: str) -> dict[str, str]:
    declared: dict[str, str] = {}
    for identity, entry in package_entries(deps, package_id):
        version = identity.split("/", 1)[1]
        runtime_targets = entry.get("runtimeTargets", {})
        if not isinstance(runtime_targets, dict):
            continue
        for relative, metadata in runtime_targets.items():
            if isinstance(metadata, dict) and metadata.get("assetType") == "native":
                declared[relative] = version
    return declared


def package_cache_root(value: str | None) -> Path | None:
    if value:
        return Path(value).expanduser().resolve()
    configured = os.environ.get("NUGET_PACKAGES")
    if configured:
        return Path(configured).expanduser().resolve()
    return None


def compare_with_cache(
    artifact: Artifact,
    relative: str,
    package_id: str,
    version: str,
    cache: Path,
) -> None:
    cached = cache / package_id.lower() / version / relative
    if not cached.is_file():
        fail(f"cannot locate {package_id} package-cache asset: {cached}")
    artifact_hash = hashlib.sha256(artifact.read(relative)).digest()
    cache_hash = hashlib.sha256(cached.read_bytes()).digest()
    if artifact_hash != cache_hash:
        fail(f"{relative} is not byte-identical to the {package_id} package-cache copy at {cached}")


def verify_dependency_manifests(artifact: Artifact, plugin: dict) -> dict[str, dict]:
    loaded: dict[str, dict] = {}
    for deps_path, packages in plugin["dependency_manifests"].items():
        deps = load_json_bytes(require_file(artifact, deps_path), deps_path)
        loaded[deps_path] = deps
        for package_id, expectation in packages.items():
            _, entry = one_package_entry(deps, package_id, deps_path)
            runtime_name = expectation.get("runtime")
            if runtime_name and not declares_runtime_file(entry, runtime_name):
                fail(f"{deps_path} declares {package_id}, but not expected runtime asset {runtime_name}")
    return loaded


def verify_native_provenance(
    artifact: Artifact,
    deps: dict,
    relative: str,
    package_id: str,
    cache: Path | None,
    strict: bool,
) -> None:
    declared = native_declarations(deps, package_id)
    if relative not in declared:
        fail(f"{relative} is not declared as a native asset of {package_id} in the plugin .deps.json")
    if strict and cache is None:
        fail("strict provenance requires --package-cache or the NUGET_PACKAGES environment variable")
    if cache is not None:
        compare_with_cache(artifact, relative, package_id, declared[relative], cache)


def verify_plugin(
    artifact: Artifact,
    contract: dict,
    provenance: str,
    cache: Path | None,
) -> list[str]:
    plugin = contract["plugin"]
    for relative in plugin["required_root_files"]:
        if "/" in relative:
            fail(f"contract error: required root file is not at archive root: {relative}")
        require_file(artifact, relative)

    helper = plugin["helper_basename"]
    expected_helper = {
        f"{helper}.dll",
        f"{helper}.deps.json",
        f"{helper}.runtimeconfig.json",
    }
    actual_helper = {name for name in artifact.names() if PurePosixPath(name).name.startswith(helper + ".")}
    if not expected_helper.issubset(actual_helper):
        fail(f"helper trio must share basename {helper} and be at artifact root")
    if artifact.count("SharpCompress.dll") != 1:
        fail("SharpCompress.dll must occur exactly once at artifact root")

    for forbidden in plugin.get("forbidden_root_files", []):
        if artifact.count(forbidden):
            fail(f"host assembly must not ship in the plugin artifact: {forbidden}")

    manifests = verify_dependency_manifests(artifact, plugin)
    plugin_deps_path = "BTCPayServer.Plugins.RgbUtexo.deps.json"
    plugin_deps = manifests[plugin_deps_path]
    helper_deps = manifests["RgbRestoreHelper.deps.json"]
    gate_package = contract["packages"]["gate"]
    core_package = contract["packages"]["core"]

    supported: list[str] = []
    for rid, pair in plugin["supported_rids"].items():
        gate = pair["gate"]
        core = pair["core"]
        require_file(artifact, gate)
        require_file(artifact, core)
        if core not in native_declarations(helper_deps, core_package):
            fail(f"RgbRestoreHelper.deps.json does not declare {core} as a native asset of {core_package}")
        supported.append(rid)

    # Check provenance for every recognizable gate/core native that ships, including tolerated
    # extras. These extras never enter `supported`: only the explicit complete-pair matrix above can
    # make a support claim.
    gate_names = {"librgbverifycffi.so", "librgbverifycffi.dylib", "rgbverifycffi.dll"}
    core_names = {"librgblibcffi.so", "librgblibcffi.dylib", "rgblibcffi.dll"}
    for relative in sorted(set(artifact.names())):
        basename = PurePosixPath(relative).name.lower()
        if basename in core_names:
            verify_native_provenance(artifact, plugin_deps, relative, core_package, cache, strict=False)
        elif basename in gate_names and provenance == "strict":
            verify_native_provenance(artifact, plugin_deps, relative, gate_package, cache, strict=True)

    messages = [f"supported plugin RIDs verified: {', '.join(supported)}"]
    if provenance == "pre-package":
        messages.append(
            "PRE-PACKAGE MODE: hand-staged rgbverifycffi is accepted; gate package provenance is not established"
        )
    return messages


def nuspec_name(artifact: Artifact, package_id: str) -> str:
    matches = [name for name in artifact.names() if PurePosixPath(name).name.lower() == package_id.lower() + ".nuspec"]
    if len(matches) != 1:
        fail(f"gate package must contain exactly one {package_id}.nuspec; found {len(matches)}")
    return matches[0]


def verify_gate_package(artifact: Artifact, contract: dict, provenance: str) -> list[str]:
    if provenance != "strict":
        fail("gate-package inspection requires --provenance strict")
    package = contract["gate_package"]
    package_id = package["id"]
    placeholder = package["placeholder"]
    if artifact.count(placeholder) != 1:
        fail(f"gate package must contain exactly one placeholder: {placeholder}")
    for relative in package["required_assets"].values():
        require_file(artifact, relative)
    nuspec_path = nuspec_name(artifact, package_id)
    try:
        nuspec = ET.fromstring(require_file(artifact, nuspec_path))
    except ET.ParseError as exc:
        fail(f"{nuspec_path} is not valid XML: {exc}")
    package_ids = [element.text for element in nuspec.iter() if element.tag.rsplit("}", 1)[-1] == "id"]
    if package_ids != [package_id]:
        fail(f"{nuspec_path} must declare package id {package_id}")
    if any(element.tag.rsplit("}", 1)[-1] == "dependency" for element in nuspec.iter()):
        fail(f"{package_id} must not declare NuGet dependencies")
    return [
        "gate package RID assets verified: " + ", ".join(package["required_assets"].keys())
    ]


def parse_args(argv: list[str]) -> argparse.Namespace:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("artifact", type=Path, help="publish directory, .btcpay/.zip, or .nupkg")
    parser.add_argument("--contract", type=Path, default=DEFAULT_CONTRACT)
    parser.add_argument("--provenance", choices=("pre-package", "strict"), default="pre-package")
    parser.add_argument(
        "--package-cache",
        help="NuGet global-packages directory; defaults to NUGET_PACKAGES when set",
    )
    parser.add_argument(
        "--gate-package",
        action="store_true",
        help="inspect an RgbVerifyCffi .nupkg instead of a plugin artifact",
    )
    return parser.parse_args(argv)


def main(argv: list[str] | None = None) -> int:
    args = parse_args(argv or sys.argv[1:])
    try:
        contract = json.loads(args.contract.read_text(encoding="utf-8"))
        artifact: Artifact = DirectoryArtifact(args.artifact) if args.artifact.is_dir() else ZipArtifact(args.artifact)
        if args.gate_package:
            messages = verify_gate_package(artifact, contract, args.provenance)
        else:
            messages = verify_plugin(
                artifact,
                contract,
                args.provenance,
                package_cache_root(args.package_cache),
            )
        for message in messages:
            print(message)
        print(f"artifact contract satisfied: {args.artifact}")
        return 0
    except (OSError, KeyError, json.JSONDecodeError, VerificationError) as exc:
        print(f"artifact verification failed: {exc}", file=sys.stderr)
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
