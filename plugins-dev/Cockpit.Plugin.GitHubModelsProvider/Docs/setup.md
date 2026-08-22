---
title: Setup
order: 10
summary: Getting a GitHub personal access token with the right scope, and picking a model id this endpoint actually serves.
icon: 🐙
---

Two things need to be true before this plugin's profile works: you have a GitHub personal access token
with the `models:read` scope, and the model id in the profile is one GitHub Models actually serves under
that token. This page covers both, plus the base URL field most profiles never need to touch.

## 1. Create a personal access token with the models:read scope {#api-key}

Go to [github.com/settings/tokens](https://github.com/settings/tokens) and create a token — a fine-grained
token scoped to `models:read` is enough, a classic token works too. Paste it into this plugin's **API key**
field.

This is a GitHub PAT, not a vendor API key: the endpoint (`models.github.ai/inference`) authenticates the
same way GitHub's other APIs do. A token without `models:read` is rejected by the endpoint on the first
request — the profile saves fine, but every session with it fails immediately, and **Fetch** on the Model
field reports it as "the token was rejected" rather than naming the missing scope.

## 2. Pick a model id {#model}

Models here are namespaced by publisher, e.g. `openai/gpt-4.1` or `meta/llama-3.3-70b-instruct` — see the
catalog at [github.com/marketplace/models](https://github.com/marketplace/models). Fill in the API key and
base URL first, then press **Fetch** to list the models your token can currently reach and pick one, or type
an id by hand if you already know it.

An id that is not in your token's catalog — or that GitHub has retired — fails the same way a rejected token
does: the profile still saves, but the session errors out on first use rather than at config time.

## 3. The base URL {#base-url}

Pre-filled with GitHub Models' own inference endpoint (`https://models.github.ai/inference`) and rarely
needs changing. Only edit it if you have an org-scoped inference URL from GitHub.

## Not GitHub Copilot {#not-copilot}

GitHub Models and GitHub Copilot are separate products with separate auth. This plugin talks to the Models
inference endpoint only — there is no supported way to reach Copilot's chat models through it, whatever a
model id might suggest.
