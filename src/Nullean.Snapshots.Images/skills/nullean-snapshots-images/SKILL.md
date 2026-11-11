---
name: nullean-snapshots-images
description: >-
  Use when a C# test needs to snapshot a rendered PNG, such as a screenshot or a generated chart, and fail when it
  visibly changes. Nullean.Snapshots.Images adds MatchImageSnapshot for byte arrays and streams, compared by pixels
  with a perceptual threshold and anti-aliasing left out, and writes a diff image on a mismatch.
---

# Nullean.Snapshots.Images

Snapshot PNG output and compare decoded pixels, not bytes, so encoder settings and metadata never fail a test. It
only reads PNG; for HTML or text output snapshot the source instead, which diffs far better.

## The calls you need

`using Nullean.Snapshots.Images;`

```csharp
byte[] png = RenderChart();
png.Should().MatchImageSnapshot();                     // .snapshots/{dir}/{Class}/{Method}.verified.png
png.Should().MatchImageSnapshot(name: "dark");         // a second image in the same test
```

```csharp
stream.Position = 0;
stream.Should().MatchImageSnapshot();
```

Allow some noise (defaults: `Threshold` 0.1, `IncludeAntiAliasing` false, nothing else allowed):

```csharp
var tolerant = new ImageSnapshotOptions { MaxDifferentRatio = 0.001, MaxDifferentPixels = 50 };
png.Should().MatchImageSnapshot(tolerant);
```

## Gotchas

- The bytes must be a PNG. JPEG, an empty array or a truncated file throws `FormatException`; it does not fail the
  assertion.
- A stream is read from its current position. After writing to a `MemoryStream`, set `Position = 0` first.
- `Threshold` and `MaxDifferentRatio` must be 0 to 1 and `MaxDifferentPixels` not negative, or the call throws
  `ArgumentOutOfRangeException`. With both allowances set, the larger wins.
- A different width or height fails at once, with no diff image. Pin the viewport and device scale.
- On a mismatch the received image and `{name}.received-diff.png` are written in the received folder, next to each
  other. Upload that folder from CI to see them.
- There is no `SnapshotOptions` parameter: images are not scrubbed.
- Raising `Threshold` also hides real colour changes. Prefer `MaxDifferentRatio` for noisy renders.
- Two image snapshots in one test need distinct `name` values, or the second throws `InvalidOperationException`.

Docs: https://snapshots.nullean.net/formats/images
