# Discord (assistant channel)

Adds Discord as a second door onto the assistant's own conversation (AC-1024). The host owns identity and consent filtering; this plugin supplies the Discord.NET direct-message transport. Consent prompts use Approve/Deny buttons with a `JA`/`NEE` text fallback. The **Send me a Discord DM** flow step and messages from plugins such as Schedules use the same owner-only DM route.

## What you need

- A Discord account with a server where you can install a bot, or **Manage Server** permission on one.
- An application and bot from the [Discord Developer Portal](https://discord.com/developers/applications).
- The bot's **Message Content Intent** enabled and the bot installed in the server you share with it.
- Your numeric Discord user id. Enable Developer Mode under **User Settings → Advanced**, right-click your account, and choose **Copy User ID**.

## Setup

This is the same walkthrough as [`Docs/setup.md`](Docs/setup.md), which ships with the plugin and opens from the **?** beside the token field.

1. Create an application, open its **Bot** page, and copy or reset the bot token.
2. On that page, enable the privileged **Message Content Intent**.
3. Under **Installation**, enable **Guild Install** and add the **bot** scope with **Send Messages**, **Read Message History**, and **Use Slash Commands**.
4. Open the install link, add the bot to a server you control, and allow direct messages from server members.
5. In Cockpit, enter your own Discord user id. Only DMs from that account are accepted; channel messages and DMs from other accounts are ignored.

## Settings

| Field | What it does |
|---|---|
| Your Discord user id | Links the one account allowed to talk to the assistant and receive its messages. |
| How much of the conversation to relay | Final answer only, everything including tool use, or short status lines instead of full tool traffic. |
| Bot token | From setup step 1. Stored encrypted at rest, like every other plugin secret. |

Without a linked user, Discord receives and sends nothing. The user id may be saved before the bot token so setup can be completed in stages.

## Your responsibility, not this plugin's

This plugin connects a bot account you control to your direct messages. Whether doing so is consistent with Discord's terms of service and developer policy is for you to check.
