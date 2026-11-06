"""Run owned disposable probe processes; smoke output is never gate evidence."""
import json
import os
from pathlib import Path
import subprocess
import sys
import uuid
import hashlib
import platform
import threading
import time

from machine import activity, builders

sys.stdout.reconfigure(encoding="utf-8")
ROOT = Path(__file__).resolve().parent.parent
SCRATCH = Path("C:/ck/487726/ac2142")


def main() -> None:
    environment = os.environ.copy()
    temporary = SCRATCH / "t"
    temporary.mkdir(parents=True, exist_ok=True)
    environment.update(TEMP=str(temporary), TMP=str(temporary), PYTHONUTF8="1")
    mode = sys.argv[1] if len(sys.argv) > 1 else "smoke"
    measurement = mode == "measure"
    if measurement and "--quiet-confirmed" not in sys.argv:
        raise SystemExit("Requires cockpit-assistant send_prompt for a quiet machine, then explicit --quiet-confirmed.")
    scenarios = [(800, 600, 1, 1)]
    if mode in ("functional", "measure"):
        scenarios += [(1920, 1080, 1, 1), (960, 540, 2, 1), (800, 600, 1, 4)]
    runs = [(route, scenario) for scenario in scenarios for route in ("local", "shared", "poll")]
    if measurement:
        orders = [("local", "shared", "poll"), ("poll", "shared", "local"), ("local", "poll", "shared")]
        runs = [(route, scenario) for scenario in scenarios for order in orders for route in order]
        assert len(runs) == 36
    if mode == "silent":
        runs = [("silent", scenarios[0])]
    if mode == "shared":
        runs = [("shared", scenarios[0])]
    session = SCRATCH / ("comparison-" + uuid.uuid4().hex if measurement else mode)
    session.mkdir(parents=True, exist_ok=True)
    executable = ROOT / ".ac2142/bin/Release/net10.0/Probe.exe"
    assembly_hash = hashlib.file_digest((executable.parent / "Probe.dll").open("rb"), "sha256").hexdigest()
    page_hash = hashlib.file_digest((ROOT / ".ac2142/page.html").open("rb"), "sha256").hexdigest()
    git_sha = subprocess.check_output(["git", "rev-parse", "HEAD"], cwd=ROOT, text=True).strip()
    if measurement:
        assert not subprocess.check_output(["git", "status", "--porcelain"], cwd=ROOT, text=True).strip(), "Freeze a clean WIP first."
        before = builders()
        time.sleep(1)
        active = activity(before, builders())
        if active:
            raise SystemExit("Machine has active builders: " + json.dumps(active))
    manifest = {"mode": mode, "git": git_sha, "assemblySha256": assembly_hash, "pageSha256": page_hash, "machine": platform.node(), "platform": platform.platform(), "logicalProcessors": os.cpu_count(), "plan": runs, "results": [], "quietSignal": measurement}
    manifest_path = session / "manifest.json"
    manifest_path.write_text(json.dumps(manifest, indent=2), encoding="utf-8")
    for run_index, (route, (width, height, scale, tabs)) in enumerate(runs):
        name = "ac2142-" + uuid.uuid4().hex
        state = session / name
        state.mkdir(parents=True)
        environment["COCKPIT_STATE_ROOT"] = str(state)
        assert hashlib.file_digest((executable.parent / "Probe.dll").open("rb"), "sha256").hexdigest() == assembly_hash
        assert hashlib.file_digest((ROOT / ".ac2142/page.html").open("rb"), "sha256").hexdigest() == page_hash
        command = [str(executable), route, str(state), str(width), str(height), str(scale), str(tabs), name]
        if not measurement:
            command.append("--functional")
        (state / "identity.json").write_text(json.dumps({"mode": mode, "route": route, "scenario": [width, height, scale, tabs], "git": subprocess.check_output(["git", "rev-parse", "HEAD"], cwd=ROOT, text=True).strip(), "assemblySha256": hashlib.file_digest((executable.parent / "Probe.dll").open("rb"), "sha256").hexdigest(), "pageSha256": hashlib.file_digest((ROOT / ".ac2142/page.html").open("rb"), "sha256").hexdigest()}), encoding="utf-8")
        violations = []
        monitoring_errors = []
        stop = threading.Event()

        def monitor() -> None:
            try:
                previous = builders()
                while not stop.wait(0.5):
                    current = builders()
                    violations.extend(activity(previous, current))
                    previous = current
                violations.extend(activity(previous, builders()))
            except Exception as error:
                monitoring_errors.append(str(error))

        monitor_thread = threading.Thread(target=monitor)
        if measurement:
            monitor_thread.start()
        with (state / "stdout.log").open("w", encoding="utf-8") as output, (state / "stderr.log").open("w", encoding="utf-8") as errors:
            with subprocess.Popen(command, cwd=ROOT, env=environment, stdout=output, stderr=errors) as process:
                try:
                    exit_code = process.wait(timeout=90)
                except subprocess.TimeoutExpired:
                    if process.poll() is None:
                        subprocess.run(["taskkill", "/PID", str(process.pid), "/T", "/F"], check=True)
                    process.wait()
                    raise
                finally:
                    stop.set()
                    if measurement:
                        monitor_thread.join()
        record = {"kind": "definitive" if measurement else "functional-smoke-only", "index": run_index, "route": route, "scenario": [width, height, scale, tabs], "exit": exit_code, "state": str(state), "activeBuilders": violations, "monitoringErrors": monitoring_errors}
        manifest["results"].append(record)
        manifest_path.write_text(json.dumps(manifest, indent=2), encoding="utf-8")
        print(json.dumps(record), flush=True)
        print((state / "stderr.log").read_text(encoding="utf-8")[-4000:])
        print((state / "stdout.log").read_text(encoding="utf-8")[-4000:])
        if exit_code:
            sys.exit(exit_code)
        if violations or monitoring_errors:
            raise SystemExit("Comparison INVALID: other builds or unobservable machine state; retain only invalid evidence, no parity verdict.")
    print(f"Manifest: {manifest_path}", flush=True)


if __name__ == "__main__":
    main()
