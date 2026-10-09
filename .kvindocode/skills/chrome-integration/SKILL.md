---
name: chrome-integration
description: Set up and fix KvindoCode's Chrome integration (the Browser tool over the DevTools protocol) — remote debugging, the user's own profile, the Settings → Chrome button, and every common failure. Use when asked to "set up chrome integration", "browser tool does not work", "connect to Chrome", "CDP", or the Browser button shows disconnected.
---

# Chrome integration (Browser tool over CDP)

KvindoCode drives a real Chrome through the DevTools protocol on a loopback port. It uses **your profile** (logins,
extensions) by default, so the agent can browse as you.

## 1. How it decides what to attach to

1. Look for Chrome already listening on `127.0.0.1:<ChromePort>` (default **9222**) via `/json/version`.
2. If not, and *Launch Chrome automatically* is on, start Chrome with `--remote-debugging-port` using
   *Use my own Chrome profile* (default) or a separate KvindoCode profile.
3. If Chrome is already open **without** the flag it cannot be attached to, so KvindoCode offers to restart it
   gracefully (restoring the open tabs). Nothing is ever closed without that confirmation.

## 2. Make your own Chrome start with debugging (the durable fix)

Settings → **Chrome** → *Make my Chrome start with remote debugging* writes a launcher wrapper (and a `.desktop`
override) so Chrome started from the menu/dock always listens on the port. Delete the wrapper file to undo — the
Settings window prints its path. Then close Chrome once and reopen it.

Manual equivalent, for a one-off session:

```bash
google-chrome --remote-debugging-port=9222 --user-data-dir="$HOME/.config/google-chrome"
```

`--user-data-dir` must point at the profile you actually use; a fresh directory means a logged-out browser and a lot
of re-login.

## 3. Verify before blaming the agent

```bash
curl -s http://127.0.0.1:9222/json/version | head -c 200      # must print Browser / webSocketDebuggerUrl
curl -s http://127.0.0.1:9222/json/list | head -c 300         # the tabs it can see
```

Then in a session: the header's globe icon is the live state (green = connected). `Browser` with
`action=tabs` should list them; `read_page` returns the page text; `navigate` opens a URL.

## 4. Common failures

| Symptom | Cause / fix |
|---|---|
| globe grey, "not connected" | Chrome has no `--remote-debugging-port`. Use the Settings button, then restart Chrome. |
| `ECONNREFUSED 127.0.0.1:9222` | the port changed or Chrome was started from a `.desktop` file that ignores the flag — check `ps aux \| grep remote-debugging`. |
| connects but a window appears on screen | a bring-to-front action; the agent's own tabs are opened in the background, `bring_to_front` is what raises the window. |
| the window steals focus while the agent works | not expected — attach no longer raises the window; check you are not on an old build. |
| pages load logged out | the profile is wrong: *Use my own Chrome profile* off, or `--user-data-dir` points elsewhere. |
| two sessions drive one tab | tabs are owned per session; a session only drives tabs it opened. Use `Browser → tabs` to see ownership. |
| torrent of errors after a Chrome update | delete the KvindoCode profile / relaunch with a current `--remote-debugging-port`; check `errors.log`. |

## 5. Safety

* The debugging port is **unauthenticated** — anyone who can reach `127.0.0.1:9222` can drive your browser (with your
  logins). Keep it bound to loopback, never expose it.
* The agent can act as you on any site you are logged into; treat a browsing task like giving someone your open
  browser. Confirm before anything that sends, buys or deletes.
* Secrets in pages are covered by the vault: a value found in an image is only checked by the auditor model, and an
  image produced while a secret is in play is dropped rather than shown.
