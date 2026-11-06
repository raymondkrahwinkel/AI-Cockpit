"""Build the disposable probe with isolated temporary storage."""
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
environment.update(TEMP=str(temporary), TMP=str(temporary), PYTHONUTF8="1")
for command in (["py", "-X", "utf8", ".ac2142/prepare.py"], ["dotnet", "build", ".ac2142/Probe.csproj", "-c", "Release", "-warnaserror", "-p:UseSharedCompilation=false"]):
    result = subprocess.run(command, cwd=root, env=environment)
    if result.returncode:
        sys.exit(result.returncode)
if len(sys.argv) > 1:
    result = subprocess.run(["py", "-X", "utf8", ".ac2142/run.py", sys.argv[1]], cwd=root, env=environment)
    sys.exit(result.returncode)
