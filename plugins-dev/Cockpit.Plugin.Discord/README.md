# Discord (assistant channel)

Adds Discord as a second door onto the assistant's own conversation (AC-1024), through the
`AssistantChannelContribution` seam that also backs the Slack plugin — the host owns identity and consent
filtering, this plugin supplies the Discord-specific transport: a **Discord.NET** gateway connection, and
consent prompts relayed as Approve/Deny buttons (Discord's Components API) with a "type JA/NEE" text
fallback.

## What you need

- A Discord account with a server you can install a bot into (or **Manage Server** permission on one).
- Five things done in the [Discord Developer Portal](https://discord.com/developers/applications) before a
  token works below: an application, a bot, its **Message Content Intent** turned on, the bot invited into
  your server, and the channel's id. See **Setup** below.

## Setup

This is the same walkthrough as [`Docs/setup.md`](Docs/setup.md), which also ships with the plugin and opens
from its settings — the **?** beside the token field.

1. **Create the application and get the bot token.** [New Application](https://discord.com/developers/applications) →
   name it → the **Bot** page has the token (**Reset Token**/**Copy**).
2. **Turn on the Message Content Intent (privileged).** Still on the **Bot** page, under **Privileged Gateway
   Intents**. Skip this and the bot connects and shows online, but every message it receives arrives with an
   empty body — the assistant never responds, because from its side no one said anything. This plugin
   requests the intent unconditionally (`GatewayIntents.MessageContent` in `DiscordGatewayConnection`), so
   there is no lesser mode that works around it.
3. **Choose bot permissions and generate an invite link.** **OAuth2** page → **OAuth2 URL Generator** → tick
   the **bot** scope → tick at least **Send Messages** and **Read Message History** under **Bot Permissions**
   → copy the generated URL.
4. **Invite the bot to your server.** Open that URL, pick the server, confirm.
5. **Find the channel id.** Turn on **Developer Mode** (**User Settings → Advanced**), then right-click the
   channel → **Copy Channel ID**.

## Settings

| Field | What it does |
|---|---|
| Who may talk to the assistant here | The AC-1023 three-level access model: one Discord account, a named list, or everyone in the channel — widening past one account requires acknowledging a warning. |
| How much of the conversation to relay | Final answer only, everything including tool use, or short status lines instead of full tool traffic. |
| Bot token | From step 1 above. Stored encrypted at rest, like every other plugin secret. |
| Channel id | From step 5 above — the numeric id, not the channel name. |

## Your responsibility, not this plugin's

This plugin connects a bot account you control to a channel you control. Whether doing so — and who you let
talk to your assistant through it — is consistent with Discord's own terms of service and developer policy is
for you to check, not something this plugin verifies on your behalf.
