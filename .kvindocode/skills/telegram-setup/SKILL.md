---
name: telegram-setup
description: Set up native Telegram access for KvindoCode — either a BOT (@BotFather token; only sees what is sent to it) or a USER session (MTProto; acts as you, reads history). Covers the vault entries, the settings, the interactive login and verification. Use whenever the user asks to "set up telegram", "add a telegram bot", "use my own telegram account", "read/send Telegram messages from a session", or when the Telegram tool says no token/session is configured.
---

# Telegram setup (native `Telegram` tool)

KvindoCode has a native `Telegram` tool with **two modes**. Pick one with **Settings → Security & advanced → Telegram → API**:

| Mode | What it can do | What it needs |
|---|---|---|
| **bot** (default) | Reads only messages addressed to the bot, sends to any chat the bot is in | a bot token from @BotFather |
| **user** | Acts as **you**: reads a chat's history, sends as you | `api_id` + `api_hash` from my.telegram.org, then a one-time interactive login |

Everything sensitive is a **vault entry referenced by name** — never a value in settings, a tool parameter or the
conversation. Nothing here should ever be pasted into the chat.

## A. Bot mode (simplest)

1. Chat with **@BotFather** → `/newbot` → give it a name and a username ending in `bot`.
2. BotFather replies with a token shaped `123456789:AA...`.
3. Store it in the vault under a name, e.g. `telegram-bot-token` (the default):

   ```
   Secrets {action: "create", name: "telegram-bot-token", value_file: "/path/where/the/human/saved/it"}
   ```

   Ask the human to save the token to a file first; never ask them to paste it.
4. Settings → Telegram → *API = bot*, and the token entry name.
5. The human must **/start** the bot once (a bot cannot message a user who never talked to it), then:

   ```
   Telegram {action: "read"}     # shows the chat_id of every message the bot received
   Telegram {action: "send", text: "connected"}
   ```

A bot **has no history**: it only sees messages sent after it existed, and in a group only those that mention it (unless
it is an admin with privacy mode off).

## B. User mode (acts as you)

This is what to use when the user wants to *read their chats* or *send as themselves* — a bot cannot do either.

> **Warn the user first:** Telegram can limit or ban an account that automates it. It is their account and their call,
> which is why bot stays the default.

1. Get credentials at **my.telegram.org** → *API development tools* → note `api_id` (a number) and `api_hash` (32 hex
   chars).
2. Put both in the vault, by name (never in the chat):

   ```
   Secrets {action: "create", name: "telegram-api-id",   value_file: "/path/to/api_id"}
   Secrets {action: "create", name: "telegram-api-hash", value_file: "/path/to/api_hash"}
   ```

3. Settings → Telegram → *API = user*, and the two entry names.
4. **The human runs the interactive login themselves** (Telegram sends a code to their phone, and asks for their 2FA
   password if set — nobody else can answer that):

   ```bash
   kvindocode --telegram-login
   ```

   Success prints `Signed in as …`. Re-run it any time; if it is already signed in it just reports who it is.
5. Verify:

   ```
   Telegram {action: "me"}      # which account this session is
   ```

**Where the session lives:** the MTProto session is a **file** — `~/.kvindocode/telegram-user.session`, owner-only
(0600) — not a vault value and not a "session string". It holds the auth key, so treat the file like a credential: it is
the thing that must be revoked (Telegram → *Active sessions*) if the machine is compromised. Deleting it forces a fresh
login.

## Failure modes

| Symptom | Cause | Fix |
|---|---|---|
| `No vault entry named '…'` | the name in Settings does not match the vault | `Secrets {action: "list"}`, then fix the name |
| `Unauthorized (error_code 401)` (bot) | token wrong or revoked in BotFather | re-create it and update the vault |
| `chat not found` | no chat ever started, or the id belongs to another bot | have the human `/start` it, then `read` for the id |
| `Telegram rate limit reached — retry after N seconds` | Telegram's own FLOOD_WAIT, not an HTTP 429 | wait N seconds; it is reported so a caller can act on it |
| `The Telegram session was revoked or logged out…` | the user session was ended from another device | re-run `kvindocode --telegram-login` |
| A user session asks for a code that never arrives | wrong `api_id`, or the code was already used | check the vault entries; codes are single-use and expire |
| `The vault is missing 'telegram-api-id' or …` | api_id/api_hash not stored yet | step B.2 above |

## Notes

- Rate limits: ~30 messages/second and ~20/minute per group; in user mode Telegram answers with `FLOOD_WAIT_<n>`.
- Tool actions: `me`, `read`, `send`, `send_file`, `send_photo`, `send_document`, `edit`, `delete`, `chat`, `admins`.
  Only the read-only ones are allowed in plan mode.
- Bot mode scans every error it returns for the token before it reaches the transcript. The user-mode api_hash and the
  session file are never compared, printed or sent to a model.
