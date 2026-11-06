"""Summarize only a complete quiet-machine comparison; keep raw traces in scratch."""
import collections
import json
import math
from pathlib import Path
import statistics
import sys

sys.stdout.reconfigure(encoding="utf-8")


def percentile(samples: list[float], fraction: float) -> float:
    return sorted(samples)[math.ceil(len(samples) * fraction) - 1]


def spread(rows: list[dict], key: str) -> str:
    values = [row[key] for row in rows]
    return f"{statistics.median(values):.2f} [{min(values):.2f}–{max(values):.2f}]"


def main() -> None:
    manifest_path = Path(sys.argv[1])
    manifest = json.loads(manifest_path.read_text(encoding="utf-8"))
    assert manifest["mode"] == "measure" and manifest["quietSignal"]
    assert len(manifest["results"]) == 36
    grouped = collections.defaultdict(list)
    details = []
    for record in manifest["results"]:
        assert record["exit"] == 0 and not record["activeBuilders"] and not record["monitoringErrors"]
        state = Path(record["state"])
        identity = json.loads((state / "identity.json").read_text(encoding="utf-8"))
        assert identity["assemblySha256"] == manifest["assemblySha256"]
        assert identity["pageSha256"] == manifest["pageSha256"]
        result = [json.loads(line) for line in (state / "stdout.log").read_text(encoding="utf-8").splitlines() if line.startswith('{')]
        assert len(result) == 1
        result = result[0]
        assert all(not sample["UnobservedPids"] for sample in result["memorySamples"][1:])
        assert all(len(result["latency"][kind]) == 40 for kind in ("type", "scroll"))
        assert len(result["correlations"]) == 80
        assert len({row["eventId"] for row in result["correlations"]}) == 80
        assert all(row["frameId"] > 0 and row["copied"] >= row["dispatched"] for row in result["correlations"])
        width, height, scale, tabs = record["scenario"]
        assert result["frameWidth"] == int(width * scale) and result["frameHeight"] == int(height * scale)
        assert result["animation"]["offered"] > 0 and result["animation"]["consumed"] > 0
        row = {"index": record["index"], "route": record["route"], "scenario": tuple(record["scenario"])}
        for kind in ("type", "scroll"):
            row[kind + "50"] = percentile(result["latency"][kind], .5)
            row[kind + "95"] = percentile(result["latency"][kind], .95)
        row.update(cpu=result["cpuCorePercent"], rss=max(sample["WorkingSetBytes"] for sample in result["memorySamples"]) / 1048576,
                   private=max(sample["PrivateBytes"] for sample in result["memorySamples"]) / 1048576,
                   offered=result["animation"]["offered"] / result["animation"]["seconds"],
                   fps=result["animation"]["consumed"] / result["animation"]["seconds"],
                   copies=result["copyBytes"] / 1048576,
                   equivalent=result["copyBytes"] / (result["consumed"] * width * height * scale * scale * 4),
                   allocations=result["bufferAllocations"], payloadAllocatedMiB=result["transportAllocatedBytes"] / 1048576)
        grouped[(row["scenario"], row["route"])].append(row)
        details.append(row)
    assert len(grouped) == 12 and all(len(rows) == 3 for rows in grouped.values())
    lines = ["# AC-2142 — definitieve Windows-vergelijking", "", "[gemeten] Drie herhalingen per route/scenario, afwisselende volgorde. Waarden: mediaan van de drie runstatistieken, met minimum–maximum tussen blokhaken. Geen procentuele of achteraf bedachte tolerantie.", "",
             f"[gelezen] Git `{manifest['git']}`, assembly SHA-256 `{manifest['assemblySha256']}`, pagina SHA-256 `{manifest['pageSha256']}`; Windows `{manifest['platform']}`, machine `{manifest['machine']}`, {manifest['logicalProcessors']} logische processors.", "",
             "[gemeten] Rustig-meetseintje vooraf vereist; tijdens elke run werden alleen procesnamen/PID/CPU-tijd van buildexecutables gelezen. Geen builderactiviteit of observatiefout in de geaccepteerde runs. Ruwe QPC/event/framecorrelaties en logs staan lokaal bij het manifest.", "",
             "| Scenario (CSS px @ schaal / tabs) | Route | Type p50/p95 ms | Scroll p50/p95 ms | Aangeboden/verwerkt per s | CPU core-% | RSS/private MiB | Kopie MiB | Kopie-equivalenten/frame | Payload-allocaties |",
             "|---|---|---|---|---|---|---|---|---|---|"]
    for (scenario, route), rows in sorted(grouped.items()):
        width, height, scale, tabs = scenario
        lines.append(f"| {width}×{height}@{scale} / {tabs} | {route} | {spread(rows,'type50')} / {spread(rows,'type95')} | {spread(rows,'scroll50')} / {spread(rows,'scroll95')} | {spread(rows,'offered')} / {spread(rows,'fps')} | {spread(rows,'cpu')} | {spread(rows,'rss')} / {spread(rows,'private')} | {spread(rows,'copies')} | {spread(rows,'equivalent')} | {spread(rows,'allocations')} |")
    lines += ["", "## Uitkomst zonder eigen marge", ""]
    regressions = []
    inconclusive = []
    for scenario in sorted({key[0] for key in grouped}):
        local, shared, poll = (grouped[(scenario, route)] for route in ("local", "shared", "poll"))
        bad = []
        for metric in ("type50", "type95", "scroll50", "scroll95", "cpu", "rss", "private", "equivalent"):
            if min(row[metric] for row in shared) > max(row[metric] for row in local):
                bad.append(metric)
        if max(row["fps"] for row in shared) < min(row["fps"] for row in local):
            bad.append("fps")
        if bad:
            regressions.append((scenario, bad))
        slower = [metric for metric in ("type50", "type95", "scroll50", "scroll95") if min(row[metric] for row in poll) > max(row[metric] for row in local)]
        if not slower and max(row["fps"] for row in poll) >= min(row["fps"] for row in local):
            inconclusive.append(scenario)
        lines.append(f"- [gemeten] {scenario}: kandidaat volledig buiten ongunstige kant van referentiespreiding voor: {', '.join(bad) or 'geen van de gerapporteerde metrieken'}. Pollingcontrole volledig slechter voor: {', '.join(slower) or 'geen latencymaat'}.")
    if regressions or inconclusive:
        lines += ["", "[gemeten] **Geen groene prestatiepoort. Epic stopt op AC-2142; resultaat eerst naar cockpit-assistant. Alleen Raymond beslist over een concrete marge of andere route.**"]
        if inconclusive:
            lines.append(f"[gemeten] Pollingtegenproef niet onderscheidend op alle scenario's: {inconclusive}; geen controlebewijs verzinnen.")
    else:
        lines += ["", "[gemeten] Geen volledig gescheiden ongunstige kandidaatspreiding gevonden. Dit beschrijft de gemeten spreiding; het is geen zelfgekozen marge of productiegoedkeuring. Resultaat eerst naar cockpit-assistant voordat code behouden wordt."]
    lines += ["", "## Meetdefinities en grenzen", "",
              "[gelezen] Latency: opdracht vóór DevTools-arming/inputdispatch tot BGRA-bitmapkopie met hetzelfde 20-bit pixel-event-ID; QPC eindtijd wordt in de kopiecallback vastgelegd. Geen input-to-photon. P50/p95 zijn nearest rank van 40 samples per soort/run. De animatiefase duurt 3 seconden en produceert dezelfde RAF-paginawijzigingen. Meerdere tabs zijn allemaal actief, input roteert over tabs; het venster toont de bovenste surface.",
              "[gelezen] CPU is som van eigen ouder/host/CEF-procesboom, met 100% voor één volle core. Proceshandles blijven open zodat de eind-CPU van vertrokken kinderen meetelt. Een nieuw kind dat tijdens de meetperiode vóór uitlezing verdwijnt maakt de meting ongeldig. RSS/private zijn maxima van vier momentopnamen van levende processen; geen echte high-watermark, gedeelde RSS-pagina's kunnen dubbel tellen. Kopieën tellen payloadbytes op beide benen; vaste shared-map/bitmapallocaties zijn niet in payload-allocaties opgenomen. Managed GC-totalen en GPU-interne kopieën worden niet gemeten.",
              "[gelezen] local gebruikt de bestaande volledige _Paint/_Blit-route met meethaken in een bronkopie. shared kopieert dirty boundingrects in hergebruikte mapping, coalescet en signaleert met event; bij mutexcontentie worden dirty gebieden doorgeschoven naar een volgende paint. poll reconstrueert de beschreven oude file-backed/16ms-polling/full-frame/allocatieroute; de oorspronkelijke weggegooide proefbron is niet beschikbaar. Geen claim van bit-identieke oude implementatie.",
              "[gelezen] De afzonderlijke verse frame-ready-mutatie faalde op event 11 zonder pixels, daarna git reset --hard naar WIP en verse build/sharedrun groen. Geen nieuwe testmethodes/cases/journeys. Geen productiecode, requiresRestart, workflows of nightly gewijzigd.",
              "[vermoeden] Deze transportproef bewijst geen volledige plugin-, security-, crash-, IPC- of Linux-GPU-pariteit. Named probe-objecten en registratie/policystubs zijn geen productiecontract.", "",
              "## Runvolgorde", "", "| Index | Scenario | Route | Type p50/p95 | Scroll p50/p95 | Verwerkt/s |", "|---|---|---|---|---|---|"]
    for row in details:
        lines.append(f"| {row['index']} | {row['scenario']} | {row['route']} | {row['type50']:.2f}/{row['type95']:.2f} | {row['scroll50']:.2f}/{row['scroll95']:.2f} | {row['fps']:.2f} |")
    output = manifest_path.parent / "README.md"
    output.write_text('\n'.join(lines) + '\n', encoding="utf-8")
    print(output)
    print(json.dumps({"regressions": regressions, "inconclusivePolling": inconclusive}))


if __name__ == "__main__":
    main()
