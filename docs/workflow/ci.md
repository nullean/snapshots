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
