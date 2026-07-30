# AC-524 — voortgang

Worktree: `D:\Projects\dotnet\Cockpit\.claude\worktrees\agent-ab85a7608bbe1dd14`
Branch: `worktree-agent-ab85a7608bbe1dd14`

## Status — af

- [x] Deel A — `AcquireForSessionAsync` met eigen, ruimere marge (55 min)
- [x] Deel B — loopback doorstuur-endpoint per OAuth-server, met SSE-streaming en een
      verbinding-in-stand-houdend antwoord bij een mislukte verversing
- [x] Deel C — verval, verversingspoging en uitkomst in het log; nooit een tokenwaarde
- [x] Single-flight per server op het verversingspad (rolling refresh tokens)
- [x] Niet-indringende meldladder: stil gebruiken → stil verversen → pas dán vragen, één melding per
      toestandsovergang, met oorzaak én actie per geval
- [x] Tests + mutatie-uitslagen (alle 16 mutaties rood, zie onder)
- [x] `dotnet build` nul warnings; CHANGELOG bijgewerkt; twee commits op de eigen branch

## Wat er gebouwd is

Nieuw:
- `src/Cockpit.Core/Mcp/McpOAuthAttentionReason.cs` — drie oorzaken (nooit aangemeld / aanmelding
  verlopen / server onbereikbaar). Ordinal 0 = `None`, de veilige waarde.
- `src/Cockpit.Core/Mcp/McpOAuthSignInGuidance.cs` — de ene zin met oorzaak + actie, per oorzaak
  anders. Eén plek, want hij wordt op drie plaatsen geschreven.
- `src/Cockpit.Core/Abstractions/Mcp/IMcpOAuthProxy.cs`
- `src/Cockpit.Infrastructure/Mcp/McpOAuthProxyHost.cs` — eigen Kestrel-listener per OAuth-server,
  `127.0.0.1:0`, lui gemount, hergebruikt over sessies heen.
- `src/Cockpit.Infrastructure/Mcp/McpOAuthProxyForwarder.cs` — de header-herschrijvende reverse
  proxy zelf.

Gewijzigd: `IMcpOAuthCoordinator` / `McpOAuthCoordinator`, `McpOAuthAccess`, `McpOAuthTokenCache`,
`McpOAuthAuthorizer` (geeft de logger door), `PluginSessionDriverAdapter`,
`PluginTtySessionProviderAdapter`, `SessionDriverFactory`, `TtySessionProviderResolver`,
`Screenshotter` (no-op coordinator moest de nieuwe methode krijgen).

## Besluiten en aannames

1. **`CockpitMcpEndpointHost.cs` is niet aangeraakt.** Wel *hergebruikt* (niet gewijzigd):
   `McpAuthMiddleware.Require` — de bestaande poortwachter voor de loopback-sleutel. Kopiëren zou een
   tweede, driftende security-check opleveren.
2. **Alleen `Auth == OAuth` verandert.** `MountAsync` weigert meteen voor elke andere auth-vorm of
   transport; beide adapters vallen terug op de bestaande tak zodra er geen proxy-URL is.
3. **Sessiemarge 55 minuten.** Afgezet tegen het access-token van één uur dat deze servers uitgeven:
   een opgeslagen token wordt dan alleen in zijn eerste vijf minuten hergebruikt, dus in de praktijk
   start elke sessie op een net-ververst token. Een vaste marge in plaats van "ververs onder de
   helft" omdat de store geen uitgiftetijdstip bewaart (een fractie is niet berekenbaar zonder het
   on-disk record te verbreden) én omdat een vaste marge de churn al aan beide kanten begrenst: een
   server met lange tokens wordt nooit ververst, een server met korte kost precies één
   token-endpoint-round-trip per sessiestart.
4. **De rand "marge > levensduur" is expliciet afgehandeld** in `_ConnectAndReadAsync`: een net
   ververst token dat de 55 minuten niet haalt maar de 2 minuten van een enkele request wél, wordt
   alsnog gebruikt, met een Warning die het tekort noemt. Zou dat er niet staan, dan zou een server
   met tokens korter dan de marge permanent onbruikbaar zijn — slechter dan het probleem.
5. **Per-request-marge blijft 2 minuten**, ook op het proxypad: het token wordt daar meteen gebruikt.
6. **Single-flight**: één verversing per `IdentityKey` tegelijk; wie aanklopt wacht op díé taak en
   leest daarna zelf de store opnieuw (dus geen tweede inwisseling van hetzelfde refresh-token). De
   gedeelde taak draait op `CancellationToken.None` en kan niet faulted raken — beide bewust, en
   beide staan als reden in de code (BuildTraps AC-513). De slot wordt vrijgegeven als het *werk*
   eindigt, niet als een wachter opgeeft; anders zou de TTY-route met zijn 5-secondenbudget de slot
   vrijmaken terwijl de handshake nog loopt.
7. **Interactief aanmelden staat buiten de single-flight** — de operator drukte op de knop en die
   moet echt een aanmelding doen, niet meeliften op een stille verversing.
8. **Vorm van het "niet stilvallen"-antwoord**: HTTP 200 + JSON-RPC-error-envelope met het `id` uit
   het verzoek (POST met id), 202 Accepted (notificatie zonder id), 405 (GET — de MCP-spec laat een
   server die geen SSE-stream aanbiedt 405 antwoorden, wat de client accepteert). Een 401 is precies
   wat de CLI de server laat laten vallen.
9. **Een 401/403 van de échte server wordt óók niet doorgegeven** maar als "verversing mislukt"
   behandeld. Zelfde klasse, zelfde gevolg als het wél doorgegeven zou worden.
10. **Niet-indringend**: de melding met oorzaak + actie wordt door de coordinator geschreven bij de
    *overgang* naar een toestand, niet per aanroep. De proxy raakt dit pad bij elke request; zonder
    de latch zou dat een log vol één zin zijn. De regel die de adapters bij een sessiestart schrijven
    is verlaagd naar Information, want de Warning is al gevallen toen het waar werd.
11. **Een server zonder bruikbare aanmelding wordt bij de start nog steeds weggelaten** (bestaand
    gedrag), maar niet meer stil: de oorzaak-en-actie-regel valt één keer, en de sessieregel noemt
    welke sessie welke server misloopt. Bewuste keuze om hem *niet* mee te sturen: een server die
    niet eens kan initialiseren verschijnt anders in elke sessie als kapotte server.

## Open punten / waar ik twijfel

- **Het kanaal van de melding is het log, niet de UI.** `IAttentionNotifier` is sessie-gescoped en
  staat onder operator-toggles; `IToastNotifier` omzeilt die routering. Beide zijn gedeeld met
  niet-OAuth-paden, dus buiten de scope-grens. Wat de operator wél ziet zonder het log te openen: de
  JSON-RPC-error die de proxy teruggeeft draagt dezelfde zin, en de agent leest die voor in het
  gesprek. Een echte UI-melding (rij in het MCP-scherm, toast) is niet gebouwd.
- **`IHttpResponseBodyFeature.DisableBuffering()`** in de forwarder is niet met een mutatie te
  bewijzen: Kestrel buffert standaard niet, dus weghalen maakt geen test rood. Het staat er omdat het
  de gedocumenteerde manier is om een SSE-respons tegen tussengeschoven buffering te beschermen. Wat
  wél bewezen is (mutatie 8) is dat de respons chunk voor chunk wordt doorgegeven in plaats van eerst
  volledig ingelezen.
- **De echte SDK-verversing wordt in de nieuwe tests gefaked** (`RenewingMcpOAuthAuthorizer` schrijft
  het token dat de token-cache anders zou schrijven). Wat getest wordt is de rekenkunde van de
  coordinator, die volledig aan deze kant van de naad ligt; dat de SDK er echt een refresh-grant
  doorheen draait is elders end-to-end gedekt (`McpOAuthOfflineAccessFlowTests`).
- **Niet live geverifieerd tegen Depot.** De proxy is end-to-end getest met een echte Kestrel-server
  aan beide kanten en een echte HttpClient ertussen, maar niet tegen een draaiende Depot met een echt
  verlopend token. Dat is de enige claim die ik niet met bewijs kan onderbouwen.
- **De TTY-route deelt één budget van 5 seconden** voor verversing én proxy-mount samen (niet elk
  hun eigen). Het budget is het venster waarin de app niet hertekent, en dat is er één per launch.
  Een mount is lokaal en snel; loopt hij toch over het budget, dan valt de launch terug op het token
  in de config en zegt dat in het log.

## Mutatie-uitslagen

Elke mutatie is met `git diff --stat` gecontroleerd op landen (CRLF-val uit BuildTraps) en daarna
teruggedraaid. Alle 16 leverden precies de test op die de betreffende guard claimt te dekken.

| # | Mutatie | Test die rood werd |
|---|---------|--------------------|
| 1 | sessiemarge → request-marge | `AcquireForSession_ForATokenWithElevenMinutesLeft_RefusesToHandItToASession` (+ #4-test) |
| 2 | rand-afhandeling "marge > levensduur" weg | `AcquireForSession_WhenEveryTokenThisServerIssuesIsShorterThanTheMargin_UsesItAnyway` |
| 3 | single-flight vindt de lopende verversing nooit | `AcquireForSession_WhenSeveralSessionsStartAtOnce_RenewsOnceAndGivesThemAllTheSameToken` |
| 4 | slot wordt na afloop niet vrijgegeven | `AcquireForSession_AfterARenewalHasFinished_RenewsAgainWhenTheNextTokenGoesStale` |
| 5 | melding elke keer i.p.v. bij de overgang | `Acquire_WhenTheSameFailureRepeats_TellsTheOperatorOnlyOnce` |
| 6 | onbereikbaar wordt "aanmelding verlopen" | `Acquire_WhenTheServerCannotBeReached_DoesNotTellTheOperatorToSignInAgain` |
| 7 | proxy zonder auth-gate | `AProxiedCall_WithoutTheLocalKey_NeverReachesTheServer` |
| 8 | respons eerst volledig inlezen i.p.v. streamen | `AnEventStream_ReachesTheAgentAsItIsWritten_NotWhenTheServerIsDone` |
| 9 | upstream-401 doorgeven | `WhenTheServerItselfRefusesTheToken_ThatRefusalIsNotPassedOnAsA401` |
| 10 | "geen credential"-antwoord wordt een 401 | idem + `WhenTheCredentialCannotBeRenewed_TheCallIsAnsweredAndTheServerStaysConnected` |
| 11 | notificatie krijgt een envelope i.p.v. 202 | `WhenTheCredentialCannotBeRenewed_ANotificationIsAcknowledgedRatherThanAnswered` |
| 12 | request-body wordt uitgeleend mét sluitrecht | `WhenTheServerItselfRefusesTheToken_ThatRefusalIsNotPassedOnAsA401` |
| 13 | tweede mount bindt een tweede listener | `MountingTheSameServerTwice_ReusesTheOneEndpoint` |
| 14 | OAuth-only scope-gate weg | `AServerThatIsNotOAuthProtected_GetsNoEndpointAtAll` |
| 15 | SDK-/TTY-route negeert de proxy | `SdkSession_/TtyLaunch_WhenTheServerIsProxied_WritesTheLoopbackAddressAndNoTokenAtAll` |
| 16 | SDK-/TTY-route terug naar de request-marge | `SdkSession_AsksThroughTheSessionEntryPoint_…`, `TtyLaunch_BoundsHowLongItWaitsForARenewal` |

**Eén vals-groen gevonden en gerepareerd.** De SSE-test overleefde mutatie 8 aanvankelijk: de
deadline stond alleen op de *reads*, terwijl een bufferende proxy zijn eigen response-headers
vasthoudt tot de server klaar is — de reads slaagden dus alsnog, dertig seconden later. De deadline
staat nu op de `SendAsync` zelf; daarna is de mutatie rood.

## Suite

`dotnet build Cockpit.slnx` — nul warnings.
`Cockpit.Core.Tests` 3445/3445 · `Cockpit.App.ViewTests` 646/646 ·
`Cockpit.Infrastructure.Tests` 795/797 — de twee bekende, op Windows altijd rode tests uit
`BuildTraps.md` (`PhysicalResourceIdentityTests.Canonicalize_ARootedPathThatDoesNotExist_IsReturnedUnchanged`
en `WorktreeManagerTests.RemoveAsync_RepositoryRootIsAFolderButNoLongerAGitRepository_StillReportsTheLeftoverFolder`),
geen van beide geraakt door deze wijziging.
