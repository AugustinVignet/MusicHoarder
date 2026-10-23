using System.IO.Abstractions.TestingHelpers;
using MusicHoarder.Api.Library;
using MusicHoarder.Api.Options;
using MusicHoarder.Api.Persistence;

namespace MusicHoarder.Api.Tests.Library;

public class CaseCanonicalDestinationPathResolverTests
{
    private const string Root = "/dest-root";

    [Fact]
    public void Canonicalize_ReusesOnDiskSpellingOfExistingFolders()
    {
        var resolver = Create(new MockFileSystem(new Dictionary<string, MockFileData>
        {
            [Root + "/Morphine/1997 - B-Sides and Otherwise/01 - Old.mp3"] = new("x"),
        }));

        Assert.Equal(Root + "/Morphine/1997 - B-Sides and Otherwise/02 - New.mp3",
            resolver.Canonicalize(Root + "/Morphine/1997 - B-Sides And Otherwise/02 - New.mp3"));
    }

    [Fact]
    public void Canonicalize_KeepsExactMatchAndLeavesAmbiguousVariantsUnchanged()
    {
        var resolver = Create(new MockFileSystem(new Dictionary<string, MockFileData>
        {
            [Root + "/The Doors/1967 - The doors/a.mp3"] = new("x"),
            [Root + "/The Doors/1967 - The Doors/b.mp3"] = new("x"),
            [Root + "/The Doors/1971 - l.a. woman/c.mp3"] = new("x"),
            [Root + "/The Doors/1971 - L.A. WOMAN/d.mp3"] = new("x"),
        }));

        Assert.Equal(Root + "/The Doors/1967 - The Doors/x.mp3", resolver.Canonicalize(Root + "/The Doors/1967 - The Doors/x.mp3"));
        Assert.Equal(Root + "/The Doors/1971 - L.A. Woman/x.mp3", resolver.Canonicalize(Root + "/The Doors/1971 - L.A. Woman/x.mp3"));
    }

    [Fact]
    public void Canonicalize_IgnoresPathsOutsideTheDestinationRootAndMissingFolders()
    {
        var resolver = Create(new MockFileSystem(new Dictionary<string, MockFileData> { [Root + "/ABBA/x.mp3"] = new("x") }));

        Assert.Equal("/elsewhere/abba/x.mp3", resolver.Canonicalize("/elsewhere/abba/x.mp3"));
        Assert.Equal(Root + "/ABBA/1992 - Gold/01 - Dancing Queen.mp3", resolver.Canonicalize(Root + "/abba/1992 - Gold/01 - Dancing Queen.mp3"));
    }

    [Fact]
    public void ResolvePath_ReturnsTheCanonicalSpelling()
    {
        var resolver = Create(new MockFileSystem(new Dictionary<string, MockFileData>
        {
            [Root + "/Kanye West/2004 - the college dropout/02 - x.mp3"] = new("x"),
        }));
        var song = new SongMetadata
        {
            SourcePath = "/source/a.mp3", FileName = "a.mp3", Extension = ".mp3", FileSizeBytes = 1000, LastModifiedUtc = DateTime.UtcNow, IndexedAtUtc = DateTime.UtcNow, Artist = "Kanye West",
            Album = "The College Dropout", Title = "Through The Wire", Year = 2004, TrackNumber = 1,
        };

        Assert.Equal(Root + "/Kanye West/2004 - the college dropout/01 - Through The Wire.mp3", resolver.ResolvePath(song));
    }

    private static CaseCanonicalDestinationPathResolver Create(MockFileSystem files)
    {
        var options = Microsoft.Extensions.Options.Options.Create(new MusicEnricherOptions
        {
            SourceDirectory = "/source",
            DestinationDirectory = Root,
        });
        return new CaseCanonicalDestinationPathResolver(new DestinationPathResolver(options), options, files);
    }
}
