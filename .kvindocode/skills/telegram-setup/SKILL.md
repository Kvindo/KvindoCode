---
name: telegram-setup
description: Set up native Telegram access for KvindoCode — create a bot with @BotFather, store its token in the encrypted vault, find the chat id, and verify with the Telegram tool. Use whenever the user asks to "set up telegram", "add a telegram bot", "connect KvindoCode to Telegram", "read/send Telegram messages from a session", or when the Telegram tool reports that no token is configured.
---

# Telegram setup (native `Telegram` tool)

KvindoCode has a native `Telegram` tool: a session can read the messages a bot received and send messages or files. The
bot token is a **secret**: it lives in the encrypted vault and only its **name** is a setting — the token itself is never
a tool parameter, never printed and never sent to the model.

## 1. Create the bot

1. In Telegram, open a chat with **@BotFather** and send `/newbot`.
2. Give it a display name and a username ending in `bot` (e.g. `kvindo_ops_bot`).
3. BotFather replies with a token shaped `123456789:AA...`. That is the credential.

Do this in the user's own Telegram client. **Never** ask the user to paste the token into the conversation — it would
land in the transcript. Ask them to put it in a file instead (step 2).

## 2. Store the token in the vault

The vault is the only place a token should live. From a shell (the user runs this, or the agent runs it with the token
already in a file the human created):

```bash
# the human saves the token to a private file, e.g. by copying from BotFather into an editor
install -m 600 /dev/null ~/telegram-token.txt
#   ... paste the token, save, quit ...

# then store it under a name — the value never enters argv or the transcript
#   (in KvindoCode the tool call is:  Secrets {action: create, name: "telegram-bot-token", value_file: "~/telegram-token.txt"} )
rm -P ~/telegram-token.txt   # once it is in the vault
```

Name it `telegram-bot-token` (the default) or set **Settings → Security & advanced → Telegram → Vault entry holding the
bot token** to whatever name you used. The Settings line confirms it found the entry and shows a short SHA-256 that
matches the vault — proof the right value is there without revealing it.

## 3. Find the chat to send to

A bot cannot message a user who has never talked to it. So:

1. Have the human open the bot's chat and press **Start** (`/start`), or add the bot to a group.
2. Read what the bot received — this also reveals the `chat_id`:

   ```
   Telegram {action: "read"}
   ```

   Each line shows the chat (`chat_id -100...` for a group) and the sender.
3. Store it as the default so later sends need no id: **Settings → Telegram → Default chat** (a numeric id, or
   `@channelusername` for a public channel / a supergroup with a public username).

Facts worth remembering:
- **No history**: the Bot API returns only updates from *after* the bot existed. There is nothing to backfill.
- A **group** bot only sees messages after it was added, and — unless it is an administrator with privacy mode off —
  only messages that mention it or reply to it.
- A **private** user must `/start` the bot once.

## 4. Verify

```
Telegram {action: "me"}                                  # which bot is configured; proves the token works
Telegram {action: "send", text: "KvindoCode is connected"}   # uses the default chat
Telegram {action: "read", wait: 20}                      # long-poll: waits for a reply
Telegram {action: "send_file", file_path: "./report.pdf", caption: "nightly report"}
```

`read` is **incremental**: the last update id is remembered in `~/.kvindocode/telegram-offset.json`, so a second `read`
returns only new messages. Pass `mark_read: false` to peek without consuming.

## 5. Where it can go wrong

| Symptom | Cause | Fix |
|---|---|---|
| `No vault entry named '…'` | the name in Settings does not match the vault | `Secrets {action: "list"}`, then fix the name |
| `Unauthorized (error_code 401)` | the token is wrong or was revoked in BotFather | re-create it and update the vault entry |
| `chat not found` | no chat ever started, or the id is from another bot | have the human `/start` it, then `read` for the id |
| `bot was blocked by the user` | the human stopped the bot | they must unblock / `/start` again |
| `read` keeps returning nothing | the bot is not in the chat, or in a group it is not an admin | add it, or promote it so it can read all messages |
| `Not enough rights to send…` | the bot is not allowed to post in that channel | make it an administrator with post rights |

## Notes

- Rate limits: about 30 messages/second and 20 messages/minute per group. Sending in a loop will hit `429` with a
  `retry_after` — the tool reports it; wait that long.
- The tool's `action` list is: `me`, `read`, `send`, `send_file`, `send_photo`, `send_document`, `edit`, `delete`, `chat`,
  `admins`. Only the read-only ones are allowed in plan mode.
- The token is scrubbed from every error message before it reaches the transcript, so a failure never leaks it.
