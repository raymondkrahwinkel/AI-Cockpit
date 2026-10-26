# Slack (assistant channel)

Adds Slack as a second door onto the assistant's own conversation (AC-1025), through the
`AssistantChannelContribution` seam that also backs the Discord plugin — the host owns identity and consent
filtering, this plugin supplies the Slack-specific transport: a **SlackNet** Socket Mode connection, and
consent prompts relayed as Approve/Deny buttons (Block Kit) with a "type JA/NEE" text fallback.

The transport is private in both directions: Cockpit opens the DM with the one linked owner, accepts only
messages from that DM, and posts the conversation back into it. Slack channels are not an assistant route.

## What you need

- A Slack workspace you can create an app in and install a bot into.
- Five things done on [api.slack.com/apps](https://api.slack.com/apps) before the tokens below work: an app,
  **Socket Mode** turned on (that produces one token), **Interactivity** turned on, the bot's scopes chosen
  and the app installed (that produces the other token), and the App Home **Messages Tab** enabled. See
  **Setup** below.

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
   least `chat:write`, `im:history`, `im:write` and `files:read` — without
   that last one Slack answers a file's private URL with its sign-in page and a `200`, so images silently
   never arrive (the message text still does, with a ⚠️ on it). **Event Subscriptions**
   page: turn subscriptions on and add the `message.im` bot event — this is what makes Slack deliver DMs over
   the socket at all. Then **Install to Workspace** and copy the `xoxb-…` **Bot User OAuth Token**.
   **After updating from Slack 1.x, reinstall and authorize the app again** so Slack grants these new scopes.
5. **Enable direct messages.** Open **App Home**, enable the **Messages Tab**, and allow users to send messages
   from it. You can now open the app in Slack and send it a DM.

## Settings

| Field | What it does |
|---|---|
| Your Slack member id | The one owner account allowed to talk to the assistant, in its DM with the app. |
| How much of the conversation to relay | Final answer only, everything including tool use, or short status lines instead of full tool traffic. |
| Bot token | From step 4 above (`xoxb-…`). Stored encrypted at rest, like every other plugin secret. |
| App-level token | From step 2 above (`xapp-…`) — Socket Mode's own token, separate from the bot token. |

## Your responsibility, not this plugin's

This plugin connects a bot account you control to the DM of the one member id you link. Whether doing so is
consistent with Slack's own terms of service and API guidelines is for you to check, not something this plugin
verifies on your behalf.
