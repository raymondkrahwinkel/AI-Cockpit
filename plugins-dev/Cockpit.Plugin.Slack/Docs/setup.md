---
title: Setup
order: 10
summary: Creating the Slack app, and the two settings that silently break the connection.
icon: 💬
---

Five things need to be true before the two tokens in this plugin's settings work: an app exists, Socket
Mode is on (that is where the app-level token comes from), Interactivity is on, the bot has the scopes it
needs and is installed to your workspace, and its Messages Tab is enabled. This page walks through all five in
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
`chat:write` (to post), `im:history` (to receive DMs), `im:write` (to open the owner's DM) and `files:read`
(to fetch an image sent along with a message). Then, under **Event Subscriptions**, turn subscriptions on
and add the `message.im` bot event. That event is what makes Slack deliver DMs over the socket at all,
Interactivity notwithstanding.

`files:read` is the one to double-check, because forgetting it fails in a way that looks like nothing at
all. A Slack file lives behind a private URL that only answers to the bot token; without the scope Slack
does not return an error for it but its **sign-in page**, with a `200` on it. This plugin refuses a
response that is not an image, so the picture simply never arrives: your message and its text still reach
the assistant, and you get a ⚠️ on your own message in Slack. If images are the only thing not coming
through, this scope is the first place to look — and note that adding a scope means re-installing the app,
which produces a new bot token to paste in.

Press **Install to Workspace** at the top of **OAuth & Permissions**, and authorize. That produces the bot
token — starting with `xoxb-` — shown as **Bot User OAuth Token**; copy it into this plugin's **Bot token**
field.

If this app was already installed for Slack 1.x, **reinstall and authorize it again after adding these scopes
and the event**. An existing installation does not receive new permissions automatically; without that step,
no DMs arrive.

## 5. Enable direct messages {#direct-messages}

Open **App Home** in the Slack app settings, enable the **Messages Tab**, and allow users to send messages from
it. In Slack, open the app from **Apps** and send it a direct message. Cockpit opens that same DM for outgoing
answers; there is no channel id to configure.

## Your responsibility, not this plugin's {#terms-of-service}

This plugin connects a bot account you control to the DM of the one member id you link. Whether doing so is
consistent with Slack's own terms of service and API guidelines is for you to check, not something this plugin
verifies on your behalf.
