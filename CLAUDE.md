# CLAUDE.md

## Eigenaarschapsgrens Cockpit ↔ Depot

Depot = opslag- en waarheidslaag. Wat er is opgeslagen, wie er toegang toe heeft, wat er server-side mee gebeurde: bestanden, artifacts, quota, memberships, tokens, API-gebruik per project. Metrics die Depot meet gaan over Depot-gebruik. Cockpit = werkomgeving. Wat er in een sessie gebeurt: agent-runs, tokenverbruik, kosten, autopilot-stappen, lokale sessie-state. Metrics die Cockpit meet gaan over sessies en agents. Overlapt een cijfer beide (bv. "activiteit per project"), dan meet elk systeem zijn eigen kant en linkt hooguit — geen synchronisatie, geen gedeelde metrics-store.

Zie AC-300.
