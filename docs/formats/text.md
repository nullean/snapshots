# Text

```csharp
report.Should().MatchSnapshot();
```

Plain text is compared line by line. A mismatch shows unified hunks with three lines of context around each change:

```text
Text did not match snapshot: tests/App.Tests/.snapshots/ReportTests/Renders.verified.txt
1 difference(s):
@@ line 1 @@
 <table>
-  <tr><td>{kibana}</td></tr>
+  <tr><td>{Kibana}</td></tr>
   <tr><td>beats</td></tr>
 </table>
```

Braces in the content are safe: the message is never used as a format string. Lines longer than 500 characters are
cut, so one minified line cannot flood a log.

Line endings are always normalised, so a checkout with CRLF endings compares the same as one with LF.
