using System.IO.Abstractions.TestingHelpers;
using AwesomeAssertions;

namespace Nullean.Snapshots.Tests;

/// <summary>The <c># @tags</c> of a journey or <c>.http</c> file's header, read without a parser, for selecting test cases.</summary>
public class SnapshotFileTagsTests
{
	private static IReadOnlyList<string> Tags(string text)
	{
		var fs = new MockFileSystem();
		var file = MockUnixSupport.Path(@"c:\repo\file.txt");
		fs.AddFile(file, new(text));
		return SnapshotFile.ReadTags(fs, file);
	}

	[Test]
	public void Journey_TagsLinesInTheHeader_AreRead() =>
		Tags("# a note\n# @tags navigation smoke\n# @tags more\n# @requires x\nopen /\n").Should().Equal("navigation", "smoke", "more");

	[Test]
	public void Journey_TagsAfterTheFirstStep_AreNotRead() =>
		Tags("# @tags one\nopen /\n# @tags two\n").Should().Equal("one");

	[Test]
	public void Http_TagsBeforeVariablesAndTheFirstRequest_AreRead() =>
		Tags("# @tags mcp\n@host = https://example.test\n// @tags smoke\n\n### health\nGET {{host}}/\n# @tags late\n").Should().Equal("mcp", "smoke");

	[Test]
	public void Http_TagsInALaterBlock_AreNotRead() =>
		Tags("GET https://example.test/\n\n###\n# @tags late\nGET https://example.test/two\n").Should().BeEmpty();

	[Test]
	public void Http_TagsAfterTheFirstSeparator_AreNotRead() =>
		Tags("# @tags first\n\n### health\n# @tags late\nGET https://example.test/\n").Should().Equal("first");

	[Test]
	public void InvalidAndDuplicateNames_AreLeftOut() =>
		Tags("# @tags Bad ok ok good.one a_b -x\nopen /\n").Should().Equal("ok", "good.one", "a_b");

	[Test]
	public void MissingFile_HasNoTags() =>
		SnapshotFile.ReadTags(new MockFileSystem(), MockUnixSupport.Path(@"c:\repo\nope.journey")).Should().BeEmpty();

	[Test]
	public void FileWithoutDirectives_HasNoTags() =>
		Tags("# just a comment\nopen /\n").Should().BeEmpty();
}
