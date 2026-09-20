using WorldBuilder.Shared.Lib;
using Xunit;

namespace WorldBuilder.Tests {
    public class TerrainPalCodeTests {
        [Fact]
        public void GetPalCode_MatchesClientReference() {
            // Corners c0..c3 as (r1,t1)..(r4,t4) => same as ClientReference (r0,t0)..(r3,t3).
            uint fromRef = ClientReference.GetPalCode(1, 5, 0, 10, 2, 15, 3, 20);
            uint fromShared = TerrainPalCode.GetPalCode(1, 0, 2, 3, 5, 10, 15, 20);
            Assert.Equal(fromRef, fromShared);
        }

        [Fact]
        public void FourCornerCodes_RotationsMatchClientLayout() {
            Span<uint> codes = stackalloc uint[4];
            uint sizeBits = 1u << 28;
            TerrainPalCode.GetPalCodesForCell(1, 5, 0, 10, 2, 15, 3, 20, sizeBits, codes);

            Assert.NotEqual(codes[0], codes[1]);
            Assert.NotEqual(codes[0], codes[2]);
        }

        [Theory]
        [InlineData(100, 200, true)]
        [InlineData(100, 200, false)]
        public void GetBeginRotIndex_IsInRange(int x, int y, bool minimize) {
            Span<uint> codes = stackalloc uint[4] { 10, 20, 5, 30 };
            int ix = TerrainPalCode.GetBeginRotIndex(x, y, codes, minimize);
            Assert.InRange(ix, 0, 3);
        }

        [Fact]
        public void MinimizePicksSmallestCode() {
            Span<uint> codes = stackalloc uint[4] { 100, 20, 50, 30 };
            Assert.Equal(1, TerrainPalCode.GetBeginRotIndex(0, 0, codes, minimizePal: true));
        }

        [Fact]
        public void TextureSizeBits_EmbeddedInPalCode() {
            uint bits4 = TerrainPalCode.GetTextureSizeBits(isPalShifted: false);
            uint code = TerrainPalCode.GetPalCode(0, 0, 0, 0, 1, 2, 3, 4, bits4);
            Assert.Equal(4u, TerrainPalCode.GetTextureSizeFromPalCode(code));
        }

        [Fact]
        public void RotatedPalCodePath_CanDifferFromSingleMergedCode() {
            uint sizeBits = TerrainPalCode.GetTextureSizeBits(isPalShifted: false);
            Span<uint> palCodes = stackalloc uint[4];
            TerrainPalCode.GetPalCodesForCell(1, 5, 0, 10, 2, 15, 3, 20, sizeBits, palCodes);

            uint merged = TerrainPalCode.GetPalCode(1, 0, 2, 3, 5, 10, 15, 20, sizeBits);
            int beginRot = TerrainPalCode.GetBeginRotIndex(42, 17, palCodes, minimizePal: false);

            Assert.NotEqual(merged, palCodes[beginRot]);
        }

    }
}
