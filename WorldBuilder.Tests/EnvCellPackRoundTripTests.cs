using Acme.Dat;
using WorldBuilder.Shared.Lib;
using Xunit;

namespace WorldBuilder.Tests {
    public class EnvCellPackRoundTripTests {
        [Fact]
        public void Pack_Unpack_RoundTripsTypicalDungeonCell() {
            var original = new EnvCell {
                Id = 0x01D90100,
                Flags = (uint)(EnvCellFlags.HasStaticObjs | EnvCellFlags.HasRestrictionObj),
                EnvironmentId = 0x0D000001,
                CellStructure = 0x0002,
                Position = new Frame {
                    Origin = new System.Numerics.Vector3(10, 20, 30),
                    Orientation = System.Numerics.Quaternion.Identity
                },
                Surfaces = new List<uint> { 0x0800032A, 0x0800032B, 0x0800032C },
                CellPortals = new List<CellPortal> {
                    new() { PolygonId = 1, OtherCellId = 0x0200, OtherPortalId = 2, Flags = 0 }
                },
                VisibleCells = new List<ushort> { 0x0200, 0x0300 },
                StaticObjects = new List<Stab> {
                    new() { Id = 0x01D90200, Frame = new Frame() }
                },
                RestrictionObj = 0
            };

            byte[] packed = DatNativeRecords.Pack(original);
            Assert.True(packed.Length > 0);

            int checksum = PortalChecksum.CalcChecksum32(packed);
            Assert.NotEqual(0, checksum);

            var restored = DatNativeRecords.Unpack<EnvCell>(packed);

            Assert.Equal(original.Id, restored.Id);
            Assert.Equal(original.Flags, restored.Flags);
            Assert.Equal(original.EnvironmentId, restored.EnvironmentId);
            Assert.Equal(original.CellStructure, restored.CellStructure);
            Assert.Equal(original.Surfaces.Count, restored.Surfaces.Count);
            Assert.Equal(original.CellPortals.Count, restored.CellPortals.Count);
            Assert.Equal(original.VisibleCells.Count, restored.VisibleCells.Count);
            Assert.Equal(original.StaticObjects.Count, restored.StaticObjects.Count);
            Assert.Equal(original.CellPortals[0].PolygonId, restored.CellPortals[0].PolygonId);
            Assert.Equal(original.CellPortals[0].OtherCellId, restored.CellPortals[0].OtherCellId);
        }

        [Fact]
        public void PackSize_MatchesManualClientLayoutEstimate() {
            var cell = new EnvCell {
                Id = 0xAABB0100,
                Flags = (uint)EnvCellFlags.HasStaticObjs,
                EnvironmentId = 1,
                CellStructure = 2,
                Position = new Frame(),
                Surfaces = new List<uint> { 1, 2 },
                CellPortals = new List<CellPortal> {
                    new() { PolygonId = 5, OtherCellId = 0x0200, OtherPortalId = 6, Flags = 0 }
                },
                VisibleCells = new List<ushort> { 0x0200 },
                StaticObjects = new List<Stab> {
                    new() { Id = 0xAABB0200, Frame = new Frame() }
                }
            };

            int drwSize = DatNativeRecords.Pack(cell).Length;

            int nSurf = cell.Surfaces.Count;
            int nPort = cell.CellPortals.Count;
            int nVis = cell.VisibleCells.Count;
            int nStab = cell.StaticObjects.Count;
            int expected = 4 + 4 + 4 + 1 + 1 + 2 + (2 * nSurf) + 2 + 2 + 28
                + (8 * nPort) + (2 * nVis) + 4 + (32 * nStab);

            Assert.InRange(drwSize, expected - 4, expected + 8);
        }

        [Fact]
        public void AfterDeserialize_HydratesNestedCellStructGeometry() {
            var env = new Acme.Dat.Environment {
                Id = 0x0D000001,
                CellsEntries = [
                    new MapEntry<uint, CellStruct> {
                        Key = 2,
                        Value = new CellStruct {
                            VertexType = (int)VertexType.CSWVertexType,
                            RenderVerticesEntries = [
                                new MapEntry<ushort, GfxVertex> {
                                    Key = 0,
                                    Value = new GfxVertex {
                                        OriginArray = [1, 2, 3],
                                        NormalArray = [0, 0, 1],
                                        Uvs = [[0.25f, 0.75f]],
                                    },
                                },
                            ],
                            PolygonsEntries = [
                                new MapEntry<uint, Polygon> {
                                    Key = 7,
                                    Value = new Polygon {
                                        VertexIds = [0, 1, 2],
                                        PosSurface = 0,
                                    },
                                },
                            ],
                        },
                    },
                ],
            };

            env.AfterDeserialize();

            Assert.True(env.Cells.TryGetValue(2, out var cell));
            Assert.True(cell.Polygons.ContainsKey(7));
            Assert.Equal(3, cell.Polygons[7].VertexIds.Count);
            Assert.True(cell.VertexArray.Vertices.TryGetValue(0, out var vertex));
            Assert.Equal(1, vertex.Origin.X);
            Assert.Equal(0.25f, vertex.UVs[0].U);
        }
    }
}
