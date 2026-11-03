# Paths and options

## Paths

JSON and YAML differences are reported by a JSONPath-like path:

| Path | Means |
|---|---|
| `$` | the root |
| `$.name` | the `name` key |
| `$["key with.dots"]` | a key that is not a plain identifier |
| `$.items[2]` | the third item |
| `$doc[1].name` | in the second document of a YAML stream |
| `$[1].v (actual $[2].v)` | the verified path, and where the item sits in the actual tree |

## Ignore patterns

`TreeDiffOptions.IgnorePaths` takes paths with wildcards:

| Pattern | Ignores |
|---|---|
| `$.meta.generatedAt` | that key |
| `$.items[*].etag` | `etag` in every item |
| `$.config.*` | every key under `config` |
| `$..id` | every `id` at any depth |

## TreeDiffOptions

| Option | Default | Effect |
|---|---|---|
| `MapOrder` | `Insensitive` | `Significant` reports a different key order |
| `StrictNumbers` | `false` | compare numbers by kind and text, so `1` and `1.0` differ |
| `NullEqualsMissing` | `false` | a key with `null` equals the key being absent |
| `IgnorePaths` | none | see above |
| `CellCap` | 4,000,000 | arrays whose lengths multiply to more are aligned by position, not by LCS |

## SnapshotOptions

Per call: `Scrub(Func<string, string>)`, `Scrub(Regex, string)` and `IgnoreDefaultScrubbers`. See
[scrubbing](../formats/index.md#scrubbing-volatile-values).
