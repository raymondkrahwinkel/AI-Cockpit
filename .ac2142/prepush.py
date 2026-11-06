"""Run the repository's existing pre-push gates with this session's temporary/state roots."""
import os
from pathlib import Path
import subprocess
import sys

sys.stdout.reconfigure(encoding="utf-8")
root = Path(__file__).resolve().parent.parent
scratch = Path("C:/ck/487726/ac2142")
temporary = scratch / "t"
temporary.mkdir(parents=True, exist_ok=True)
environment = os.environ.copy()
environment.update(TEMP=str(temporary), TMP=str(temporary), TMPDIR=str(temporary), PYTHONUTF8="1", COCKPIT_STATE_ROOT=str(scratch / "gate-state"), MSYS_NO_PATHCONV="1")
commands = [
    ("windows-full-gate", ["C:/Program Files/Git/bin/bash.exe", "scripts/local-gate.sh", "--full"]),
    ("linux-browser-gate", ["C:/Program Files/Git/bin/bash.exe", "scripts/local-gate.sh", "--linux", "plugins-dev/Cockpit.Plugin.Browser.Tests/Cockpit.Plugin.Browser.Tests.csproj"]),
]
for label, command in commands:
    path = scratch / (label + ".log")
    with path.open("w", encoding="utf-8") as log:
        result = subprocess.run(command, cwd=root, env=environment, stdout=log, stderr=subprocess.STDOUT)
    print(f"{label}: exit={result.returncode}; log={path}", flush=True)
    if result.returncode:
        print(path.read_text(encoding="utf-8", errors="replace")[-6000:], flush=True)
        sys.exit(result.returncode)
