#!/usr/bin/env python3
"""Load the plugin in the real ASF host with disabled bots and loopback-only IPC."""

import json
import os
from pathlib import Path
import secrets
import shutil
import signal
import socket
import subprocess
import tempfile
import time
import urllib.error
import urllib.request


def main():
    project = Path(__file__).resolve().parent.parent
    reference = Path(subprocess.check_output([str(project / "scripts/prepare-asf.sh")], text=True).strip())
    dotnet = os.environ.get("DOTNET") or shutil.which("dotnet") or str(Path.home() / ".dotnet/dotnet")
    executable = os.environ.get("ASF_SMOKE_EXECUTABLE")
    host_command = [executable] if executable else [dotnet, str(reference / "ArchiSteamFarm.dll")]
    with tempfile.TemporaryDirectory(prefix="smartidle-host-") as temporary:
        runtime = Path(temporary)
        config = runtime / "config"
        plugin = runtime / "plugins/SmartIdle"
        config.mkdir()
        plugin.mkdir(parents=True)
        for filename in ("SmartIdle.dll", "SmartIdle.Core.dll"):
            shutil.copyfile(project / "src/SmartIdle.Plugin/bin/Release/net10.0" / filename, plugin / filename)
        shutil.copyfile(project / "tests/SmartIdle.HostProbe/bin/Release/net10.0/SmartIdle.HostProbe.dll", plugin / "SmartIdle.HostProbe.dll")
        with socket.socket() as sock:
            sock.bind(("127.0.0.1", 0))
            port = sock.getsockname()[1]
        password = secrets.token_hex(24)
        (config / "ASF.json").write_text(json.dumps({"IPC": True, "IPCPassword": password, "Headless": True, "Statistics": False, "UpdatePeriod": 0, "AutoRestart": False}))
        (config / "IPC.config").write_text(json.dumps({"Kestrel": {"Endpoints": {"HTTP": {"Url": f"http://127.0.0.1:{port}"}}}}))
        (config / "Smoke.json").write_text(json.dumps({"Enabled": False, "SmartIdle": {"Enabled": True}}))
        (config / "Conflict.json").write_text(json.dumps({"Enabled": False, "GamesPlayedWhileIdle": [570], "CustomGamePlayedWhileIdle": "WYFANG.NET", "FarmingPreferences": 129, "SmartIdle": {"Enabled": True}}))
        log_path = runtime / "host.log"
        with log_path.open("w+") as log:
            process = subprocess.Popen([*host_command, "--path", str(runtime), "--no-restart"], stdout=log, stderr=subprocess.STDOUT, start_new_session=True)
            try:
                url = f"http://127.0.0.1:{port}/Api/Command"

                def command(value, authenticated=True):
                    headers = {"Content-Type": "application/json"}
                    if authenticated:
                        headers["Authentication"] = password
                    request = urllib.request.Request(url, json.dumps({"Command": value}).encode(), headers)
                    with urllib.request.urlopen(request, timeout=2) as response:
                        return json.load(response)

                deadline = time.monotonic() + 40
                while True:
                    if process.poll() is not None:
                        raise RuntimeError("ASF exited before IPC was ready")
                    try:
                        result = command("SIDLE Smoke HELP")
                        break
                    except (urllib.error.URLError, TimeoutError):
                        if time.monotonic() >= deadline:
                            raise RuntimeError("ASF IPC startup timeout") from None
                        time.sleep(0.25)
                assert result["Success"] and "SIDLE STATUS" in result["Result"], result
                log.flush()
                assert "SMARTIDLE_HOST_PROBE_PASS" in log_path.read_text(), "Core probe did not pass in the real host"
                print("PASS scheduling, JSON and file persistence in the real ASF runtime")
                print("PASS real ASF loads SmartIdle and routes SIDLE Smoke HELP")
                result = command("SIDLE Smoke STATUS")
                assert "等待 Steam 登录" in result["Result"], result
                print("PASS disabled bot stays offline and waits for Steam login")
                result = command("SIDLE Conflict STATUS")
                assert "等待 Steam 登录" in result["Result"], result
                preserved = json.loads((config / "Conflict.json").read_text())
                assert preserved["GamesPlayedWhileIdle"] == [570]
                assert preserved["CustomGamePlayedWhileIdle"] == "WYFANG.NET"
                assert preserved["FarmingPreferences"] == 129
                print("PASS plugin accepts and preserves native idle games, custom status and farming preferences")
                try:
                    command("SIDLE Smoke HELP", authenticated=False)
                    raise AssertionError("Unauthenticated IPC was accepted")
                except urllib.error.HTTPError as error:
                    assert error.code in (401, 403), error
                print("PASS unauthenticated IPC is rejected")
                assert not (config / "SmartIdle-state").exists(), "Offline host created an account state"
                print("Host smoke test passed; no Steam credentials or game sessions used.")
            except Exception:
                log.flush()
                print(log_path.read_text()[-14000:])
                raise
            finally:
                if process.poll() is None:
                    os.killpg(process.pid, signal.SIGTERM)
                    try:
                        process.wait(timeout=5)
                    except subprocess.TimeoutExpired:
                        os.killpg(process.pid, signal.SIGKILL)
                        process.wait()


if __name__ == "__main__":
    main()
