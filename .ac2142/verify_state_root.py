"""Run the original read-only path test without an inherited state-root override."""
import os
from pathlib import Path
import subprocess
import sys

sys.stdout.reconfigure(encoding="utf-8")
root = Path(__file__).resolve().parent.parent
scratch = Path("C:/ck/487726/ac2142")
environment = os.environ.copy()
environment.update(TEMP=str(scratch / "t"), TMP=str(scratch / "t"), TMPDIR=str(scratch / "t"), PYTHONUTF8="1", COCKPIT_STATE_ROOT=str(scratch / "gate-state"))
environment.pop("MSYS_NO_PATHCONV", None)
project = "tests/Cockpit.Core.Tests/Cockpit.Core.Tests.csproj"
test_filter = "FullyQualifiedName~StateRootOverrideTests"
commands = [
    ("state-root-original-build", ["dotnet", "build", project, "-c", "Release", "-warnaserror", "--no-restore"]),
    ("state-root-original-windows", ["dotnet", "test", project, "-c", "Release", "--no-build", "--filter", test_filter, "--logger", "trx", "--results-directory", str(scratch / "state-root-original-trx")]),
    ("test-count-final", ["py", "-X", "utf8", "scripts/check-test-count.py"]),
    ("comment-length-final", ["py", "-X", "utf8", "scripts/check-comment-length.py"]),
]
for label, command in commands:
    path = scratch / (label + ".log")
    command_environment = environment.copy()
    if label == "state-root-original-windows":
        command_environment.pop("COCKPIT_STATE_ROOT", None)
    with path.open("w", encoding="utf-8") as log:
        result = subprocess.run(command, cwd=root, env=command_environment, stdout=log, stderr=subprocess.STDOUT)
    print(f"{label}: exit={result.returncode}; log={path}", flush=True)
    if result.returncode:
        print(path.read_text(encoding="utf-8", errors="replace")[-4000:])
        sys.exit(result.returncode)
