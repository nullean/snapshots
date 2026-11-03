# Images

```csharp
using Nullean.Snapshots.Images;

byte[] png = await page.ScreenshotAsync();
png.Should().MatchImageSnapshot();

using var stream = File.OpenRead(path);
stream.Should().MatchImageSnapshot(new ImageSnapshotOptions { MaxDifferentRatio = 0.001 });
```

Image snapshots are PNG files, stored exactly as given and compared **by their pixels**, so they hold up when the
same picture is rendered on another machine:

- **Pixels, not bytes.** Both images are decoded to RGBA and every metadata chunk is ignored, so a different encoder,
  compression level or `tEXt`/`gAMA`/`iCCP` chunk never fails a test. A match never rewrites the verified file.
- **Differences measured as people see them.** Each pixel's difference is a distance in YIQ colour space, which
  weighs brightness the way the eye does. A pixel differs when that distance exceeds `Threshold`.
- **Anti-aliasing left out.** Font and edge rendering differ between operating systems and graphics stacks mostly in
  the grey pixels along edges. A differing pixel that sits between a darker and a brighter neighbour, each in a flat
  area in both images, is classed as anti-aliasing and not counted. An edge that moves one pixel therefore passes;
  one that moves two fails.

On a mismatch the message gives the count, the share and the regions that differ, and points at the received image
and a diff image:

```text
Image did not match snapshot: tests/App.Tests/.snapshots/ChartTests/Draws.verified.png
1,204 of 480,000 pixels differ (0.25%), 0 allowed; 36 anti-aliased pixel(s) left out
2 difference(s):
x 120-184, y 40-58 (65x19): 1,180 pixel(s)
x 610-613, y 402-405 (4x4): 24 pixel(s)
Received: .artifacts/.snapshots/received/App.Tests/ChartTests/Draws.received.png
Diff:     .artifacts/.snapshots/received/App.Tests/ChartTests/Draws.received-diff.png
```

The diff image is the verified image faded to grey, with differing pixels in red and anti-aliasing in yellow. Both
images sit in the received folder, so a CI job that uploads `artifacts/` keeps them. A different width or height
fails without a pixel comparison and without a diff image.

## Options

| Option | Default | Means |
|---|---|---|
| `Threshold` | `0.1` | how different a pixel may look, from 0 (any change) to 1 (black equals white) |
| `IncludeAntiAliasing` | `false` | count anti-aliasing pixels as differences |
| `MaxDifferentPixels` | `0` | how many pixels may differ |
| `MaxDifferentRatio` | `0` | the share of pixels that may differ; with both set, the larger allowance applies |

Start with the defaults. Raise `MaxDifferentRatio` for a rendering that is noisy by nature (gradients, video
frames); raise `Threshold` only for colour drift, as it also hides real colour changes. Each is a trade between
noise and what the test can catch.

## Rendering the same everywhere

Tolerance absorbs rasteriser noise, but not a different font: a fallback font reflows the text and moves everything
after it. Keep renders comparable at the source:

- Render in a pinned container image (the Playwright image for its version) locally and in CI.
- Ship the fonts the page uses with the test instead of relying on system fonts.
- Fix the viewport and use `deviceScaleFactor: 1`.
- In headless Chromium, `--disable-gpu` and `--font-render-hinting=none` remove two sources of drift.
- Turn off animations, transitions and the text caret before taking the screenshot.

## PNG support

The package has its own PNG decoder, in managed code with no dependencies, so every machine decodes a file to the
same pixels. It reads every colour type, bit depth and interlacing the PNG specification allows, including palettes
and `tRNS` transparency; 16-bit samples are reduced to their high byte. Colour management (`gAMA`, `cHRM`, `sRGB`,
`iCCP`) is not applied. Images up to 8192 × 8192 pixels are accepted. Other formats are not: convert them to PNG
first. A value that is not a valid PNG fails the assertion before anything is written.

Git detects PNG files as binary, but a rule such as `*.verified.* text` overrides that and lets git rewrite line
endings inside them. If your `.gitattributes` has one, mark images binary after it:

```text
*.verified.png binary
```

`snap` treats image snapshots as binary: `snap accept` copies them byte for byte, and `snap diff` and `snap review`
show their paths, sizes and the diff image instead of a line diff.
