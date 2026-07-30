# AC-524 — voortgang

Worktree: `D:\Projects\dotnet\Cockpit\.claude\worktrees\agent-ab85a7608bbe1dd14`
Branch: `worktree-agent-ab85a7608bbe1dd14`

## Status

- [x] Verkenning: coordinator, token-cache, beide bake-in-paden, `ClaudeMcpConfig._ToEntry`, `McpAuthKey`/`McpAuthMiddleware`/`SessionMcpKeyring`, DI-scan.
- [ ] Deel A — `AcquireForSessionAsync` met ruimere marge
- [ ] Deel B — loopback doorstuur-endpoint per OAuth-server
- [ ] Deel C — logging van verval/verversing/uitkomst
- [ ] Tests + mutatie-uitslagen
- [ ] CHANGELOG + commit

## Aannames / besluiten

1. **`CockpitMcpEndpointHost.cs` blijft onaangeraakt.** De proxy is een eigen klasse
   (`McpOAuthProxyHost`) met een eigen Kestrel-listener per OAuth-server. Wel *hergebruikt* (niet
   gewijzigd) wordt `McpAuthMiddleware.Require` — dat is de bestaande poortwachter voor de
   loopback-sleutel; hem kopiëren zou een tweede, driftende security-check opleveren.
2. **Alleen `server.Auth == McpServerAuth.OAuth` verandert van gedrag.** `MountAsync` weigert
   meteen voor elke andere auth-vorm; het mappingpad in beide adapters valt terug op de bestaande
   tak zodra er geen proxy-URL is.
3. **Sessiemarge = 30 minuten** (zie code-comment voor de argumentatie). De rand "marge > hele
   levensduur van het token" wordt expliciet afgehandeld: na een verversing wordt een token dat de
   ruime marge niet haalt maar de request-marge wél, alsnog geaccepteerd — anders zou een server met
   korte tokens nooit meer starten.
4. **Upstream 401/403 wordt niet doorgegeven** maar als "verversing mislukt" behandeld (AC 3).
   Doorgeven is precies wat de tools laat verdwijnen.
5. **Vorm van het "verbinding in stand houden"-antwoord**: HTTP 200 + JSON-RPC-error-envelope met
   het `id` uit het verzoek (POST met id), 202 Accepted (notificatie zonder id), 405 (GET — de
   MCP-spec laat een server die geen SSE-stream aanbiedt 405 antwoorden, wat de client accepteert).
