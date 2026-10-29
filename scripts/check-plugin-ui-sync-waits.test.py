#!/usr/bin/env python3
"""Tests for scripts/check-plugin-ui-sync-waits.py, against a throwaway plugins-dev tree.

The counterproof is the first case: one plugin UI file that blocks turns the guard red.

    scripts/check-plugin-ui-sync-waits.test.py
"""
import importlib.util
import tempfile
from pathlib import Path

spec = importlib.util.spec_from_file_location("gate", Path(__file__).parent / "check-plugin-ui-sync-waits.py")
gate = importlib.util.module_from_spec(spec)
spec.loader.exec_module(gate)

failures = []


def check(name, relative_path, body, expect_red):
    with tempfile.TemporaryDirectory() as directory:
        root = Path(directory)
        source = root / "plugins-dev" / relative_path
        source.parent.mkdir(parents=True)
        source.write_text(body, encoding="utf-8")
        red = gate.main(root) != 0
    if red != expect_red:
        failures.append(f"{name}: expected red={expect_red}, got red={red}")
    else:
        print(f"ok: {name}")


check("a plugin UI file that blocks on its channel is red", "Cockpit.Plugin.X/Ui/XControl.cs",
      "var answer = host.Channel.InvokeAsync(Action, payload).GetAwaiter().GetResult();\n", True)
check("the UI folder spelled UI is guarded too", "Cockpit.Plugin.X/UI/XControl.cs",
      "channel.InvokeAsync(Action, default)\n    .GetAwaiter().GetResult();\n", True)
check("an awaited call is green", "Cockpit.Plugin.X/Ui/XControl.cs",
      "var answer = await host.Channel.InvokeAsync(Action, payload);\n", False)
check("a comment naming the pattern is green", "Cockpit.Plugin.X/Ui/XControl.cs",
      "// never .GetAwaiter().GetResult() here\n", False)
check("the backend part is not the UI part", "Cockpit.Plugin.X/XPlugin.cs",
      "Run().GetAwaiter().GetResult();\n", False)
check("a plugin's tests are not its UI part", "Cockpit.Plugin.X.Tests/Ui/XControlTests.cs",
      "Run().GetAwaiter().GetResult();\n", False)

if failures:
    print("\n".join(failures))
    raise SystemExit(1)
