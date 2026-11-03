# Your first snapshot

```csharp
using AwesomeAssertions;
using Nullean.Snapshots;

public class GreetingTests
{
	[Test]
	public void Greets() =>
		Greeter.Greet("world").Should().MatchSnapshot();
}
```

## The first run fails

There is no verified file yet, so the test fails and says where it put what it got:

```text
Text snapshot is new: no verified file at tests/App.Tests/.snapshots/GreetingTests/Greets.verified.txt
Received: .artifacts/.snapshots/received/App.Tests/GreetingTests/Greets.received.txt
Accept:   snap accept App.Tests/GreetingTests/Greets   (or: dnx Nullean.Snapshots.Cli accept App.Tests/GreetingTests/Greets)
```

## Accept it

Read the received file, then accept it:

```sh
snap diff Greets       # what would change
snap accept Greets     # write it as verified, delete the received file
```

A key can be given in full, as any unique suffix (`Greets`, `GreetingTests/Greets`) or as a glob
(`GreetingTests/*`). Or walk through everything pending at once with `snap review`.

You can also accept while running tests, locally only:

```sh
SNAPSHOT_ACCEPT=1 dotnet test --project tests/App.Tests --treenode-filter "/App.Tests/App.Tests/GreetingTests/Greets"
```

## Commit the verified file

`tests/App.Tests/.snapshots/GreetingTests/Greets.verified.txt` is now part of your tests. Review its diff like code:
when a later change alters the output, the test fails, the received file shows the new output, and accepting it is a
decision you make, with the change in front of you.

## More than one snapshot per test

Name them, or the second call fails because both would use the same file:

```csharp
html.Should().MatchHtmlSnapshot(name: "header");
json.Should().MatchJsonSnapshot(name: "payload");
```
