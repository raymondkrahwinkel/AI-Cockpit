# CLAUDE.md

## Eigenaarschapsgrens Cockpit ↔ Depot

Depot = opslag- en waarheidslaag. Wat er is opgeslagen, wie er toegang toe heeft, wat er server-side mee gebeurde: bestanden, artifacts, quota, memberships, tokens, API-gebruik per project. Metrics die Depot meet gaan over Depot-gebruik. Cockpit = werkomgeving. Wat er in een sessie gebeurt: agent-runs, tokenverbruik, kosten, autopilot-stappen, lokale sessie-state. Metrics die Cockpit meet gaan over sessies en agents. Overlapt een cijfer beide (bv. "activiteit per project"), dan meet elk systeem zijn eigen kant en linkt hooguit — geen synchronisatie, geen gedeelde metrics-store.

Zie AC-300.

## Mockups horen niet in deze repository

Mockups (HTML-proxy's + renders voor grooming/design-review) horen niet in deze repository — geen branch, geen PR, ook niet als "niet mergen"-bespreekbijlage. Ze gaan uitsluitend naar Depot (artifact-upload).

Zie AC-781.

## Laag-conventie: plugin-eigen functionaliteit hoort niet in Cockpit.Core/Cockpit.Infrastructure

Cockpit.Core en Cockpit.Infrastructure zijn wat werkt zonder enige plugin. Functionaliteit die aan één plugin hangt — inclusief de MCP-tools die die plugin registreert — hoort in de plugin zelf, niet in de kern. Beslisvraag bij twijfel: "zou dit blijven werken als deze plugin niet geïnstalleerd is?" Nee ⇒ hoort in de plugin.

Zie AC-885.
