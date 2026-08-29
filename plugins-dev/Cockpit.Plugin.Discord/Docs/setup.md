---
title: Setup
order: 10
summary: Creating the Discord application and bot, and the one setting that silently breaks the connection.
icon: 🤖
---

Five things need to be true before a bot token works in this plugin's settings: an application exists, it
has a bot, the bot's privileged Message Content intent is on, the bot is in your server, and you have the
channel's id. This page walks through all five in order. The same walkthrough is also in the repo as this
plugin's `README.md`, for whoever's reading it outside the app.

## 1. Create the application and get the bot token {#create-application}

Go to the [Discord Developer Portal](https://discord.com/developers/applications) and press **New
Application**. Give it a name and accept the terms — that lands you on the application's **General
Information** page.

Open the **Bot** page in the left-hand sidebar. This is where the bot token lives; press **Reset Token** (or
**Copy**, if one is already showing) to get it, and paste it into this plugin's **Bot token** field. Treat it
like a password — anyone holding it can act as your bot.

## 2. Turn on the Message Content Intent {#message-content-intent}

Still on the **Bot** page, scroll to **Privileged Gateway Intents** and turn on **Message Content Intent**.

This is the step that is easy to skip because nothing tells you it is missing. Without it, Discord still
delivers the *events* for messages in your channel, but every message arrives with an empty body — the
plugin connects, the bot shows online, and the assistant simply never responds, because from its side no one
said anything. This plugin asks the gateway for this intent unconditionally (`GatewayIntents.MessageContent`
in `DiscordGatewayConnection`), so there is no lesser mode that works around it.

Message Content is a *privileged* intent. On an app past [verification](https://support-dev.discord.com/hc/en-us/articles/23926564536471-How-Do-I-Get-My-App-Verified),
Discord may ask why you need it before letting you enable it — for a personal or small-server bot like this
one, the toggle is available straight away.

## 3. Choose bot permissions and copy the install link {#invite-link}

Open **Installation** in the left-hand sidebar. Under **Installation Contexts**, enable **Guild Install**.
Under **Default Install Settings**, add the **bot** scope, then select at least **Send Messages**, **Read
Message History**, and **Use Slash Commands** — add more if you plan to use the bot for anything past what
this plugin relays. Copy the **Install Link**.

## 4. Invite the bot to your server {#invite-bot}

Open the link from the previous step in a browser, select **Add to server** in the installation prompt, then
pick the server and confirm. You need the **Manage Server** permission on that server to complete this step.

## 5. Find the channel id {#channel-id}

The plugin needs the numeric id of the text channel to relay into, not its name. Discord only shows ids once
**Developer Mode** is on: in the Discord client, open **User Settings → Advanced** and turn on **Developer
Mode**. Then right-click the channel and choose **Copy Channel ID**, and paste that into this plugin's
**Channel id** field.

## Your responsibility, not this plugin's {#terms-of-service}

This plugin connects a bot account you control to a channel you control. Whether doing so — and who you let
talk to your assistant through it — is consistent with Discord's own terms of service and developer policy is
for you to check, not something this plugin verifies on your behalf.
