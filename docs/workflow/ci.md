# Snapshots in CI

With the `CI` environment variable set, as GitHub Actions, Azure Pipelines, GitLab and most other CI systems do,
snapshots are never accepted. A missing verified file fails the test, and `SNAPSHOT_ACCEPT=1` is ignored with a note
in the failure message:

```text
SNAPSHOT_ACCEPT is ignored because CI is set: snapshots are never accepted in CI.
```

## Getting the received files out of CI

Received files go to your artifacts folder when the build uses one (`UseArtifactsOutput`; the folder is `artifacts/`
unless `ArtifactsPath` says otherwise), so uploading it is enough:

```yaml
- uses: actions/upload-artifact@v7
  if: failure()
  with:
    name: received-snapshots
    path: artifacts/.snapshots/received/
```

Download that into the same place locally and `snap review` picks it up.

Without an artifacts folder they are in each test project's `obj/snapshots/received/`.

## Line endings

Snapshots are compared after normalising line endings, so a Windows checkout with CRLF passes. To keep the working
tree consistent too, add this to `.gitattributes`:

```text
*.verified.* text eol=lf
```

## Windows

Three things differ on a Windows runner, and only the first is handled for you:

- **Snapshot text.** Line endings are normalised before comparing (above), so a CRLF checkout of a verified file passes.
- **Text your test reads from source.** A raw string literal in a `.cs` file takes that file's line endings, so on a Windows checkout with
  `core.autocrlf` a fixture written inline is CRLF. The snapshot of what you produce from it is still normalised, but the fixture itself is
  not: anything that depends on its exact text, such as character offsets in a parser's error message, differs from Linux and macOS. Keep source files LF:

  ```text
  *.cs text eol=lf
  ```

  (A multi-line string passed as a test argument is read as `\n` for the file-name hash, so cases keep their names.)
- **Paths.** Absolute paths, drive letters from an in-memory file system and JSON-escaped backslashes are for a scrubber:
  `Scrubbers.Path(root, "{repo}")` and `Scrubbers.TempDirectory()`, or `Scrubbers.Path("C:/docs", "/docs")` for a mock file system's
  `C:\docs\...`. See [scrubbing](../formats/index.md#scrubbing-volatile-values).
