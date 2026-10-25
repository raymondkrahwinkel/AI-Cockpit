---
title: Setup
order: 10
summary: Creating the Discord application and bot, then linking your own account for direct messages.
icon: 🤖
---

Four things need to be true before Discord can relay messages: an application exists, it has a bot, the privileged Message Content intent is on, and the bot is installed in a server you share with it. Cockpit then links your own Discord user id. Channel ids are not used.

## 1. Create the application and get the bot token {#create-application}

Go to the [Discord Developer Portal](https://discord.com/developers/applications), press **New Application**, and give it a name. Open **Bot** in the left sidebar and press **Reset Token** or **Copy**. Paste the value into Cockpit's **Bot token** field and treat it like a password.

## 2. Turn on the Message Content Intent {#message-content-intent}

On the **Bot** page, enable **Message Content Intent** under **Privileged Gateway Intents**. Without it, Discord delivers message events with an empty body, so the assistant cannot respond. This plugin requests `GatewayIntents.MessageContent` together with `GatewayIntents.DirectMessages`.

## 3. Install the bot {#invite-link}

Open **Installation**, enable **Guild Install**, and add the **bot** scope. Select **Send Messages**, **Read Message History**, and **Use Slash Commands**, then copy the install link. Open it, select **Add to server**, choose a server you control, and confirm.

## 4. Link your own account {#link-account}

In Discord, enable Developer Mode under **User Settings → Advanced**. Right-click your account, choose **Copy User ID**, and paste it into **Your Discord user id** in Cockpit. Make sure Discord allows direct messages from members of the shared server.

Only direct messages from this linked account become assistant turns. Replies, consent buttons, and messages from other plugins return to the same DM. Channel messages and DMs from every other account are ignored without a reply.

You may save the user id before entering a bot token. Until both a linked user and a working token exist, Discord communication remains off.

## Your responsibility, not this plugin's {#terms-of-service}

This plugin connects a bot account you control to your direct messages. Whether doing so is consistent with Discord's terms of service and developer policy is for you to check.
