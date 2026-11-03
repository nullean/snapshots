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
