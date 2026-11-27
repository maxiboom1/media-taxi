using System.Text;
using MediaTaxi.Configuration;
using MediaTaxi.Storage;

namespace MediaTaxi.Tests;

public sealed class ReviewRegressionTests
{
    [Theory]
    [InlineData("<AbekasClipMeta1><UUID>broken</AbekasClipMeta1>")]
    [InlineData("<AbekasClipMeta1><UUID>not-a-uuid</UUID></AbekasClipMeta1>")]
    [InlineData("<AbekasClipMeta1></AbekasClipMeta1>")]
    [InlineData("foreign media without an Abekas header")]
    [InlineData("<!DOCTYPE AbekasClipMeta1 [<!ENTITY owner SYSTEM 'file:///missing'>]><AbekasClipMeta1><UUID>&owner;</UUID></AbekasClipMeta1>")]
    public void UntrustedOrMalformedClipHeaderIsNeverRecognizedAsOwned(string xml)
    {
        using var temporary = new TemporaryFolder();
        var clip = Path.Combine(temporary.Path, "foreign.clip");
        var bytes = new byte[32768]; Encoding.UTF8.GetBytes(xml).CopyTo(bytes, 0);
        File.WriteAllBytes(clip, bytes);
        Assert.False(FileOperations.ClipOwned(clip, Guid.NewGuid().ToString("N")));
    }

    [Fact]
    public void TruncatedHeaderOrInvalidExpectedOwnerCannotEscapeOwnershipCheck()
    {
        using var temporary = new TemporaryFolder();
        var clip = Path.Combine(temporary.Path, "short.clip");
        File.WriteAllText(clip, "<AbekasClipMeta1>");
        Assert.False(FileOperations.ClipOwned(clip, Guid.NewGuid().ToString("N")));
        var owner = Guid.NewGuid();
        var bytes = new byte[32768];
        Encoding.UTF8.GetBytes($"<AbekasClipMeta1><UUID>{owner:D}</UUID></AbekasClipMeta1>").CopyTo(bytes, 0);
        File.WriteAllBytes(clip, bytes);
        Assert.True(FileOperations.ClipOwned(clip, owner.ToString("N")));
        Assert.False(FileOperations.ClipOwned(clip, "not-a-job-id"));
        Assert.False(FileOperations.ClipOwned(clip, Guid.NewGuid().ToString("N")));
    }

    [Fact]
    public void FilesystemRootIsAncestorAndCannotBeConfiguredAsOverlappingOutput()
    {
        using var temporary = new TemporaryFolder();
        var root = Path.GetPathRoot(temporary.Path)!;
        Assert.True(OptionsLoader.Within(temporary.Path, root));
        Assert.False(OptionsLoader.Within(root, root));
        var options = new TaxiOptions
        {
            Folders = new()
            {
                Watch = Path.Combine(temporary.Path, "watch"), Finished = Path.Combine(temporary.Path, "finished"),
                Failed = Path.Combine(temporary.Path, "failed"), Proxy = root,
                Work = Path.Combine(temporary.Path, "work"), State = Path.Combine(temporary.Path, "state")
            },
            Destinations = [new() { Id = "one", Path = Path.Combine(temporary.Path, "destination") }]
        };
        var error = Assert.Throws<InvalidDataException>(() => OptionsLoader.Validate(options));
        Assert.Contains("overlap", error.Message, StringComparison.OrdinalIgnoreCase);
    }
}
