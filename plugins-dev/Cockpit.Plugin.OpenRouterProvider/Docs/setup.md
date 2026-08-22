---
title: Setup
order: 10
summary: Creating an OpenRouter API key, and the vendor/model id format this plugin expects.
icon: 🔀
---

Two things need to be true before this plugin's profile works: you have an OpenRouter API key, and the
model id in the profile is a `vendor/model` string OpenRouter actually routes. This page covers both, plus
the base URL field most profiles never need to touch.

## 1. Create an API key {#api-key}

Go to [openrouter.ai/settings/keys](https://openrouter.ai/settings/keys) and create a key there. Paste it
into this plugin's **API key** field.

A missing or rejected key fails at request time, not at save time: the profile stores whatever you typed,
and the session only errors out once it actually calls `openrouter.ai/api/v1`. **Fetch** on the Model field
surfaces a rejected key sooner, as "the key was rejected."

## 2. Pick a vendor/model id {#model}

OpenRouter routes by `vendor/model`, e.g. `anthropic/claude-sonnet-4.5` or `openai/gpt-5.1` — see the
catalog at [openrouter.ai/models](https://openrouter.ai/models). Fill in the API key and base URL first,
then press **Fetch** to list models, or type an id by hand — OpenRouter's catalog is large enough that a
model you know about may not appear in the fetched suggestions yet. These strings pass straight through as
the request's model id with no parsing of their own, so a misspelled vendor or model segment is only caught
by OpenRouter rejecting the request, not by this plugin.

## 3. The base URL {#base-url}

Pre-filled with OpenRouter's OpenAI-compatible endpoint (`https://openrouter.ai/api/v1`) and rarely needs
changing.

## No usage figures from this endpoint {#no-usage-signals}

OpenRouter's chat-completions response carries no rolling allowance or context-usage figure to read, unlike
some other providers — this plugin does not show any such indicator, by design, not because something is
missing.
