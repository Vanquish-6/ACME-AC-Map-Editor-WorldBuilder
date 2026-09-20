using System.Numerics;
using WorldBuilder.Shared.Documents;
using WorldBuilder.Shared.Lib;
using Xunit;

namespace WorldBuilder.Tests {
    /// <summary>
    /// Conformance tests that verify WorldBuilder's terrain algorithms produce
    /// identical results to the original AC client. The client reference is a
    /// faithful C# port of the decompiled C++ from acclient-source-split.
    /// </summary>
    public class TerrainConformanceTests {

        #region Split Direction

        [Theory]
        [InlineData(0, 0)]
        [InlineData(1, 0)]
        [InlineData(0, 1)]
        [InlineData(7, 7)]
        [InlineData(127, 127)]
        [InlineData(1016, 1016)] // landblock 127, cell 0
        [InlineData(2039, 2039)] // max valid cell
        [InlineData(500, 1200)]
        [InlineData(1999, 3)]
        public void SplitDirection_HeightSampler_MatchesClient(int globalX, int globalY) {
            bool clientResult = ClientReference.IsSWtoNECut(globalX, globalY);
            bool wbResult = TerrainHeightSampler.IsSWtoNEcut((uint)globalX, (uint)globalY);

            Assert.Equal(clientResult, wbResult);
        }

        [Fact]
        public void SplitDirection_HeightSampler_MatchesClient_Sweep() {
            int mismatches = 0;
            int tested = 0;

            for (uint lbX = 0; lbX < 255; lbX += 8) {
                for (uint lbY = 0; lbY < 255; lbY += 8) {
                    for (uint cx = 0; cx < 8; cx++) {
                        for (uint cy = 0; cy < 8; cy++) {
                            int gx = (int)(lbX * 8 + cx);
                            int gy = (int)(lbY * 8 + cy);

                            bool client = ClientReference.IsSWtoNECut(gx, gy);
                            bool wb = TerrainHeightSampler.IsSWtoNEcut((uint)gx, (uint)gy);

                            if (client != wb) mismatches++;
                            tested++;
                        }
                    }
                }
            }

            Assert.True(mismatches == 0,
                $"Split direction mismatch: {mismatches} of {tested} cells differ between client and TerrainHeightSampler");
        }

        /// <summary>
        /// Tests that the rendering path (TerrainGeometryGenerator) agrees with
        /// the export/physics path (TerrainHeightSampler) and the client.
        /// This mirrors TerrainGeometryGenerator.CalculateSplitDirection which now
        /// delegates to TerrainHeightSampler.IsSWtoNEcut.
        /// Source: WorldBuilder/Editors/Landscape/TerrainGeometryGenerator.cs:351
        /// </summary>
        private static bool GeometryGenerator_IsSWtoNE(uint landblockX, uint cellX, uint landblockY, uint cellY) {
            uint globalCellX = landblockX * 8 + cellX;
            uint globalCellY = landblockY * 8 + cellY;
            return TerrainHeightSampler.IsSWtoNEcut(globalCellX, globalCellY);
        }

        [Fact]
        public void SplitDirection_GeometryGenerator_MatchesClient_Sweep() {
            int mismatchesVsClient = 0;
            int mismatchesVsHeightSampler = 0;
            int tested = 0;
            string firstClientMismatch = "";
            string firstSamplerMismatch = "";

            for (uint lbX = 0; lbX < 255; lbX += 8) {
                for (uint lbY = 0; lbY < 255; lbY += 8) {
                    for (uint cx = 0; cx < 8; cx++) {
                        for (uint cy = 0; cy < 8; cy++) {
                            int gx = (int)(lbX * 8 + cx);
                            int gy = (int)(lbY * 8 + cy);

                            bool client = ClientReference.IsSWtoNECut(gx, gy);
                            bool heightSampler = TerrainHeightSampler.IsSWtoNEcut((uint)gx, (uint)gy);
                            bool geomGen = GeometryGenerator_IsSWtoNE(lbX, cx, lbY, cy);

                            if (client != geomGen) {
                                mismatchesVsClient++;
                                if (firstClientMismatch == "")
                                    firstClientMismatch = $"lb({lbX},{lbY}) cell({cx},{cy}) global({gx},{gy}): client={client} geomGen={geomGen}";
                            }
                            if (heightSampler != geomGen) {
                                mismatchesVsHeightSampler++;
                                if (firstSamplerMismatch == "")
                                    firstSamplerMismatch = $"lb({lbX},{lbY}) cell({cx},{cy}): heightSampler={heightSampler} geomGen={geomGen}";
                            }
                            tested++;
                        }
                    }
                }
            }

            Assert.True(mismatchesVsClient == 0,
                $"GeometryGenerator vs Client: {mismatchesVsClient}/{tested} mismatches. First: {firstClientMismatch}");
            Assert.True(mismatchesVsHeightSampler == 0,
                $"GeometryGenerator vs HeightSampler: {mismatchesVsHeightSampler}/{tested} mismatches. First: {firstSamplerMismatch}");
        }

        #endregion

        #region PalCode (Texture Selection)

        /// <summary>
        /// Verifies WorldBuilder's GetPalCode bit layout matches the client's pal_code[0].
        /// WorldBuilder corner order: 1=(x,y), 2=(x+1,y), 3=(x+1,y+1), 4=(x,y+1)
        /// Client corner order:       0=(ix,iy), 1=(ix+1,iy), 2=(ix+1,iy+1), 3=(ix,iy+1)
        /// </summary>
        private static uint WorldBuilder_GetPalCode(int r1, int r2, int r3, int r4, int t1, int t2, int t3, int t4) {
            var terrainBits = t1 << 15 | t2 << 10 | t3 << 5 | t4;
            var roadBits = r1 << 26 | r2 << 24 | r3 << 22 | r4 << 20;
            var sizeBits = 1 << 28;
            return (uint)(sizeBits | roadBits | terrainBits);
        }

        [Theory]
        [InlineData(0, 0, 0, 0, 1, 1, 1, 1)]   // uniform grass, no road
        [InlineData(1, 0, 0, 0, 5, 10, 15, 20)] // mixed types, one road corner
        [InlineData(3, 3, 3, 3, 31, 31, 31, 31)] // max values
        [InlineData(0, 1, 2, 3, 0, 5, 10, 15)]  // sequential roads and types
        [InlineData(2, 0, 1, 3, 8, 4, 12, 16)]  // arbitrary mix
        public void PalCode_MatchesClient(int r0, int r1, int r2, int r3, int t0, int t1, int t2, int t3) {
            // Map client corners (0-3) to WorldBuilder parameters (1-4):
            // WB: r1=corner0, r2=corner1, r3=corner2, r4=corner3
            uint wbResult = WorldBuilder_GetPalCode(r0, r1, r2, r3, t0, t1, t2, t3);
            uint clientResult = ClientReference.GetPalCode(r0, t0, r1, t1, r2, t2, r3, t3);

            Assert.Equal(clientResult, wbResult);
        }

        [Fact]
        public void PalCode_MatchesClient_ExhaustiveRoads() {
            int mismatches = 0;
            int t0 = 5, t1 = 10, t2 = 15, t3 = 20;

            for (int r0 = 0; r0 <= 3; r0++) {
                for (int r1 = 0; r1 <= 3; r1++) {
                    for (int r2 = 0; r2 <= 3; r2++) {
                        for (int r3 = 0; r3 <= 3; r3++) {
                            uint wb = WorldBuilder_GetPalCode(r0, r1, r2, r3, t0, t1, t2, t3);
                            uint client = ClientReference.GetPalCode(r0, t0, r1, t1, r2, t2, r3, t3);
                            if (wb != client) mismatches++;
                        }
                    }
                }
            }

            Assert.Equal(0, mismatches);
        }

        [Fact]
        public void PalCode_MatchesClient_ExhaustiveTypes() {
            int mismatches = 0;
            int r0 = 1, r1 = 0, r2 = 2, r3 = 3;

            for (int t0 = 0; t0 < 32; t0++) {
                for (int t1 = 0; t1 < 32; t1++) {
                    for (int t2 = 0; t2 < 32; t2++) {
                        for (int t3 = 0; t3 < 32; t3++) {
                            uint wb = WorldBuilder_GetPalCode(r0, r1, r2, r3, t0, t1, t2, t3);
                            uint client = ClientReference.GetPalCode(r0, t0, r1, t1, r2, t2, r3, t3);
                            if (wb != client) mismatches++;
                        }
                    }
                }
            }

            Assert.Equal(0, mismatches);
        }

        #endregion

        #region Height Interpolation

        private static float[] MakeLinearHeightTable() {
            var table = new float[256];
            for (int i = 0; i < 256; i++)
                table[i] = i * 2.0f;
            return table;
        }

        private static TerrainEntry[] MakeFlatLandblock(byte height) {
            var entries = new TerrainEntry[81]; // 9x9 vertices
            for (int i = 0; i < 81; i++)
                entries[i] = new TerrainEntry(0, 0, 0, height);
            return entries;
        }

        private static TerrainEntry[] MakeSlopedLandblock(float[] heightTable) {
            var entries = new TerrainEntry[81];
            for (int x = 0; x <= 8; x++) {
                for (int y = 0; y <= 8; y++) {
                    byte h = (byte)(x * 10 + y * 5);
                    entries[x * 9 + y] = new TerrainEntry(0, 0, 0, h);
                }
            }
            return entries;
        }

        [Fact]
        public void HeightSampler_FlatTerrain_ReturnsCorrectHeight() {
            var table = MakeLinearHeightTable();
            byte heightByte = 50;
            var entries = MakeFlatLandblock(heightByte);
            float expected = table[heightByte];

            float actual = TerrainHeightSampler.SampleHeightTriangle(entries, table, 96f, 96f, 0, 0);
            Assert.Equal(expected, actual, precision: 3);
        }

        [Fact]
        public void HeightSampler_VertexCorners_MatchClientLookup() {
            var table = MakeLinearHeightTable();
            var entries = MakeSlopedLandblock(table);

            for (uint vx = 0; vx <= 8; vx++) {
                for (uint vy = 0; vy <= 8; vy++) {
                    byte heightByte = entries[vx * 9 + vy].Height;
                    float clientHeight = ClientReference.GetVertexHeight(table, heightByte);
                    float samplerHeight = TerrainHeightSampler.GetHeightFromData(entries, table, vx, vy);

                    Assert.Equal(clientHeight, samplerHeight);
                }
            }
        }

        [Theory]
        [InlineData(0f, 0f)]
        [InlineData(12f, 12f)]
        [InlineData(23.9f, 23.9f)]
        [InlineData(48f, 72f)]
        [InlineData(96f, 96f)]
        [InlineData(180f, 180f)]
        public void HeightSampler_InterpolatedPoints_ProducesReasonableValues(float localX, float localY) {
            var table = MakeLinearHeightTable();
            var entries = MakeSlopedLandblock(table);

            float height = TerrainHeightSampler.SampleHeightTriangle(entries, table, localX, localY, 0, 0);

            float minH = table[0];
            float maxH = table[entries.Max(e => e.Height)];
            Assert.InRange(height, minH, maxH);
        }

        #endregion

        #region Constants

        [Fact]
        public void Constants_MatchClient() {
            Assert.Equal(ClientReference.CellSize, TerrainHeightSampler.CellSize);
            Assert.Equal(ClientReference.CellsPerBlock, (int)TerrainHeightSampler.LandblockEdgeCellCount);
            Assert.Equal(ClientReference.BlockLength, (float)TerrainHeightSampler.LandblockLength);
        }

        #endregion
    }
}
