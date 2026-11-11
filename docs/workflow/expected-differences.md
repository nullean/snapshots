# Expected differences

Comparing a journey or a `.http` file against a verified snapshot says "this is what it was". Comparing it between two
sites, a production baseline and a preview, needs the other thing: "this is how they may differ". A `.differences` file
says it, in a few lines you check in next to the journey.

```
# journeys/shop.differences
include? known/shop.differences           # this preview's changes, which come and go

## snapshot -c -s nav                     # the section: a step's command, or a request as written
added   link "What's new"                 # the preview has a new link
removed link "Legacy*"
changed heading [level=1] "Elastic Docs" -> "Elastic Documentation"
ignore  navigation link /^v9\.\d+$/       # anything about these links

## search: GET {{host}}/api/search
added   body $.hits[*].highlight
removed header x-legacy
changed status 200 -> 404
maybe added body $..debug                 # may be absent on some runs
```

The file for `journeys/shop.journey` is `journeys/shop.differences`, and for `http/orders.http` it is
`http/orders.differences`. A missing file means no rules.

A file named `.differences` in a folder applies to every journey and `.http` file in that folder and below, from the
repository root down, the nearest last, and the file's own rules after them. It is how a whole folder of journeys shares
`ignore url` or a known banner without a line in each one, and how any journey can be compared with no edits at all. Rules in a
folder file can `include?` a file too.

## Reading a file

- A line is `[maybe] verb [target] [old -> new]`. A `#` starting a word, followed by a space, starts a comment;
  `#tags` inside a heading are not comments.
- `## heading` starts a section. Its heading is a glob (`*` matches anything) over a step's command or a request as
  written, and the rules below it apply to the sections it matches. Rules before the first `##` apply to every section.
- Values are `"quoted"` (a glob), `/regular expressions/` (matching anywhere in the text) or one bare word (a glob).
  `\"` and `\n` work in quotes, `\/` in a regular expression.
- `added` is something the target has and the baseline lacks, `removed` the reverse, `changed` a value that differs
  (`old -> new` are optional: without them any change of the target is expected) and `ignore` any difference at the target
  and everything below it.
- A rule with no target (`added`, `removed`) is about a whole section, so it needs a `##` above it.
- Parse errors are collected and reported together as `file:line: message`.

## What a target names

**Journeys.** `url` and `text` are a read's page URL and text. Anything else is an accessibility selector: a role, then
optionally a `"name"` or `/regex/` and `[attr]` or `[attr=value]` filters. Several selectors separated by spaces mean
"inside", as in CSS: `navigation link "Docs"` is a link named Docs somewhere in a navigation.

- `added link "x"` names the node that appeared. `added heading [selected]` is the attribute that appeared on a heading
  (an attribute that is on one side only has to be named).
- `changed heading "old" -> "new"` is the node whose name changed, found by its old or its new name. For an attribute or
  a value the same line works: `changed heading [level] 1 -> 2`.
- `ignore navigation` covers the navigation, everything in it, and any change to it.

**`.http` files.** `status`, `header NAME` (a glob, any case), `body [PATH]` (the path inside a JSON body, with `[*]`, `.*`
and `..key` as in ignored paths of a JSON snapshot), `text`, `binary` and `request`. `ignore body $.meta` covers
`$.meta` and everything inside it.

## Includes: forever and for now

```
include  //tests/shared/nav.differences   # must exist; a differences that last
include? known/shop.differences           # may not exist; the differences of one preview
```

An include goes before the first `##`. A plain path is relative to the file that includes it, `//path` to the root of
the git repository. Includes nest, and a loop is an error.

Everything is read through a file system scoped to the repository: an include cannot leave it, even as `include?`, and a
symbolic link is refused. Both are errors, not a quiet "not found".

The journey's own file holds the differences that are there for good. The optional include holds the ones of a given
preview: it can be checked in on the pull request that needs it and deleted when the change ships, or never checked in.
Moving a line from it into the journey's file, or into a shared one, makes it permanent.

## Rules that match nothing

A rule that explained no difference is **stale**, and a run with a stale rule fails, so a difference that shipped does
not stay excused. `maybe` opts a rule out. Rules from `include` and from a folder's `.differences` (shared by many files, so
one file not needing a rule says nothing) are exempt; rules in the file itself and in `include?` files are checked.

## Running it

`snap journey compare` and `snap http compare` run a file against two sites and apply these rules; see
[comparing two sites](snap.md#comparing-two-sites). The agent's loop is: compare, `--record journeys/known/shop.differences`,
read the lines it appended, run again with `--reuse-baseline` to confirm they pass.

## From the library

```csharp
var rules = RuleLoader.Load(fileSystem, RuleLoader.FindRepositoryRoot(fileSystem, folder), RuleLoader.SidecarPath(journeyFile));
var bound = ExpectedDifferences.Bind(rules, JourneyRuleTargets.Instance);   // HttpRuleTargets.Instance for .http
var result = ExpectedDifferences.Apply(TreeDiffer.Instance.Diff(baseline, target), baseline, target, bound, JourneyRuleTargets.Instance);
// result.Unexplained, result.Explained, result.Stale; result.IsClean when nothing is left
```

`JourneySnapshot.CompareAsync` and `HttpFileSnapshot.CompareAsync` run a file against both sites in a test and return a
`SiteComparisonResult` (`Passed`, `FailureMessage`, what each rule did). `ExpectedDifferences.Suggest` gives the rule lines that would explain a difference, and `ExpectedDifferences.Record`
appends them to an `include?` file (and to no other), under a heading per section, so an agent working through a preview
can mark what it has seen. `ExpectedDifferences.Prune` removes the stale rules of such a file.
