# AC-1268 — nightly tests

1. Rebase the existing implementation on `origin/main` and inspect its three-point diff.
2. Push a disposable branch containing a dispatch-only measurement workflow with the full existing test sequence and no publish or release job. Run it on GitHub-hosted Ubuntu; record elapsed wall time, outcome, and any flakes.
3. Only if the measurement is green and Raymond approves a blocking gate, remove the test job's `changes` dependency and require its success from `publish`. Keep coverage reporting without a threshold.
4. Run the repository build and mandated guards, push, notify `cockpit-assistant` with `git diff --stat origin/main...HEAD`, then wait for approval before updating the existing PR. Never merge it.
