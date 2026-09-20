using System.Numerics;
using Acme.Dat;
using WorldBuilder.Shared.Lib;

namespace WorldBuilder.Tests {
    public class LegacyDatSupportTests {
        [Fact]
        public void DetectLegacyDirectory_SelectsReadOnlyLegacyMode() {
            string dir = Path.Combine(Path.GetTempPath(), $"acme-legacy-{Guid.NewGuid():N}");
            Directory.CreateDirectory(dir);

            try {
                File.WriteAllBytes(Path.Combine(dir, "cell.dat"), Array.Empty<byte>());
                File.WriteAllBytes(Path.Combine(dir, "portal.dat"), Array.Empty<byte>());

                bool ok = DatProjectModeInfo.TryDetectFromDirectory(dir, out var mode, out var summary, out var errors);

                Assert.True(ok);
                Assert.Equal(DatProjectMode.LegacyPreTod, mode);
                Assert.Empty(errors);
                Assert.Contains("read-only", summary, StringComparison.OrdinalIgnoreCase);
            }
            finally {
                Directory.Delete(dir, recursive: true);
            }
        }

        [Fact]
        public void DecodeLandBlock_ReadsLegacyTerrainCells() {
            var buffer = new byte[4 + 4 + (81 * 2) + 81 + 4];
            var writer = new DatBinWriter(buffer);

            writer.WriteUInt32(0x1234FFFF);
            writer.WriteUInt32(1);
            writer.WriteUInt16((ushort)(1 | (5 << 2) | (3 << 11)));
            for (int i = 1; i < 81; i++) {
                writer.WriteUInt16(0);
            }
            writer.WriteByte(42);
            for (int i = 1; i < 81; i++) {
                writer.WriteByte(0);
            }
            writer.Align(4);

            bool ok = LegacyDatDecoders.TryDecodeLandBlock(
                buffer.AsSpan(0, writer.Offset).ToArray(),
                LegacyDatVersion.DarkMajesty,
                iteration: 67,
                out var landBlock);

            Assert.True(ok);
            Assert.NotNull(landBlock);
            Assert.True(landBlock.HasObjects);
            Assert.Equal((byte)1, new TerrainInfo(landBlock.Terrain[0]).Road);
            Assert.Equal((TerrainTextureType)5, new TerrainInfo(landBlock.Terrain[0]).Type);
            Assert.Equal((byte)3, new TerrainInfo(landBlock.Terrain[0]).Scenery);
            Assert.Equal((byte)42, landBlock.Height[0]);
        }

        [Fact]
        public void DecodeLandBlockInfo_ReadsObjectsBuildingsAndRestrictions() {
            var buffer = new byte[1024];
            var writer = new DatBinWriter(buffer);

            var stab = new Stab {
                Id = 0x02000123,
                Frame = new Frame {
                    Origin = new Vector3(1, 2, 3),
                    Orientation = Quaternion.Identity,
                }
            };
            var building = new BuildingInfo {
                ModelId = 0x02000456,
                Frame = new Frame {
                    Origin = new Vector3(4, 5, 6),
                    Orientation = Quaternion.Identity,
                },
                NumLeaves = 2,
                Portals = new List<BuildingPortal> {
                    new() {
                        OtherCellId = 0x0100,
                        OtherPortalId = 0x0001,
                        StabList = new List<ushort> { 0x0001 }
                    }
                },
            };

            writer.WriteUInt32(0x1234FFFE);
            writer.WriteUInt32(7);
            writer.WriteUInt32(1);
            writer.WriteItem(stab);
            writer.WriteUInt16(1);
            writer.WriteUInt16(1);
            writer.WriteItem(building);
            writer.Align(4);
            writer.WriteUInt16(1);
            writer.WriteUInt16(8);
            writer.WriteUInt32(0xABCDEF01);
            writer.WriteUInt32(0x00000123);
            writer.Align(4);

            bool ok = LegacyDatDecoders.TryDecodeLandBlockInfo(
                buffer.AsSpan(0, writer.Offset).ToArray(),
                LegacyDatVersion.DarkMajesty,
                out var landBlockInfo);

            Assert.True(ok);
            Assert.NotNull(landBlockInfo);
            Assert.Equal((uint)7, landBlockInfo.NumCells);
            Assert.Single(landBlockInfo.Objects);
            Assert.Single(landBlockInfo.Buildings);
            Assert.Equal(0x02000123u, landBlockInfo.Objects[0].Id);
            Assert.Equal(0x02000456u, landBlockInfo.Buildings[0].ModelId);
            Assert.Equal(0x00000123u, landBlockInfo.RestrictionTable[0xABCDEF01]);
        }

        [Fact]
        public void DecodeEnvCell_ReadsDarkMajestyLayout() {
            var buffer = new byte[1024];
            var writer = new DatBinWriter(buffer);

            var frame = new Frame {
                Origin = new Vector3(10, 20, 30),
                Orientation = Quaternion.Identity,
            };
            var portal = new CellPortal {
                Flags = 0,
                PolygonId = 7,
                OtherCellId = 0x0200,
                OtherPortalId = 8,
            };
            var stab = new Stab {
                Id = 0x02000333,
                Frame = frame,
            };

            writer.WriteUInt32((uint)(EnvCellFlags.HasStaticObjs | EnvCellFlags.HasRestrictionObj));
            writer.WriteUInt32(0x12340100);
            writer.WriteByte(1);
            writer.WriteByte(1);
            writer.WriteUInt16(1);
            writer.WriteUInt16(0x003A);
            writer.Align(4);
            writer.WriteUInt16(0x0042);
            writer.WriteUInt16(0x0007);
            writer.WriteItem(frame);
            writer.WriteItem(portal);
            writer.WriteUInt16(0x0200);
            writer.Align(4);
            writer.WriteUInt32(1);
            writer.WriteItem(stab);
            writer.WriteUInt32(0xDEADBEEF);

            bool ok = LegacyDatDecoders.TryDecodeEnvCell(
                buffer.AsSpan(0, writer.Offset).ToArray(),
                LegacyDatVersion.DarkMajesty,
                iteration: 67,
                out var envCell);

            Assert.True(ok);
            Assert.NotNull(envCell);
            Assert.Equal(0x12340100u, envCell.Id);
            Assert.Equal(0x003Au, envCell.Surfaces[0]);
            Assert.Equal(0x0042u, envCell.EnvironmentId);
            Assert.Equal((ushort)0x0007, envCell.CellStructure);
            Assert.Single(envCell.CellPortals);
            Assert.Single(envCell.VisibleCells);
            Assert.Single(envCell.StaticObjects);
            Assert.Equal(0xDEADBEEFu, envCell.RestrictionObj);
        }

        [Fact]
        public void DecodeEnvironment_ReadsLegacyCellStructGeometry() {
            bool ok = LegacyDatDecoders.TryDecodeEnvironment(
                BuildLegacyEnvironmentBytes(),
                LegacyDatVersion.DarkMajesty,
                out var environment);

            Assert.True(ok);
            Assert.NotNull(environment);
            Assert.Equal(0x0D000094u, environment.Id);
            Assert.True(environment.Cells.ContainsKey(7));

            var cellStruct = environment.Cells[7];
            Assert.Equal(3, cellStruct.VertexArray.Vertices.Count);
            Assert.Single(cellStruct.Polygons);
            Assert.Single(cellStruct.Portals);
            Assert.Single(cellStruct.PhysicsPolygons);
            Assert.Equal((ushort)11, cellStruct.Portals[0]);
            Assert.NotNull(cellStruct.CellBSP);
            Assert.NotNull(cellStruct.CellBSP.Root);
            Assert.Equal(BSPNodeType.Leaf, cellStruct.CellBSP.Root.Type);
            Assert.NotNull(cellStruct.PhysicsBSP);
            Assert.NotNull(cellStruct.PhysicsBSP.Root);
            Assert.Equal(BSPNodeType.Leaf, cellStruct.PhysicsBSP.Root.Type);
            Assert.Equal(1, cellStruct.PhysicsBSP.Root.Solid);
            Assert.NotNull(cellStruct.DrawingBSP);
            Assert.NotNull(cellStruct.DrawingBSP.Root);
            Assert.Equal(BSPNodeType.Portal, cellStruct.DrawingBSP.Root.Type);
            Assert.Equal(cellStruct.Portals.Count, cellStruct.DrawingBSP.Root.Portals.Count);
            Assert.Single(cellStruct.Portals);
            Assert.Equal((ushort)11, cellStruct.Portals[0]);
        }

        [Fact]
        public void DecodeEnvironment_WithRebuiltBsp_PacksAndUnpacksAsRetail() {
            bool ok = LegacyDatDecoders.TryDecodeEnvironment(
                BuildLegacyEnvironmentBytes(),
                LegacyDatVersion.DarkMajesty,
                out var environment);

            Assert.True(ok);
            Assert.NotNull(environment);
            Assert.Equal(
                environment!.Cells[7].Portals.Count,
                environment.Cells[7].DrawingBSP!.Root!.Portals.Count);

            var buffer = new byte[32768];
            var writer = new DatBinWriter(buffer);
            byte[] packedEnv = DatNativeRecords.Pack(environment!);

            var copy = DatNativeRecords.Unpack<Acme.Dat.Environment>(packedEnv);

            Assert.True(copy.Cells.ContainsKey(7));
            var cellStruct = copy.Cells[7];
            Assert.NotNull(cellStruct.CellBSP);
            Assert.NotNull(cellStruct.CellBSP.Root);
            Assert.Equal(BSPNodeType.Leaf, cellStruct.CellBSP.Root.Type);
            Assert.NotNull(cellStruct.PhysicsBSP);
            Assert.NotNull(cellStruct.PhysicsBSP.Root);
            Assert.Equal(BSPNodeType.Leaf, cellStruct.PhysicsBSP.Root.Type);
            Assert.NotNull(cellStruct.DrawingBSP);
            Assert.NotNull(cellStruct.DrawingBSP.Root);
            Assert.Equal(BSPNodeType.Portal, cellStruct.DrawingBSP.Root.Type);
            Assert.Single(cellStruct.Portals);
            Assert.Equal((ushort)11, cellStruct.Portals[0]);
            Assert.Equal(cellStruct.Portals.Count, cellStruct.DrawingBSP.Root.Portals.Count);
            Assert.False(LegacyBspRetailNormalizer.EnsureDrawingBspPortalRefs(cellStruct));
        }

        [Fact]
        public void DecodeEnvironment_WithLegacyDrawingPortBsp_PackRoundTripPreservesPortalTree() {
            bool ok = LegacyDatDecoders.TryDecodeEnvironment(
                BuildLegacyEnvironmentWithDrawingPortBytes(),
                LegacyDatVersion.DarkMajesty,
                out var environment);

            Assert.True(ok);
            var buffer = new byte[32768];
            var writer = new DatBinWriter(buffer);
            byte[] packedEnv = DatNativeRecords.Pack(environment!);

            var copy = DatNativeRecords.Unpack<Acme.Dat.Environment>(packedEnv);

            var cellStruct = copy.Cells[7];
            Assert.NotNull(cellStruct.DrawingBSP?.Root);
            Assert.True(LegacyBspRetailNormalizer.CountDrawingPortalRefs(cellStruct.DrawingBSP.Root) >= 1);
        }

        [Fact]
        public void DecodeEnvironment_WithLegacyDrawingPortBsp_PreservesPortalTree() {
            bool ok = LegacyDatDecoders.TryDecodeEnvironment(
                BuildLegacyEnvironmentWithDrawingPortBytes(),
                LegacyDatVersion.DarkMajesty,
                out var environment);

            Assert.True(ok);
            Assert.NotNull(environment);
            var cellStruct = environment!.Cells[7];
            Assert.NotNull(cellStruct.DrawingBSP?.Root);
            Assert.Equal(BSPNodeType.Portal, cellStruct.DrawingBSP.Root.Type);
            Assert.Equal(1, LegacyBspRetailNormalizer.CountDrawingPortalRefs(cellStruct.DrawingBSP.Root));
            Assert.Single(cellStruct.Portals);
        }

        [Fact]
        public void EnsureDrawingBspPortalRefs_RestoresRootPortalsAfterPackRoundTrip() {
            bool ok = LegacyDatDecoders.TryDecodeEnvironment(
                BuildLegacyEnvironmentBytes(),
                LegacyDatVersion.DarkMajesty,
                out var environment);

            Assert.True(ok);
            var cellStruct = environment!.Cells[7];
            Assert.NotNull(cellStruct.DrawingBSP?.Root);
            cellStruct.DrawingBSP.Root.Portals.Clear();

            Assert.True(LegacyBspRetailNormalizer.EnsureDrawingBspPortalRefs(cellStruct));
            Assert.Equal(cellStruct.Portals.Count, cellStruct.DrawingBSP.Root.Portals.Count);
        }

        [Fact]
        public void TryVerifyEnvironmentPackable_RejectsNonSerializableBpolPortalRoot() {
            bool ok = LegacyDatDecoders.TryDecodeEnvironment(
                BuildLegacyEnvironmentBytes(),
                LegacyDatVersion.DarkMajesty,
                out var environment);

            Assert.True(ok);
            var cellStruct = environment!.Cells[7];
            Assert.NotNull(cellStruct.DrawingBSP?.Root);

            var keys = cellStruct.Polygons.Keys.OrderBy(id => id).ToList();
            cellStruct.DrawingBSP.Root = BspGenerator.DrawingPolygonNode(
                keys,
                cellStruct.Polygons,
                cellStruct.VertexArray.Vertices,
                BspGenerator.BuildPortalRefs(cellStruct, cellStruct.Polygons));

            Assert.False(LegacyBspRetailNormalizer.TryVerifyEnvironmentPackable(environment));

            LegacyBspRetailNormalizer.EnsureEnvironmentPackable(environment);

            Assert.True(LegacyBspRetailNormalizer.TryVerifyEnvironmentPackable(environment));
            Assert.Equal(BSPNodeType.Portal, environment.Cells[7].DrawingBSP?.Root?.Type);
            Assert.True(LegacyBspRetailNormalizer.HasAdequateDrawingPortalCoverage(environment.Cells[7]));
        }

        [Fact]
        public void DarkMajesty_FailingInteriorCell_EnvironmentPreservesDrawingPortalRefs() {
            const uint envId = 0x0D0003AD;
            const ushort structId = 0x0001;
            string legacy = @"C:\Users\chris\OneDrive\Documents\ACME WorldBuilder\Projects\darkmaj\dats\base";
            if (!File.Exists(Path.Combine(legacy, "portal.dat"))) {
                return;
            }

            using var reader = new LegacyDatReader(legacy);
            Assert.True(reader.TryGet<Acme.Dat.Environment>(envId, out var environment) && environment != null);
            Assert.True(environment.Cells.TryGetValue(structId, out var cellStruct));

            int portalRefs = LegacyBspRetailNormalizer.CountDrawingPortalRefs(cellStruct.DrawingBSP?.Root);
            Assert.True(portalRefs > 0, $"legacy env 0x{envId:X8} struct 0x{structId:X4} should carry drawing portal refs");

            LegacyBspRetailNormalizer.EnsureEnvironmentPackable(environment);
            Assert.True(environment.Cells.TryGetValue(structId, out cellStruct));
            Assert.True(
                LegacyBspRetailNormalizer.HasAdequateDrawingPortalCoverage(cellStruct),
                "retail-safe environment pack path must preserve drawing portal coverage");
        }

        [Fact]
        public void BuildLegacyRetailSafe_CellStruct_FallbackBuildsShallowPhysicsLeaves() {
            var cellStruct = new CellStruct {
                VertexArray = new VertexArray {
                    VertexType = VertexType.CSWVertexType,
                    Vertices = new Dictionary<ushort, SWVertex> {
                        [0] = Vertex(new Vector3(0f, 0f, 0f)),
                        [1] = Vertex(new Vector3(1f, 0f, 0f)),
                        [2] = Vertex(new Vector3(0f, 1f, 0f)),
                        [3] = Vertex(new Vector3(0f, 0f, 1f)),
                        [4] = Vertex(new Vector3(0f, 1f, 1f)),
                        [5] = Vertex(new Vector3(0f, 0f, 2f)),
                        [6] = Vertex(new Vector3(2f, 0f, 1f)),
                        [7] = Vertex(new Vector3(2f, 0f, 2f)),
                        [8] = Vertex(new Vector3(2f, 1f, 1f)),
                        [9] = Vertex(new Vector3(0f, 0f, -1f)),
                        [10] = Vertex(new Vector3(1f, 0f, -1f)),
                        [11] = Vertex(new Vector3(0f, 1f, -1f)),
                        [12] = Vertex(new Vector3(4f, 0f, 1f)),
                        [13] = Vertex(new Vector3(4f, 1f, 1f)),
                        [14] = Vertex(new Vector3(4f, 0f, 2f)),
                    },
                },
                Polygons = new Dictionary<ushort, Polygon> {
                    [11] = Triangle(0, 1, 2),
                    [12] = Triangle(3, 4, 5),
                    [13] = Triangle(6, 8, 7),
                    [14] = Triangle(9, 10, 11),
                    [15] = Triangle(12, 13, 14),
                },
            };

            BspGenerator.BuildLegacyRetailSafe(cellStruct);

            Assert.NotNull(cellStruct.CellBSP);
            Assert.NotNull(cellStruct.CellBSP.Root);
            Assert.Equal(BSPNodeType.BPIN, cellStruct.CellBSP.Root.Type);
            Assert.NotNull(cellStruct.CellBSP.Root.PosNode);
            Assert.Equal(BSPNodeType.Leaf, cellStruct.CellBSP.Root.PosNode.Type);
            Assert.NotNull(cellStruct.CellBSP.Root.NegNode);
            Assert.Equal(BSPNodeType.Leaf, cellStruct.CellBSP.Root.NegNode.Type);

            Assert.NotNull(cellStruct.PhysicsBSP);
            Assert.NotNull(cellStruct.PhysicsBSP.Root);
            Assert.Equal(BSPNodeType.BPIN, cellStruct.PhysicsBSP.Root.Type);
            Assert.NotNull(cellStruct.PhysicsBSP.Root.PosNode);
            Assert.Equal(BSPNodeType.Leaf, cellStruct.PhysicsBSP.Root.PosNode.Type);
            Assert.NotNull(cellStruct.PhysicsBSP.Root.NegNode);
            Assert.Equal(BSPNodeType.Leaf, cellStruct.PhysicsBSP.Root.NegNode.Type);
            Assert.Equal(new ushort[] { 11, 12, 13, 14, 15 }, cellStruct.PhysicsBSP.Root.PosNode.Polygons.OrderBy(id => id).ToArray());
            Assert.Equal(cellStruct.PhysicsBSP.Root.PosNode.Polygons, cellStruct.PhysicsBSP.Root.NegNode.Polygons);
        }

        [Fact]
        public void CreateFallbackRegion_ProvidesSafeTerrainDefaults() {
            const uint fallbackTextureId = 0x05000042;

            var region = LegacyDatDecoders.CreateFallbackRegion(0x13000000, fallbackTextureId);

            Assert.Equal(0x13000000u, region.Id);
            Assert.Equal(256, region.LandDefs.LandHeightTable.Length);
            Assert.Equal(0f, region.LandDefs.LandHeightTable[0]);
            Assert.Equal(510f, region.LandDefs.LandHeightTable[^1]);
            Assert.Single(region.TerrainInfo.LandSurfaces.TexMerge.TerrainDesc);
            Assert.Equal(fallbackTextureId, (uint)region.TerrainInfo.LandSurfaces.TexMerge.TerrainDesc[0].TerrainTex.TextureId);
        }

        [Fact]
        public void RetailRegion_HasLandSurfTexMerge() {
            const string dir = @"C:\Users\chris\Downloads\ac-updates";
            if (!File.Exists(Path.Combine(dir, "client_portal.dat"))) {
                return;
            }

            using var dats = new DefaultDatReaderWriter(dir, DatAccessType.Read);
            Assert.True(dats.TryGet<Region>(0x13000000, out var region));
            Assert.NotNull(region.LandSurf);
            Assert.NotNull(region.LandSurf.TexMerge);
            Assert.NotEmpty(region.LandSurf.TexMerge.TerrainDesc);
        }

        [Fact]
        public void RetailEnvironment_HydratesCellStructPolygons() {
            const string dir = @"C:\Users\chris\Downloads\ac-updates";
            if (!File.Exists(Path.Combine(dir, "client_portal.dat"))) {
                return;
            }

            using var dats = new DefaultDatReaderWriter(dir, DatAccessType.Read);
            uint envId = dats.GetAllIdsOfType<Acme.Dat.Environment>().FirstOrDefault();
            Assert.NotEqual(0u, envId);
            Assert.True(dats.TryGet<Acme.Dat.Environment>(envId, out var env));
            Assert.NotEmpty(env.Cells);
            var cell = env.Cells.Values.First();
            Assert.True(cell.Polygons.Count > 0 || cell.VertexArray.Vertices.Count > 0);
        }

        [Fact]
        public void RetailEnvironment_HoltburgPrefabHasEveryCellStruct() {
            const string dir = @"C:\Users\chris\Downloads\ac-updates";
            if (!File.Exists(Path.Combine(dir, "client_portal.dat"))) {
                return;
            }

            using var dats = new DefaultDatReaderWriter(dir, DatAccessType.Read);
            Assert.True(dats.TryGet<Acme.Dat.Environment>(0x0D00034F, out var env));
            Assert.Equal(17u, env.CellCount);
            Assert.Equal((int)env.CellCount, env.Cells.Count);
            Assert.True(env.Cells.ContainsKey(1));
            Assert.True(env.Cells.ContainsKey(7));
        }

        [Fact]
        public void TryGet_OnDemand_ReusesDecodedEnvironmentInstance() {
            const string dir = @"C:\Users\chris\Downloads\ac-updates";
            if (!File.Exists(Path.Combine(dir, "client_portal.dat"))) {
                return;
            }

            using var cached = new DefaultDatReaderWriter(dir, DatAccessType.Read, FileCachingStrategy.OnDemand, IndexCachingStrategy.Never);
            Assert.True(cached.TryGet<Acme.Dat.Environment>(0x0D00034F, out var first));
            Assert.True(cached.TryGet<Acme.Dat.Environment>(0x0D00034F, out var second));
            Assert.Same(first, second);

            using var uncached = new DefaultDatReaderWriter(dir, DatAccessType.Read, FileCachingStrategy.Never, IndexCachingStrategy.Never);
            Assert.True(uncached.TryGet<Acme.Dat.Environment>(0x0D00034F, out var a));
            Assert.True(uncached.TryGet<Acme.Dat.Environment>(0x0D00034F, out var b));
            Assert.NotSame(a, b);
        }

        [Fact]
        public void TryGet_OnDemand_ConcurrentDecodeSharesCachedInstance() {
            const string dir = @"C:\Users\chris\Downloads\ac-updates";
            if (!File.Exists(Path.Combine(dir, "client_portal.dat"))) {
                return;
            }

            using var cached = new DefaultDatReaderWriter(dir, DatAccessType.Read, FileCachingStrategy.OnDemand, IndexCachingStrategy.Never);
            Acme.Dat.Environment? one = null;
            Acme.Dat.Environment? two = null;
            var t1 = Task.Run(() => cached.TryGet<Acme.Dat.Environment>(0x0D00034F, out one));
            var t2 = Task.Run(() => cached.TryGet<Acme.Dat.Environment>(0x0D00034F, out two));
            Task.WaitAll(t1, t2);
            Assert.True(t1.Result);
            Assert.True(t2.Result);
            Assert.NotNull(one);
            Assert.NotNull(two);
            Assert.Same(one, two);
        }

        [Fact]
        public void RetailLandblock_LoadsBuildingEnvCells() {
            const string dir = @"C:\Users\chris\Downloads\ac-updates";
            if (!File.Exists(Path.Combine(dir, "client_portal.dat"))
                || !File.Exists(Path.Combine(dir, "client_cell_1.dat"))) {
                return;
            }

            using var dats = new DefaultDatReaderWriter(dir, DatAccessType.Read);
            uint[] candidates = [0x7D64FFFE, 0xA9B4FFFE, 0xC6A9FFFE, 0xDE51FFFE];
            LandBlockInfo? lbi = null;
            uint infoId = 0;
            foreach (uint id in candidates) {
                if (dats.TryGet<LandBlockInfo>(id, out var found) && found.NumCells > 0 && found.Buildings.Count > 0) {
                    lbi = found;
                    infoId = id;
                    break;
                }
            }

            if (lbi == null) {
                foreach (uint id in dats.ListFileIds(DatArchive.Cell)) {
                    if ((id & 0xFFFF) != 0xFFFE) continue;
                    if (dats.TryGet<LandBlockInfo>(id, out var found) && found.NumCells > 0 && found.Buildings.Count > 0) {
                        lbi = found;
                        infoId = id;
                        break;
                    }
                }
            }

            Assert.NotNull(lbi);
            uint lbId = infoId >> 16;
            int loaded = 0;
            int withGeometry = 0;
            for (uint c = 0x0100; c < 0x0100 + lbi.NumCells; c++) {
                uint cellId = (lbId << 16) | c;
                if (!dats.TryGet<EnvCell>(cellId, out var envCell)) continue;
                loaded++;
                uint envFileId = envCell.EnvironmentId | 0x0D000000u;
                if (!dats.TryGet<Acme.Dat.Environment>(envFileId, out var env)) continue;
                if (env.Cells.TryGetValue(envCell.CellStructure, out var cellStruct)
                    && (cellStruct.Polygons.Count > 0 || cellStruct.VertexArray.Vertices.Count > 0)) {
                    withGeometry++;
                }
            }

            Assert.True(loaded > 0, $"LBI 0x{infoId:X8} NumCells={lbi.NumCells} but no EnvCells decoded");
            Assert.True(
                withGeometry == loaded,
                $"LBI 0x{infoId:X8} decoded {loaded} EnvCells but only {withGeometry} had CellStruct geometry");
        }

        [Fact]
        public void RetailEnvCell_DrawingPolysHaveResolvableSurfaces() {
            const string dir = @"C:\Users\chris\Downloads\ac-updates";
            if (!File.Exists(Path.Combine(dir, "client_portal.dat"))
                || !File.Exists(Path.Combine(dir, "client_cell_1.dat"))) {
                return;
            }

            using var dats = new DefaultDatReaderWriter(dir, DatAccessType.Read);
            uint infoId = 0xA9B4FFFE;
            if (!dats.TryGet<LandBlockInfo>(infoId, out var lbi) || lbi.NumCells == 0) {
                infoId = 0x7D64FFFE;
                if (!dats.TryGet<LandBlockInfo>(infoId, out lbi) || lbi.NumCells == 0) {
                    return;
                }
            }

            uint lbId = infoId >> 16;
            int envCells = 0, missingEnv = 0, missingStruct = 0, noDraw = 0;
            int surfOk = 0, surfBadIdx = 0, surfMissing = 0, texMissing = 0;
            int drawingKeys = 0, physicsKeys = 0, overlap = 0, asPortal = 0;
            uint sampleFlags = 0;
            Vector3 sampleOrigin = default;

            for (uint c = 0x0100; c < 0x0100 + lbi.NumCells; c++) {
                uint cellId = (lbId << 16) | c;
                if (!dats.TryGet<EnvCell>(cellId, out var envCell)) continue;
                envCells++;
                sampleFlags = envCell.Flags;
                sampleOrigin = envCell.Position.Origin;
                uint envFileId = envCell.EnvironmentId | 0x0D000000u;
                if (!dats.TryGet<Acme.Dat.Environment>(envFileId, out var env)) {
                    missingEnv++;
                    continue;
                }
                if (!env.Cells.TryGetValue(envCell.CellStructure, out var cellStruct)) {
                    missingStruct++;
                    Console.WriteLine(
                        $"missingStruct cell=0x{cellId:X8} env=0x{envFileId:X8} struct={envCell.CellStructure} " +
                        $"envCells={env.Cells.Count}/{env.CellCount} keys=[{string.Join(",", env.Cells.Keys.OrderBy(k => k).Take(12))}]");
                    continue;
                }
                if (cellStruct.DrawingTailBytes.Length > 0) {
                    var tail = cellStruct.DrawingTailBytes;
                    var preview = string.Join(" ", tail.Take(16).Select(b => b.ToString("X2")));
                    Console.WriteLine(
                        $"struct={envCell.CellStructure} tailLen={tail.Length} head={preview}");
                }
                drawingKeys = cellStruct.Polygons.Count;
                physicsKeys = cellStruct.PhysicsPolygons.Count;
                overlap = cellStruct.Polygons.Keys.Count(k => cellStruct.PhysicsPolygons.ContainsKey(k));
                if (physicsKeys > 0) {
                    asPortal = cellStruct.Polygons.Keys.Count(k => !cellStruct.PhysicsPolygons.ContainsKey(k));
                }
                if (cellStruct.Polygons.Count == 0) {
                    noDraw++;
                    continue;
                }
                foreach (var poly in cellStruct.Polygons.Values) {
                    if (poly.PosSurface < 0 || poly.PosSurface >= envCell.Surfaces.Count) {
                        surfBadIdx++;
                        continue;
                    }
                    uint surfaceId = envCell.Surfaces[poly.PosSurface] | 0x08000000u;
                    if (!dats.TryGet<Surface>(surfaceId, out var surface)) {
                        surfMissing++;
                        continue;
                    }
                    if (surface.Type.HasFlag(SurfaceType.Base1Solid)) {
                        surfOk++;
                        continue;
                    }
                    if (!dats.TryGet<SurfaceTexture>(surface.OrigTextureId, out var st) || st.Textures.Count == 0) {
                        texMissing++;
                        continue;
                    }
                    if (!dats.TryGet<RenderSurface>(st.Textures.Last(), out _)) {
                        texMissing++;
                        continue;
                    }
                    surfOk++;
                }
            }

            string stats =
                $"LB 0x{lbId:X4} pack={lbi.PackMask} buildings={lbi.Buildings.Count} numCells={lbi.NumCells} " +
                $"envCells={envCells} missingEnv={missingEnv} missingStruct={missingStruct} noDraw={noDraw} " +
                $"drawing={drawingKeys} physics={physicsKeys} overlap={overlap} asPortal={asPortal} " +
                $"surfOk={surfOk} badIdx={surfBadIdx} surfMissing={surfMissing} texMissing={texMissing} " +
                $"flags=0x{sampleFlags:X} origin=({sampleOrigin.X:F1},{sampleOrigin.Y:F1},{sampleOrigin.Z:F1})";
            Console.WriteLine(stats);
            Assert.True(envCells > 0 && surfOk > 0 && missingStruct == 0, stats);
        }

        [Fact]
        public void DecodeRegion_ReadsLegacySceneAndTerrainMappingsAfterSkyPayload() {
            var buffer = new byte[4096];
            var writer = new DatBinWriter(buffer);

            writer.WriteUInt32(0x130F0000);
            writer.WriteUInt32(1);
            writer.WriteUInt32(67);
            writer.WriteItem(new AC1LegacyPStringBase<byte>());
            writer.Align(4);

            writer.WriteItem(new LandDefs {
                NumBlockLength = 254,
                NumBlockWidth = 254,
                SquareLength = 24f,
                LBlockLength = 192,
                VertexPerCell = 9,
                MaxObjHeight = 512f,
                SkyHeight = 2048f,
                RoadWidth = 5f,
                LandHeightTable = Enumerable.Range(0, 256).Select(i => (float)i).ToArray(),
            });

            writer.WriteDouble(0d);
            writer.WriteUInt32(10);
            writer.WriteSingle(24f);
            writer.WriteUInt32(360);
            writer.WriteItem(new AC1LegacyPStringBase<byte>());
            writer.Align(4);
            writer.WriteUInt32(0); // Times of day
            writer.WriteUInt32(0); // Days of week
            writer.WriteUInt32(0); // Seasons

            writer.WriteUInt32((uint)(PartsMask.HasSkyInfo | PartsMask.HasSceneInfo));

            writer.WriteDouble(1d);
            writer.WriteDouble(2d);
            writer.Align(4);
            writer.WriteUInt32(1); // Day group count
            writer.WriteSingle(1f);
            writer.WriteItem(new AC1LegacyPStringBase<byte>());
            writer.Align(4);
            writer.WriteUInt32(1); // Sky object count
            writer.WriteSingle(0f);
            writer.WriteSingle(1f);
            writer.WriteSingle(2f);
            writer.WriteSingle(3f);
            writer.WriteSingle(4f);
            writer.WriteSingle(5f);
            writer.WriteUInt32(0x01000011);
            writer.WriteUInt32(0); // Properties, no PES object in DM
            writer.Align(4);
            writer.WriteUInt32(1); // Sky time count
            writer.WriteSingle(0f);
            writer.WriteSingle(1f);
            writer.WriteSingle(2f);
            writer.WriteSingle(3f);
            writer.WriteUInt32(0x04030201);
            writer.WriteSingle(4f);
            writer.WriteUInt32(0x08070605);
            writer.WriteSingle(5f);
            writer.WriteSingle(6f);
            writer.WriteUInt32(0x0C0B0A09);
            writer.WriteUInt32(7);
            writer.Align(4);
            writer.WriteUInt32(1); // Sky replacement count
            writer.WriteUInt32(8);
            writer.WriteUInt32(0x01000022);
            writer.WriteSingle(9f);
            writer.WriteSingle(10f);
            writer.WriteSingle(11f);
            writer.WriteSingle(12f);
            writer.Align(4);

            writer.WriteUInt32(1); // Scene type count
            writer.WriteUInt32(7);
            writer.WriteUInt32(1); // Scene count
            writer.WriteUInt32(0x12000033);

            writer.WriteUInt32(1); // Terrain type count
            writer.WriteItem(new AC1LegacyPStringBase<byte>());
            writer.Align(4);
            writer.WriteUInt32(0x00112233);
            writer.WriteUInt32(1); // Scene mapping count
            writer.WriteUInt32(0); // Scene type index
            writer.WriteUInt32(0); // Land surface type
            writer.WriteUInt32(1); // Base texture size
            writer.WriteUInt32(0); // Corner maps
            writer.WriteUInt32(0); // Side maps
            writer.WriteUInt32(0); // Road maps
            writer.WriteUInt32(1); // Terrain desc count
            writer.WriteInt32((int)TerrainTextureType.Grassland);
            writer.WriteUInt32(0x05000042);
            writer.WriteUInt32(1);
            writer.WriteUInt32(255);
            writer.WriteUInt32(0);
            writer.WriteUInt32(255);
            writer.WriteUInt32(0);
            writer.WriteUInt32(255);
            writer.WriteUInt32(0);
            writer.WriteUInt32(1);
            writer.WriteUInt32(0);

            bool ok = LegacyDatDecoders.TryDecodeRegion(
                buffer.AsSpan(0, writer.Offset).ToArray(),
                LegacyDatVersion.DarkMajesty,
                fallbackTerrainTextureId: 0x0500DEAD,
                out var region);

            Assert.True(ok);
            Assert.NotNull(region);
            Assert.Single(region.SceneInfo.SceneTypes);
            Assert.Single(region.SceneInfo.SceneTypes[0].Scenes);
            Assert.Equal(0x12000033u, (uint)region.SceneInfo.SceneTypes[0].Scenes[0]);
            Assert.Single(region.TerrainInfo.TerrainTypes);
            Assert.Single(region.TerrainInfo.TerrainTypes[0].SceneTypes);
            Assert.Equal((uint)0, region.TerrainInfo.TerrainTypes[0].SceneTypes[0]);
            Assert.Single(region.TerrainInfo.LandSurfaces.TexMerge.TerrainDesc);
            Assert.Equal(0x05000042u, (uint)region.TerrainInfo.LandSurfaces.TexMerge.TerrainDesc[0].TerrainTex.TextureId);
            Assert.Equal(255f, region.LandDefs.LandHeightTable[255]);
        }

        [Fact]
        public void DecodeLegacySurface_ReadsDirectTextureReference() {
            var buffer = new byte[64];
            var writer = new DatBinWriter(buffer);

            writer.WriteUInt32(0x08000123);
            writer.WriteUInt32((uint)SurfaceType.Base1Image);
            writer.WriteUInt32(0x05000022);
            writer.WriteUInt32(0x04000055);
            writer.WriteSingle(0.25f);
            writer.WriteSingle(0.5f);
            writer.WriteSingle(0.75f);

            bool ok = LegacyDatDecoders.TryDecodeSurface(
                buffer.AsSpan(0, writer.Offset).ToArray(),
                LegacyDatVersion.DarkMajesty,
                out var surface);

            Assert.True(ok);
            Assert.NotNull(surface);
            Assert.Equal(0x08000123u, surface.Id);
            Assert.Equal(SurfaceType.Base1Image, surface.Type);
            Assert.Equal(0x05000022u, (uint)surface.OrigTextureId);
            Assert.Equal(0x04000055u, (uint)surface.OrigPaletteId);
            Assert.Equal(0.25f, surface.Translucency);
        }

        [Fact]
        public void DecodeLegacySurfaceTexture_WidensIndex8IntoSyntheticRenderSurface() {
            uint syntheticRenderSurfaceId = LegacyDatDecoders.CreateSyntheticRenderSurfaceId(0x05000042);
            var buffer = new byte[64];
            var writer = new DatBinWriter(buffer);

            writer.WriteUInt32(0x05000042);
            writer.WriteUInt32(2); // INDEX8
            writer.WriteInt32(2);
            writer.WriteInt32(1);
            writer.WriteByte(3);
            writer.WriteByte(9);
            writer.WriteUInt32(0x04000077);

            bool ok = LegacyDatDecoders.TryDecodeSurfaceTexture(
                buffer.AsSpan(0, writer.Offset).ToArray(),
                LegacyDatVersion.DarkMajesty,
                syntheticRenderSurfaceId,
                out var surfaceTexture,
                out var renderSurface);

            Assert.True(ok);
            Assert.NotNull(surfaceTexture);
            Assert.NotNull(renderSurface);
            Assert.Equal(0x05000042u, surfaceTexture.Id);
            Assert.Single(surfaceTexture.Textures);
            Assert.Equal(syntheticRenderSurfaceId, (uint)surfaceTexture.Textures[0]);
            Assert.Equal((uint)PixelFormat.PFID_INDEX16, renderSurface.Format);
            Assert.Equal(0x04000077u, renderSurface.DefaultPaletteId);
            Assert.Equal(new byte[] { 3, 0, 9, 0 }, renderSurface.SourceData);
        }

        [Fact]
        public void DecodeLegacyDirectRenderSurface_NormalizesRgb888IntoRetailBgra() {
            var buffer = new byte[64];
            var writer = new DatBinWriter(buffer);

            writer.WriteUInt32(0x06000066);
            writer.WriteInt32(2);
            writer.WriteInt32(1);
            var pixels = new byte[] { 0x11, 0x22, 0x33, 0x44, 0x55, 0x66 };
            writer.WriteBytes(pixels, pixels.Length);

            bool ok = LegacyDatDecoders.TryDecodeDirectRenderSurface(
                buffer.AsSpan(0, writer.Offset).ToArray(),
                LegacyDatVersion.DarkMajesty,
                out var renderSurface);

            Assert.True(ok);
            Assert.NotNull(renderSurface);
            Assert.Equal(0x06000066u, renderSurface.Id);
            Assert.Equal(2, renderSurface.Width);
            Assert.Equal(1, renderSurface.Height);
            Assert.Equal((uint)PixelFormat.PFID_A8R8G8B8, renderSurface.Format);
            Assert.Equal(new byte[] {
                0x33, 0x22, 0x11, 0xFF,
                0x66, 0x55, 0x44, 0xFF,
            }, renderSurface.SourceData);
        }

        [Fact]
        public void DecodeLegacyGfxObj_ReadsLegacySurfaceArrayAndVertexPayload() {
            var buffer = new byte[256];
            var writer = new DatBinWriter(buffer);

            writer.WriteUInt32(0x01000011);
            writer.WriteUInt32(0); // No optional sections
            writer.WriteUInt32(1); // Surface count
            writer.WriteUInt32(0x08000123);
            writer.WriteInt32((int)VertexType.CSWVertexType);
            writer.WriteUInt32(1); // Vertex count
            writer.WriteUInt16(7); // Vertex id
            writer.WriteUInt16(1); // UV count
            writer.WriteVector3(new Vector3(1f, 2f, 3f));
            writer.WriteVector3(Vector3.UnitZ);
            writer.WriteSingle(0.25f);
            writer.WriteSingle(0.75f);
            writer.WriteVector3(Vector3.Zero);

            bool ok = LegacyDatDecoders.TryDecodeGfxObj(
                buffer.AsSpan(0, writer.Offset).ToArray(),
                LegacyDatVersion.DarkMajesty,
                out var gfxObj);

            Assert.True(ok);
            Assert.NotNull(gfxObj);
            Assert.Equal(0x01000011u, gfxObj.Id);
            Assert.Single(gfxObj.Surfaces);
            Assert.Equal(0x08000123u, (uint)gfxObj.Surfaces[0]);
            Assert.True(gfxObj.VertexArray.Vertices.TryGetValue(7, out var vertex));
            Assert.Equal(new Vector3(1f, 2f, 3f), vertex.Origin);
            Assert.Single(vertex.UVs);
            Assert.Equal(0.25f, vertex.UVs[0].U);
            Assert.Equal(0.75f, vertex.UVs[0].V);
        }

        [Fact]
        public void DecodeLegacyGfxObj_PreservesLegacyBspTreesFromPayload() {
            var buffer = new byte[768];
            var writer = new DatBinWriter(buffer);

            writer.WriteUInt32(0x01000022);
            writer.WriteUInt32((uint)(GfxObjFlags.HasPhysics | GfxObjFlags.HasDrawing));
            writer.WriteUInt32(1);
            writer.WriteUInt32(0x08000124);
            writer.WriteInt32((int)VertexType.CSWVertexType);
            writer.WriteUInt32(3);
            writer.WriteUInt16(1);
            writer.WriteUInt16(1);
            writer.WriteVector3(Vector3.Zero);
            writer.WriteVector3(Vector3.UnitZ);
            writer.WriteSingle(0f);
            writer.WriteSingle(0f);
            writer.WriteUInt16(2);
            writer.WriteUInt16(1);
            writer.WriteVector3(Vector3.UnitX);
            writer.WriteVector3(Vector3.UnitZ);
            writer.WriteSingle(1f);
            writer.WriteSingle(0f);
            writer.WriteUInt16(3);
            writer.WriteUInt16(1);
            writer.WriteVector3(Vector3.UnitY);
            writer.WriteVector3(Vector3.UnitZ);
            writer.WriteSingle(0f);
            writer.WriteSingle(1f);

            writer.WriteUInt32(1); // physics polygons
            writer.WriteUInt16(11);
            writer.WriteByte(3);
            writer.WriteByte((byte)StipplingType.NoPos);
            writer.WriteInt32((int)CullMode.None);
            writer.WriteInt16(0);
            writer.WriteInt16(0);
            writer.WriteInt16(1);
            writer.WriteInt16(2);
            writer.WriteInt16(3);
            writer.Align(4);
            writer.WriteUInt32(0x4C454146); // LEAF
            writer.WriteInt32(0); // leaf index
            writer.WriteInt32(1); // solid
            writer.WriteVector3(Vector3.Zero);
            writer.WriteSingle(1f);
            writer.WriteUInt32(0); // in-polys
            writer.Align(4);

            writer.WriteVector3(new Vector3(4f, 5f, 6f)); // sort center

            writer.WriteUInt32(1); // drawing polygons
            writer.WriteUInt16(12);
            writer.WriteByte(3);
            writer.WriteByte((byte)StipplingType.NoPos);
            writer.WriteInt32((int)CullMode.None);
            writer.WriteInt16(0);
            writer.WriteInt16(0);
            writer.WriteInt16(1);
            writer.WriteInt16(2);
            writer.WriteInt16(3);
            writer.Align(4);
            writer.WriteUInt32(0x4C454146); // LEAF
            writer.WriteInt32(2); // leaf index
            writer.Align(4);

            bool ok = LegacyDatDecoders.TryDecodeGfxObj(
                buffer.AsSpan(0, writer.Offset).ToArray(),
                LegacyDatVersion.DarkMajesty,
                out var gfxObj);

            Assert.True(ok);
            Assert.NotNull(gfxObj);
            Assert.Equal(new Vector3(4f, 5f, 6f), gfxObj.SortCenter);
            Assert.NotNull(gfxObj.PhysicsBSP);
            Assert.NotNull(gfxObj.PhysicsBSP.Root);
            Assert.Equal(BSPNodeType.Leaf, gfxObj.PhysicsBSP.Root.Type);
            Assert.Equal(1, gfxObj.PhysicsBSP.Root.Solid);
            Assert.Empty(gfxObj.PhysicsBSP.Root.Polygons);
            Assert.True(gfxObj.PhysicsPolygons.ContainsKey(11));
            Assert.False(gfxObj.PhysicsPolygons.ContainsKey(12));
            Assert.True(gfxObj.Polygons.ContainsKey(12));
            Assert.NotNull(gfxObj.DrawingBSP);
            Assert.NotNull(gfxObj.DrawingBSP.Root);
            Assert.Equal(BSPNodeType.Leaf, gfxObj.DrawingBSP.Root.Type);
        }

        [Fact]
        public void DecodeLegacyGfxObj_WithLegacyLeafBsp_PacksAndUnpacksAsRetail() {
            bool ok = LegacyDatDecoders.TryDecodeGfxObj(
                BuildLegacyInternalNodeGfxObjBytes(),
                LegacyDatVersion.DarkMajesty,
                out var gfxObj);

            Assert.True(ok);
            Assert.NotNull(gfxObj);
            Assert.NotNull(gfxObj.PhysicsBSP);
            Assert.NotNull(gfxObj.DrawingBSP);
            var physicsRoot = gfxObj.PhysicsBSP.Root;
            var drawingRoot = gfxObj.DrawingBSP.Root;
            Assert.NotNull(physicsRoot);
            Assert.NotNull(drawingRoot);
            Assert.Equal(BSPNodeType.Leaf, physicsRoot.Type);
            Assert.Equal(BSPNodeType.Leaf, drawingRoot.Type);
            Assert.Equal(new ushort[] { 11, 12, 13 }, gfxObj.PhysicsPolygons.Keys.OrderBy(id => id).ToArray());
            Assert.Equal(new ushort[] { 21, 22, 23 }, gfxObj.Polygons.Keys.OrderBy(id => id).ToArray());

            var buffer = new byte[32768];
            var writer = new DatBinWriter(buffer);
            byte[] packedGfx = DatNativeRecords.Pack(gfxObj);

            var copy = DatNativeRecords.Unpack<GfxObj>(packedGfx);

            Assert.NotNull(copy.PhysicsBSP);
            Assert.NotNull(copy.DrawingBSP);
            Assert.NotNull(copy.PhysicsBSP.Root);
            Assert.NotNull(copy.DrawingBSP.Root);
            Assert.Equal(BSPNodeType.Leaf, copy.PhysicsBSP.Root.Type);
            Assert.Equal(BSPNodeType.Leaf, copy.DrawingBSP.Root.Type);
            Assert.Equal(new ushort[] { 11, 12, 13 }, copy.PhysicsPolygons.Keys.OrderBy(id => id).ToArray());
            Assert.Equal(new ushort[] { 21, 22, 23 }, copy.Polygons.Keys.OrderBy(id => id).ToArray());
        }

        [Fact]
        public void BspGenerator_ClearsFlagsWhenNoRetailSafeBspCanBeBuilt() {
            var gfxObj = new GfxObj {
                Id = 0x01000099,
                Flags = (uint)(GfxObjFlags.HasPhysics | GfxObjFlags.HasDrawing),
                VertexArray = new VertexArray {
                    VertexType = VertexType.CSWVertexType,
                    Vertices = new Dictionary<ushort, SWVertex>(),
                },
                PhysicsBSP = new PhysicsBSPTree(),
                DrawingBSP = new DrawingBSPTree(),
            };

            BspGenerator.Build(gfxObj);

            Assert.False(gfxObj.Flags.HasFlag(GfxObjFlags.HasPhysics));
            Assert.False(gfxObj.Flags.HasFlag(GfxObjFlags.HasDrawing));
            Assert.Null(gfxObj.PhysicsBSP);
            Assert.Null(gfxObj.DrawingBSP);
        }

        private static byte[] BuildLegacyInternalNodeGfxObjBytes() {
            var buffer = new byte[4096];
            var writer = new DatBinWriter(buffer);

            writer.WriteUInt32(0x01000044);
            writer.WriteUInt32((uint)(GfxObjFlags.HasPhysics | GfxObjFlags.HasDrawing));
            writer.WriteUInt32(1);
            writer.WriteUInt32(0x08000124);

            writer.WriteInt32((int)VertexType.CSWVertexType);
            writer.WriteUInt32(9);
            WriteLegacyVertex(writer, 1, new Vector3(0f, 0f, 0f), new Vector2(0f, 0f));
            WriteLegacyVertex(writer, 2, new Vector3(1f, 0f, 0f), new Vector2(1f, 0f));
            WriteLegacyVertex(writer, 3, new Vector3(0f, 1f, 0f), new Vector2(0f, 1f));
            WriteLegacyVertex(writer, 4, new Vector3(0f, 0f, 1f), new Vector2(0f, 0f));
            WriteLegacyVertex(writer, 5, new Vector3(1f, 0f, 1f), new Vector2(1f, 0f));
            WriteLegacyVertex(writer, 6, new Vector3(0f, 1f, 1f), new Vector2(0f, 1f));
            WriteLegacyVertex(writer, 7, new Vector3(0f, 0f, -1f), new Vector2(0f, 0f));
            WriteLegacyVertex(writer, 8, new Vector3(1f, 0f, -1f), new Vector2(1f, 0f));
            WriteLegacyVertex(writer, 9, new Vector3(0f, 1f, -1f), new Vector2(0f, 1f));

            writer.WriteUInt32(3);
            WriteLegacyTriangle(writer, 11, 1, 2, 3);
            WriteLegacyTriangle(writer, 12, 4, 5, 6);
            WriteLegacyTriangle(writer, 13, 7, 8, 9);
            writer.WriteUInt32(0x4C454146);
            writer.WriteInt32(0);
            writer.WriteInt32(1);
            writer.WriteVector3(Vector3.Zero);
            writer.WriteSingle(1f);
            writer.WriteUInt32(0);
            writer.Align(4);

            writer.WriteVector3(new Vector3(0f, 0f, 0f));

            writer.WriteUInt32(3);
            WriteLegacyTriangle(writer, 21, 1, 2, 3);
            WriteLegacyTriangle(writer, 22, 4, 5, 6);
            WriteLegacyTriangle(writer, 23, 7, 8, 9);
            writer.WriteUInt32(0x4C454146);
            writer.WriteInt32(0);
            writer.Align(4);

            return buffer.AsSpan(0, writer.Offset).ToArray();
        }

        private static void WriteLegacyVertex(DatBinWriter writer, ushort id, Vector3 origin, Vector2 uv) {
            writer.WriteUInt16(id);
            writer.WriteUInt16(1);
            writer.WriteVector3(origin);
            writer.WriteVector3(Vector3.UnitZ);
            writer.WriteSingle(uv.X);
            writer.WriteSingle(uv.Y);
        }

        private static void WriteLegacyTriangle(DatBinWriter writer, ushort key, short a, short b, short c) {
            writer.WriteUInt16(key);
            writer.WriteByte(3);
            writer.WriteByte((byte)StipplingType.NoPos);
            writer.WriteInt32((int)CullMode.None);
            writer.WriteInt16(0);
            writer.WriteInt16(0);
            writer.WriteInt16(a);
            writer.WriteInt16(b);
            writer.WriteInt16(c);
            writer.Align(4);
        }

        private static SWVertex Vertex(Vector3 origin) => new() {
            Origin = origin,
            Normal = Vector3.UnitZ,
            UVs = new List<Vec2Duv> { new() { U = 0f, V = 0f } },
        };

        private static Polygon Triangle(short a, short b, short c) => new() {
            VertexIds = new List<ushort> { (ushort)a, (ushort)b, (ushort)c },
            PosSurface = 0,
            NegSurface = -1,
            PosUVIndices = new List<byte> { 0, 0, 0 },
            Stippling = StipplingType.NoPos,
            SidesType = (int)CullMode.None,
        };

        private static byte[] BuildLegacyEnvironmentBytes() {
            var buffer = new byte[2048];
            var writer = new DatBinWriter(buffer);

            writer.WriteUInt32(0x0D000094);
            writer.WriteUInt32(1); // cell count
            writer.WriteUInt32(7); // cell struct key

            writer.WriteUInt32(1); // polygon count
            writer.WriteUInt32(0); // physics polygon count
            writer.WriteUInt32(1); // portal count

            writer.WriteInt32((int)VertexType.CSWVertexType);
            writer.WriteUInt32(3); // vertex count
            WriteLegacyVertex(writer, 0, new Vector3(0f, 0f, 0f), new Vector2(0f, 0f));
            WriteLegacyVertex(writer, 1, new Vector3(1f, 0f, 0f), new Vector2(1f, 0f));
            WriteLegacyVertex(writer, 2, new Vector3(0f, 1f, 0f), new Vector2(0f, 1f));

            WriteLegacyTriangle(writer, 11, 0, 1, 2);

            writer.WriteUInt16(11); // portal polygon ref
            writer.Align(4);

            writer.WriteUInt32(0x4C454146); // cell BSP leaf
            writer.WriteInt32(0);
            writer.Align(4);

            writer.WriteUInt32(0x4C454146); // physics BSP leaf
            writer.WriteInt32(0);
            writer.WriteInt32(1); // solid
            writer.WriteVector3(Vector3.Zero);
            writer.WriteSingle(1f);
            writer.WriteUInt32(0); // polygon refs
            writer.Align(4);

            writer.WriteUInt32(0); // no legacy drawing BSP
            writer.Align(4);

            return buffer.AsSpan(0, writer.Offset).ToArray();
        }

        private static byte[] BuildLegacyEnvironmentWithDrawingPortBytes() {
            var buffer = new byte[4096];
            var writer = new DatBinWriter(buffer);

            writer.WriteUInt32(0x0D000094);
            writer.WriteUInt32(1);
            writer.WriteUInt32(7);

            writer.WriteUInt32(1);
            writer.WriteUInt32(0);
            writer.WriteUInt32(1);

            writer.WriteInt32((int)VertexType.CSWVertexType);
            writer.WriteUInt32(3);
            WriteLegacyVertex(writer, 0, new Vector3(0f, 0f, 0f), new Vector2(0f, 0f));
            WriteLegacyVertex(writer, 1, new Vector3(1f, 0f, 0f), new Vector2(1f, 0f));
            WriteLegacyVertex(writer, 2, new Vector3(0f, 1f, 0f), new Vector2(0f, 1f));
            WriteLegacyTriangle(writer, 11, 0, 1, 2);
            writer.WriteUInt16(11);
            writer.Align(4);

            writer.WriteUInt32(0x4C454146);
            writer.WriteInt32(0);
            writer.Align(4);

            writer.WriteUInt32(0x4C454146);
            writer.WriteInt32(0);
            writer.WriteInt32(1);
            writer.WriteVector3(Vector3.Zero);
            writer.WriteSingle(1f);
            writer.WriteUInt32(0);
            writer.Align(4);

            writer.WriteUInt32(1); // has drawing BSP
            WriteLegacyDrawingPortNode(writer);
            writer.Align(4);

            return buffer.AsSpan(0, writer.Offset).ToArray();
        }

        private static void WriteLegacyDrawingPortNode(DatBinWriter writer) {
            writer.WriteUInt32(0x504F5254); // PORT
            writer.WriteVector3(Vector3.UnitZ);
            writer.WriteSingle(0f);

            writer.WriteUInt32(0x4C454146);
            writer.WriteInt32(0);
            writer.Align(4);

            writer.WriteUInt32(0x4C454146);
            writer.WriteInt32(1);
            writer.Align(4);

            writer.WriteVector3(Vector3.Zero);
            writer.WriteSingle(2f);
            writer.WriteUInt32(0);
            writer.WriteUInt32(1);
            writer.WriteInt16(11);
            writer.WriteInt16(0);
            writer.Align(4);
        }
    }
}
