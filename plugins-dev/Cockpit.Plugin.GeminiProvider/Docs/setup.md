---
title: Setup
order: 10
summary: The API key each profile needs, and the model/base URL fields it goes with.
icon: 🔑
---

This plugin adds Gemini and OpenAI as selectable providers, each talking to an OpenAI-compatible
chat-completions endpoint. Three things need to be true before a profile works: an API key for the provider
you picked, a model id that endpoint actually serves, and — if you're pointing at something other than the
provider's own endpoint — a base URL that matches.

## 1. Get an API key {#api-key}

For **Gemini**, get a key from [Google AI Studio](https://aistudio.google.com/) — **API key**. For **OpenAI**,
get one from [platform.openai.com](https://platform.openai.com/) — **API keys**. Paste it into this plugin's
**API key** field for the matching profile; it is stored with the rest of the profile's config.

## 2. Pick a model {#model}

Type a model id into the **Model** field, or press **Fetch** first to list what the base URL below actually
serves and pick from there. Fetching needs the API key and base URL already filled in — it calls the
endpoint's own `/models` list using them. A gateway that doesn't serve `/models`, or a key it rejects, leaves
the field as free text with a status line saying so; typing an id by hand still works.

## 3. Check the base URL {#base-url}

**Base URL** is pre-filled with the provider's own OpenAI-compatible endpoint — Gemini's
`generativelanguage.googleapis.com` endpoint, or `api.openai.com` for OpenAI — and only needs changing if
you're routing through a different OpenAI-compatible gateway.

## What breaks silently if you skip a step {#silent-breaks}

- **No API key**: the profile cannot be saved at all — the key, model and base URL are all required fields.
- **Wrong model id for the base URL**: this plugin brings no tool search or planning of its own
  (`HostToolLoop: ToolsAndSearch`) — it is plain chat completions — so a model id the endpoint doesn't
  recognize surfaces only as a request failure once a session actually opens, not as a validation error here.
- **Base URL pointed at a gateway that doesn't match the key's provider**: authentication fails per-turn, the
  same as a wrong key — there's nothing in this plugin that cross-checks the two.
