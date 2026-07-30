# AC-524 — voortgang

Worktree: `D:\Projects\dotnet\Cockpit\.claude\worktrees\agent-ab85a7608bbe1dd14`
Branch: `worktree-agent-ab85a7608bbe1dd14` (gemerged met `origin/main` @ `939234c4`, AC-517/PR #353)

## Status — af, inclusief beide blockers en alle zes review-bevindingen

- [x] Deel A — `AcquireForSessionAsync` met eigen, ruimere marge (55 min)
- [x] Deel B — loopback doorstuur-endpoint per OAuth-server, SSE-streaming, verbinding-in-stand-houdend antwoord
- [x] Deel C — verval, verversingspoging en uitkomst in het log; nooit een tokenwaarde
- [x] Single-flight per server op élk verversingspad (marge-gedreven én 401-gedreven)
- [x] **BLOCKER 1** — een 401/403 van upstream ongeldigt het lokale beeld, lokt één verversing uit en herhaalt de
      aanroep precies één keer
- [x] **BLOCKER 2** — de operator krijgt een notificatie (`IToastNotifier`), niet alleen een logregel
- [x] Bevinding 3 — foutafhandeling op de streaming-relay
- [x] Bevinding 4 — eigen oorzaak voor "verversing gelukt maar token te kort"; overal op `Reason` geasserteerd
- [x] Bevinding 5 — terugval bakt geen bijna-dood token meer in
- [x] Bevinding 6 — twee levenscyclus-fouten in `McpOAuthProxyHost`
- [x] Bevinding 7 — TTY-budgettimeout vult de oorzaak
- [x] Bevinding 8 — test op een stille SSE-stream
- [x] Bevinding 9 — mount-gate per server
- [x] Punt B — de `continue`-keuze staat nu uitgeschreven in beide adapters
- [x] Mutatiebewijs per guard, nul build-warnings

## De architectuur na de review

**Twee vragen, niet één.** De adapters mounten nu het endpoint **eerst** en kiezen dáárna welke vraag ze stellen:

| Situatie | Vraag | Waarom |
|---|---|---|
| Endpoint staat ervoor | `AcquireAsync(interactive: false)` — 2 min | De sessie houdt het token niet vast; het endpoint haalt per aanroep een vers token. Een server met tokens van 10 minuten werkt hier prima. |
| Geen endpoint (terugval) | `AcquireForSessionAsync` — 55 min | Het token gaat de config in en blijft daar; het moet de hele sessie overleven. |

Dat lost bevinding 5 op zonder een vlag: het antwoord hangt af van wat er met het token gaat gebeuren, en dat weet
alleen de adapter.

**De floor-fallback is weg.** Eerder accepteerde `_ConnectAndReadAsync` een net-ververst token dat de sessiemarge niet
haalde maar de 2-minutenmarge wél. Dat was precies het gedrag dat bevinding 5 aanwijst: het bakte een token van tien
minuten in. Nu komt dat geval terug als `TokenTooShortLived` en laat de sessiestart de server wég, met de oorzaak.
Onder een endpoint doet de korte levensduur er niet toe, en dáár wordt de vraag ook niet gesteld.

**Vier oorzaken in plaats van drie**, elk met eigen advies (`McpOAuthSignInGuidance`): nooit aangemeld · aanmelding
verlopen/ingetrokken · server onbereikbaar · token te kortlevend. Plus `CallCouldNotBeRepeated` voor de ene aanroep
die na een verversing niet herhaald kon worden.

**De 401-lus.** `RenewRejectedAsync(server, rejectedAccessToken)`:
1. Is het opgeslagen token niet meer het geweigerde → geef het huidige terug, géén verversing. Dit is de gewone vorm
   van honderd gelijktijdige 401's: één ververst, de rest vindt een token dat ze nog niet geprobeerd hebben.
2. Anders: ververs, via dezelfde single-flight-gate. Honderd 401's ⇒ één token-endpoint-round-trip.
3. De forwarder herhaalt de aanroep dan één keer. Weigert de server ook een vers token, dan stopt het daar en volgt
   de error-envelope — nooit een lus.

**Herhaalbare body.** `EnableBuffering(bufferThreshold: 128 KB)`, zonder harde bufferlimiet: een limiet zou de
gewóne doorgifte al afbreken, en een grote aanroep weigeren is een slechter antwoord dan hem doorgeven en alleen de
herhaling opgeven. De herhaling zelf is begrensd op 8 MB (`RepeatableBodyLimit`); daarboven krijgt de agent
`CallCouldNotBeRepeated` — "de credential is ververst, stuur dit verzoek opnieuw", wat een agent kan opvolgen.

## Aannames en besluiten

1. **`CockpitMcpEndpointHost.cs` blijft onaangeraakt.** Wel hergebruikt (niet gewijzigd): `McpAuthMiddleware.Require`.
2. **Alleen `Auth == OAuth` verandert van gedrag.** `MountAsync` weigert elke andere auth-vorm/transport meteen.
3. **Sessiemarge 55 minuten**, argumentatie in de code: afgezet tegen het access-token van één uur; vaste marge omdat
   de store geen uitgiftetijdstip bewaart en omdat een vaste marge de churn al aan beide kanten begrenst.
4. **Interactief aanmelden staat buiten de single-flight** en buiten de notificatie-ladder — de operator drukte zelf
   op de knop en de dialoog wacht erop.
5. **Notificatie via `IToastNotifier.NotifyAsync`** (door de coordinator expliciet vrijgegeven), fire-and-forget met
   de fout binnen de task gevangen: een toast die niet getoond kan worden mag het credentialpad niet meenemen.
   Gekoppeld aan dezelfde `_reported`-latch als de logregel, dus één melding per toestandsovergang.
6. **Upstream 401/403 wordt nooit doorgegeven** — na de herhaling volgt de error-envelope.

## Open punten / waar ik twijfel

- **`IHttpResponseBodyFeature.DisableBuffering()`** blijft niet met een mutatie te bewijzen (Kestrel buffert
  standaard niet). Het staat er als de gedocumenteerde SSE-bescherming. Wat wél bewezen is: chunk-voor-chunk
  doorgeven in plaats van eerst volledig inlezen (mutatie 8).
- **Bevinding 6 (levenscyclus) heeft geen test.** `_apps.Add` ná `StartAsync` en de omgekeerde dispose-volgorde zijn
  correctheidsfixes op inspectie; een test zou een geannuleerde Kestrel-start of een dispose-race moeten ensceneren
  en dat is zelf timing-gevoelig — precies de klasse test die deze ronde twee keer als flake terugkwam.
- **Bevinding 9 (gate per server) heeft geen test.** Een trage mount ensceneren vraagt een haak in Kestrel's start.
  De wijziging is klein en per inspectie te lezen; ik heb geen bewijs geleverd en claim het dus ook niet.
- **De echte SDK-verversing wordt in de nieuwe tests gefaked** (`RenewingMcpOAuthAuthorizer`). Wat getest wordt is de
  rekenkunde en de gating van de coordinator, volledig aan deze kant van de naad; dat de SDK er echt een
  refresh-grant doorheen draait is elders end-to-end gedekt (`McpOAuthOfflineAccessFlowTests`).
- **Niet live geverifieerd tegen Depot.** End-to-end getest met echte Kestrel aan beide kanten en een echte
  HttpClient ertussen — inclusief de 401-herhaling, SSE, en een stream die eerst drie seconden zwijgt — maar niet
  tegen een draaiende Depot met een echt verlopend of ingetrokken token. Dat is de enige claim die ik niet met
  bewijs kan onderbouwen.
- **De TTY-route deelt één budget van 5 seconden** voor mount én verversing samen. Het budget is per launch, niet per
  server; de mount-gate is nu wel per server, dus één trage server blokkeert de andere niet meer.

## Mutatie-uitslagen

Elke mutatie is met `git diff --stat` gecontroleerd op landen (CRLF-val uit BuildTraps) en daarna teruggedraaid.

### Nieuw in deze ronde

| # | Mutatie | Test die rood werd |
|---|---------|--------------------|
| 17 | een refusal wordt beantwoord i.p.v. ververst+herhaald | `WhenTheServerRefusesTheToken_…`, `…ItStopsAtOneRetry…`, `WhenTheRenewalAfterARefusalFails_…` |
| 18 | de herhaling spoelt de body niet terug | `WhenTheServerRefusesTheToken_…`, `…ItStopsAtOneRetry…` |
| 19 | een tweede weigering wordt tóch als 401 doorgegeven | `WhenTheServerRefusesEvenAFreshToken_ItStopsAtOneRetryAndAnswersTheCall` |
| 20 | er wordt herhaald hoewel de verversing mislukte | `WhenTheRenewalAfterARefusalFails_TheCallIsAnsweredWithThatReason_NotTheRefusal` |
| 21 | een gemelde weigering ververst ook als een ander het token al verving | `RenewRejected_ForATokenSomebodyElseAlreadyReplaced_HandsBackTheCurrentOneWithoutRenewing` |
| 22 | een gemelde weigering wordt alleen gehonoreerd als de klok het eens is | `RenewRejected_WhenTheServerRefusedATokenTheClockSaysIsFine_RenewsItAnyway`, `…WhenEveryCallIsRefusedAtOnce_StillRenewsOnlyOnce` |
| 23 | er bereikt nooit iets het bureaublad | 3 van de 4 `McpOAuthOperatorNoticeTests` |
| 24 | een mislukte verversing telt als een gelukte met een kort token | `AcquireForSession_WhenTheRenewalItselfFailed_…` + 3 `McpSignInStageTests` |
| 25 | de terugval stelt de per-request-vraag en bakt een stervend token in | `SdkSession_WhenTheProxyIsGoneAndTheTokensAreTooShortLived_…`, `SdkSession_AsksThroughTheSessionEntryPoint_…` |
| 26 | een geproxyde server moet tóch de sessie overleven | `SdkSession_WhenTheProxyIsMounted_AsksOnlyWhetherASignInExists` + bovenstaande |
| 27 | een stream die afbreekt wordt niet gelogd | `WhenTheServerBreaksOffMidStream_ThatIsHandledAndSaidRatherThanEscaping` |

### Uit de eerste ronde, hergecontroleerd ná merge + herstructurering

1 (sessiemarge) · 3 (single-flight) · 8 (SSE streamen i.p.v. bufferen) · 12 (geleende body-stream) — alle vier
opnieuw rood. De overige uit ronde 1 (4–7, 9–11, 13–16) raken code die deze ronde niet is verplaatst.
Mutatie 2 uit ronde 1 (de floor-fallback) is vervallen: die code bestaat niet meer, en het gedrag dat ervoor in de
plaats kwam is gedekt door 24/25/26.

### Twee vals-groenen die deze ronde aan het licht kwamen

- **Mutatie 25 overleefde eerst.** `SdkSession_WhenTheProxyIsGoneAndTheTokensAreTooShortLived_…` slaagde om de
  verkeerde reden: het opgeslagen token had een andere `ResourceUrl` dan de server in de catalogus, dus de
  origin-check gooide het weg en de test mat "nooit aangemeld" in plaats van de kortelevensduur-regel. Gerepareerd
  door de adressen gelijk te trekken en er een tegenproef naast te zetten (dezelfde opstelling mét endpoint houdt de
  server wél).
- **`WhenTheServerBreaksOffMidStream_…` was timing-gevoelig.** Met `context.Abort()` op de upstream valt ook de
  agent-kant weg, en welke van de twee de proxy het eerst ziet is een race — "de agent hing op" is met opzet stil,
  dus de test asserteerde een muntworp. Viel één keer om in de volle solution-run. Herschreven naar een upstream die
  meer `Content-Length` belooft dan hij levert: dat breekt alleen de bovenstroomse kant. Nu 0,3 s in plaats van 10 s,
  en de mutatie is nog steeds rood.

## Suite

`dotnet build Cockpit.slnx` — **nul warnings**.
Drie volledige solution-runs ná de merge: `Cockpit.Core.Tests` 3459/3459 · `Cockpit.App.ViewTests` 646/646 ·
`Cockpit.Infrastructure.Tests` 795/797 (de twee bekende, op Windows altijd rode tests uit `BuildTraps.md`).

⚠️ **Eén flake die niet van mij is.** `Cockpit.Core.Tests.ViewModels.ProjectDialogResourceRowTests.
RapidEdits_CancelTheOlderInFlightCheck_NotJustDiscardItsResult` viel in twee van de drie solution-brede runs om en
is in vijf losse runs én in een volledige project-run steeds groen. Het bestand is **byte-identiek aan
`origin/main`** (`git diff --quiet origin/main HEAD -- …` → geen verschil) en komt uit AC-499; mijn diff raakt
`ViewModels/` niet. Dit is de contentie-tussen-testprojecten-hypothese uit `BuildTraps.md`, niet een regressie van
deze branch — maar het is een echte, timing-gevoelige test en het is het melden waard.
