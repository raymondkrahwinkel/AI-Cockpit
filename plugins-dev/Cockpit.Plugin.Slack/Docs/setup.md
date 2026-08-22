---
title: Setup
order: 10
summary: Creating the Slack app, and the two settings that silently break the connection.
icon: 💬
---

Five things need to be true before the two tokens in this plugin's settings work: an app exists, Socket
Mode is on (that is where the app-level token comes from), Interactivity is on, the bot has the scopes it
needs and is installed to your workspace, and it is in your channel. This page walks through all five in
order. The same walkthrough is also in the repo as this plugin's `README.md`, for whoever's reading it
outside the app.

## 1. Create the app {#create-application}

Go to [api.slack.com/apps](https://api.slack.com/apps) and press **Create New App**. Choose **From scratch**,
give it a name, and pick the workspace to develop it in. That lands you on the app's **Basic Information**
page.

## 2. Turn on Socket Mode and generate the app-level token {#socket-mode}

Open **Socket Mode** in the left-hand sidebar and toggle **Enable Socket Mode** on. Slack walks you through
generating an app-level token as part of that; if it doesn't, go to **Basic Information → App-Level Tokens**
and press **Generate Token and Scopes**, add the `connections:write` scope, and generate. Copy the resulting
token — it starts with `xapp-` — into this plugin's **App-level token** field.

Without Socket Mode, this plugin has nothing to connect to: it talks to Slack over a WebSocket it opens
itself (`SlackServiceBuilder.UseAppLevelToken` in `SlackGatewayConnection`), not over a public HTTP endpoint,
so there is no fallback transport that works without this token.

## 3. Turn on Interactivity {#interactivity}

Open **Interactivity & Shortcuts** in the left-hand sidebar and toggle **Interactivity** on. Under Socket
Mode you do not need to fill in a Request URL — the toggle itself is what matters.

This is the step that looks optional because the bot still posts and still replies to plain messages without
it. What breaks silently is the Approve/Deny buttons on consent prompts: with Interactivity off, a button
click never reaches this plugin, and only the "type JA/NEE" text fallback keeps working.

## 4. Choose bot scopes and install the app {#install}

Open **OAuth & Permissions** in the left-hand sidebar. Under **Scopes → Bot Token Scopes**, add at least
`chat:write` (to post) and `channels:history` (to read the channel back — use `groups:history` instead if
your channel is private). Then, still on **Event Subscriptions**, turn subscriptions on and add the
`message.channels` bot event (`message.groups` for a private channel) — this is what makes Slack deliver
messages over the socket at all, Interactivity notwithstanding.

Press **Install to Workspace** at the top of **OAuth & Permissions**, and authorize. That produces the bot
token — starting with `xoxb-` — shown as **Bot User OAuth Token**; copy it into this plugin's **Bot token**
field.

## 5. Invite the bot to the channel and find its id {#channel-id}

In Slack, open the channel to relay into and invite the bot the same way you would invite a person (`/invite
@your-bot-name`, or through the channel's member list). Then open the channel details and copy its **Channel
ID** (it looks like `C0123456789`) into this plugin's **Channel id** field.

## Your responsibility, not this plugin's {#terms-of-service}

This plugin connects a bot account you control to a channel you control. Whether doing so — and who you let
talk to your assistant through it — is consistent with Slack's own terms of service and API guidelines is for
you to check, not something this plugin verifies on your behalf.
