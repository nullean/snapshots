# Working with coding agents

The failure messages are written to be read by people and agents alike: plain text, paths relative to the
repository, capped, with the exact command to accept. An agent that changes output can see what it changed and how to
accept it without asking.

Accepting stays a decision. Add something like this to your `AGENTS.md` or `CLAUDE.md`:

```markdown
## Snapshot tests

Snapshot tests compare output with verified files under `tests/*/.snapshots/`. When one fails, the message shows the
differences and where the received file is.

- Read the difference first. Only accept it when the change is intended: `snap accept <key>`.
- Never accept with `--all` without reading every diff (`snap status`, then `snap diff <key>`).
- Always read `git diff` of the verified files before committing them, and say in the commit or PR which snapshots
  changed and why.
- `snap orphans` lists verified files no test uses any more, after a full test run.
```

## Browser journeys

An agent that explores a site with agent-browser can leave a [journey](../formats/browser.md) behind:

```markdown
## Browser journeys

- Journeys live in `tests/<Project>/journeys/*.journey`: one agent-browser command per line.
- Use `find role … --name`, `find label`, `find text` or CSS, never `@e` refs; they change between runs.
- Check a new or changed journey with `snap journey run <file> --base-url <url>`, then read the received transcript
  before accepting it with `snap accept <key>`.
```
