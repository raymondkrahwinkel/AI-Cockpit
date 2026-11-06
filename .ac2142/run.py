"""Run owned disposable probe processes; smoke output is never gate evidence."""
import json
import os
from pathlib import Path
import subprocess
import sys
import uuid
import hashlib

sys.stdout.reconfigure(encoding="utf-8")
ROOT = Path(__file__).resolve().parent.parent
SCRATCH = Path("C:/ck/487726/ac2142")


def main() -> None:
    environment = os.environ.copy()
    temporary = SCRATCH / "t"
    temporary.mkdir(parents=True, exist_ok=True)
    environment.update(TEMP=str(temporary), TMP=str(temporary), PYTHONUTF8="1")
    mode = sys.argv[1] if len(sys.argv) > 1 else "smoke"
    if mode == "measure":
        raise SystemExit("Definitive measurement requires the explicit quiet-machine signal; launcher not yet enabled.")
    scenarios = [(800, 600, 1, 1)]
    if mode == "functional":
        scenarios += [(1920, 1080, 1, 1), (960, 540, 2, 1), (800, 600, 1, 4)]
    runs = [(route, scenario) for scenario in scenarios for route in ("local", "shared", "poll")]
    if mode == "silent":
        runs = [("silent", scenarios[0])]
    if mode == "shared":
        runs = [("shared", scenarios[0])]
    for route, (width, height, scale, tabs) in runs:
        name = "ac2142-" + uuid.uuid4().hex
        state = SCRATCH / mode / name
        state.mkdir(parents=True)
        environment["COCKPIT_STATE_ROOT"] = str(state)
        executable = ROOT / ".ac2142/bin/Release/net10.0/Probe.exe"
        command = [str(executable), route, str(state), str(width), str(height), str(scale), str(tabs), name, "--functional"]
        (state / "identity.json").write_text(json.dumps({"mode": mode, "route": route, "scenario": [width, height, scale, tabs], "git": subprocess.check_output(["git", "rev-parse", "HEAD"], cwd=ROOT, text=True).strip(), "assemblySha256": hashlib.file_digest((executable.parent / "Probe.dll").open("rb"), "sha256").hexdigest(), "pageSha256": hashlib.file_digest((ROOT / ".ac2142/page.html").open("rb"), "sha256").hexdigest()}), encoding="utf-8")
        with (state / "stdout.log").open("w", encoding="utf-8") as output, (state / "stderr.log").open("w", encoding="utf-8") as errors:
            with subprocess.Popen(command, cwd=ROOT, env=environment, stdout=output, stderr=errors) as process:
                try:
                    exit_code = process.wait(timeout=90)
                except subprocess.TimeoutExpired:
                    if process.poll() is None:
                        subprocess.run(["taskkill", "/PID", str(process.pid), "/T", "/F"], check=True)
                    process.wait()
                    raise
        print(json.dumps({"kind": "functional-smoke-only", "route": route, "scenario": [width, height, scale, tabs], "exit": exit_code, "state": str(state)}), flush=True)
        print((state / "stderr.log").read_text(encoding="utf-8")[-4000:])
        print((state / "stdout.log").read_text(encoding="utf-8")[-4000:])
        if exit_code:
            sys.exit(exit_code)


if __name__ == "__main__":
    main()
