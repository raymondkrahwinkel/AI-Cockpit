# Slack (assistant channel)

Adds Slack as a second door onto the assistant's own conversation (AC-1025), through the
`AssistantChannelContribution` seam that also backs the Discord plugin — the host owns identity and consent
filtering, this plugin supplies the Slack-specific transport: a **SlackNet** Socket Mode connection, and
consent prompts relayed as Approve/Deny buttons (Block Kit) with a "type JA/NEE" text fallback.

## What you need

- A Slack workspace you can create an app in and install a bot into.
- Five things done on [api.slack.com/apps](https://api.slack.com/apps) before the tokens below work: an app,
  **Socket Mode** turned on (that produces one token), **Interactivity** turned on, the bot's scopes chosen
  and the app installed (that produces the other token), and the bot invited into your channel. See **Setup**
  below.

## Setup

This is the same walkthrough as [`Docs/setup.md`](Docs/setup.md), which also ships with the plugin and opens
from its settings — the **?** beside the token fields.

1. **Create the app.** [Create New App](https://api.slack.com/apps) → **From scratch** → name it and pick a
   workspace.
2. **Turn on Socket Mode and generate the app-level token.** **Socket Mode** page → toggle **Enable Socket
   Mode** on (or **Basic Information → App-Level Tokens → Generate Token and Scopes** with the
   `connections:write` scope). This plugin talks to Slack over the WebSocket that token opens
   (`SlackServiceBuilder.UseAppLevelToken` in `SlackGatewayConnection`), not over a public HTTP endpoint —
   skip this and there is nothing to connect to at all. Copy the `xapp-…` token.
3. **Turn on Interactivity.** **Interactivity & Shortcuts** page → toggle **Interactivity** on (no Request
   URL needed under Socket Mode). Skip this and the bot still posts and still replies to plain messages — what
   silently breaks is the Approve/Deny buttons on consent prompts, leaving only the "type JA/NEE" fallback.
4. **Choose bot scopes and install the app.** **OAuth & Permissions** page → **Bot Token Scopes**: add at
   least `chat:write`, `channels:history` (`groups:history` for a private channel) and `files:read` — without
   that last one Slack answers a file's private URL with its sign-in page and a `200`, so images silently
   never arrive (the message text still does, with a ⚠️ on it). **Event Subscriptions**
   page: turn subscriptions on and add the `message.channels` bot event (`message.groups` for private) — this
   is what makes Slack deliver messages over the socket at all. Then **Install to Workspace** and copy the
   `xoxb-…` **Bot User OAuth Token**.
5. **Invite the bot to the channel and find its id.** `/invite @your-bot-name` in the channel, then copy its
   **Channel ID** (`C0123456789`-shaped) from the channel details.

## Settings

| Field | What it does |
|---|---|
| Who may talk to the assistant here | The AC-1023 three-level access model: one Slack account, a named list, or everyone in the channel — widening past one account requires acknowledging a warning. |
| How much of the conversation to relay | Final answer only, everything including tool use, or short status lines instead of full tool traffic. |
| Bot token | From step 4 above (`xoxb-…`). Stored encrypted at rest, like every other plugin secret. |
| App-level token | From step 2 above (`xapp-…`) — Socket Mode's own token, separate from the bot token. |
| Channel id | From step 5 above. |

## Your responsibility, not this plugin's

This plugin connects a bot account you control to a channel you control. Whether doing so — and who you let
talk to your assistant through it — is consistent with Slack's own terms of service and API guidelines is for
you to check, not something this plugin verifies on your behalf.
