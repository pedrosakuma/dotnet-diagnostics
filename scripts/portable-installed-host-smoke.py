#!/usr/bin/env python3
"""Pack/install portable-worker-enabled tools and optionally run native imports."""

from __future__ import annotations

import argparse
import base64
import datetime as dt
import hashlib
import json
import os
from pathlib import Path
import queue
import shutil
import stat
import subprocess
import sys
import threading
import time
import uuid
import zipfile
import xml.etree.ElementTree as ET

ROOT = Path(__file__).resolve().parents[1]
DEFAULT_PRIVATE_CONFIG = Path.home() / ".nuget/NuGet/NuGet.Config"
# Tracked source of the fixture; the CLI test project only links it into its output.
IMPORT_FIXTURE = ROOT / "tests/DotnetDiagnostics.Core.Tests/Fixtures/PortableImport/known-counters-v1-v2.ddcapture"
REQUIRED_ENV = {
    "DOTNET_CLI_TELEMETRY_OPTOUT": "1",
    "DOTNET_SKIP_FIRST_TIME_EXPERIENCE": "1",
    "DOTNET_CLI_WORKLOAD_UPDATE_NOTIFY_DISABLE": "1",
    "DOTNET_NOLOGO": "1",
}
TOOLS = {
    "cli": {
        "project": ROOT / "src/DotnetDiagnostics.Cli/DotnetDiagnostics.Cli.csproj",
        "package": "dotnet-diagnostics-cli",
        "command": "dotnet-diagnostics-cli",
    },
    "mcp": {
        "project": ROOT / "src/DotnetDiagnostics.Mcp/DotnetDiagnostics.Mcp.csproj",
        "package": "dotnet-diagnostics-mcp",
        "command": "dotnet-diagnostics-mcp",
    },
}
CHUNK_BYTES = 24 * 1024


def sha256(path: Path) -> str:
    value = hashlib.sha256()
    with path.open("rb") as stream:
        for chunk in iter(lambda: stream.read(1024 * 1024), b""):
            value.update(chunk)
    return value.hexdigest()


def run(command: list[str], *, cwd: Path = ROOT, env: dict[str, str] | None = None,
        timeout: int = 120, stdout: Path | None = None) -> dict[str, object]:
    started = time.time()
    actual_env = os.environ.copy()
    actual_env.update(REQUIRED_ENV)
    if env:
        actual_env.update(env)
    stdout_handle = stdout.open("w", encoding="utf-8") if stdout else subprocess.PIPE
    try:
        completed = subprocess.run(
            command,
            cwd=cwd,
            env=actual_env,
            text=True,
            stdout=stdout_handle,
            stderr=subprocess.PIPE,
            timeout=timeout,
            check=False,
        )
    finally:
        if stdout is not None and hasattr(stdout_handle, "close"):
            stdout_handle.close()
    if stdout is not None and completed.stdout is not None:
        raise AssertionError("stdout cannot be captured and redirected together")
    record = {
        "command": redact_command(command),
        "exitCode": completed.returncode,
        "durationSeconds": round(time.time() - started, 3),
        "stderr": completed.stderr[-4000:],
    }
    if stdout is None:
        record["stdout"] = completed.stdout[-4000:]
    else:
        record["stdoutFile"] = str(stdout)
    if completed.returncode != 0:
        raise RuntimeError(json.dumps(record, indent=2))
    return record


def redact_command(command: list[str]) -> list[str]:
    redacted: list[str] = []
    skip = False
    for index, item in enumerate(command):
        if skip:
            redacted.append("<configfile>")
            skip = False
            continue
        redacted.append(item)
        if item == "--configfile" and index + 1 < len(command):
            skip = True
    return redacted


def ensure_fresh(path: Path) -> None:
    if path.exists():
        shutil.rmtree(path)
    path.mkdir(parents=True)


def require_no_public_source(config: Path) -> None:
    text = config.read_text(encoding="utf-8", errors="replace")
    if "api.nuget.org" in text or "nuget.org" in text.lower():
        raise ValueError(f"{config} contains a public NuGet source; refusing to continue")


def private_source_config(private_config: Path, local_packages: Path, destination: Path) -> list[str]:
    require_no_public_source(private_config)
    source_tree = ET.parse(private_config)
    source_root = source_tree.getroot()
    package_sources = source_root.find("packageSources")
    if package_sources is None:
        raise ValueError(f"{private_config} has no packageSources")
    selected: list[tuple[str, str]] = []
    for add in package_sources.findall("add"):
        key = add.attrib.get("key")
        value = add.attrib.get("value")
        if not key or not value:
            continue
        if "nuget.org" in key.lower() or "nuget.org" in value.lower() or "api.nuget.org" in value.lower():
            continue
        selected.append((key, value))
    if not selected:
        raise ValueError(f"{private_config} has no non-public package source")

    root = ET.Element("configuration")
    sources = ET.SubElement(root, "packageSources")
    ET.SubElement(sources, "clear")
    ET.SubElement(sources, "add", key="local-packages", value=str(local_packages))
    for key, value in selected:
        ET.SubElement(sources, "add", key=key, value=value)

    credentials = source_root.find("packageSourceCredentials")
    if credentials is not None:
        copied_credentials = ET.SubElement(root, "packageSourceCredentials")
        wanted = {key for key, _ in selected}
        for child in list(credentials):
            if child.tag in wanted:
                copied_credentials.append(child)
        if not list(copied_credentials):
            root.remove(copied_credentials)

    destination.parent.mkdir(parents=True, exist_ok=True)
    ET.ElementTree(root).write(destination, encoding="utf-8", xml_declaration=True)
    require_no_public_source(destination)
    return [key for key, _ in selected]


def provenance_revision(assets: Path) -> str:
    root = ET.parse(assets / "provenance.xml").getroot()
    revision = root.findtext("Revision")
    if not revision:
        raise ValueError("provenance.xml lacks Revision")
    return revision.strip()


def zip_inventory(path: Path) -> dict[str, object]:
    entries = []
    with zipfile.ZipFile(path) as archive:
        for info in archive.infolist():
            if "NativeAssets/portable-capture/linux-x64" in info.filename:
                entries.append({
                    "name": info.filename,
                    "mode": format((info.external_attr >> 16) & 0o7777, "04o"),
                    "size": info.file_size,
                })
    return {"path": str(path), "sha256": sha256(path), "portableEntries": entries}


def tree_inventory(path: Path) -> list[dict[str, object]]:
    results = []
    for item in sorted(path.rglob("*")):
        if item.is_file() and ("NativeAssets" in item.parts or item.parent.name == ".store"):
            info = item.stat()
            results.append({
                "path": str(item.relative_to(path)),
                "mode": format(stat.S_IMODE(info.st_mode), "04o"),
                "size": info.st_size,
            })
    return results


def find_native_assets(tool_path: Path) -> Path:
    matches = sorted(tool_path.rglob("NativeAssets/portable-capture/linux-x64/capture-worker"))
    if len(matches) != 1:
        raise ValueError(f"Expected exactly one installed capture-worker under {tool_path}, found {len(matches)}")
    directory = matches[0].parent
    for name in ("capture-worker", "libe_sqlite3.so", "provenance.xml", "sqlite-LICENSE.txt", "worker-LICENSE.txt"):
        if not (directory / name).is_file():
            raise ValueError(f"Installed assets missing {name} in {directory}")
    return directory


def pack_and_install(args: argparse.Namespace, manifest: dict[str, object]) -> dict[str, object]:
    work = args.work_dir.resolve()
    ensure_fresh(work)
    local_packages = work / "local-packages"
    local_packages.mkdir()
    logs = work / "logs"
    logs.mkdir()
    install_config = work / "nuget" / "NuGet.Config"
    private_keys = private_source_config(args.private_nuget_config.resolve(), local_packages, install_config)

    commands = []
    commands.append(run(["dotnet", "restore", str(TOOLS["cli"]["project"]), "--configfile", str(args.private_nuget_config.resolve())], timeout=180))
    commands.append(run(["dotnet", "restore", str(TOOLS["mcp"]["project"]), "--configfile", str(args.private_nuget_config.resolve())], timeout=180))
    for name, tool in TOOLS.items():
        commands.append(run([
            "dotnet", "pack", str(tool["project"]), "-c", "Release", "-o", str(local_packages),
            f"-p:Version={args.version}",
            f"-p:PortableCaptureWorkerAssetsDir={args.assets_dir.resolve()}",
            "-p:RequirePortableCaptureWorkerAssets=true",
            "--no-restore",
        ], timeout=180, stdout=logs / f"pack-{name}.log"))

    installs: dict[str, dict[str, object]] = {}
    for name, tool in TOOLS.items():
        tool_path = work / f"installed-{name}"
        tool_path.mkdir()
        commands.append(run([
            "dotnet", "tool", "install", str(tool["package"]),
            "--version", args.version,
            "--tool-path", str(tool_path),
            "--add-source", str(local_packages),
            "--configfile", str(install_config),
            "--no-cache",
        ], timeout=180, stdout=logs / f"install-{name}.log"))
        native = find_native_assets(tool_path)
        installs[name] = {
            "toolPath": str(tool_path),
            "command": str(tool_path / str(tool["command"])),
            "nativeAssets": str(native),
            "inventory": tree_inventory(tool_path),
        }

    install_config.unlink(missing_ok=True)
    packages = sorted(local_packages.glob("*.nupkg"))
    manifest.update({
        "version": args.version,
        "privateSourceKeys": private_keys,
        "commands": commands,
        "packages": [zip_inventory(path) for path in packages],
        "installs": installs,
    })
    return installs


def run_preflight(args: argparse.Namespace, installs: dict[str, dict[str, object]], manifest: dict[str, object]) -> None:
    output = args.work_dir / "portable-worker-preflight-installed.json"
    command = [
        "python3", str(ROOT / "scripts/portable-worker-preflight.py"),
        "--assets-dir", str(args.assets_dir.resolve()),
        "--trusted-root", str(args.trusted_root.resolve()),
        "--repo-root", str(ROOT),
        "--expected-revision", provenance_revision(args.assets_dir.resolve()),
        "--runtime-uid", str(os.geteuid()),
        "--output", str(output),
    ]
    for item in installs.values():
        command.extend(["--installed-dir", str(item["nativeAssets"])])
    manifest["preflight"] = run(command, timeout=60)
    manifest["preflight"]["report"] = json.loads(output.read_text(encoding="utf-8"))


def parse_json_output(path: Path) -> dict[str, object]:
    return json.loads(path.read_text(encoding="utf-8"))


def run_cli_import(args: argparse.Namespace, installs: dict[str, dict[str, object]], manifest: dict[str, object]) -> dict[str, object]:
    root = args.work_dir / "execute-import" / "cli"
    root.mkdir(parents=True, exist_ok=True)
    command = str(installs["cli"]["command"])
    native = Path(str(installs["cli"]["nativeAssets"]))
    env = {
        "DOTNET_DIAGNOSTICS_IMPORT_WORKER": str(native / "capture-worker"),
        "DOTNET_DIAGNOSTICS_SQLITE_LIBRARY": str(native / "libe_sqlite3.so"),
    }
    fixture = IMPORT_FIXTURE
    first_out = root / "import-a.json"
    export_out = root / "export-a.json"
    second_out = root / "import-b.json"
    query_out = root / "query-b.json"
    source = root / "source"
    dest = root / "dest"
    bundle = root / "roundtrip.ddcapture"
    records = []
    records.append(run([command, "captures", "import", "--capture-root", str(source), "--file", str(fixture), "--acknowledge-risk", "high", "--json"], env=env, timeout=120, stdout=first_out))
    first = parse_json_output(first_out)
    entries = first["data"]["import"]["entries"]
    export_command = [command, "captures", "export", "--capture-root", str(source)]
    for entry in entries:
        export_command.extend(["--entry", entry["mapping"]["localCaptureId"] + "=installed-cli"])
    export_command.extend(["--file", str(bundle), "--acknowledge-risk", "high", "--json"])
    records.append(run(export_command, env=env, timeout=120, stdout=export_out))
    records.append(run([command, "captures", "import", "--capture-root", str(dest), "--file", str(bundle), "--acknowledge-risk", "high", "--json"], env=env, timeout=120, stdout=second_out))
    second = parse_json_output(second_out)
    mapping = second["data"]["import"]["entries"][0]["mapping"]
    records.append(run([command, "query", "--capture-root", str(dest), "--capture-id", mapping["localCaptureId"], "--artifact-id", mapping["artifacts"][0]["localArtifactId"], "--view", "records", "--json"], env=env, timeout=60, stdout=query_out))
    result = {
        "commands": records,
        "bundle": {"path": str(bundle), "sha256": sha256(bundle), "size": bundle.stat().st_size},
        "queryRecordName": parse_json_output(query_out)["data"]["records"][0]["record"]["name"],
    }
    manifest.setdefault("imports", {})["cli"] = result
    return result


class McpClient:
    def __init__(self, command: str, env: dict[str, str], cwd: Path, transcript: Path):
        actual_env = os.environ.copy()
        actual_env.update(REQUIRED_ENV)
        actual_env.update(env)
        self.proc = subprocess.Popen(
            [command, "--stdio"],
            cwd=cwd,
            env=actual_env,
            text=True,
            stdin=subprocess.PIPE,
            stdout=subprocess.PIPE,
            stderr=subprocess.PIPE,
        )
        self.next_id = 1
        self.transcript = transcript
        self.log = []
        self.responses: queue.Queue[dict[str, object] | None] = queue.Queue()
        self.reader = threading.Thread(target=self._read_stdout, daemon=True)
        self.reader.start()

    def _read_stdout(self) -> None:
        assert self.proc.stdout is not None
        try:
            for line in self.proc.stdout:
                if line.strip():
                    self.responses.put(json.loads(line))
        except Exception as error:
            self.responses.put({"readerError": str(error)})
        finally:
            self.responses.put(None)

    def close(self) -> None:
        if self.proc.stdin:
            self.proc.stdin.close()
        try:
            self.proc.wait(timeout=30)
        except subprocess.TimeoutExpired:
            self.proc.kill()
            self.proc.wait(timeout=10)
        self.reader.join(timeout=5)
        stderr = self.proc.stderr.read() if self.proc.stderr else ""
        self.transcript.write_text(json.dumps({"messages": self.log, "stderrTail": stderr[-4000:], "exitCode": self.proc.returncode}, indent=2) + "\n", encoding="utf-8")

    def request(self, method: str, params: dict[str, object] | None = None, timeout: int = 60) -> dict[str, object]:
        identifier = self.next_id
        self.next_id += 1
        payload: dict[str, object] = {"jsonrpc": "2.0", "id": identifier, "method": method}
        if params is not None:
            payload["params"] = params
        line = json.dumps(payload, separators=(",", ":"))
        assert self.proc.stdin is not None and self.proc.stdout is not None
        self.proc.stdin.write(line + "\n")
        self.proc.stdin.flush()
        deadline = time.monotonic() + timeout
        while True:
            remaining = deadline - time.monotonic()
            if remaining <= 0:
                raise TimeoutError(f"No MCP response for {method}")
            try:
                response = self.responses.get(timeout=remaining)
            except queue.Empty as error:
                raise TimeoutError(f"No MCP response for {method}") from error
            if response is None:
                break
            self.log.append({"sent": payload, "received": response})
            if "readerError" in response:
                raise RuntimeError(str(response["readerError"]))
            if response.get("id") == identifier:
                if "error" in response:
                    raise RuntimeError(json.dumps(response, indent=2))
                return response["result"]
        raise TimeoutError(f"No MCP response for {method}")

    def notify(self, method: str, params: dict[str, object] | None = None) -> None:
        payload: dict[str, object] = {"jsonrpc": "2.0", "method": method}
        if params is not None:
            payload["params"] = params
        assert self.proc.stdin is not None
        self.proc.stdin.write(json.dumps(payload, separators=(",", ":")) + "\n")
        self.proc.stdin.flush()

    def tool(self, name: str, arguments: dict[str, object], timeout: int = 120) -> dict[str, object]:
        result = self.request("tools/call", {"name": name, "arguments": arguments}, timeout=timeout)
        if result.get("isError"):
            raise RuntimeError(json.dumps(result, indent=2))
        structured = result.get("structuredContent")
        if isinstance(structured, dict):
            return structured
        for item in result.get("content", []):
            if item.get("type") == "text":
                try:
                    return json.loads(item["text"])
                except json.JSONDecodeError:
                    pass
        return result


def portable_result_data(result: dict[str, object]) -> dict[str, object]:
    data = result.get("data")
    if isinstance(data, dict):
        return data
    return result


def mcp_transfer_import(client: McpClient, bundle: Path) -> dict[str, object]:
    operation_id = uuid.uuid4().hex
    requested = dt.datetime.now(dt.timezone.utc).isoformat().replace("+00:00", "Z")
    archive_hash = sha256(bundle)
    start = portable_result_data(client.tool("get_bytes", {
        "kind": "captures",
        "captureAction": "import-start",
        "captureTransfer": {
            "operationId": operation_id,
            "requestedUtc": requested,
            "archiveBytes": bundle.stat().st_size,
            "archiveSha256": archive_hash,
        },
    }))
    transfer_id = start["transferId"]
    with bundle.open("rb") as stream:
        offset = 0
        while True:
            chunk = stream.read(CHUNK_BYTES)
            if not chunk:
                break
            client.tool("get_bytes", {
                "kind": "captures",
                "captureAction": "upload-chunk",
                "captureTransfer": {
                    "transferId": transfer_id,
                    "offset": offset,
                    "base64": base64.b64encode(chunk).decode("ascii"),
                    "sha256": hashlib.sha256(chunk).hexdigest(),
                },
            })
            offset += len(chunk)
    client.tool("get_bytes", {"kind": "captures", "captureAction": "import-commit", "captureTransfer": {"transferId": transfer_id}}, timeout=120)
    status = wait_mcp_transfer(client, operation_id, requested)
    return {"operationId": operation_id, "requestedUtc": requested, "archiveSha256": archive_hash, "status": status}


def wait_mcp_transfer(client: McpClient, operation_id: str, requested: str) -> dict[str, object]:
    for _ in range(60):
        status = portable_result_data(client.tool("get_bytes", {
            "kind": "captures",
            "captureAction": "transfer-status",
            "captureTransfer": {"operationId": operation_id, "requestedUtc": requested},
        }, timeout=30))
        if status.get("state") == "Completed":
            return status
        if status.get("state") in {"Failed", "Cancelled"}:
            raise RuntimeError(json.dumps(status, indent=2))
        time.sleep(1)
    raise TimeoutError("MCP transfer did not complete")


def mcp_import_entries(client: McpClient, operation_id: str, requested: str) -> list[dict[str, object]]:
    entries = []
    after = -1
    while True:
        data = portable_result_data(client.tool("get_bytes", {
            "kind": "captures",
            "captureAction": "import-result",
            "captureTransfer": {"operationId": operation_id, "requestedUtc": requested, "afterEntry": after, "pageSize": 1},
        }))
        entries.extend(data.get("entries", []))
        next_after = data.get("nextAfterEntry")
        if next_after is None:
            return entries
        after = int(next_after)


def run_mcp_import(args: argparse.Namespace, installs: dict[str, dict[str, object]], cli_result: dict[str, object], manifest: dict[str, object]) -> None:
    root = args.work_dir / "execute-import" / "mcp"
    root.mkdir(parents=True, exist_ok=True)
    native = Path(str(installs["mcp"]["nativeAssets"]))
    env = {
        "DOTNET_DIAGNOSTICS_IMPORT_WORKER": str(native / "capture-worker"),
        "DOTNET_DIAGNOSTICS_SQLITE_LIBRARY": str(native / "libe_sqlite3.so"),
        "MCP_ARTIFACT_ROOT": str(root / "artifacts"),
    }
    client = McpClient(str(installs["mcp"]["command"]), env, ROOT, root / "mcp-transcript.json")
    try:
        client.request("initialize", {"protocolVersion": "2025-11-25", "capabilities": {}, "clientInfo": {"name": "portable-installed-host-smoke", "version": "1"}}, timeout=30)
        client.notify("notifications/initialized")
        imported = mcp_transfer_import(client, Path(cli_result["bundle"]["path"]))
        entries = mcp_import_entries(client, imported["operationId"], imported["requestedUtc"])
        mapping = entries[0]["mapping"]
        query = portable_result_data(client.tool("query_snapshot", {
            "captureId": mapping["localCaptureId"],
            "artifactId": mapping["artifacts"][0]["localArtifactId"],
            "view": "records",
        }, timeout=60))
        manifest.setdefault("imports", {})["mcp"] = {
            "import": imported,
            "entryCount": len(entries),
            "queryRecordName": query["records"][0]["record"]["name"],
        }
    finally:
        client.close()


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--assets-dir", required=True, type=Path)
    parser.add_argument("--trusted-root", type=Path, default=ROOT)
    parser.add_argument("--private-nuget-config", type=Path, default=DEFAULT_PRIVATE_CONFIG)
    parser.add_argument("--work-dir", type=Path, default=ROOT / "artifacts/installed-host-smoke")
    parser.add_argument("--version", default="0.0.0-smoke." + dt.datetime.now(dt.timezone.utc).strftime("%Y%m%d%H%M%S"))
    parser.add_argument("--execute-import", action="store_true", help="Run real native imports through the installed CLI and MCP hosts.")
    args = parser.parse_args()

    manifest: dict[str, object] = {
        "schema": "dotnet-diagnostics/portable-installed-host-smoke/v1",
        "startedUtc": dt.datetime.now(dt.timezone.utc).isoformat().replace("+00:00", "Z"),
        "repoRoot": str(ROOT),
        "assetsDir": str(args.assets_dir.resolve()),
        "trustedRoot": str(args.trusted_root.resolve()),
        "importRequested": bool(args.execute_import),
        "importExecuted": False,
    }
    try:
        if args.execute_import and not (IMPORT_FIXTURE.is_file() and IMPORT_FIXTURE.stat().st_size > 0):
            raise RuntimeError(f"import fixture missing or empty: {IMPORT_FIXTURE}")
        installs = pack_and_install(args, manifest)
        run_preflight(args, installs, manifest)
        if args.execute_import:
            manifest["importExecuted"] = True
            cli_result = run_cli_import(args, installs, manifest)
            run_mcp_import(args, installs, cli_result, manifest)
        manifest["completedUtc"] = dt.datetime.now(dt.timezone.utc).isoformat().replace("+00:00", "Z")
        manifest["success"] = True
        args.work_dir.mkdir(parents=True, exist_ok=True)
        output = args.work_dir / "manifest.json"
        output.write_text(json.dumps(manifest, indent=2, sort_keys=True) + "\n", encoding="utf-8")
        print(json.dumps({"manifest": str(output), "success": True, "importExecuted": manifest["importExecuted"]}, indent=2))
        return 0
    except Exception as error:
        manifest["completedUtc"] = dt.datetime.now(dt.timezone.utc).isoformat().replace("+00:00", "Z")
        manifest["success"] = False
        manifest["error"] = str(error)
        args.work_dir.mkdir(parents=True, exist_ok=True)
        output = args.work_dir / "manifest.json"
        output.write_text(json.dumps(manifest, indent=2, sort_keys=True) + "\n", encoding="utf-8")
        print(f"portable installed-host smoke failed; manifest={output}: {error}", file=sys.stderr)
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
