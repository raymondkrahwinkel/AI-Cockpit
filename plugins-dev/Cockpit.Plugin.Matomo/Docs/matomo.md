---
title: Settings
order: 10
summary: The view-only token, the sites agents may read and the read-only reports.
icon: 📊
---

This plugin reads reports from your own Matomo server through its Reporting HTTP API. It never writes: only a fixed
list of read methods can be called.

## Connecting {#connecting}

In Matomo, create a user with **view** access to the sites you want to share, then create an auth token for that user
(**Personal › Security**) and turn on **Only allow secure requests** for it. Enter the Matomo URL and the token here and
choose **Test connection**. The token is kept in the encrypted secrets layer, sent only in the body of a POST request,
and never shown to an agent or written to a log.

After a successful test the list **Sites agents may read** appears. New sites are off until you tick them; one site can
be the default. Turn on **Offer the cockpit-matomo MCP server to sessions** to give new sessions the tools.

## Tools {#tools}

All tools are read-only and need no per-call consent. Errors come back as `{ ok: false, code, error, fix }`.

| Tool | What it does |
| --- | --- |
| `analytics_list_sites` | The sites agents may read |
| `analytics_list_fields` | The reports of a site (one dimension each) and their metrics |
| `analytics_run_report` | One report over a date range, optionally with chosen metrics and an order |
| `analytics_top` | Top pages, referrers, countries, devices, events or goals |
| `analytics_realtime` | Visits in the last few minutes |

Dates are `YYYY-MM-DD`, `today`, `yesterday` or `NdaysAgo`. Results hold at most 1,000 rows and are cut shorter when
they get large; `truncated` says so. Field values come from website visitors, so agents treat them as data.
