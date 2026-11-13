"""Print the test job of .github/workflows/nightly.yml as a bash script of run_step calls.

scripts/local-gate.sh --nightly pipes this into the Linux container, so the local run executes the workflow's own
run blocks verbatim and cannot drift from it. Steps that only make sense on a GitHub runner are left out: those
guarded by !env.ACT (sysctl, artifact upload) and the summary step (python3, GITHUB_STEP_SUMMARY).

With --base <sha> only the test projects touched since that commit run (selection by scripts/classify-changes.sh,
which walks the real ProjectReference graph); builds, Journeys and the cross-plugin annotation guard always run.
Without it everything runs. The selection is printed as a final `NIGHTLY_SELECTION=...` line for the summary.
"""
import pathlib
import re
import subprocess
import sys

base = sys.argv[sys.argv.index("--base") + 1] if "--base" in sys.argv else None


def sh(*cmd):
    return subprocess.run(cmd, check=True, capture_output=True, text=True).stdout.replace("\r", "")


# -- selection -------------------------------------------------------------------------------------------------
suites = dict(re.findall(r'"(run_\w+):(tests/[^"]+)"', pathlib.Path("scripts/classify-changes.sh").read_text(encoding="utf-8")))
suites = {path: flag for flag, path in suites.items()}
flags, plugins, changed = {}, None, []
if base:
    flags = dict(l.split("=", 1) for l in sh("bash", "scripts/classify-changes.sh", base, "HEAD").splitlines() if "=" in l)
    plugins = set(re.findall(r'"([^"]+)"', flags.get("plugins", "[]")))
    names = {m.group(2) for f in sh("git", "diff", "--name-only", base, "HEAD").splitlines()
             if (m := re.match(r"(src|plugins-dev|tests)/([^/]+)/", f))}
    changed = sorted(names)
chosen, skipped = [], []


def wanted(test_csproj):
    """Keep this test project? Records the verdict for the summary."""
    name = pathlib.PurePosixPath(test_csproj).stem
    if base is None:
        keep = True
    elif test_csproj in suites:
        keep = flags.get(suites[test_csproj]) == "true"
    elif test_csproj.startswith("plugins-dev/"):
        keep = name.removesuffix(".Tests") in plugins
    else:
        keep = True  # Journeys and the cross-plugin guards: short, and coupled to everything
    (chosen if keep else skipped).append(name)
    return keep


# -- the workflow's own steps ----------------------------------------------------------------------------------
lines = pathlib.Path(".github/workflows/nightly.yml").read_text(encoding="utf-8").splitlines()
start = lines.index("  test:")
end = next((i for i in range(start + 1, len(lines)) if re.match(r"  \S", lines[i])), len(lines))

steps, current = [], None
for line in lines[start + 1:end]:
    if line.startswith("      - "):
        current = ["        " + line[8:]]
        steps.append(current)
    elif current is not None:
        current.append(line)

emitted = 0


def emit(name, run):
    global emitted
    print(f"run_step '{name.replace(chr(39), '')}' <<'__NIGHTLY_STEP__'\n{run}\n__NIGHTLY_STEP__")
    emitted += 1


for step in steps:
    fields, i = {}, 0
    while i < len(step):
        m = re.match(r"^        (\w+): ?(.*)$", step[i])
        i += 1
        if not m:
            continue
        key, value = m.groups()
        if value in ("|", ">"):
            block = []
            while i < len(step) and (not step[i].strip() or step[i].startswith("          ")):
                block.append(step[i][10:])
                i += 1
            value = "\n".join(block).strip("\n")
        fields[key] = value
    run, name = fields.get("run"), fields.get("name", "step")
    if run is None or "!env.ACT" in fields.get("if", "") or "python3" in run:
        continue
    if "${{" in run:
        sys.exit(f"nightly.yml step '{name}' uses an expression; teach nightly-test-steps.py about it")

    test = re.search(r"dotnet test (\S+\.csproj)", run)
    if test:
        if wanted(test.group(1)):
            emit(name, run)
    elif "dotnet test PluginTests.slnx" in run:
        slnx = pathlib.Path("PluginTests.slnx").read_text(encoding="utf-8")
        for project in re.findall(r'Path="([^"]+\.Tests\.csproj)"', slnx):
            if wanted(project):
                stem = pathlib.PurePosixPath(project).stem
                emit(f"{name}: {stem}", run.replace("PluginTests.slnx", project)
                     .replace("TestResults/Plugins", f"TestResults/Plugins/{stem}"))
    else:
        emit(name, run)

if not emitted:
    sys.exit("no steps found in the test job of nightly.yml")

short = lambda names: ", ".join(n.removeprefix("Cockpit.").removeprefix("Plugin.") for n in names) or "none"
if base:
    print(f"NIGHTLY_SELECTION='since {base[:7]}; changed: {short(changed)}; running: {short(chosen)}; skipped: {len(skipped)} unaffected'")
else:
    print("NIGHTLY_SELECTION='all test projects'")
