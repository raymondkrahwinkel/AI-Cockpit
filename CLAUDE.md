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

## Een scherm bekijken doe je headless — nooit via de muis en het toetsenbord van de operator

Wil je zien hoe een scherm er echt uitziet, render het dan:

```
dotnet run --project src/Cockpit.App -- --screenshot <pad>.png --scene <naam> [--size 1100x760]
```

Dit raakt niets van de operator aan: geen focus, geen cursor, geen venster, en ook zijn state-directory
niet — de render draait vóór de single-instance-check en bouwt de app-stack niet op. Je mag hem dus
gerust draaien terwijl de cockpit van de operator openstaat. Zonder `--scene` krijg je het hoofdvenster;
een onbekende scene-naam faalt met de lijst van geldige namen erbij. Het commando levert óf een bestand
op — met het volledige pad op stdout — óf een niet-nul exitcode met de reden. Stil slagen kan het niet.

Wat je nooit gebruikt om een UI te bedienen: `SetCursorPos`, `mouse_event`, `SendKeys`,
`SetForegroundWindow`, `SetWindowPos` en verwante injectie via user32 of `System.Windows.Forms`. Die
APIs kennen geen procesgrens — ze landen in het venster dat op dat moment focus heeft en nemen de
machine van de operator over, ook als je ze op je eigen PID richt.

Zie AC-1235.

## SDK↔TTY: launch-time is gedeeld, turn-time niet

Cockpit heeft twee routes naar dezelfde provider. Bij een SDK-sessie draait de cockpit de beurt zelf; bij
een TTY-sessie draait de aanbieder zijn eigen TUI in de pane en bezit de cockpit precies één moment — de
start — plus een bytestroom en, als de aanbieder een `IPluginTranscriptReader` levert, een transcript dat
hij mag teruglezen.

Beslisvraag bij elke feature die een sessieroute raakt:

- **Raakt het de start?** Een launch-argument, een env-var, een bestand op schijf, een MCP-config, de
  appended system prompt. Dan hoort het op **beide** routes, en één route overslaan is een gat — geen
  follow-up-ticket waard maar meteen meenemen.
- **Raakt het de beurt?** Een rij in het transcript, een turn-grens, ingrijpen halverwege. Dan is
  SDK-only het **eindpunt**, niet een tussenstand. Er hoeft geen pariteit-ticket te komen, en er hoort
  er ook geen te blijven staan.

Wat daarmee permanent SDK-only is: het host-eigen transcript (schrijven, terugschilderen, trimmen), de
leesniveaus, alles wat op turn-start of turn-end hangt, de lokale send-queue en elke mid-turn-interventie
(model- of permissieswitch, een afbeelding overhandigen, een permissieprompt beantwoorden). Wat er
daarentegen op beide routes hóórt te werken: de MCP-sets die een sessie krijgt, de statusline, het
teruglezen van een transcript, tekst en Enter naar binnen duwen, en de delegatie-nudge.

Een TTY-sessie heeft dus wél een transcript. "Geen transcript" is sinds AC-609 geen geldige reden meer
om iets voor een TTY-pane te weigeren; "geen beurt die de host bezit" is dat wel.

Zie AC-294.
