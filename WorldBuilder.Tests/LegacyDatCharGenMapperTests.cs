using Acme.Dat;
using WorldBuilder.Shared.Lib;

namespace WorldBuilder.Tests;

public sealed class LegacyDatCharGenMapperTests {
    [Fact]
    public void LandBlockFileIdForCell_UsesHighWordSuffixes() {
        Assert.Equal(0xA9B0FFFFu, LegacyDatCharGenMapper.LandBlockFileIdForCell(0xA9B00014));
        Assert.Equal(0xA9B0FFFEu, LegacyDatCharGenMapper.LandBlockInfoFileIdForCell(0xA9B00014));
    }

    [Fact]
    public void CollectStartingAreaCellFileIds_IncludesParentLandblockIds() {
        string legacy = @"C:\Users\chris\OneDrive\Documents\ACME WorldBuilder\Projects\darkmaj\dats\base";
        if (!Directory.Exists(legacy)) {
            return;
        }

        using var reader = new LegacyDatReader(legacy);
        var ids = LegacyDatCharGenMapper.CollectStartingAreaCellFileIds(reader).ToHashSet();
        Assert.NotEmpty(ids);

        if (reader.TryGet<CharGen>(
                LegacyDatCharGenMapper.CharGenId,
                out var charGen)
            && charGen?.StartingAreas.Count > 0
            && charGen.StartingAreas[0].Locations.Count > 0) {
            uint cellId = charGen.StartingAreas[0].Locations[0].CellId;
            Assert.Contains(cellId, ids);
            Assert.Contains(LegacyDatCharGenMapper.LandBlockFileIdForCell(cellId), ids);
            Assert.Contains(LegacyDatCharGenMapper.LandBlockInfoFileIdForCell(cellId), ids);
        }
    }
}
