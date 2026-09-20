using WorldBuilder.Shared.Lib;

namespace WorldBuilder.Tests;

public sealed class RetailClientDatSpecTests {
    [Fact]
    public void LoadEmbedded_HasRetailFilesAndReferenceSeed() {
        var spec = RetailClientDatSpec.LoadEmbedded();

        Assert.True(spec.Version >= 1);
        Assert.NotEmpty(spec.Title);
        Assert.Contains("client_portal.dat", spec.RetailFiles);
        Assert.Contains("client_cell_1.dat", spec.RetailFiles);
        Assert.Contains("client_local_English.dat", spec.RetailFiles);
        Assert.NotNull(spec.ReferenceSeed);
        Assert.True(spec.ReferenceSeed!.Fingerprints.ContainsKey("client_portal.dat"));
    }

    [Fact]
    public void ParseHexId_AcceptsPrefixedAndBareHex() {
        Assert.Equal(0x232D400u, RetailClientDatSpec.ParseHexId("0x232D400"));
        Assert.Equal(0x232D400u, RetailClientDatSpec.ParseHexId("232D400"));
    }
}
