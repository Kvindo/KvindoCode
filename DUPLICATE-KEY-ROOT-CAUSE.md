# The duplicate-key error: root cause

**Reported:** `Unexpected error: An item with the same key has already been added. Key: head_limit (Parameter 'key')`
(2026-10-05 and again 2026-10-06, with a screenshot the second time). Unfixable from the transcript, because the
error text itself could not be selected or copied.

## What is actually going on

`JsonNode.Parse(string)` does **not** build a dictionary. `JsonObject` keeps the raw text and materialises its
dictionary lazily, on the first index / enumerate / `Count`. Measured on .NET 9:

| call | result |
|---|---|
| `JsonNode.Parse(dup)` then `ToJsonString()` | ok |
| `JsonNode.Parse(dup)` then `DeepClone()` | ok |
| `JsonNode.Parse(dup)` then `node["head_limit"]` | **throws** `ArgumentException … Key: head_limit (Parameter 'key')` |
| `JsonNode.Parse(dup)` then `foreach (k,v)` | **throws** — same |
| `JsonNode.Parse(dup)` then `node.Count` | **throws** — same |
| `JsonDocument.Parse(dup)` then `GetProperty` | ok (last value wins) |

So the parse succeeds, `ParseArgs` returns a perfectly good object, the tool runs, the turn continues — and the
exception surfaces **somewhere else entirely**, on the first lookup of that object. The stack therefore pointed
nowhere near the guilty call, which is why the first two attempts at this bug fixed the parse and changed nothing.

## The site that actually threw

`SecretRedactor.RedactJson` walks the argument tree:

```csharp
case JsonObject o:
    foreach (var (k, v) in o) co[k] = RedactJson(v);   // <-- first lookup materialises the dictionary
```

`MaskToolCalls` / `ProtectArguments` / `Mask(AgentEvent)` call it for every request. So a session whose tool
arguments repeated a key (`head_limit` twice in one Grep call) died on the **next** outbound request, with an
error that named `head_limit` and no relation to the request that carried it.

Proven by a test that fails against the old code:

```
KvindoCode.Tests.DuplicateJsonKeyTests.The_redactor_can_walk_arguments_that_repeat_a_key
```

## Fix

`Core/JsonText.cs` — parse through `JsonDocument` and build a fresh, fully materialised tree (a repeated key keeps
its **last** value), keeping integral numbers as `int` so `(int?)` argument reads still work. Every site that
touches model-written JSON now goes through it: `AgentSession` (arguments, masking, withheld args, todos),
`ClaudeStorage` (meta, leaf, persisted tool calls), `PlanReview`, `AuditingLlmClient`, `LlmClient`, `SecretAuditor`,
`ScoreStore`, `ModelCatalog`, `ScriptedLlmClient`, `BrowserSession`, `Cdp`, `Hooks`, `DocumentReaders`.
`RedactJson` also gained a last-ditch fallback: if it is ever handed a lazily-parsed object anyway, it re-parses
the serialised form instead of throwing.

## Why the first attempt looked fixed

`AgentSession.ParseArgs` was already rewritten to use `JsonDocument` (2026-10-06). That made the *tool* path safe
and the reproduction test passed — but every other `JsonNode.Parse` in the codebase still handed out lazy objects,
so the redactor kept throwing. The lesson is in the test now: the failing case is driven through
`AuditingLlmClient` with a real vault and a repeated-key argument, not through `ParseArgs` alone.

## Related regression found while fixing this

`ClaudeStorage.SaveMeta` read a timestamp with `(long)o["lastActivityAt"]`. Under `JsonNode.Parse` that value was
`JsonElement`-backed, so `(long)` worked; an eagerly materialised `int` makes the cast throw
("A value of type 'System.Int32' cannot be converted to a 'System.Int64'"). It reads the number through
`JsonText.Num` now, which no longer cares which integer width was boxed.

## Later regressions from the same change

Two more came out of routing provider JSON through `JsonText`, both in `ScoreStore` and both silent (benchmarks
disappeared from the model list, reported 2026-10-07): `TryGetPropertyValue` called on a `JsonNode` (the method is
only on `JsonObject`), and `(double)` casts on values that `JsonText` now boxes as `int`. Fixed by
`JsonText.Num`/`JsonText.Dbl` and a `JsonObject` check; `ScoreStoreTests` loads the real bundled `scores.json` so
neither can come back unnoticed. Details in the memory entry `kvindocode-score-store-and-jsontext-pitfalls`.
