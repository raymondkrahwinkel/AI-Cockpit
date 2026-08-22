---
title: Setup
order: 10
summary: Creating an xAI API key, and why the model field has no default here.
icon: 🛰️
---

Two things need to be true before this plugin's profile works: you have an xAI API key, and the model id in
the profile is one xAI currently serves. This page covers both, plus the base URL field most profiles never
need to touch.

## 1. Create an API key {#api-key}

Go to [console.x.ai](https://console.x.ai) → **API Keys** and create a key there. Paste it into this
plugin's **API key** field.

A missing or rejected key fails at request time, not at save time: the profile stores whatever you typed,
and the session only errors out once it actually tries to talk to `api.x.ai`. **Fetch** on the Model field
surfaces a rejected key sooner, as "the key was rejected."

## 2. Pick a model id {#model}

Unlike the sibling provider plugins, this field starts empty — xAI retires model names fast enough that a
hardcoded default would go stale. Fill in the API key and base URL first, then press **Fetch** to list what
your key can currently reach, or type an id by hand (e.g. `grok-4.6`) if you already know it. See
[docs.x.ai/docs/models](https://docs.x.ai/docs/models) for the current list.

An id xAI has since retired is accepted at save time — nothing here validates against the live catalog
except **Fetch** — but starts returning errors on every session that uses it.

## 3. The base URL {#base-url}

Pre-filled with xAI's OpenAI-compatible **legacy chat-completions** endpoint (`https://api.x.ai/v1`) —
deliberately not the newer Responses API. Rarely needs changing.

## Not Grok's ACP agent mode {#not-agent-mode}

xAI also offers a separate ACP-capable agent mode. This plugin does not use it — it only talks to the
chat-completions endpoint above, via Microsoft.Extensions.AI, with tools routed through the cockpit's own
loop.
