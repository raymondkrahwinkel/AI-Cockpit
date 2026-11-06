"""Prove frame-ready is necessary, then restore the committed WIP and rebuild."""
import os
from pathlib import Path
import subprocess
import sys

sys.stdout.reconfigure(encoding="utf-8")
ROOT = Path(__file__).resolve().parent.parent
SCRATCH = Path("C:/ck/487726/ac2142")
environment = os.environ.copy()
environment.update(TEMP=str(SCRATCH / "t"), TMP=str(SCRATCH / "t"), PYTHONUTF8="1")


def run(arguments: list[str], label: str) -> tuple[int, str]:
    result = subprocess.run(arguments, cwd=ROOT, env=environment, stdout=subprocess.PIPE, stderr=subprocess.STDOUT, encoding="utf-8")
    (SCRATCH / (label + ".log")).write_text(result.stdout, encoding="utf-8")
    print(f"{label}: exit {result.returncode}", flush=True)
    return result.returncode, result.stdout


def main() -> None:
    assert not subprocess.check_output(["git", "status", "--porcelain"], cwd=ROOT, text=True).strip(), "Commit the WIP before mutation."
    checkpoint = subprocess.check_output(["git", "rev-parse", "HEAD"], cwd=ROOT, text=True).strip()
    source = ROOT / ".ac2142/SharedFrame.cs"
    original = source.read_text(encoding="utf-8")
    needle = "if (_suppressSignal == false && _legacy == false)"
    assert original.count(needle) == 1
    try:
        source.write_text(original.replace(needle, "if (_suppressSignal && _legacy == false)"), encoding="utf-8")
        code, output = run(["py", "-X", "utf8", ".ac2142/build.py"], "mutation-build")
        assert code == 0, output[-3000:]
        code, output = run(["py", "-X", "utf8", ".ac2142/run.py", "shared"], "mutation-red")
        assert code != 0 and "Missing frame-ready update: event=" in output, output[-3000:]
    finally:
        subprocess.run(["git", "reset", "--hard", checkpoint], cwd=ROOT, check=True)
    code, output = run(["py", "-X", "utf8", ".ac2142/build.py", "shared"], "mutation-restored-green")
    assert code == 0, output[-3000:]
    print(f"Frame-ready mutation detected; reset to {checkpoint}; fresh restored build and shared run passed.")


if __name__ == "__main__":
    main()
