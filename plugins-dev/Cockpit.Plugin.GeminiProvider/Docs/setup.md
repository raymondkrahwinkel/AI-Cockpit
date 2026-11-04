---
title: Setup
order: 10
summary: The API key each profile needs, and the model/base URL fields it goes with.
icon: 🔑
---

This plugin talks to any OpenAI-compatible chat-completions endpoint. Pick a named provider below, or
**Custom (OpenAI-compatible)** and fill in the base URL yourself. Three things need to be true before a profile works: an API key for the provider
you picked, a model id that endpoint actually serves, and — if you're pointing at something other than the
provider's own endpoint — a base URL that matches.

## What works per provider {#providers}

Taken from each provider's own documentation (November 2026), not from live tests. Every provider gets chat
and streaming. Tools run through the cockpit, images depend on the model, and this plugin does not show token
usage, reasoning or cache hits for any of them.

| Provider | Base URL | Tools | Reports differently |
|---|---|---|---|
| Gemini | `https://generativelanguage.googleapis.com/v1beta/openai/` | May fail on Gemini 3: its docs require thought signatures for multi-turn function calling, which this plugin does not return | `reasoning_effort` or `extra_body` for thinking |
| OpenAI | `https://api.openai.com/v1` | Yes | No reasoning text, only a token count |
| Groq | `https://api.groq.com/openai/v1` | Yes | `reasoning_format` selects how reasoning returns |
| Mistral | `https://api.mistral.ai/v1` | Yes | Thinking arrives as chunks inside `content` |
| Cerebras | `https://api.cerebras.ai/v1` | Yes | `delta.reasoning`; JSON mode does not stream |

**Grok and OpenRouter** have their own plugins, which are the better fit: use those. Grok also works here through
Custom with `https://api.x.ai/v1`, on the chat-completions route xAI now marks as legacy.

**Other endpoints, with Custom:** Hetzner Inference (`https://inference.hetzner.com/api/v1`, also still fine on
an existing OpenAI profile), Together (`https://api.together.ai/v1`), Fireworks
(`https://api.fireworks.ai/inference/v1`, **Fetch** does not list its models, so type the id), or a server of
your own. A local server that takes no key still needs some text in the **API key** field.

**DeepSeek does not work with tools yet.** Its docs answer with a 400 unless `reasoning_content` is sent back on
every turn, and this plugin does not send it. Perplexity retired its chat completions for this kind of use.

## 1. Get an API key {#api-key}

For **Gemini**, get a key from [Google AI Studio](https://aistudio.google.com/) — **API key**. For **OpenAI**,
get one from [platform.openai.com](https://platform.openai.com/) — **API keys**. Other providers have the same
page in their own console. Paste it into this plugin's
**API key** field for the matching profile; it is stored with the rest of the profile's config.

On a Cockpit server you can leave the field empty and set `GEMINI_API_KEY` in `session.env` instead: the key then comes from the container secret, and the profile reports "API key from secret". The variable is only used while the base URL is this provider's own; a profile that points elsewhere needs its own key in the config. This applies to the Gemini profile only.

## 2. Pick a model {#model}

Type a model id into the **Model** field, or press **Fetch** first to list what the base URL below actually
serves and pick from there. Fetching needs the API key and base URL already filled in — it calls the
endpoint's own `/models` list using them. A gateway that doesn't serve `/models`, or a key it rejects, leaves
the field as free text with a status line saying so; typing an id by hand still works.

## 3. Check the base URL {#base-url}

**Base URL** is pre-filled with the provider's own OpenAI-compatible endpoint and only needs changing if you're
routing through a different OpenAI-compatible gateway. **Custom** starts empty: fill in the full address,
including its version part such as `/v1`.

## What breaks silently if you skip a step {#silent-breaks}

- **No API key**: the profile cannot be saved at all — the key, model and base URL are all required fields.
- **Wrong model id for the base URL**: this plugin brings no tool search or planning of its own
  (`HostToolLoop: ToolsAndSearch`) — it is plain chat completions — so a model id the endpoint doesn't
  recognize surfaces only as a request failure once a session actually opens, not as a validation error here.
- **Base URL pointed at a gateway that doesn't match the key's provider**: authentication fails per-turn, the
  same as a wrong key — there's nothing in this plugin that cross-checks the two.
