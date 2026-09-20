using System.Buffers.Binary;
using System.Numerics;
using System.Text;
using Acme.Dat;
using WorldBuilder.Shared.Lib;

namespace WorldBuilder.Tests {
    public sealed class LegacyDatConversionTests {
        [Fact]
        public void Audit_TracksSyntheticFallbackAndDeferredObjects() {
            string dir = CreateLegacyDatFixture(includeLanguageDat: true);

            try {
                var audit = LegacyDatConversionService.Audit(dir);

                Assert.Equal("Dark Majesty", audit.SourceVersion);
                Assert.True(audit.HasLanguageDat);

                var renderSurface = Assert.Single(audit.Types.Where(type => type.ObjectType == "RenderSurface"));
                Assert.Equal(1, renderSurface.SourceCount);
                Assert.Equal(1, renderSurface.ConvertibleCount);
                Assert.Equal(1, renderSurface.SyntheticCount);

                var region = Assert.Single(audit.Types.Where(type => type.ObjectType == "Region"));
                Assert.Equal(1, region.SourceCount);
                Assert.Equal(1, region.ConvertibleCount);
                Assert.Equal(1, region.FallbackCount);

                var gfxObj = Assert.Single(audit.Types.Where(type => type.ObjectType == "GfxObj"));
                Assert.Equal(1, gfxObj.SourceCount);
                Assert.Equal(1, gfxObj.ConvertibleCount);
                Assert.Equal(0, gfxObj.DeferredCount);

                var stringTable = Assert.Single(audit.Types.Where(type => type.ObjectType == "StringTable"));
                Assert.Equal(1, stringTable.SourceCount);
                Assert.Equal(1, stringTable.DeferredCount);

                Assert.Contains(audit.Findings, finding =>
                    finding.ObjectType == "RenderSurface"
                    && finding.Message.Contains("synthetic", StringComparison.OrdinalIgnoreCase));
                Assert.Contains(audit.Findings, finding =>
                    finding.ObjectType == "Region"
                    && finding.Message.Contains("fallback", StringComparison.OrdinalIgnoreCase));
                Assert.Contains(audit.Findings, finding =>
                    finding.ObjectType == "client_local_English.dat"
                    && finding.Message.Contains("deferred", StringComparison.OrdinalIgnoreCase));
            }
            finally {
                Directory.Delete(dir, recursive: true);
            }
        }

        [Fact]
        public void Audit_TreatsDecodableEnvironmentAsConvertible() {
            string dir = CreateLegacyDatFixture(includeLanguageDat: false, includeEnvironment: true);

            try {
                var audit = LegacyDatConversionService.Audit(dir);

                var environment = Assert.Single(audit.Types.Where(type => type.ObjectType == "Environment"));
                Assert.Equal(1, environment.SourceCount);
                Assert.Equal(1, environment.ConvertibleCount);
                Assert.Equal(0, environment.DeferredCount);
                Assert.Equal(0, environment.UnsupportedCount);
            }
            finally {
                Directory.Delete(dir, recursive: true);
            }
        }

        [Fact]
        public void CollectSetupReferences_ReadsPartIdsWithoutFullUnpack() {
            var parts = new List<uint>();
            bool ok = LegacyDatDecoders.CollectSetupReferences(
                BuildLegacySetupBytes(0x02009999u, 0x01000001u, 0x01000002u),
                parts.Add);

            Assert.True(ok);
            Assert.Equal(new[] { 0x01000001u, 0x01000002u }, parts);
        }

        [Fact]
        public void TrackLandBlockInfo_ClassifiesBuildingModelByIdPrefix() {
            var closure = new LegacyDatWorldReferenceClosure();
            var landBlockInfo = new LandBlockInfo { Id = 0x7D64FFFEu };
            landBlockInfo.Buildings.Add(new BuildingInfo { ModelId = 0x010022D2u });

            LegacyDatWorldReferenceCollector.TrackLandBlockInfo(closure, landBlockInfo);

            Assert.True(closure.Contains("GfxObj", 0x010022D2u));
            Assert.False(closure.Contains("Setup", 0x010022D2u));
        }

        [Fact]
        public void PortalBootstrap_KeepsRetailGlobalIdPrefixes() {
            Assert.True(LegacyDatPortalBootstrap.ShouldKeepRetailPortalGlobal(0x0E00000Eu));
            Assert.True(LegacyDatPortalBootstrap.ShouldKeepRetailPortalGlobal(0x13000000u));
            Assert.False(LegacyDatPortalBootstrap.ShouldKeepRetailPortalGlobal(0x01000022u));
        }

        [Fact]
        public void PortalBootstrap_KeepsClientMetadataPrefixes() {
            Assert.True(LegacyDatPortalBootstrap.ShouldKeepRetailPortalClientMetadata(0x26000001u));
            Assert.True(LegacyDatPortalBootstrap.ShouldKeepRetailPortalClientMetadata(0x32000001u));
            Assert.False(LegacyDatPortalBootstrap.ShouldKeepRetailPortalClientMetadata(0x01000022u));
        }

        [Fact]
        public void TrackEnvCell_UsesFullPortalFileIds() {
            var closure = new LegacyDatWorldReferenceClosure();
            var envCell = new EnvCell {
                EnvironmentId = 0x0363,
            };
            envCell.Surfaces.Add(0x0124);

            LegacyDatWorldReferenceCollector.TrackEnvCell(closure, envCell);

            Assert.True(closure.Contains("Environment", 0x0D000363u));
            Assert.True(closure.Contains("Surface", 0x08000124u));
        }

        [Fact]
        public void ClientExportFixer_SyncHeaderFileSizeUpdatesHeader() {
            string path = Path.Combine(Path.GetTempPath(), $"acme-dat-header-{Guid.NewGuid():N}.bin");
            try {
                const int HeaderOffset = 0x140;
                const int FileSizeFieldOffset = HeaderOffset + 8;
                byte[] data = new byte[4096];
                BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(FileSizeFieldOffset), 1024);
                File.WriteAllBytes(path, data);

                LegacyDatClientExportFixer.SyncHeaderFileSize(path);

                byte[] updated = File.ReadAllBytes(path);
                Assert.Equal(data.Length, BinaryPrimitives.ReadInt32LittleEndian(updated.AsSpan(FileSizeFieldOffset)));
            }
            finally {
                File.Delete(path);
            }
        }

        [Fact]
        public void ClientExportFixer_WriteNativeNoOpTransactionUsesNativeDiskTransactInfoHeader() {
            string path = Path.Combine(Path.GetTempPath(), $"acme-dat-txn-{Guid.NewGuid():N}.bin");
            try {
                File.WriteAllBytes(path, new byte[512]);
                LegacyDatClientExportFixer.WriteNativeNoOpTransaction(path);

                byte[] transaction = File.ReadAllBytes(path).AsSpan(0x100, 8).ToArray();
                Assert.Equal([0x00, 0x50, 0x4C, 0x00, 0x00, 0x00, 0x00, 0x00], transaction);
            }
            finally {
                File.Delete(path);
            }
        }

        [Fact]
        public void ClientExportFixer_RestoresUntouchedRetailCompanionFilesFromSeed() {
            string root = Path.Combine(Path.GetTempPath(), $"acme-dat-restore-{Guid.NewGuid():N}");
            string seedDir = Path.Combine(root, "seed");
            string outputDir = Path.Combine(root, "output");
            Directory.CreateDirectory(seedDir);
            Directory.CreateDirectory(outputDir);

            try {
                foreach (string datFile in LegacyDatClientExportFixer.UntouchedRetailCompanionDatFiles) {
                    byte[] seedBytes = Encoding.UTF8.GetBytes($"seed:{datFile}");
                    byte[] outputBytes = Encoding.UTF8.GetBytes($"mutated:{datFile}");
                    File.WriteAllBytes(Path.Combine(seedDir, datFile), seedBytes);
                    File.WriteAllBytes(Path.Combine(outputDir, datFile), outputBytes);
                }

                var restorePolicy = new LegacyDatExportPolicy {
                    Shell = new LegacyDatClientShellPolicy { Ui = LegacyDatContentSource.Retail },
                };
                LegacyDatClientExportFixer.RestoreUntouchedRetailSeedFiles(seedDir, outputDir, restorePolicy);

                foreach (string datFile in LegacyDatClientExportFixer.UntouchedRetailCompanionDatFiles) {
                    byte[] expected = File.ReadAllBytes(Path.Combine(seedDir, datFile));
                    byte[] actual = File.ReadAllBytes(Path.Combine(outputDir, datFile));
                    Assert.Equal(expected, actual);
                }
            }
            finally {
                Directory.Delete(root, recursive: true);
            }
        }

        [Fact]
        public void ClientExportValidator_PassesOnKnownGoodSlimMergeExport() {
            string exportDir = @"C:\Users\chris\Downloads\madeyoulook-fixtest";
            if (!File.Exists(Path.Combine(exportDir, "client_portal.dat"))) {
                return;
            }

            var policy = LegacyDatExportPolicy.FromLegacyMode(LegacyDatExportMode.SlimMerge);
            var errors = LegacyDatClientExportValidator.Validate(exportDir, policy);
            Assert.Empty(errors);
        }

        [Fact]
        public void DatExportChecksumReport_TracksMissingCounts() {
            var report = new DatExportChecksumReport();
            report.SetTrackedMissing("client_portal.dat", 3);
            Assert.True(report.HasTrackedMissingEntries);
            Assert.Equal(3, report.TrackedPortalMissing);
        }

        [Fact]
        public void ClientExportFixer_SlimMergeLaunchPortalSwap_IsDeprecatedNoOp() {
            string root = Path.Combine(Path.GetTempPath(), $"acme-portal-swap-{Guid.NewGuid():N}");
            string seedDir = Path.Combine(root, "seed");
            string outputDir = Path.Combine(root, "output");
            Directory.CreateDirectory(seedDir);
            Directory.CreateDirectory(outputDir);

            try {
                byte[] convertedPortal = Encoding.UTF8.GetBytes("converted-portal");
                File.WriteAllBytes(Path.Combine(outputDir, "client_portal.dat"), convertedPortal);

#pragma warning disable CS0618
                LegacyDatClientExportFixer.ApplySlimMergeClientLaunchPortal(seedDir, outputDir);
#pragma warning restore CS0618

                Assert.Equal(convertedPortal, File.ReadAllBytes(Path.Combine(outputDir, "client_portal.dat")));
            }
            finally {
                Directory.Delete(root, recursive: true);
            }
        }

        [Fact]
        public void SlimMergeKeepIds_IncludesIterationObject() {
            string seed = @"C:\Users\chris\Downloads\ac-updates";
            if (!File.Exists(Path.Combine(seed, "client_portal.dat"))) {
                return;
            }

            var keepIds = LegacyDatPortalBootstrap.CollectSlimMergeKeepIds(seed, new DatExportChecksumTargets());
            Assert.Contains(LegacyDatPortalFileIds.Iteration, keepIds);
        }

        [Fact]
        public void SlimMergeKeepIds_IncludesCharGenShellAssetsFromRetailSeed() {
            string seed = @"C:\Users\chris\Downloads\ac-updates";
            if (!File.Exists(Path.Combine(seed, "client_portal.dat"))) {
                return;
            }

            var keepIds = LegacyDatPortalBootstrap.CollectSlimMergeKeepIds(seed, new DatExportChecksumTargets());
            var shellIds = LegacyDatRetailClientShellCollector.CollectPortalIds(seed);

            Assert.NotEmpty(shellIds);
            Assert.All(shellIds, id => Assert.Contains(id, keepIds));

            using var reader = new DefaultDatReaderWriter(
                seed,
                DatAccessType.Read,
                FileCachingStrategy.Never,
                IndexCachingStrategy.OnDemand);
            if (reader.TryGet(LegacyDatRetailClientShellCollector.CharGenId, out CharGen? charGen)
                && charGen != null) {
                uint setupId = charGen.HeritageGroups.First().Value.SetupId;
                Assert.Contains(setupId, keepIds);
            }
        }

        [Fact]
        public void LegacyCharGen_DecodesBrandnewDmStartingAreas() {
            string legacy = @"C:\Users\chris\OneDrive\Documents\ACME WorldBuilder\Projects\Brandnewdm\dats\base";
            if (!File.Exists(Path.Combine(legacy, "portal.dat"))) {
                return;
            }

            using var reader = new LegacyDatReader(legacy);
            Assert.True(reader.TryGet<CharGen>(LegacyDatCharGenMapper.CharGenId, out var charGen));
            Assert.NotNull(charGen);
            Assert.True(charGen!.StartingAreas.Count >= 3);
        }

        [Fact]
        public void LegacyCharGen_DecodesDarkMajestyStartingAreas() {
            string legacy = @"C:\Users\chris\OneDrive\Documents\ACME WorldBuilder\Projects\darkmaj\dats\base";
            if (!File.Exists(Path.Combine(legacy, "portal.dat"))) {
                return;
            }

            using var reader = new LegacyDatReader(legacy);
            Assert.True(reader.TryGet<CharGen>(LegacyDatCharGenMapper.CharGenId, out var charGen));
            Assert.NotNull(charGen);

            // Full pre-ToD decode: the DM client table has exactly 18 starting areas (2 locations
            // each) and 3 real heritage groups. The old heuristic scanner reported 19 areas with
            // >100 phantom locations; these expectations pin the byte-exact decode instead.
            Assert.Equal(18, charGen!.StartingAreas.Count);
            Assert.Equal(3, charGen.HeritageGroups.Count);

            foreach (var area in charGen.StartingAreas) {
                Assert.False(string.IsNullOrWhiteSpace(area.Name?.ToString()));
                Assert.Equal(2, area.Locations.Count);
            }

            var nonZeroCells = charGen.StartingAreas
                .SelectMany(area => area.Locations)
                .Where(loc => loc.CellId != 0)
                .Select(loc => loc.CellId)
                .ToList();

            Assert.Equal(36, nonZeroCells.Count);
            Assert.Contains(0xA9B00014u, nonZeroCells);
            Assert.DoesNotContain(0x860201ADu, nonZeroCells);

            // Heritage groups carry real DM setup refs (0x02 band), not synthetic 0x31 text ids.
            foreach (var group in charGen.HeritageGroups.Values) {
                Assert.Equal(0x02u, group.SetupId >> 24);
            }
        }

        [Fact]
        public void EnvCellExportFixer_RewritesOtherPortalIdForOutsidePortals() {
            var inside = new EnvCell {
                Id = 0xA9B00100u,
                EnvironmentId = 1,
                CellStructure = 1,
                CellPortals = {
                    new CellPortal {
                        Flags = 0,
                        PolygonId = 6,
                        OtherCellId = 0x0101,
                        OtherPortalId = 99,
                    },
                },
            };

            var exit = new EnvCell {
                Id = 0xA9B00101u,
                EnvironmentId = 1,
                CellStructure = 1,
                Position = new Frame {
                    Origin = new System.Numerics.Vector3(60f, 60f, 20f),
                    Orientation = System.Numerics.Quaternion.Identity,
                },
                VisibleCells = { 0x0100, 0x0102 },
                CellPortals = {
                    new CellPortal {
                        Flags = 0,
                        PolygonId = 7,
                        OtherCellId = 0x0100,
                        OtherPortalId = 99,
                    },
                    new CellPortal {
                        Flags = 0,
                        PolygonId = 8,
                        OtherCellId = 0xFFFF,
                        OtherPortalId = 65535,
                    },
                },
            };

            inside.CellPortals[0].OtherCellId = 0x0101;
            var cells = new List<EnvCell> { inside, exit };

            int rewritten = LegacyDatEnvCellExportFixer.RewriteOtherPortalIndices(cells);
            rewritten += LegacyDatEnvCellExportFixer.FixOutsidePortalOtherPortalIds(cells);

            Assert.True(rewritten >= 3);
            Assert.Equal(0u, inside.CellPortals[0].OtherPortalId);
            Assert.Equal(0u, exit.CellPortals[0].OtherPortalId);
            Assert.Equal(0u, exit.CellPortals[1].OtherPortalId);
            Assert.DoesNotContain(exit.VisibleCells, vc => vc is >= 0x0001 and <= 0x0040);
            Assert.Empty(exit.StaticObjects);
        }

        [Fact]
        public void SlimMergeKeepIds_IncludesUiShellPortalAssetsFromRetailSeed() {
            string seed = @"C:\Users\chris\Downloads\ac-updates";
            if (!File.Exists(Path.Combine(seed, "client_portal.dat"))) {
                return;
            }

            var policy = LegacyDatExportPolicy.FromPreset(LegacyDatExportPreset.HalfDm);
            var keepIds = LegacyDatPortalBootstrap.CollectSlimMergeKeepIds(seed, new DatExportChecksumTargets(), policy);
            var uiShellIds = LegacyDatRetailUiShellCollector.CollectPortalIds(seed);

            Assert.True(LegacyDatMergeRuleResolver.ShouldMergeRetailUiShellAssets(policy));
            Assert.NotEmpty(uiShellIds);
            Assert.All(uiShellIds, id => Assert.Contains(id, keepIds));
        }

        [Fact]
        public void RewriteOtherPortalIndices_PreservesExistingValidBacklinkIndex() {
            var source = new EnvCell {
                Id = 0x02720147u,
                CellPortals = {
                    new CellPortal {
                        PolygonId = 0x0007,
                        OtherCellId = 0x0294,
                        OtherPortalId = 6,
                    }
                }
            };

            var target = new EnvCell {
                Id = 0x02720294u,
                CellPortals = {
                    new CellPortal { PolygonId = 0x0034, OtherCellId = 0x0279, OtherPortalId = 1 },
                    new CellPortal { PolygonId = 0x0011, OtherCellId = 0x0147, OtherPortalId = 2 },
                    new CellPortal { PolygonId = 0x0033, OtherCellId = 0x0298, OtherPortalId = 0 },
                    new CellPortal { PolygonId = 0x0031, OtherCellId = 0x0296, OtherPortalId = 0 },
                    new CellPortal { PolygonId = 0x0032, OtherCellId = 0x0297, OtherPortalId = 0 },
                    new CellPortal { PolygonId = 0x0035, OtherCellId = 0x02A6, OtherPortalId = 3 },
                    new CellPortal { PolygonId = 0x000F, OtherCellId = 0x0147, OtherPortalId = 3 },
                    new CellPortal { PolygonId = 0x0010, OtherCellId = 0x0147, OtherPortalId = 4 },
                }
            };

            int rewritten = LegacyDatEnvCellExportFixer.RewriteOtherPortalIndices(new List<EnvCell> { source, target });

            Assert.True(rewritten >= 0);
            Assert.Equal((ushort)6, source.CellPortals[0].OtherPortalId);
        }

        [Fact]
        public void ReorderCellPortalsToMatchCellStruct_ReordersSafeOneToOneCases() {
            var cell = new EnvCell {
                Id = 0x12340100u,
                EnvironmentId = 1,
                CellStructure = 7,
                CellPortals = {
                    new CellPortal { PolygonId = 0x0008, OtherCellId = 0x0102, OtherPortalId = 1 },
                    new CellPortal { PolygonId = 0x0006, OtherCellId = 0x0101, OtherPortalId = 0 },
                    new CellPortal { PolygonId = 0x0007, OtherCellId = 0x0103, OtherPortalId = 2 },
                },
            };

            var cellStruct = new CellStruct {
                Polygons = new Dictionary<ushort, Polygon> {
                    [0x0006] = new Polygon(),
                    [0x0007] = new Polygon(),
                    [0x0008] = new Polygon(),
                },
                Portals = new List<ushort> { 0x0006, 0x0007, 0x0008 },
            };

            bool changed = LegacyDatEnvCellExportFixer.TryReorderCellPortalsToMatchCellStruct(cell, cellStruct);

            Assert.True(changed);
            Assert.Equal(new ushort[] { 0x0006, 0x0007, 0x0008 }, cell.CellPortals.Select(cp => (ushort)cp.PolygonId).ToArray());
        }

        [Fact]
        public void ReorderCellPortalsToMatchCellStruct_SkipsDuplicatePolygonCases() {
            var cell = new EnvCell {
                Id = 0x12340100u,
                EnvironmentId = 1,
                CellStructure = 7,
                CellPortals = {
                    new CellPortal { PolygonId = 0x0007, OtherCellId = 0x0101, OtherPortalId = 0 },
                    new CellPortal { PolygonId = 0x0007, OtherCellId = 0x0102, OtherPortalId = 1 },
                    new CellPortal { PolygonId = 0x0008, OtherCellId = 0x0103, OtherPortalId = 2 },
                },
            };

            var cellStruct = new CellStruct {
                Polygons = new Dictionary<ushort, Polygon> {
                    [0x0006] = new Polygon(),
                    [0x0007] = new Polygon(),
                    [0x0008] = new Polygon(),
                },
                Portals = new List<ushort> { 0x0006, 0x0007, 0x0008 },
            };

            bool changed = LegacyDatEnvCellExportFixer.TryReorderCellPortalsToMatchCellStruct(cell, cellStruct);

            Assert.False(changed);
            Assert.Equal(new ushort[] { 0x0007, 0x0007, 0x0008 }, cell.CellPortals.Select(cp => (ushort)cp.PolygonId).ToArray());
        }

        [Fact]
        public void SpecializeSharedCellStructPortalSequences_ClonesStructWhenCellsNeedDifferentPortalShapes() {
            var environment = new Acme.Dat.Environment {
                Id = 0x0D000001,
                Cells = {
                    [7] = new CellStruct {
                        VertexArray = new VertexArray(),
                        Polygons = new Dictionary<ushort, Polygon> {
                            [0x0006] = new Polygon(),
                            [0x0007] = new Polygon(),
                            [0x0008] = new Polygon(),
                        },
                        Portals = new List<ushort> { 0x0006, 0x0008, 0x0007 },
                        CellBSP = new CellBSPTree {
                            Root = new CellBSPNode { Type = BSPNodeType.Leaf },
                        },
                        PhysicsBSP = new PhysicsBSPTree {
                            Root = new PhysicsBSPNode { Type = BSPNodeType.Leaf },
                        },
                    },
                },
            };

            var primary = new EnvCell {
                Id = 0x12340100u,
                EnvironmentId = 1,
                CellStructure = 7,
                CellPortals = {
                    new CellPortal { PolygonId = 0x0006, OtherCellId = 0x0101, OtherPortalId = 0 },
                    new CellPortal { PolygonId = 0x0008, OtherCellId = 0x0102, OtherPortalId = 1 },
                    new CellPortal { PolygonId = 0x0007, OtherCellId = 0x0103, OtherPortalId = 2 },
                },
            };

            var duplicateDoorway = new EnvCell {
                Id = 0x12340101u,
                EnvironmentId = 1,
                CellStructure = 7,
                CellPortals = {
                    new CellPortal { PolygonId = 0x0006, OtherCellId = 0x0104, OtherPortalId = 0 },
                    new CellPortal { PolygonId = 0x0007, OtherCellId = 0x0105, OtherPortalId = 1 },
                    new CellPortal { PolygonId = 0x0007, OtherCellId = 0x0106, OtherPortalId = 2 },
                    new CellPortal { PolygonId = 0x0008, OtherCellId = 0x0107, OtherPortalId = 3 },
                },
            };

            bool changed = LegacyDatEnvCellExportFixer.TrySpecializeSharedCellStructPortalSequences(
                environment,
                new[] { primary, duplicateDoorway },
                out var changedCells,
                out int variantsAdded);

            Assert.True(changed);
            Assert.Equal(1, variantsAdded);
            Assert.Single(changedCells);
            Assert.Equal((ushort)7, primary.CellStructure);
            Assert.NotEqual((ushort)7, duplicateDoorway.CellStructure);
            Assert.True(environment.Cells.ContainsKey(duplicateDoorway.CellStructure));
            Assert.Equal(
                new ushort[] { 0x0006, 0x0008, 0x0007 },
                environment.Cells[7].Portals.ToArray());
            Assert.Equal(
                new ushort[] { 0x0006, 0x0007, 0x0007, 0x0008 },
                environment.Cells[duplicateDoorway.CellStructure].Portals.ToArray());
        }

        [Fact]
        public void SpecializeSharedCellStructPortalSequences_KeepsSharedStructWhenPortalShapesMatch() {
            var environment = new Acme.Dat.Environment {
                Id = 0x0D000001,
                Cells = {
                    [7] = new CellStruct {
                        VertexArray = new VertexArray(),
                        Polygons = new Dictionary<ushort, Polygon> {
                            [0x0006] = new Polygon(),
                            [0x0007] = new Polygon(),
                            [0x0008] = new Polygon(),
                        },
                        Portals = new List<ushort> { 0x0006, 0x0008, 0x0007 },
                        CellBSP = new CellBSPTree {
                            Root = new CellBSPNode { Type = BSPNodeType.Leaf },
                        },
                        PhysicsBSP = new PhysicsBSPTree {
                            Root = new PhysicsBSPNode { Type = BSPNodeType.Leaf },
                        },
                    },
                },
            };

            var first = new EnvCell {
                Id = 0x12340100u,
                EnvironmentId = 1,
                CellStructure = 7,
                CellPortals = {
                    new CellPortal { PolygonId = 0x0006, OtherCellId = 0x0101, OtherPortalId = 0 },
                    new CellPortal { PolygonId = 0x0008, OtherCellId = 0x0102, OtherPortalId = 1 },
                    new CellPortal { PolygonId = 0x0007, OtherCellId = 0x0103, OtherPortalId = 2 },
                },
            };

            var second = new EnvCell {
                Id = 0x12340101u,
                EnvironmentId = 1,
                CellStructure = 7,
                CellPortals = {
                    new CellPortal { PolygonId = 0x0006, OtherCellId = 0x0104, OtherPortalId = 0 },
                    new CellPortal { PolygonId = 0x0008, OtherCellId = 0x0105, OtherPortalId = 1 },
                    new CellPortal { PolygonId = 0x0007, OtherCellId = 0x0106, OtherPortalId = 2 },
                },
            };

            bool changed = LegacyDatEnvCellExportFixer.TrySpecializeSharedCellStructPortalSequences(
                environment,
                new[] { first, second },
                out var changedCells,
                out int variantsAdded);

            Assert.False(changed);
            Assert.Equal(0, variantsAdded);
            Assert.Empty(changedCells);
            Assert.Single(environment.Cells);
            Assert.Equal((ushort)7, first.CellStructure);
            Assert.Equal((ushort)7, second.CellStructure);
        }

        [Fact]
        public void RetailUiShellCollector_DoesNotIncludeRegion() {
            string seed = @"C:\Users\chris\Downloads\ac-updates";
            if (!File.Exists(Path.Combine(seed, "client_portal.dat"))) {
                return;
            }

            var uiShellIds = LegacyDatRetailUiShellCollector.CollectPortalIds(seed);
            Assert.DoesNotContain(0x13000000u, uiShellIds);
        }

        [Fact]
        public void ExportPolicy_BothModes_IncludeConnectionUiShellByDefault() {
            var fullDm = LegacyDatExportPolicy.FromPreset(LegacyDatExportPreset.FullDm);
            Assert.True(fullDm.Shell.ConnectionLoginUiShell);
            Assert.True(fullDm.Shell.PatchConnectionScreenStrings);
            Assert.True(fullDm.Shell.PatchInGameLayoutShape);
            Assert.True(LegacyDatMergeRuleResolver.ShouldMergeRetailUiShellAssets(fullDm));
            Assert.True(LegacyDatMergeRuleResolver.ShouldPatchConnectionScreenStrings(fullDm));
            Assert.True(LegacyDatMergeRuleResolver.ShouldPatchInGameLayoutShape(fullDm));

            var halfDm = LegacyDatExportPolicy.FromPreset(LegacyDatExportPreset.HalfDm);
            Assert.True(halfDm.Shell.ConnectionLoginUiShell);
            Assert.False(halfDm.Shell.PatchConnectionScreenStrings);
            Assert.False(halfDm.Shell.PatchInGameLayoutShape);
            Assert.True(LegacyDatMergeRuleResolver.ShouldMergeRetailUiShellAssets(halfDm));
            Assert.False(LegacyDatMergeRuleResolver.ShouldPatchConnectionScreenStrings(halfDm));
            Assert.False(LegacyDatMergeRuleResolver.ShouldPatchInGameLayoutShape(halfDm));
        }

        [Fact]
        public void ConnectionStringMap_LoadsEmbeddedEntries() {
            var map = LegacyDatDmStringTableMap.LoadEmbeddedConnectionMap();
            Assert.True(map.Entries.Count >= 15);
            Assert.Contains(map.Entries, e => e.RetailKey == 3318519);
            Assert.Contains(map.Entries, e => e.RetailTableId == "0x23000010" && e.RetailKey == 164627218);
        }

        [Fact]
        public void PreGameStringMap_LoadsEmbeddedEntries() {
            var map = LegacyDatDmStringTableMap.LoadEmbeddedMap(LegacyDatDmStringTableMapNames.PreGameExe);
            Assert.True(map.Entries.Count >= 10);
            Assert.Contains(map.Entries, e => e.Screen == "intro" && e.Gap && e.RetailKey == 105320788);
            Assert.Contains(map.Entries, e => e.Screen == "credits" && e.DmText == "Restore Character");
        }

        [Fact]
        public void CharGenLayoutSpec_LoadsEmbeddedEntries() {
            var spec = LegacyDatDmLayoutSpec.LoadEmbeddedSpec(LegacyDatDmLayoutSpecNames.CharGenWizard);
            Assert.True(spec.Layouts.Count >= 8);
            Assert.Contains(spec.Layouts, layout => layout.LayoutIdValue == 0x21000038);
            Assert.Contains(spec.Layouts, layout => layout.LayoutIdValue == 0x21000049 && layout.HardcodedProgressState == 4);
            Assert.True(spec.BlockedFeatures.Count >= 3);
            Assert.Contains(spec.BlockedFeatures, blocked => blocked.Area == "heraldry_page");
            Assert.True(spec.Operations.Count >= 90);
            Assert.Contains(spec.Operations, op =>
                op.LayoutIdValue == 0x21000049
                && op.ElementIdValue == 0x100003A9
                && op.X == 32
                && op.Y == 154);
        }

        [Fact]
        public void InGameLayoutSpec_LoadsEmbeddedEntries() {
            var spec = LegacyDatDmLayoutSpec.LoadEmbeddedSpec(LegacyDatDmLayoutSpecNames.InGamePanels);
            Assert.Single(spec.Layouts);
            Assert.Contains(spec.Layouts, layout => layout.LayoutIdValue == 0x21000032);
            Assert.Single(spec.Operations);
            Assert.Contains(spec.Operations, op =>
                op.LayoutIdValue == 0x21000032
                && op.ElementIdValue == 0x100005C0
                && op.X == 0
                && op.Y == 0
                && op.Width == 0
                && op.Height == 0);
        }

        [Fact]
        public void CharGenLayoutSpec_ValidatesBaseLayoutChainsOnRetailSeed() {
            string seed = @"C:\Users\chris\Downloads\ac-updates";
            if (!File.Exists(Path.Combine(seed, "client_local_English.dat"))) {
                return;
            }

            using var writer = new DefaultDatReaderWriter(
                seed,
                DatAccessType.Read,
                FileCachingStrategy.Never,
                IndexCachingStrategy.OnDemand);

            var spec = LegacyDatDmLayoutSpec.LoadEmbeddedSpec(LegacyDatDmLayoutSpecNames.CharGenWizard);
            var report = LegacyDatDmLayoutValidator.ValidateSpecAgainstLocalDat(writer, spec, warnings: null);

            Assert.Equal(0, report.MissingLayouts);
            Assert.Equal(spec.Layouts.Count, report.ReadableLayouts);
            Assert.Equal(0, report.BaseRefIssues);
            Assert.Equal(0, report.OperationIssues);
        }

        [Fact]
        public void InGameLayoutSpec_ValidatesBaseLayoutChainsOnRetailSeed() {
            string seed = @"C:\Users\chris\Downloads\ac-updates";
            if (!File.Exists(Path.Combine(seed, "client_local_English.dat"))) {
                return;
            }

            using var writer = new DefaultDatReaderWriter(
                seed,
                DatAccessType.Read,
                FileCachingStrategy.Never,
                IndexCachingStrategy.OnDemand);

            var spec = LegacyDatDmLayoutSpec.LoadEmbeddedSpec(LegacyDatDmLayoutSpecNames.InGamePanels);
            var report = LegacyDatDmLayoutValidator.ValidateSpecAgainstLocalDat(writer, spec, warnings: null);

            Assert.Equal(0, report.MissingLayouts);
            Assert.Equal(spec.Layouts.Count, report.ReadableLayouts);
            Assert.Equal(0, report.BaseRefIssues);
            Assert.Equal(0, report.OperationIssues);
        }

        [Fact]
        public void ConnectionScreenStrings_PatchAppliesDmLogonPrefix() {
            string seed = @"C:\Users\chris\Downloads\ac-updates";
            if (!File.Exists(Path.Combine(seed, "client_local_English.dat"))) {
                return;
            }

            string root = Path.Combine(Path.GetTempPath(), $"acme-conn-patch-{Guid.NewGuid():N}");
            string outDir = Path.Combine(root, "out");
            try {
                Directory.CreateDirectory(outDir);
                foreach (string name in new[] {
                    "client_local_English.dat",
                    "client_local_English.dat.meta",
                }) {
                    string src = Path.Combine(seed, name);
                    if (File.Exists(src)) {
                        File.Copy(src, Path.Combine(outDir, name), overwrite: true);
                    }
                }

                using var writer = new DefaultDatReaderWriter(
                    outDir,
                    DatAccessType.ReadWrite,
                    FileCachingStrategy.Never,
                    IndexCachingStrategy.OnDemand);

                var report = LegacyDatDmStringTableExporter.ApplyConnectionScreenStrings(
                    writer,
                    warnings: null,
                    reportPath: null);

                Assert.True(report.Patched >= 10, $"Expected many connection patches, got {report.Patched}");
                Assert.NotNull(report.Coverage);
                Assert.Contains(report.Coverage!, row => row.Screen == "connection" && row.Failed == 0 && row.Gap >= 2);

                Assert.True(writer.Local().TryGet<StringTable>(0x23000010, out var errors) && errors?.Strings != null);
                Assert.True(errors!.Strings.TryGetValue(164627218, out var logonRow) && logonRow != null);
                string logonText = string.Join(
                    " ",
                    logonRow.Strings);
                Assert.StartsWith("Client:", logonText, StringComparison.Ordinal);
                Assert.Contains("logon server", logonText, StringComparison.OrdinalIgnoreCase);
            }
            finally {
                if (Directory.Exists(root)) {
                    Directory.Delete(root, recursive: true);
                }
            }
        }

        [Fact]
        public void CatalogEntryReplacer_RewritesCellIdsWithoutLeavingDuplicates() {
            string seedPath = @"C:\Users\chris\Downloads\ac-updates\client_cell_1.dat";
            if (!File.Exists(seedPath)) {
                return;
            }

            string tempPath = Path.Combine(Path.GetTempPath(), $"acme-cell-replace-{Guid.NewGuid():N}.dat");
            File.Copy(seedPath, tempPath, overwrite: true);

            try {
                DatExportFixer.PatchFreeBlocksBeforeExport(tempPath);

                using var cell = new CellDatabase(tempPath, DatAccessType.ReadWrite);
                uint landBlockInfoId = cell.Tree
                    .Select(file => file.Id)
                    .First(id => (id & 0xFFFF) == 0xFFFE);

                Assert.True(cell.TryGet<LandBlockInfo>(landBlockInfoId, out var landBlockInfo));
                Assert.NotNull(landBlockInfo);
                Assert.Equal(1, LegacyDatCatalogEntryReplacer.CountCatalogEntries(cell, landBlockInfoId));

                Assert.True(LegacyDatCatalogEntryReplacer.TryReplaceFile(cell, landBlockInfo!, cell.Iteration.CurrentIteration));
                Assert.Equal(1, LegacyDatCatalogEntryReplacer.CountCatalogEntries(cell, landBlockInfoId));

                Assert.True(LegacyDatCatalogEntryReplacer.TryReplaceFile(cell, landBlockInfo!, cell.Iteration.CurrentIteration));
                Assert.Equal(1, LegacyDatCatalogEntryReplacer.CountCatalogEntries(cell, landBlockInfoId));
            }
            finally {
                File.Delete(tempPath);
            }
        }

        [Fact]
        public void CatalogEntryReplacer_RewritesLocalIdsWithoutLeavingDuplicates() {
            string seedPath = @"C:\Users\chris\Downloads\ac-updates\client_local_English.dat";
            if (!File.Exists(seedPath)) {
                return;
            }

            string tempPath = Path.Combine(Path.GetTempPath(), $"acme-local-replace-{Guid.NewGuid():N}.dat");
            File.Copy(seedPath, tempPath, overwrite: true);

            try {
                DatExportFixer.PatchFreeBlocksBeforeExport(tempPath);

                using var local = new LocalDatabase(tempPath, DatAccessType.ReadWrite);
                const uint tableId = 0x23000010u;
                Assert.True(local.TryGet<StringTable>(tableId, out var table));
                Assert.NotNull(table);
                Assert.Equal(1, LegacyDatCatalogEntryReplacer.CountCatalogEntries(local, tableId));

                Assert.True(LegacyDatCatalogEntryReplacer.TryReplaceFile(local, table!, local.Iteration.CurrentIteration));
                Assert.Equal(1, LegacyDatCatalogEntryReplacer.CountCatalogEntries(local, tableId));

                Assert.True(LegacyDatCatalogEntryReplacer.TryReplaceFile(local, table!, local.Iteration.CurrentIteration));
                Assert.Equal(1, LegacyDatCatalogEntryReplacer.CountCatalogEntries(local, tableId));
            }
            finally {
                File.Delete(tempPath);
            }
        }

        [Fact]
        public void DefaultDatReaderWriter_ReadWriteOpen_RepairsStaleHeaderFileSize() {
            string seedDir = @"C:\Users\chris\Downloads\ac-updates";
            if (!File.Exists(Path.Combine(seedDir, "client_local_English.dat"))) {
                return;
            }

            string root = Path.Combine(Path.GetTempPath(), $"acme-header-sync-{Guid.NewGuid():N}");
            Directory.CreateDirectory(root);

            try {
                foreach (string datFile in new[] {
                    "client_cell_1.dat",
                    "client_portal.dat",
                    "client_local_English.dat",
                    "client_highres.dat",
                }) {
                    File.Copy(Path.Combine(seedDir, datFile), Path.Combine(root, datFile), overwrite: true);
                }

                string localPath = Path.Combine(root, "client_local_English.dat");
                byte[] bytes = File.ReadAllBytes(localPath);
                int staleSize = bytes.Length - 1024;
                BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(0x140 + 8, 4), staleSize);
                File.WriteAllBytes(localPath, bytes);

                using (var writer = new DefaultDatReaderWriter(
                           root,
                           DatAccessType.ReadWrite,
                           FileCachingStrategy.Never,
                           IndexCachingStrategy.OnDemand)) {
                    Assert.NotNull(writer.Dats);
                }

                byte[] repaired = File.ReadAllBytes(localPath);
                int headerSize = BinaryPrimitives.ReadInt32LittleEndian(repaired.AsSpan(0x140 + 8, 4));
                Assert.Equal(repaired.Length, headerSize);
            }
            finally {
                Directory.Delete(root, recursive: true);
            }
        }

        [Fact]
        public void RemoveExistingPortalEntry_RemovesSingleRetailSeedEntryBeforeRewrite() {
            string seedPath = @"C:\Users\chris\Downloads\ac-updates\client_portal.dat";
            if (!File.Exists(seedPath)) {
                return;
            }

            string tempPath = Path.Combine(Path.GetTempPath(), $"acme-portal-replace-{Guid.NewGuid():N}.dat");
            File.Copy(seedPath, tempPath, overwrite: true);

            try {
                DatExportFixer.PatchFreeBlocksBeforeExport(tempPath);

                using var portal = new DefaultDatReaderWriter(tempPath, DatAccessType.ReadWrite);
                uint setupId = portal.Tree
                    .Select(file => file.Id)
                    .First(id => (id >> 24) == 0x02);

                Assert.True(portal.TryGetFileBytes(setupId, out byte[]? bytes, autoDecompress: false));
                Assert.NotNull(bytes);
                Assert.Equal(1, LegacyDatPortalCatalogDeduplicator.CountCatalogEntries(portal, setupId));

                LegacyDatPortalCatalogDeduplicator.RemoveExistingPortalEntry(portal, setupId);
                Assert.Equal(0, LegacyDatPortalCatalogDeduplicator.CountCatalogEntries(portal, setupId));

                Assert.True(portal.TryWriteFileBytes(setupId, bytes!, bytes!.Length, portal.Iteration.CurrentIteration));
                Assert.Equal(1, LegacyDatPortalCatalogDeduplicator.CountCatalogEntries(portal, setupId));
            }
            finally {
                File.Delete(tempPath);
            }
        }

        [Fact]
        public void TryReplacePortalFile_ReusesRetailSeedSlotWithoutGrowingPortalDat() {
            string seedPath = @"C:\Users\chris\Downloads\ac-updates\client_portal.dat";
            if (!File.Exists(seedPath)) {
                return;
            }

            string tempPath = Path.Combine(Path.GetTempPath(), $"acme-portal-overlay-{Guid.NewGuid():N}.dat");
            File.Copy(seedPath, tempPath, overwrite: true);

            try {
                DatExportFixer.PatchFreeBlocksBeforeExport(tempPath);

                using var seed = new DefaultDatReaderWriter(seedPath, DatAccessType.Read);
                using var output = new DefaultDatReaderWriter(tempPath, DatAccessType.ReadWrite);
                uint setupId = seed.Portal().Tree
                    .Select(file => file.Id)
                    .First(id => (id >> 24) == 0x02);
                var templates = seed.Portal().Tree.ToDictionary(file => file.Id);
                long initialLength = new FileInfo(tempPath).Length;

                Assert.True(LegacyDatPortalCatalogDeduplicator.TryReplacePortalFile(
                    seed,
                    output,
                    setupId,
                    output.Iteration.CurrentIteration,
                    templates));
                long afterFirstReplace = new FileInfo(tempPath).Length;

                Assert.True(LegacyDatPortalCatalogDeduplicator.TryReplacePortalFile(
                    seed,
                    output,
                    setupId,
                    output.Iteration.CurrentIteration,
                    templates));
                long afterSecondReplace = new FileInfo(tempPath).Length;

                Assert.Equal(initialLength, afterFirstReplace);
                Assert.Equal(initialLength, afterSecondReplace);
            }
            finally {
                File.Delete(tempPath);
            }
        }

        [Fact]
        public void GapFillMissingShellGlobals_RetailSeedOverlay_DoesNotGrowPortalDat() {
            string seedDir = @"C:\Users\chris\Downloads\ac-updates";
            if (!File.Exists(Path.Combine(seedDir, "client_portal.dat"))) {
                return;
            }

            string root = Path.Combine(Path.GetTempPath(), $"acme-shell-gapfill-{Guid.NewGuid():N}");
            Directory.CreateDirectory(root);

            try {
                foreach (string datFile in new[] {
                    "client_cell_1.dat",
                    "client_portal.dat",
                    "client_local_English.dat",
                    "client_highres.dat",
                }) {
                    File.Copy(Path.Combine(seedDir, datFile), Path.Combine(root, datFile), overwrite: true);
                }

                string portalPath = Path.Combine(root, "client_portal.dat");
                long initialLength = new FileInfo(portalPath).Length;
                var policy = LegacyDatExportPolicy.FromPreset(LegacyDatExportPreset.HalfDm);

                int copied = LegacyDatFullDmPortalKeeper.GapFillMissingShellGlobals(
                    root,
                    seedDir,
                    policy,
                    portalIteration: null,
                    onProgress: null);
                long finalLength = new FileInfo(portalPath).Length;

                Assert.True(copied > 0);
                Assert.Equal(initialLength, finalLength);
            }
            finally {
                Directory.Delete(root, recursive: true);
            }
        }

        [Fact]
        public void ClientExportFixer_RestoresUntouchedRetailCompanionFiles_KeepsLocalWhenLayoutPatchEnabled() {
            string root = Path.Combine(Path.GetTempPath(), $"acme-dat-restore-layout-{Guid.NewGuid():N}");
            string seedDir = Path.Combine(root, "seed");
            string outputDir = Path.Combine(root, "output");
            Directory.CreateDirectory(seedDir);
            Directory.CreateDirectory(outputDir);

            try {
                foreach (string datFile in LegacyDatClientExportFixer.UntouchedRetailCompanionDatFiles) {
                    byte[] seedBytes = Encoding.UTF8.GetBytes($"seed:{datFile}");
                    byte[] outputBytes = Encoding.UTF8.GetBytes($"mutated:{datFile}");
                    File.WriteAllBytes(Path.Combine(seedDir, datFile), seedBytes);
                    File.WriteAllBytes(Path.Combine(outputDir, datFile), outputBytes);
                }

                var restorePolicy = new LegacyDatExportPolicy {
                    Shell = new LegacyDatClientShellPolicy {
                        Ui = LegacyDatContentSource.Retail,
                        PatchCharGenLayoutShape = true,
                        PatchInGameLayoutShape = true,
                    },
                };

                LegacyDatClientExportFixer.RestoreUntouchedRetailSeedFiles(seedDir, outputDir, restorePolicy);

                byte[] localActual = File.ReadAllBytes(Path.Combine(outputDir, "client_local_English.dat"));
                byte[] highresActual = File.ReadAllBytes(Path.Combine(outputDir, "client_highres.dat"));

                Assert.Equal(Encoding.UTF8.GetBytes("mutated:client_local_English.dat"), localActual);
                Assert.Equal(
                    File.ReadAllBytes(Path.Combine(seedDir, "client_highres.dat")),
                    highresActual);
            }
            finally {
                Directory.Delete(root, recursive: true);
            }
        }

        [Fact]
        public void CharGenLayoutSpec_PatchAppliesSixStateApproximation() {
            string seed = @"C:\Users\chris\Downloads\ac-updates";
            if (!File.Exists(Path.Combine(seed, "client_local_English.dat"))) {
                return;
            }

            string root = Path.Combine(Path.GetTempPath(), $"acme-chargen-layout-preview-{Guid.NewGuid():N}");
            try {
                Directory.CreateDirectory(root);

                using var writer = new DefaultDatReaderWriter(
                    seed,
                    DatAccessType.Read,
                    FileCachingStrategy.Never,
                    IndexCachingStrategy.OnDemand);

                string reportPath = Path.Combine(root, "dm_ui_layout_patch_report.txt");
                var snapshots = new Dictionary<uint, LayoutDesc>();
                var report = LegacyDatDmLayoutExporter.ApplyLayoutSpec(
                    writer,
                    warnings: null,
                    reportPath: reportPath,
                    specName: LegacyDatDmLayoutSpecNames.CharGenWizard,
                    persistChanges: false,
                    patchedLayoutSnapshots: snapshots);

                Assert.True(report.ValidatedLayouts >= 8);
                Assert.Equal(0, report.MissingLayouts);
                Assert.True(report.PatchedLayouts >= 9, $"Expected multiple patched layouts, got {report.PatchedLayouts}");
                Assert.True(report.PatchedElements >= 90, $"Expected many layout element patches, got {report.PatchedElements}");
                Assert.True(report.BlockedFeatures >= 3);
                Assert.True(File.Exists(reportPath));

                Assert.True(snapshots.TryGetValue(0x21000039, out var topTabs) && topTabs != null);
                var topTabRoot = FindLayoutElement(topTabs, 0x100003EEu);
                var appearanceTab = FindLayoutElement(topTabs, 0x100003EEu, 0x100003F2u);
                Assert.Equal(24u, topTabRoot.X);
                Assert.Equal(12u, topTabRoot.Y);
                Assert.Equal(752u, topTabRoot.Width);
                Assert.Equal(56u, topTabRoot.Height);
                Assert.Equal(364u, appearanceTab.X);
                Assert.Equal(130u, appearanceTab.Width);

                Assert.True(snapshots.TryGetValue(0x21000049, out var appearance) && appearance != null);
                var faceToggle = FindLayoutElement(appearance, 0x100003A6u, 0x100003A9u);
                var previewPanel = FindLayoutElement(appearance, 0x100003A6u, 0x100003BAu);
                Assert.Equal(32u, faceToggle.X);
                Assert.Equal(154u, faceToggle.Y);
                Assert.Equal(120u, faceToggle.Width);
                Assert.Equal(548u, previewPanel.X);
                Assert.Equal(384u, previewPanel.Height);

                Assert.True(snapshots.TryGetValue(0x21000046, out var heritage) && heritage != null);
                var firstHeritage = FindLayoutElement(heritage, 0x100003BDu, 0x100003BFu);
                Assert.Equal(36u, firstHeritage.X);
                Assert.Equal(92u, firstHeritage.Y);
                Assert.Equal(276u, firstHeritage.Width);

                Assert.True(snapshots.TryGetValue(0x2100003A, out var footer) && footer != null);
                var randomButton = FindLayoutElement(footer, 0x100003C5u, 0x100003CBu);
                Assert.Equal(574u, randomButton.X);
                Assert.Equal(88u, randomButton.Width);
            }
            finally {
                if (Directory.Exists(root)) {
                    Directory.Delete(root, recursive: true);
                }
            }
        }

        [Fact]
        public void InGameLayoutSpec_PatchHidesRetailOnlyVoidSchool() {
            string seed = @"C:\Users\chris\Downloads\ac-updates";
            if (!File.Exists(Path.Combine(seed, "client_local_English.dat"))) {
                return;
            }

            string root = Path.Combine(Path.GetTempPath(), $"acme-ingame-layout-preview-{Guid.NewGuid():N}");
            try {
                Directory.CreateDirectory(root);

                using var writer = new DefaultDatReaderWriter(
                    seed,
                    DatAccessType.Read,
                    FileCachingStrategy.Never,
                    IndexCachingStrategy.OnDemand);

                string reportPath = Path.Combine(root, "dm_ui_layout_patch_report.txt");
                var snapshots = new Dictionary<uint, LayoutDesc>();
                var report = LegacyDatDmLayoutExporter.ApplyLayoutSpec(
                    writer,
                    warnings: null,
                    reportPath: reportPath,
                    specName: LegacyDatDmLayoutSpecNames.InGamePanels,
                    persistChanges: false,
                    patchedLayoutSnapshots: snapshots);

                Assert.Equal(1, report.ValidatedLayouts);
                Assert.Equal(0, report.MissingLayouts);
                Assert.Equal(1, report.PatchedLayouts);
                Assert.True(report.PatchedElements >= 1);
                Assert.True(File.Exists(reportPath));

                Assert.True(snapshots.TryGetValue(0x21000032, out var spellbook) && spellbook != null);
                var voidButton = FindLayoutElement(spellbook, 0x10000294u, 0x10000297u, 0x100005C0u);
                Assert.Equal(0u, voidButton.X);
                Assert.Equal(0u, voidButton.Y);
                Assert.Equal(0u, voidButton.Width);
                Assert.Equal(0u, voidButton.Height);
            }
            finally {
                if (Directory.Exists(root)) {
                    Directory.Delete(root, recursive: true);
                }
            }
        }

        [Fact]
        public void PreGameScreenStrings_PatchAppliesCharacterManagementLabels() {
            string seed = @"C:\Users\chris\Downloads\ac-updates";
            if (!File.Exists(Path.Combine(seed, "client_local_English.dat"))) {
                return;
            }

            string root = Path.Combine(Path.GetTempPath(), $"acme-pregame-patch-{Guid.NewGuid():N}");
            string outDir = Path.Combine(root, "out");
            try {
                Directory.CreateDirectory(outDir);
                foreach (string name in new[] {
                    "client_local_English.dat",
                    "client_local_English.dat.meta",
                }) {
                    string src = Path.Combine(seed, name);
                    if (File.Exists(src)) {
                        File.Copy(src, Path.Combine(outDir, name), overwrite: true);
                    }
                }

                using var writer = new DefaultDatReaderWriter(
                    outDir,
                    DatAccessType.ReadWrite,
                    FileCachingStrategy.Never,
                    IndexCachingStrategy.OnDemand);

                var report = LegacyDatDmStringTableExporter.ApplyScreenStrings(
                    writer,
                    warnings: null,
                    reportPath: null,
                    LegacyDatDmStringTableMapNames.PreGameExe);

                Assert.True(report.Patched >= 6, $"Expected several pre-game patches, got {report.Patched}");
                Assert.NotNull(report.Coverage);
                Assert.Contains(report.Coverage!, row => row.Screen == "intro" && row.Gap >= 1);
                Assert.Contains(report.Coverage!, row => row.Screen == "credits" && row.Patched >= 3);

                Assert.True(writer.Local().TryGet<StringTable>(0x23000002, out var labels) && labels?.Strings != null);

                Assert.True(labels!.Strings.TryGetValue(184855186, out var restoreRow) && restoreRow != null);
                string restoreText = string.Join(
                    " ",
                    restoreRow.Strings);
                Assert.Equal("Restore Character", restoreText);

                Assert.True(labels.Strings.TryGetValue(197392594, out var deleteRow) && deleteRow != null);
                string deleteText = string.Join(
                    " ",
                    deleteRow.Strings);
                Assert.Equal("Delete Character", deleteText);

                Assert.True(labels.Strings.TryGetValue(149740564, out var exitRow) && exitRow != null);
                string exitText = string.Join(
                    " ",
                    exitRow.Strings);
                Assert.Equal("Exit", exitText);
            }
            finally {
                if (Directory.Exists(root)) {
                    Directory.Delete(root, recursive: true);
                }
            }
        }

        [Fact]
        public void ApplyDmTextToRow_PreservesTemplateSlotsForDynamicRows() {
            string seed = @"C:\Users\chris\Downloads\ac-updates";
            if (!File.Exists(Path.Combine(seed, "client_local_English.dat"))) {
                return;
            }

            using var writer = new DefaultDatReaderWriter(
                seed,
                DatAccessType.Read,
                FileCachingStrategy.Never,
                IndexCachingStrategy.OnDemand);

            Assert.True(writer.Local().TryGet<StringTable>(0x23000001, out var labels) && labels?.Strings != null);

            uint chatSelectedKey = AcClientStringHash.Compute("ID_Chat_TellToSelected");
            Assert.True(labels!.Strings.TryGetValue(chatSelectedKey, out var chatSelectedRow) && chatSelectedRow != null);
            LegacyDatDmStringTableExporter.ApplyDmTextToRowForTesting(chatSelectedRow!, "Tell to ");
            Assert.Equal(2, chatSelectedRow!.Strings.Count);
            Assert.Equal("Tell to ", chatSelectedRow.Strings[0]);
            Assert.Equal(string.Empty, chatSelectedRow.Strings[1]);

            uint fellowRequestKey = AcClientStringHash.Compute("ID_Fellowship_FellowshipRequest");
            Assert.True(labels.Strings.TryGetValue(fellowRequestKey, out var fellowRequestRow) && fellowRequestRow != null);
            LegacyDatDmStringTableExporter.ApplyDmTextToRowForTesting(
                fellowRequestRow!,
                " has invited you to join their fellowship. Do you accept?");
            Assert.Equal(2, fellowRequestRow!.Strings.Count);
            Assert.Equal(string.Empty, fellowRequestRow.Strings[0]);
            Assert.Equal(
                " has invited you to join their fellowship. Do you accept?",
                fellowRequestRow.Strings[1]);
        }

        [Fact]
        public void InGameScreenStrings_PatchAppliesFellowshipAndTradeRows() {
            string seed = @"C:\Users\chris\Downloads\ac-updates";
            if (!File.Exists(Path.Combine(seed, "client_local_English.dat"))) {
                return;
            }

            string root = Path.Combine(Path.GetTempPath(), $"acme-ingame-patch-{Guid.NewGuid():N}");
            string outDir = Path.Combine(root, "out");
            try {
                Directory.CreateDirectory(outDir);
                foreach (string name in new[] {
                    "client_local_English.dat",
                    "client_local_English.dat.meta",
                }) {
                    string src = Path.Combine(seed, name);
                    if (File.Exists(src)) {
                        File.Copy(src, Path.Combine(outDir, name), overwrite: true);
                    }
                }

                using var writer = new DefaultDatReaderWriter(
                    outDir,
                    DatAccessType.ReadWrite,
                    FileCachingStrategy.Never,
                    IndexCachingStrategy.OnDemand);

                var report = LegacyDatDmStringTableExporter.ApplyScreenStrings(
                    writer,
                    warnings: null,
                    reportPath: null,
                    LegacyDatDmStringTableMapNames.InGameExe);

                Assert.True(report.Patched >= 28, $"Expected several in-game patches, got {report.Patched}");
                Assert.NotNull(report.Coverage);
                Assert.Contains(report.Coverage!, row => row.Screen == "main_hud" && row.Patched >= 1 && row.Skipped >= 1);
                Assert.Contains(report.Coverage!, row => row.Screen == "chat" && row.Patched >= 8 && row.Skipped >= 7);
                Assert.Contains(report.Coverage!, row => row.Screen == "inventory" && row.Skipped >= 1);
                Assert.Contains(report.Coverage!, row => row.Screen == "skills" && row.Skipped >= 10 && row.Gap == 0);
                Assert.Contains(report.Coverage!, row => row.Screen == "fellowship" && row.Patched >= 16 && row.Skipped >= 9 && row.Gap >= 1);
                Assert.Contains(report.Coverage!, row => row.Screen == "trade" && row.Patched >= 2);

                Assert.True(writer.Local().TryGet<StringTable>(0x23000001, out var labels) && labels?.Strings != null);
                uint logoffConfirmKey = AcClientStringHash.Compute("ID_Client_LogoffConfirm");
                Assert.True(labels!.Strings.TryGetValue(logoffConfirmKey, out var logoffConfirmRow) && logoffConfirmRow != null);
                Assert.Equal(
                    "This will exit your character from the game world.\\n\\nAre you sure?",
                    string.Join(" ", logoffConfirmRow!.Strings));

                Assert.True(labels!.Strings.TryGetValue(77232035, out var emptyFellowshipRow) && emptyFellowshipRow != null);
                Assert.Equal(
                    "You do not belong to a Fellowship.\\nTo create a fellowship, enter a name in the box below, then click Create Fellowship.\\nOnce you've created a Fellowship, you can start recruiting members.",
                    string.Join(" ", emptyFellowshipRow!.Strings));

                uint dismissKey = AcClientStringHash.Compute("ID_Fellowship_Error_CantDismissSelf");
                Assert.True(labels.Strings.TryGetValue(dismissKey, out var dismissRow) && dismissRow != null);
                Assert.Equal(
                    "You can't dismiss yourself.",
                    string.Join(" ", dismissRow!.Strings));

                uint notInFellowshipKey = AcClientStringHash.Compute("ID_Fellowship_Error_DismisseeNotInFellowship");
                Assert.True(labels.Strings.TryGetValue(notInFellowshipKey, out var notInFellowshipRow) && notInFellowshipRow != null);
                Assert.Equal(
                    "That person isn't in your Fellowship",
                    string.Join(" ", notInFellowshipRow!.Strings));

                Assert.True(labels.Strings.TryGetValue(197229772, out var fellowshipNameRow) && fellowshipNameRow != null);
                Assert.Equal(
                    "FELLOWSHIP NAME:",
                    string.Join(" ", fellowshipNameRow!.Strings));

                uint tellToSelectedKey = AcClientStringHash.Compute("ID_Chat_TellToSelected");
                Assert.True(labels.Strings.TryGetValue(tellToSelectedKey, out var tellToSelectedRow) && tellToSelectedRow != null);
                Assert.Equal("Tell to ", tellToSelectedRow!.Strings[0]);
                Assert.Equal(string.Empty, tellToSelectedRow.Strings[1]);

                uint squelchSelectedKey = AcClientStringHash.Compute("ID_Chat_SquelchSelected");
                Assert.True(labels.Strings.TryGetValue(squelchSelectedKey, out var squelchSelectedRow) && squelchSelectedRow != null);
                Assert.Equal("Squelch (ignore) ", squelchSelectedRow!.Strings[0]);
                Assert.Equal(string.Empty, squelchSelectedRow.Strings[1]);

                Assert.True(writer.TryGet<StringTable>(0x23000003, out var options03) && options03?.Strings != null);
                uint fellowshipShareXpHelpKey = AcClientStringHash.Compute("ID_PlayerOption_FellowshipShareXP_Help");
                Assert.True(options03!.Strings.TryGetValue(fellowshipShareXpHelpKey, out var fellowshipShareXpHelp03Row) && fellowshipShareXpHelp03Row != null);
                Assert.Equal(
                    "Click to share experience in new Fellowships you create.",
                    string.Join(" ", fellowshipShareXpHelp03Row!.Strings));

                uint fellowshipShareLootHelpKey = AcClientStringHash.Compute("ID_PlayerOption_FellowshipShareLoot_Help");
                Assert.True(options03.Strings.TryGetValue(fellowshipShareLootHelpKey, out var fellowshipShareLootHelp03Row) && fellowshipShareLootHelp03Row != null);
                Assert.Equal(
                    "Click to share creature loot in Fellowships you create or join.",
                    string.Join(" ", fellowshipShareLootHelp03Row!.Strings));

                Assert.True(options03!.Strings.TryGetValue(237541360, out var shareXp03Row) && shareXp03Row != null);
                Assert.Equal(
                    "Share Fellowship Experience",
                    string.Join(" ", shareXp03Row!.Strings));

                Assert.True(options03.Strings.TryGetValue(143989828, out var shareLoot03Row) && shareLoot03Row != null);
                Assert.Equal(
                    "Share Fellowship Loot",
                    string.Join(" ", shareLoot03Row!.Strings));

                Assert.True(options03.Strings.TryGetValue(115802515, out var acceptFellowship03Row) && acceptFellowship03Row != null);
                Assert.Equal(
                    "Accept Fellowship Requests",
                    string.Join(" ", acceptFellowship03Row!.Strings));

                Assert.True(writer.TryGet<StringTable>(0x23000005, out var options05) && options05?.Strings != null);
                Assert.True(options05!.Strings.TryGetValue(fellowshipShareXpHelpKey, out var fellowshipShareXpHelp05Row) && fellowshipShareXpHelp05Row != null);
                Assert.Equal(
                    "Click to share experience in new Fellowships you create.",
                    string.Join(" ", fellowshipShareXpHelp05Row!.Strings));

                Assert.True(options05.Strings.TryGetValue(fellowshipShareLootHelpKey, out var fellowshipShareLootHelp05Row) && fellowshipShareLootHelp05Row != null);
                Assert.Equal(
                    "Click to share creature loot in Fellowships you create or join.",
                    string.Join(" ", fellowshipShareLootHelp05Row!.Strings));

                Assert.True(options05!.Strings.TryGetValue(231164339, out var ignoreTradeRow) && ignoreTradeRow != null);
                Assert.Equal(
                    "Ignore All Trade Requests",
                    string.Join(" ", ignoreTradeRow!.Strings));
            }
            finally {
                if (Directory.Exists(root)) {
                    Directory.Delete(root, recursive: true);
                }
            }
        }

        [Fact]
        public void HalfDmPolicy_DoesNotWipeRetailPortalShellBeforeOverlay() {
            var halfDm = LegacyDatExportPolicy.FromPreset(LegacyDatExportPreset.HalfDm);
            Assert.True(LegacyDatMergeRuleResolver.UsesSlimMergeOverlayFinalize(halfDm));
            Assert.True(LegacyDatMergeRuleResolver.UsesFullRetailPortalSeed(halfDm));
            Assert.True(LegacyDatMergeRuleResolver.ShouldMergeRetailUiShellAssets(halfDm));
        }

        [Fact]
        public void HalfDmOverlay_RestoresUiShellPortalBytesFromRetailSeed() {
            string legacy = @"C:\Users\chris\OneDrive\Documents\ACME WorldBuilder\Projects\darkmaj\dats\base";
            string seed = @"C:\Users\chris\Downloads\ac-updates";
            if (!File.Exists(Path.Combine(legacy, "portal.dat"))
                || !File.Exists(Path.Combine(seed, "client_portal.dat"))) {
                return;
            }

            var uiShellIds = LegacyDatRetailUiShellCollector.CollectPortalIds(seed);
            Assert.NotEmpty(uiShellIds);

            string root = Path.Combine(Path.GetTempPath(), $"acme-ui-shell-{Guid.NewGuid():N}");
            string outDir = Path.Combine(root, "out");
            try {
                _ = LegacyDatConversionService.Convert(new LegacyToRetailConversionOptions {
                    LegacyDatDirectory = legacy,
                    RetailSeedDirectory = seed,
                    OutputDirectory = outDir,
                    ExportPolicy = LegacyDatExportPolicy.FromPreset(LegacyDatExportPreset.HalfDm),
                });

                int matched = 0;
                int present = 0;
                foreach (uint id in uiShellIds.Take(64)) {
                    if (!TryReadPortalObjectBytes(outDir, id, out byte[]? outputBytes)) {
                        continue;
                    }

                    present++;
                    if (!TryReadPortalObjectBytes(seed, id, out byte[]? retailBytes)) {
                        continue;
                    }

                    if (retailBytes.AsSpan().SequenceEqual(outputBytes)) {
                        matched++;
                    }
                }

                Assert.True(present > 0, "No UI-shell portal objects present in slim-merge output.");
                Assert.True(matched > 0, "UI-shell portal objects were not restored from retail seed after overlay.");
            }
            finally {
                if (Directory.Exists(root)) {
                    Directory.Delete(root, recursive: true);
                }
            }
        }

        private static bool TryReadPortalObjectBytes(string datDirectory, uint id, out byte[]? bytes) {
            using var reader = new DefaultDatReaderWriter(
                datDirectory,
                DatAccessType.Read,
                FileCachingStrategy.Never,
                IndexCachingStrategy.OnDemand);
            return reader.TryGetFileBytes(DatArchive.Portal, id, out bytes, false) && bytes != null;
        }

        [Fact]
        public void SlimMergeKeepIds_IncludesHighresShellPortalAssetsFromRetailSeed() {
            string seed = @"C:\Users\chris\Downloads\ac-updates";
            if (!File.Exists(Path.Combine(seed, "client_portal.dat"))) {
                return;
            }

            var keepIds = LegacyDatPortalBootstrap.CollectSlimMergeKeepIds(seed, new DatExportChecksumTargets());
            var highresShellIds = LegacyDatRetailHighresShellCollector.CollectPortalIds(seed);

            Assert.NotEmpty(highresShellIds);
            Assert.All(highresShellIds, id => Assert.Contains(id, keepIds));
        }

        [Fact]
        public void ExportModePolicy_MatchesLegacyVsRetailExpectations() {
            Assert.False(LegacyDatExportModePolicy.ShouldPruneRetailCellWorld(LegacyDatExportMode.FullMerge));
            Assert.True(LegacyDatExportModePolicy.ShouldPruneRetailCellWorld(LegacyDatExportMode.SlimMerge));
            Assert.True(LegacyDatExportModePolicy.ShouldPruneRetailCellWorld(LegacyDatExportMode.FullDm));
            Assert.True(LegacyDatExportModePolicy.UsesEmptyCellShell(LegacyDatExportMode.SlimMerge));
            Assert.True(LegacyDatExportModePolicy.UsesEmptyCellShell(LegacyDatExportMode.FullDm));
            Assert.False(LegacyDatExportModePolicy.UsesEmptyCellShell(LegacyDatExportMode.FullMerge));
            Assert.False(LegacyDatExportModePolicy.UsesEmptyPortalShell(LegacyDatExportMode.SlimMerge));
            Assert.True(LegacyDatExportModePolicy.UsesEmptyPortalShell(LegacyDatExportMode.FullDm));
            Assert.True(LegacyDatExportModePolicy.UsesFullRetailPortalSeed(LegacyDatExportMode.SlimMerge));
            Assert.True(LegacyDatExportModePolicy.UsesFullRetailPortalSeed(LegacyDatExportMode.FullDm));
            Assert.False(LegacyDatExportModePolicy.ShouldPrunePortalCatalog(LegacyDatExportMode.SlimMerge));
            Assert.True(LegacyDatExportModePolicy.ShouldPrunePortalCatalog(LegacyDatExportMode.FullDm));
            Assert.True(LegacyDatExportModePolicy.ShouldMergeRetailPortalGlobals(LegacyDatExportMode.SlimMerge));
            Assert.False(LegacyDatExportModePolicy.ShouldMergeRetailPortalGlobals(LegacyDatExportMode.FullDm));
        }

        [Fact]
        public void ExportPolicy_PresetsMatchExpectedPortalDeployModels() {
            // Legacy tri-mode shim keeps the historical world-closure semantics for old manifests.
            var legacySlim = LegacyDatExportPolicy.FromLegacyMode(LegacyDatExportMode.SlimMerge);
            Assert.True(LegacyDatMergeRuleResolver.UsesFullRetailPortalSeed(legacySlim));
            Assert.True(LegacyDatMergeRuleResolver.ShouldMergeRetailPortalGlobals(legacySlim));
            Assert.True(LegacyDatMergeRuleResolver.ShouldPruneSlimMergePortal(legacySlim));
            Assert.True(LegacyDatMergeRuleResolver.ShouldApplyDmPortalStringPatches(legacySlim));
            Assert.True(LegacyDatMergeRuleResolver.ShouldApplyLegacyCharGenStartingAreas(legacySlim));

            // Full DM: everything DM + DM UI replacement on the retail layout skeleton.
            var fullDm = LegacyDatExportPolicy.FromPreset(LegacyDatExportPreset.FullDm);
            Assert.Equal(LegacyDatExportPreset.FullDm, fullDm.Preset);
            Assert.Equal(LegacyDatExportMode.FullDm, fullDm.ToLegacyMode());
            Assert.Equal("Full DM", fullDm.GetDisplayName());
            Assert.Equal(LegacyDatWorldSource.DmOnly, fullDm.World);
            Assert.Equal(LegacyDatPortalCatalogPolicy.FullDmCatalog, fullDm.Portal);
            Assert.Equal(LegacyDatPortalDeployPolicy.OverlayOnRetailSeed, fullDm.Deploy);
            Assert.True(LegacyDatMergeRuleResolver.UsesFullRetailPortalSeed(fullDm));
            Assert.False(LegacyDatMergeRuleResolver.IncludePhase3LocalData(fullDm));
            Assert.True(LegacyDatMergeRuleResolver.ShouldApplyDmPortalStringPatches(fullDm));
            Assert.True(LegacyDatMergeRuleResolver.ShouldPatchConnectionScreenStrings(fullDm));
            Assert.True(LegacyDatMergeRuleResolver.ShouldPatchDmInGameStrings(fullDm));
            Assert.True(LegacyDatMergeRuleResolver.ShouldPatchCharGenLayoutShape(fullDm));
            Assert.True(LegacyDatMergeRuleResolver.ShouldPatchInGameLayoutShape(fullDm));
            Assert.Equal(LegacyDatContentSource.Dm, fullDm.Shell.CharGen);
            Assert.Equal(LegacyDatContentSource.Dm, fullDm.Shell.AppearanceTables);
            Assert.True(LegacyDatMergeRuleResolver.ShouldApplyDmCharGenExport(fullDm));
            Assert.False(LegacyDatMergeRuleResolver.ShouldPatchDmStartingAreas(fullDm));

            // Half DM: DM world/textures/objs/setups/cells with the retail UI shell.
            var halfDm = LegacyDatExportPolicy.FromPreset(LegacyDatExportPreset.HalfDm);
            Assert.Equal(LegacyDatExportPreset.HalfDm, halfDm.Preset);
            Assert.Equal("Half DM", halfDm.GetDisplayName());
            Assert.Equal(LegacyDatWorldSource.DmOnly, halfDm.World);
            Assert.Equal(LegacyDatPortalCatalogPolicy.FullDmCatalog, halfDm.Portal);
            Assert.Equal(LegacyDatPortalDeployPolicy.OverlayOnRetailSeed, halfDm.Deploy);
            Assert.True(LegacyDatMergeRuleResolver.UsesFullRetailPortalSeed(halfDm));
            Assert.True(LegacyDatMergeRuleResolver.ShouldMergeRetailUiShellAssets(halfDm));
            Assert.False(LegacyDatMergeRuleResolver.ShouldApplyDmPortalStringPatches(halfDm));
            Assert.False(LegacyDatMergeRuleResolver.ShouldPatchConnectionScreenStrings(halfDm));
            Assert.False(LegacyDatMergeRuleResolver.ShouldPatchDmInGameStrings(halfDm));
            Assert.Equal(LegacyDatContentSource.Retail, halfDm.Shell.CharGen);
            Assert.Equal(LegacyDatContentSource.Retail, halfDm.Shell.AppearanceTables);
            Assert.Equal(LegacyDatContentSource.Retail, halfDm.Shell.SpellTables);
            Assert.False(LegacyDatMergeRuleResolver.ShouldApplyDmCharGenExport(halfDm));
            Assert.True(LegacyDatMergeRuleResolver.ShouldPatchDmStartingAreas(halfDm));
            Assert.True(LegacyDatMergeRuleResolver.ShouldApplyLegacyCharGenStartingAreas(halfDm));

            // Old preset names normalize onto the two supported modes.
            Assert.Equal(LegacyDatExportPreset.FullDm, LegacyDatExportPolicy.FromPreset(LegacyDatExportPreset.DmFull).Preset);
            Assert.Equal(LegacyDatExportPreset.FullDm, LegacyDatExportPolicy.FromPreset(LegacyDatExportPreset.FullDmFaithful).Preset);
            Assert.Equal(LegacyDatExportPreset.HalfDm, LegacyDatExportPolicy.FromPreset(LegacyDatExportPreset.DmRetailShell).Preset);
            Assert.Equal(LegacyDatExportPreset.HalfDm, LegacyDatExportPolicy.FromPreset(LegacyDatExportPreset.SlimMerge).Preset);
            Assert.Equal(LegacyDatExportPreset.HalfDm, LegacyDatExportPolicy.FromPreset(LegacyDatExportPreset.FullMerge).Preset);
            Assert.Equal(LegacyDatExportPreset.HalfDm, LegacyDatExportPolicy.FromPreset(LegacyDatExportPreset.RetailWorldDmArt).Preset);
        }

        [Fact]
        public void DmPortalStringReader_ExtractsAsciiFromPortalBlob() {
            byte[] bytes = "Hello DM\r\nSecond line"u8.ToArray();
            string text = LegacyDatDmPortalStringReader.ExtractAscii(bytes);
            Assert.Contains("Hello DM", text);
            Assert.Contains("Second line", text);
        }

        [Fact]
        public void DmStringTableMap_LoadsEmbeddedCharGenEntries() {
            var map = LegacyDatDmStringTableMap.LoadEmbeddedCharGenMap();
            Assert.True(map.Entries.Count >= 20);
            Assert.Contains(map.Entries, entry => entry.DmId.Equals("0x31000001", StringComparison.OrdinalIgnoreCase));
        }

        [Fact]
        public void AcClientStringHash_MatchesKnownCharGenIds() {
            Assert.Equal(79636388u, AcClientStringHash.Compute("ID_CharGen_AluvianText"));
            Assert.Equal(73488836u, AcClientStringHash.Compute("ID_CharGen_SoldierText"));
        }

        [Fact]
        public void DmPortalStringPatch_SurvivesFinalize_WritesLakeCragstone() {
            string legacy = @"C:\Users\chris\OneDrive\Documents\ACME WorldBuilder\Projects\darkmaj\dats\base";
            string seed = @"C:\Users\chris\Downloads\ac-updates";
            if (!File.Exists(Path.Combine(legacy, "portal.dat"))
                || !File.Exists(Path.Combine(seed, "client_local_English.dat"))) {
                return;
            }

            string root = Path.Combine(Path.GetTempPath(), $"acme-dm-ui-patch-{Guid.NewGuid():N}");
            string outDir = Path.Combine(root, "out");
            try {
                // World-closure shim keeps this string-patch regression fast; Full DM exercises the
                // same ShouldApplyDmPortalStringPatches path over the full catalog.
                var policy = LegacyDatExportPolicy.FromLegacyMode(LegacyDatExportMode.SlimMerge);
                var result = LegacyDatConversionService.Convert(new LegacyToRetailConversionOptions {
                    LegacyDatDirectory = legacy,
                    RetailSeedDirectory = seed,
                    OutputDirectory = outDir,
                    ExportPolicy = policy,
                });

                Assert.Contains(
                    result.ConversionWarnings,
                    w => w.Contains("patched", StringComparison.OrdinalIgnoreCase)
                        && w.Contains("StringTable", StringComparison.OrdinalIgnoreCase));

                using var reader = new DefaultDatReaderWriter(
                    outDir,
                    DatAccessType.Read,
                    FileCachingStrategy.Never,
                    IndexCachingStrategy.OnDemand);
                Assert.True(reader.Local().TryGet<StringTable>(0x23000002, out var table) && table?.Strings != null);
                var aluvian = table!.Strings[79636388];
                string text = string.Join(
                    " ",
                    aluvian.Strings);
                Assert.Contains("Lake Cragstone", text, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("Lake Blessed", text, StringComparison.OrdinalIgnoreCase);
            }
            finally {
                if (Directory.Exists(root)) {
                    Directory.Delete(root, recursive: true);
                }
            }
        }

        [Fact]
        public void ExportPolicy_WorldClosureCustomBuild_UsesRetailPortalOverlay() {
            var built = new LegacyDatExportPolicy {
                World = LegacyDatWorldSource.DmOnly,
                Portal = LegacyDatPortalCatalogPolicy.WorldClosure,
                Shell = new LegacyDatClientShellPolicy {
                    Ui = LegacyDatContentSource.Retail,
                    CharGen = LegacyDatContentSource.Retail,
                    AppearanceTables = LegacyDatContentSource.Dm,
                    SpellTables = LegacyDatContentSource.Retail,
                },
                GapFill = LegacyDatRetailGapFillPolicy.ShellReserve,
                Deploy = LegacyDatPortalDeployPolicy.OverlayOnRetailSeed,
            };

            Assert.True(LegacyDatMergeRuleResolver.UsesFullRetailPortalSeed(built));
            Assert.True(LegacyDatMergeRuleResolver.ShouldMergeRetailPortalGlobals(built));
            Assert.True(LegacyDatMergeRuleResolver.ShouldPruneSlimMergePortal(built));
            Assert.True(LegacyDatMergeRuleResolver.ShouldApplyLegacyCharGenStartingAreas(built));
            Assert.True(LegacyDatMergeRuleResolver.ShouldRequireOverlayPortalRoot(built));
        }

        [Fact]
        public void ExportPolicy_PrunedWorldClosure_DoesNotUseRetailPortalOverlay() {
            var pruned = new LegacyDatExportPolicy {
                World = LegacyDatWorldSource.DmOnly,
                Portal = LegacyDatPortalCatalogPolicy.WorldClosure,
                Deploy = LegacyDatPortalDeployPolicy.SingleConvertedCatalog,
            };

            Assert.False(LegacyDatMergeRuleResolver.UsesFullRetailPortalSeed(pruned));
            Assert.True(LegacyDatMergeRuleResolver.ShouldMergeRetailPortalGlobals(pruned));
        }

        [Fact]
        public void FullDmKeepIds_IncludesUiShellButExcludesRetailHighresWorldAssets() {
            string root = Path.Combine(Path.GetTempPath(), $"acme-fulldm-keep-{Guid.NewGuid():N}");
            string seedDir = Path.Combine(root, "seed");
            string outputDir = Path.Combine(root, "output");
            Directory.CreateDirectory(seedDir);
            Directory.CreateDirectory(outputDir);

            try {
                string retailSeed = @"C:\Users\chris\Downloads\ac-updates";
                if (!File.Exists(Path.Combine(retailSeed, "client_portal.dat"))) {
                    return;
                }

                foreach (string datFile in DatProjectModeInfo.RetailDatFiles) {
                    File.Copy(Path.Combine(retailSeed, datFile), Path.Combine(outputDir, datFile), overwrite: true);
                }

                using var seed = new DefaultDatReaderWriter(
                    retailSeed,
                    DatAccessType.Read,
                    FileCachingStrategy.Never,
                    IndexCachingStrategy.OnDemand);
                var targets = new DatExportChecksumTargets();
                targets.TrackPortal(0x02000001);
                var policy = LegacyDatExportPolicy.FromPreset(LegacyDatExportPreset.FullDm);
                var keepIds = LegacyDatFullDmPortalKeeper.CollectKeepIds(outputDir, retailSeed, targets, policy);
                var uiShellIds = LegacyDatRetailUiShellCollector.CollectPortalIds(seed);
                var highresShellIds = LegacyDatRetailHighresShellCollector.CollectPortalIds(seed);
                var highresWorldOnlyIds = highresShellIds
                    .Except(uiShellIds)
                    .Where(id => (id >> 24) is 0x01 or 0x02 or 0x05 or 0x08)
                    .ToArray();

                Assert.Contains(LegacyDatRetailClientShellCollector.CharGenId, keepIds);
                Assert.Contains(0x02000001u, keepIds);
                Assert.Contains(LegacyDatPortalFileIds.Iteration, keepIds);
                Assert.True(uiShellIds.All(keepIds.Contains));
                Assert.NotEmpty(highresWorldOnlyIds);
                Assert.All(highresWorldOnlyIds.Take(128), id => Assert.DoesNotContain(id, keepIds));
            }
            finally {
                Directory.Delete(root, recursive: true);
            }
        }

        [Fact]
        public void FullDmGapFillMissingShellGlobals_OverwritesExistingUiShellAssetIdsWhenConnectionShellEnabled() {
            string root = Path.Combine(Path.GetTempPath(), $"acme-fulldm-shell-{Guid.NewGuid():N}");
            string outputDir = Path.Combine(root, "output");
            Directory.CreateDirectory(outputDir);

            try {
                string retailSeed = @"C:\Users\chris\Downloads\ac-updates";
                if (!File.Exists(Path.Combine(retailSeed, "client_portal.dat"))) {
                    return;
                }

                foreach (string datFile in DatProjectModeInfo.RetailDatFiles) {
                    File.Copy(Path.Combine(retailSeed, datFile), Path.Combine(outputDir, datFile), overwrite: true);
                }

                uint shellSurfaceTextureId;
                byte[] retailBytes;
                using (var seed = new DefaultDatReaderWriter(
                    retailSeed,
                    DatAccessType.Read,
                    FileCachingStrategy.Never,
                    IndexCachingStrategy.OnDemand)) {
                    shellSurfaceTextureId = LegacyDatRetailUiShellCollector.CollectPortalIds(seed)
                        .First(id => (id >> 24) == 0x05);
                    Assert.True(seed.TryGetFileBytes(DatArchive.Portal, shellSurfaceTextureId, out retailBytes, autoDecompress: false));
                    Assert.NotNull(retailBytes);
                }

                byte[] customBytes = retailBytes.ToArray();
                customBytes[0] ^= 0x5A;

                using (var output = new DefaultDatReaderWriter(
                    outputDir,
                    DatAccessType.ReadWrite,
                    FileCachingStrategy.Never,
                    IndexCachingStrategy.OnDemand)) {
                    int iteration = output.GetIteration(DatArchive.Portal);
                    Assert.True(output.TryWriteFileBytes(DatArchive.Portal, shellSurfaceTextureId, customBytes, customBytes.Length, iteration));
                }

                var policy = LegacyDatExportPolicy.FromPreset(LegacyDatExportPreset.FullDm);
                LegacyDatFullDmPortalKeeper.GapFillMissingShellGlobals(outputDir, retailSeed, policy, portalIteration: null);

                using var verify = new DefaultDatReaderWriter(
                    outputDir,
                    DatAccessType.Read,
                    FileCachingStrategy.Never,
                    IndexCachingStrategy.OnDemand);
                Assert.True(verify.TryGetFileBytes(DatArchive.Portal, shellSurfaceTextureId, out byte[]? finalBytes, autoDecompress: false));
                Assert.NotNull(finalBytes);
                Assert.Equal(retailBytes, finalBytes);
            }
            finally {
                Directory.Delete(root, recursive: true);
            }
        }

        [Fact]
        public void FullDmGapFillMissingShellGlobals_OverwritesExistingCharGenShellAssetIds() {
            string root = Path.Combine(Path.GetTempPath(), $"acme-fulldm-chargen-shell-{Guid.NewGuid():N}");
            string outputDir = Path.Combine(root, "output");
            Directory.CreateDirectory(outputDir);

            try {
                string retailSeed = @"C:\Users\chris\Downloads\ac-updates";
                if (!File.Exists(Path.Combine(retailSeed, "client_portal.dat"))) {
                    return;
                }

                foreach (string datFile in DatProjectModeInfo.RetailDatFiles) {
                    File.Copy(Path.Combine(retailSeed, datFile), Path.Combine(outputDir, datFile), overwrite: true);
                }

                uint charGenShellSurfaceTextureId;
                byte[] retailBytes;
                using (var seed = new DefaultDatReaderWriter(
                    retailSeed,
                    DatAccessType.Read,
                    FileCachingStrategy.Never,
                    IndexCachingStrategy.OnDemand)) {
                    var charGenShellIds = LegacyDatRetailClientShellCollector.CollectPortalIds(seed);
                    var uiShellIds = LegacyDatRetailUiShellCollector.CollectPortalIds(seed);
                    charGenShellSurfaceTextureId = charGenShellIds
                        .Except(uiShellIds)
                        .First(id => (id >> 24) == 0x05);
                    Assert.True(seed.TryGetFileBytes(DatArchive.Portal, charGenShellSurfaceTextureId, out retailBytes, autoDecompress: false));
                    Assert.NotNull(retailBytes);
                }

                byte[] customBytes = retailBytes.ToArray();
                customBytes[0] ^= 0x5A;

                using (var output = new DefaultDatReaderWriter(
                    outputDir,
                    DatAccessType.ReadWrite,
                    FileCachingStrategy.Never,
                    IndexCachingStrategy.OnDemand)) {
                    int iteration = output.GetIteration(DatArchive.Portal);
                    Assert.True(output.TryWriteFileBytes(DatArchive.Portal, charGenShellSurfaceTextureId, customBytes, customBytes.Length, iteration));
                }

                var policy = LegacyDatExportPolicy.FromPreset(LegacyDatExportPreset.HalfDm);
                LegacyDatFullDmPortalKeeper.GapFillMissingShellGlobals(outputDir, retailSeed, policy, portalIteration: null);

                using var verify = new DefaultDatReaderWriter(
                    outputDir,
                    DatAccessType.Read,
                    FileCachingStrategy.Never,
                    IndexCachingStrategy.OnDemand);
                Assert.True(verify.TryGetFileBytes(DatArchive.Portal, charGenShellSurfaceTextureId, out byte[]? finalBytes, autoDecompress: false));
                Assert.NotNull(finalBytes);
                Assert.Equal(retailBytes, finalBytes);
            }
            finally {
                Directory.Delete(root, recursive: true);
            }
        }

        [Fact]
        public void FullDmGapFillMissingShellGlobals_OverwritesExistingAppearanceShellAssetIdsWhenAppearanceTablesRetail() {
            string root = Path.Combine(Path.GetTempPath(), $"acme-fulldm-appearance-shell-{Guid.NewGuid():N}");
            string outputDir = Path.Combine(root, "output");
            Directory.CreateDirectory(outputDir);

            try {
                string retailSeed = @"C:\Users\chris\Downloads\ac-updates";
                if (!File.Exists(Path.Combine(retailSeed, "client_portal.dat"))) {
                    return;
                }

                foreach (string datFile in DatProjectModeInfo.RetailDatFiles) {
                    File.Copy(Path.Combine(retailSeed, datFile), Path.Combine(outputDir, datFile), overwrite: true);
                }

                uint appearancePaletteId;
                byte[] retailBytes;
                using (var seed = new DefaultDatReaderWriter(
                    retailSeed,
                    DatAccessType.Read,
                    FileCachingStrategy.Never,
                    IndexCachingStrategy.OnDemand)) {
                    var charGenShellIds = LegacyDatRetailClientShellCollector.CollectPortalIds(seed);
                    var uiShellIds = LegacyDatRetailUiShellCollector.CollectPortalIds(seed);
                    appearancePaletteId = LegacyDatRetailAppearanceShellCollector.CollectPortalIds(seed)
                        .Except(charGenShellIds)
                        .Except(uiShellIds)
                        .First(id => (id >> 24) == 0x04);
                    Assert.True(seed.TryGetFileBytes(DatArchive.Portal, appearancePaletteId, out retailBytes, autoDecompress: false));
                    Assert.NotNull(retailBytes);
                }

                byte[] customBytes = retailBytes.ToArray();
                customBytes[0] ^= 0x5A;

                using (var output = new DefaultDatReaderWriter(
                    outputDir,
                    DatAccessType.ReadWrite,
                    FileCachingStrategy.Never,
                    IndexCachingStrategy.OnDemand)) {
                    int iteration = output.GetIteration(DatArchive.Portal);
                    Assert.True(output.TryWriteFileBytes(DatArchive.Portal, appearancePaletteId, customBytes, customBytes.Length, iteration));
                }

                var policy = LegacyDatExportPolicy.FromPreset(LegacyDatExportPreset.HalfDm);
                LegacyDatFullDmPortalKeeper.GapFillMissingShellGlobals(outputDir, retailSeed, policy, portalIteration: null);

                using var verify = new DefaultDatReaderWriter(
                    outputDir,
                    DatAccessType.Read,
                    FileCachingStrategy.Never,
                    IndexCachingStrategy.OnDemand);
                Assert.True(verify.TryGetFileBytes(DatArchive.Portal, appearancePaletteId, out byte[]? finalBytes, autoDecompress: false));
                Assert.NotNull(finalBytes);
                Assert.Equal(retailBytes, finalBytes);
            }
            finally {
                Directory.Delete(root, recursive: true);
            }
        }

        [Fact]
        public void FullDmGapFillMissingShellGlobals_DoesNotOverwriteAppearanceShellAssetIdsWhenAppearanceTablesDm() {
            string root = Path.Combine(Path.GetTempPath(), $"acme-fulldm-appearance-dm-{Guid.NewGuid():N}");
            string outputDir = Path.Combine(root, "output");
            Directory.CreateDirectory(outputDir);

            try {
                string retailSeed = @"C:\Users\chris\Downloads\ac-updates";
                if (!File.Exists(Path.Combine(retailSeed, "client_portal.dat"))) {
                    return;
                }

                foreach (string datFile in DatProjectModeInfo.RetailDatFiles) {
                    File.Copy(Path.Combine(retailSeed, datFile), Path.Combine(outputDir, datFile), overwrite: true);
                }

                uint appearancePaletteId;
                byte[] customBytes;
                using (var seed = new DefaultDatReaderWriter(
                    retailSeed,
                    DatAccessType.Read,
                    FileCachingStrategy.Never,
                    IndexCachingStrategy.OnDemand)) {
                    var charGenShellIds = LegacyDatRetailClientShellCollector.CollectPortalIds(seed);
                    var uiShellIds = LegacyDatRetailUiShellCollector.CollectPortalIds(seed);
                    byte[] retailBytes;
                    appearancePaletteId = LegacyDatRetailAppearanceShellCollector.CollectPortalIds(seed)
                        .Except(charGenShellIds)
                        .Except(uiShellIds)
                        .First(id => (id >> 24) == 0x04);
                    Assert.True(seed.TryGetFileBytes(DatArchive.Portal, appearancePaletteId, out retailBytes, autoDecompress: false));
                    Assert.NotNull(retailBytes);
                    customBytes = retailBytes.ToArray();
                }

                customBytes[0] ^= 0x5A;

                using (var output = new DefaultDatReaderWriter(
                    outputDir,
                    DatAccessType.ReadWrite,
                    FileCachingStrategy.Never,
                    IndexCachingStrategy.OnDemand)) {
                    int iteration = output.GetIteration(DatArchive.Portal);
                    Assert.True(output.TryWriteFileBytes(DatArchive.Portal, appearancePaletteId, customBytes, customBytes.Length, iteration));
                }

                var policy = LegacyDatExportPolicy.FromPreset(LegacyDatExportPreset.FullDm);
                LegacyDatFullDmPortalKeeper.GapFillMissingShellGlobals(outputDir, retailSeed, policy, portalIteration: null);

                using var verify = new DefaultDatReaderWriter(
                    outputDir,
                    DatAccessType.Read,
                    FileCachingStrategy.Never,
                    IndexCachingStrategy.OnDemand);
                Assert.True(verify.TryGetFileBytes(DatArchive.Portal, appearancePaletteId, out byte[]? finalBytes, autoDecompress: false));
                Assert.NotNull(finalBytes);
                Assert.Equal(customBytes, finalBytes);
            }
            finally {
                Directory.Delete(root, recursive: true);
            }
        }

        [Fact]
        public void FullDmGapFillMissingShellGlobals_DoesNotRestoreHighresOnlyWorldAssetIds() {
            string root = Path.Combine(Path.GetTempPath(), $"acme-fulldm-highres-{Guid.NewGuid():N}");
            string outputDir = Path.Combine(root, "output");
            Directory.CreateDirectory(outputDir);

            try {
                string retailSeed = @"C:\Users\chris\Downloads\ac-updates";
                if (!File.Exists(Path.Combine(retailSeed, "client_portal.dat"))) {
                    return;
                }

                foreach (string datFile in DatProjectModeInfo.RetailDatFiles) {
                    File.Copy(Path.Combine(retailSeed, datFile), Path.Combine(outputDir, datFile), overwrite: true);
                }

                uint highresOnlySurfaceTextureId;
                using (var seed = new DefaultDatReaderWriter(
                    retailSeed,
                    DatAccessType.Read,
                    FileCachingStrategy.Never,
                    IndexCachingStrategy.OnDemand)) {
                    var uiShellIds = LegacyDatRetailUiShellCollector.CollectPortalIds(seed);
                    highresOnlySurfaceTextureId = LegacyDatRetailHighresShellCollector.CollectPortalIds(seed)
                        .Except(uiShellIds)
                        .First(id => (id >> 24) == 0x05);
                }

                using (var output = new DefaultDatReaderWriter(
                    outputDir,
                    DatAccessType.ReadWrite,
                    FileCachingStrategy.Never,
                    IndexCachingStrategy.OnDemand)) {
                    Assert.True(output.Portal().Tree.TryDelete(highresOnlySurfaceTextureId, out _));
                    Assert.False(output.ContainsFile(DatArchive.Portal, highresOnlySurfaceTextureId));
                }

                var policy = LegacyDatExportPolicy.FromPreset(LegacyDatExportPreset.FullDm);
                LegacyDatFullDmPortalKeeper.GapFillMissingShellGlobals(outputDir, retailSeed, policy, portalIteration: null);

                using var verify = new DefaultDatReaderWriter(
                    outputDir,
                    DatAccessType.Read,
                    FileCachingStrategy.Never,
                    IndexCachingStrategy.OnDemand);
                Assert.False(verify.ContainsFile(DatArchive.Portal, highresOnlySurfaceTextureId));
            }
            finally {
                Directory.Delete(root, recursive: true);
            }
        }

        [Fact]
        public void PortalCatalogPruner_RebuildPortalFromKeepSet_PreservesCustomIds() {
            string root = Path.Combine(Path.GetTempPath(), $"acme-portal-prune-{Guid.NewGuid():N}");
            string outputDir = Path.Combine(root, "output");
            Directory.CreateDirectory(outputDir);

            try {
                string retailSeed = @"C:\Users\chris\Downloads\ac-updates";
                if (!File.Exists(Path.Combine(retailSeed, "client_portal.dat"))) {
                    return;
                }

                foreach (string datFile in DatProjectModeInfo.RetailDatFiles) {
                    File.Copy(Path.Combine(retailSeed, datFile), Path.Combine(outputDir, datFile), overwrite: true);
                }

                LegacyDatRetailShellBootstrap.PrepareShell(
                    retailSeed,
                    outputDir,
                    "client_portal.dat");

                const uint keepRetailId = 0x02000001u;
                const uint keepCustomId = 0x0600FF00u;
                const uint junkId = 0x0600FF01u;

                using (var seed = new DefaultDatReaderWriter(
                    retailSeed,
                    DatAccessType.Read,
                    FileCachingStrategy.Never,
                    IndexCachingStrategy.OnDemand))
                using (var writer = new DefaultDatReaderWriter(
                    outputDir,
                    DatAccessType.ReadWrite,
                    FileCachingStrategy.Never,
                    IndexCachingStrategy.OnDemand)) {
                    Assert.True(seed.TryGetFileBytes(DatArchive.Portal, keepRetailId, out byte[]? bytes, autoDecompress: false));
                    Assert.NotNull(bytes);

                    int iteration = seed.GetIteration(DatArchive.Portal);
                    Assert.True(writer.Portal().TryWriteFileBytes(keepRetailId, bytes!, bytes!.Length, iteration));
                    Assert.True(writer.TryWriteFileBytes(DatArchive.Portal, keepCustomId, bytes!, bytes!.Length, iteration));
                    Assert.True(writer.TryWriteFileBytes(DatArchive.Portal, junkId, bytes!, bytes!.Length, iteration));
                }

                var keepIds = new HashSet<uint> {
                    LegacyDatPortalFileIds.Iteration,
                    keepRetailId,
                    keepCustomId,
                };

                LegacyDatPortalCatalogPruner.PrunePortal(outputDir, keepIds, retailSeed);

                using var reader = new DefaultDatReaderWriter(
                    outputDir,
                    DatAccessType.Read,
                    FileCachingStrategy.Never,
                    IndexCachingStrategy.OnDemand);
                Assert.True(reader.ContainsFile(DatArchive.Portal, keepRetailId));
                Assert.True(reader.ContainsFile(DatArchive.Portal, keepCustomId));
                Assert.False(reader.ContainsFile(DatArchive.Portal, junkId));
            }
            finally {
                Directory.Delete(root, recursive: true);
            }
        }

        [Fact]
        public void FullDmOverlay_RequiresRetailPortalRootMatch() {
            var policy = LegacyDatExportPolicy.FromPreset(LegacyDatExportPreset.FullDm);

            Assert.True(LegacyDatMergeRuleResolver.UsesRetailPortalOverlayDeploy(policy));
            Assert.True(LegacyDatMergeRuleResolver.ShouldRequireOverlayPortalRoot(policy));
            Assert.True(LegacyDatMergeRuleResolver.ShouldEnsureRegionTerrainPortalAssets(policy));
        }

        [Fact]
        public void PortalOverlay_AllowsRegionTerrainSurfaceTextureAtSharedRetailId() {
            Assert.True(
                LegacyDatPortalOverlayRedeploy.ShouldOverlayConvertedPayload(0x05001459u, retailSeedHasId: true));
        }

        [Fact]
        public void RegionTerrainExporter_DetectsRetailSurfaceTextureStub() {
            byte[] retailStub = [0x59, 0x14, 0x00, 0x05, 0x00, 0x00, 0x00, 0x00, 0x02, 0x01, 0x00, 0x00, 0x00, 0x40, 0x6D, 0x00, 0x06];
            byte[] legacyEmbeddedPayload = new byte[4096];

            Assert.True(LegacyDatRegionTerrainPortalExporter.IsLikelyRetailSurfaceTextureStub(retailStub));
            Assert.False(LegacyDatRegionTerrainPortalExporter.IsLikelyRetailSurfaceTextureStub(legacyEmbeddedPayload));
        }

        [Fact]
        public void ClientDatRepair_FullDmRestoresRegionTerrainSurfaceTexturePayloads() {
            string retailSeed = @"C:\Users\chris\Downloads\ac-updates";
            string legacy = @"C:\Users\chris\OneDrive\Documents\ACME WorldBuilder\Projects\darkmaj\dats\base";
            if (!File.Exists(Path.Combine(retailSeed, "client_portal.dat"))
                || !File.Exists(Path.Combine(legacy, "portal.dat"))) {
                return;
            }

            string root = Path.Combine(Path.GetTempPath(), "wb-region-terrain-repair-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try {
                foreach (string file in Directory.GetFiles(retailSeed, "client_*.dat")) {
                    File.Copy(file, Path.Combine(root, Path.GetFileName(file)), overwrite: true);
                }

                var policy = LegacyDatExportPolicy.FromPreset(LegacyDatExportPreset.FullDm);
                LegacyDatClientDatRepair.Repair(root, retailSeed, policy, legacy, _ => { });

                using var verify = new DefaultDatReaderWriter(
                    root,
                    DatAccessType.Read,
                    FileCachingStrategy.Never,
                    IndexCachingStrategy.OnDemand);
                Assert.True(verify.TryGet<SurfaceTexture>(0x05001459u, out SurfaceTexture? grassSt) && grassSt!.Textures.Count > 0);
                uint grassRsId = grassSt.Textures[^1];
                Assert.True(verify.TryGet<RenderSurface>(grassRsId, out RenderSurface? grassRs) && grassRs != null);
                Assert.True(grassRs!.SourceData?.Length > 1024, "DM grassland RenderSurface should carry embedded BGRA pixels.");
                Assert.Equal(128, grassRs.Width);
            }
            finally {
                Directory.Delete(root, recursive: true);
            }
        }

        [Fact]
        public void PortalOverlayRedeploy_OnlyPinsRetailMetadataBandsWhenSeedHasThatId() {
            Assert.False(LegacyDatPortalOverlayRedeploy.ShouldOverlayConvertedPayload(0x12000006u, retailSeedHasId: true));
            Assert.False(LegacyDatPortalOverlayRedeploy.ShouldOverlayConvertedPayload(0x320005C0u, retailSeedHasId: true));

            Assert.True(LegacyDatPortalOverlayRedeploy.ShouldOverlayConvertedPayload(0x12000006u, retailSeedHasId: false));
            Assert.True(LegacyDatPortalOverlayRedeploy.ShouldOverlayConvertedPayload(0x320005C0u, retailSeedHasId: false));
            Assert.True(LegacyDatPortalOverlayRedeploy.ShouldOverlayConvertedPayload(0x02000001u, retailSeedHasId: true));
        }

        [Fact]
        public void PortalOverlayRedeploy_PreservesConvertedTemplateForOverlayPayloads() {
            string retailSeed = @"C:\Users\chris\Downloads\ac-updates";
            if (!File.Exists(Path.Combine(retailSeed, "client_portal.dat"))) {
                return;
            }

            string root = Path.Combine(Path.GetTempPath(), $"acme-portal-overlay-template-{Guid.NewGuid():N}");
            string outputDir = Path.Combine(root, "output");
            Directory.CreateDirectory(outputDir);

            try {
                foreach (string datFile in DatProjectModeInfo.RetailDatFiles) {
                    File.Copy(Path.Combine(retailSeed, datFile), Path.Combine(outputDir, datFile), overwrite: true);
                }

                const uint overlayId = 0x05001459u;
                byte[] retailBytes;
                DatBTreeFile retailTemplate;
                using (var seed = new DefaultDatReaderWriter(
                    retailSeed,
                    DatAccessType.Read,
                    FileCachingStrategy.Never,
                    IndexCachingStrategy.OnDemand)) {
                    Assert.True(seed.Portal().Tree.TryGetFile(overlayId, out retailTemplate));
                    Assert.True(seed.TryGetFileBytes(DatArchive.Portal, overlayId, out retailBytes, autoDecompress: false));
                    Assert.NotNull(retailBytes);
                }

                byte[] dmBytes = retailBytes.ToArray();
                dmBytes[0] ^= 0x5A;
                DatBTreeFile dmTemplate = retailTemplate;
                dmTemplate.Version = (ushort)(retailTemplate.Version + 7);
                dmTemplate.Iteration = retailTemplate.Iteration + 123;

                using (var writer = new DefaultDatReaderWriter(
                    outputDir,
                    DatAccessType.ReadWrite,
                    FileCachingStrategy.Never,
                    IndexCachingStrategy.OnDemand)) {
                    Assert.True(writer.Portal().TryWriteFileBytes(overlayId, dmBytes, dmBytes.Length, dmTemplate));
                }

                LegacyDatPortalOverlayRedeploy.Redeploy(
                    outputDir,
                    retailSeed,
                    LegacyDatExportPolicy.FromPreset(LegacyDatExportPreset.FullDm),
                    onProgress: null);

                using var verify = new DefaultDatReaderWriter(
                    outputDir,
                    DatAccessType.Read,
                    FileCachingStrategy.Never,
                    IndexCachingStrategy.OnDemand);
                Assert.True(verify.TryGetFileBytes(DatArchive.Portal, overlayId, out byte[]? finalBytes, autoDecompress: false));
                Assert.NotNull(finalBytes);
                Assert.Equal(dmBytes, finalBytes);
                Assert.True(verify.Portal().Tree.TryGetFile(overlayId, out DatBTreeFile finalTemplate));
                Assert.Equal(dmTemplate.Version, finalTemplate.Version);
                Assert.Equal(dmTemplate.Iteration, finalTemplate.Iteration);
            }
            finally {
                Directory.Delete(root, recursive: true);
            }
        }

        [Fact]
        public void PortalOverlayRedeploy_ProtectsRetailCharGenShellAssetsForRetailCharGenPolicies() {
            string retailSeed = @"C:\Users\chris\Downloads\ac-updates";
            if (!File.Exists(Path.Combine(retailSeed, "client_portal.dat"))) {
                return;
            }

            using var retail = new DefaultDatReaderWriter(
                retailSeed,
                DatAccessType.Read,
                FileCachingStrategy.Never,
                IndexCachingStrategy.OnDemand);
            var policy = LegacyDatExportPolicy.FromPreset(LegacyDatExportPreset.HalfDm);
            var protectedIds = LegacyDatPortalOverlayRedeploy.CollectProtectedRetailSeedPayloadIds(retail, policy);
            var uiShellIds = LegacyDatRetailUiShellCollector.CollectPortalIds(retail);
            uint charGenShellRenderSurfaceId = LegacyDatRetailClientShellCollector.CollectPortalIds(retail)
                .Except(uiShellIds)
                .First(id => (id >> 24) == 0x06);

            Assert.Contains(charGenShellRenderSurfaceId, protectedIds);
            Assert.False(
                LegacyDatPortalOverlayRedeploy.ShouldOverlayConvertedPayload(
                    charGenShellRenderSurfaceId,
                    retailSeedHasId: true,
                    protectedIds));
        }

        [Fact]
        public void PortalOverlayRedeploy_DoesNotProtectCharGenShellAssetsForDmCharGenPolicies() {
            string retailSeed = @"C:\Users\chris\Downloads\ac-updates";
            if (!File.Exists(Path.Combine(retailSeed, "client_portal.dat"))) {
                return;
            }

            using var retail = new DefaultDatReaderWriter(
                retailSeed,
                DatAccessType.Read,
                FileCachingStrategy.Never,
                IndexCachingStrategy.OnDemand);
            var policy = LegacyDatExportPolicy.FromPreset(LegacyDatExportPreset.FullDm);
            var protectedIds = LegacyDatPortalOverlayRedeploy.CollectProtectedRetailSeedPayloadIds(retail, policy);
            var uiShellIds = LegacyDatRetailUiShellCollector.CollectPortalIds(retail);
            uint charGenShellRenderSurfaceId = LegacyDatRetailClientShellCollector.CollectPortalIds(retail)
                .Except(uiShellIds)
                .First(id => (id >> 24) == 0x06);

            Assert.DoesNotContain(charGenShellRenderSurfaceId, protectedIds);
            Assert.True(
                LegacyDatPortalOverlayRedeploy.ShouldOverlayConvertedPayload(
                    charGenShellRenderSurfaceId,
                    retailSeedHasId: true,
                    protectedIds));
        }

        [Fact]
        public void ClientDatRepair_FullDmRestoresRetailCharGenShellSurfaceTexturePayloads() {
            string retailSeed = @"C:\Users\chris\Downloads\ac-updates";
            string legacy = @"C:\Users\chris\OneDrive\Documents\ACME WorldBuilder\Projects\darkmaj\dats\base";
            if (!File.Exists(Path.Combine(retailSeed, "client_portal.dat"))
                || !File.Exists(Path.Combine(legacy, "portal.dat"))) {
                return;
            }

            string root = Path.Combine(Path.GetTempPath(), $"acme-fulldm-repair-shell-{Guid.NewGuid():N}");
            string outputDir = Path.Combine(root, "output");
            Directory.CreateDirectory(outputDir);

            try {
                foreach (string datFile in DatProjectModeInfo.RetailDatFiles) {
                    File.Copy(Path.Combine(retailSeed, datFile), Path.Combine(outputDir, datFile), overwrite: true);
                }

                uint charGenShellSurfaceTextureId;
                byte[] retailBytes;
                using (var seed = new DefaultDatReaderWriter(
                    retailSeed,
                    DatAccessType.Read,
                    FileCachingStrategy.Never,
                    IndexCachingStrategy.OnDemand)) {
                    var uiShellIds = LegacyDatRetailUiShellCollector.CollectPortalIds(seed.Local()!, seed);
                    charGenShellSurfaceTextureId = LegacyDatRetailClientShellCollector.CollectPortalIds(seed)
                        .Except(uiShellIds)
                        .First(id => (id >> 24) == 0x05);
                    Assert.True(seed.TryGetFileBytes(DatArchive.Portal, charGenShellSurfaceTextureId, out retailBytes, autoDecompress: false));
                    Assert.NotNull(retailBytes);
                }

                byte[] customBytes = retailBytes.ToArray();
                customBytes[0] ^= 0x5A;

                using (var writer = new DefaultDatReaderWriter(
                    outputDir,
                    DatAccessType.ReadWrite,
                    FileCachingStrategy.Never,
                    IndexCachingStrategy.OnDemand)) {
                    int iteration = writer.GetIteration(DatArchive.Portal);
                    Assert.True(
                        LegacyDatPortalCatalogDeduplicator.TryReplacePortalPayload(
                            writer,
                            charGenShellSurfaceTextureId,
                            customBytes,
                            iteration));
                }

                var policy = LegacyDatExportPolicy.FromPreset(LegacyDatExportPreset.HalfDm);
                LegacyDatClientDatRepair.Repair(
                    outputDir,
                    retailSeed,
                    policy,
                    legacy,
                    onProgress: null);

                using var verify = new DefaultDatReaderWriter(
                    outputDir,
                    DatAccessType.Read,
                    FileCachingStrategy.Never,
                    IndexCachingStrategy.OnDemand);
                Assert.True(verify.TryGetFileBytes(DatArchive.Portal, charGenShellSurfaceTextureId, out byte[]? finalBytes, autoDecompress: false));
                Assert.NotNull(finalBytes);
                Assert.Equal(retailBytes, finalBytes);
            }
            finally {
                Directory.Delete(root, recursive: true);
            }
        }

        [Fact]
        public void ClientDatRepair_FullDmPreservesDmUiShellPortalBytesWhenConnectionShellEnabled() {
            string retailSeed = @"C:\Users\chris\Downloads\ac-updates";
            if (!File.Exists(Path.Combine(retailSeed, "client_portal.dat"))) {
                return;
            }

            string root = Path.Combine(Path.GetTempPath(), $"acme-fulldm-repair-ui-{Guid.NewGuid():N}");
            string outputDir = Path.Combine(root, "output");
            Directory.CreateDirectory(outputDir);

            try {
                foreach (string datFile in DatProjectModeInfo.RetailDatFiles) {
                    File.Copy(Path.Combine(retailSeed, datFile), Path.Combine(outputDir, datFile), overwrite: true);
                }

                uint uiShellGfxId;
                byte[] retailBytes;
                byte[] dmBytes;
                using (var seed = new DefaultDatReaderWriter(
                    retailSeed,
                    DatAccessType.Read,
                    FileCachingStrategy.Never,
                    IndexCachingStrategy.OnDemand)) {
                    uiShellGfxId = LegacyDatRetailUiShellCollector.CollectPortalIds(seed.Local()!, seed)
                        .First(id => (id >> 24) == 0x01);
                    Assert.True(seed.TryGetFileBytes(DatArchive.Portal, uiShellGfxId, out retailBytes, autoDecompress: false));
                    Assert.NotNull(retailBytes);
                }

                dmBytes = retailBytes.ToArray();
                dmBytes[0] ^= 0x5A;

                using (var writer = new DefaultDatReaderWriter(
                    outputDir,
                    DatAccessType.ReadWrite,
                    FileCachingStrategy.Never,
                    IndexCachingStrategy.OnDemand)) {
                    int iteration = writer.GetIteration(DatArchive.Portal);
                    Assert.True(
                        LegacyDatPortalCatalogDeduplicator.TryReplacePortalPayload(
                            writer,
                            uiShellGfxId,
                            dmBytes,
                            iteration));
                }

                var policy = LegacyDatExportPolicy.FromPreset(LegacyDatExportPreset.FullDm);
                LegacyDatClientDatRepair.Repair(
                    outputDir,
                    retailSeed,
                    policy,
                    legacyDatDirectory: null,
                    onProgress: null);

                using var verify = new DefaultDatReaderWriter(
                    outputDir,
                    DatAccessType.Read,
                    FileCachingStrategy.Never,
                    IndexCachingStrategy.OnDemand);
                Assert.True(verify.TryGetFileBytes(DatArchive.Portal, uiShellGfxId, out byte[]? finalBytes, autoDecompress: false));
                Assert.NotNull(finalBytes);
                Assert.Equal(dmBytes, finalBytes);
            }
            finally {
                Directory.Delete(root, recursive: true);
            }
        }

        [Fact]
        public void ExportPolicy_FromLegacyMode_RoundTripsLegacyEnum() {
            Assert.Equal(LegacyDatExportMode.FullMerge, LegacyDatExportPolicy.FromLegacyMode(LegacyDatExportMode.FullMerge).ToLegacyMode());
            Assert.Equal(LegacyDatExportMode.SlimMerge, LegacyDatExportPolicy.FromLegacyMode(LegacyDatExportMode.SlimMerge).ToLegacyMode());
            Assert.Equal(LegacyDatExportMode.FullDm, LegacyDatExportPolicy.FromLegacyMode(LegacyDatExportMode.FullDm).ToLegacyMode());
        }

        [Fact]
        public void ExportManifest_TryLoadPolicyFromJson_ParsesMultiLineCustomPolicyBlock() {
            string root = Path.Combine(Path.GetTempPath(), $"acme-policy-manifest-{Guid.NewGuid():N}");
            Directory.CreateDirectory(root);

            try {
                var policy = new LegacyDatExportPolicy {
                    World = LegacyDatWorldSource.DmOnly,
                    Portal = LegacyDatPortalCatalogPolicy.FullDmCatalog,
                    Shell = new LegacyDatClientShellPolicy {
                        Ui = LegacyDatContentSource.DmPortalStrings,
                        CharGen = LegacyDatContentSource.Dm,
                        AppearanceTables = LegacyDatContentSource.Dm,
                        SpellTables = LegacyDatContentSource.GapFillRetail,
                        ConnectionLoginUiShell = true,
                        PatchConnectionScreenStrings = true,
                        PatchInGameScreenStrings = false,
                        PatchCharGenLayoutShape = false,
                        PatchInGameLayoutShape = true,
                    },
                    GapFill = LegacyDatRetailGapFillPolicy.FillMissingOnly,
                    Deploy = LegacyDatPortalDeployPolicy.OverlayOnRetailSeed,
                };

                string manifestPath = Path.Combine(root, "worldbuilder_legacy_conversion_manifest.txt");
                File.WriteAllText(
                    manifestPath,
                    "export_preset=custom" + System.Environment.NewLine
                    + "[export_policy_json]" + System.Environment.NewLine
                    + policy.ToManifestJson() + System.Environment.NewLine
                    + "[audit]" + System.Environment.NewLine
                    + "none" + System.Environment.NewLine);

                Assert.True(LegacyDatExportManifest.TryLoadPolicyFromJson(root, out var parsed));
                Assert.Null(parsed.Preset);
                Assert.Equal(LegacyDatPortalCatalogPolicy.FullDmCatalog, parsed.Portal);
                Assert.Equal(LegacyDatContentSource.Dm, parsed.Shell.CharGen);
                Assert.True(parsed.Shell.PatchConnectionScreenStrings);
                Assert.False(parsed.Shell.PatchInGameScreenStrings);
                Assert.True(parsed.Shell.PatchInGameLayoutShape);
                Assert.False(parsed.Shell.PatchCharGenLayoutShape);
            }
            finally {
                Directory.Delete(root, recursive: true);
            }
        }

        [Fact]
        public void ExportManifest_WritePolicyMetadata_RewritesPolicyAndPreservesTailSections() {
            string root = Path.Combine(Path.GetTempPath(), $"acme-policy-write-{Guid.NewGuid():N}");
            string retailSeed = Path.Combine(root, "retail-seed");
            string legacyDat = Path.Combine(root, "legacy-dat");
            Directory.CreateDirectory(root);
            Directory.CreateDirectory(retailSeed);
            Directory.CreateDirectory(legacyDat);

            try {
                File.WriteAllText(
                    Path.Combine(root, "worldbuilder_legacy_conversion_manifest.txt"),
                    "generated_utc=2000-01-01T00:00:00.0000000Z" + System.Environment.NewLine
                    + "legacy_dat_dir=C:\\stale-legacy" + System.Environment.NewLine
                    + "retail_seed_dir=C:\\stale-seed" + System.Environment.NewLine
                    + "output_dir=C:\\stale-out" + System.Environment.NewLine
                    + "export_mode=FullDm" + System.Environment.NewLine
                    + "export_preset=custom" + System.Environment.NewLine
                    + System.Environment.NewLine
                    + "[export_policy_json]" + System.Environment.NewLine
                    + "{}" + System.Environment.NewLine
                    + System.Environment.NewLine
                    + "[audit]" + System.Environment.NewLine
                    + "kept" + System.Environment.NewLine);

                var policy = LegacyDatExportPolicy.FromPreset(LegacyDatExportPreset.FullDm);
                LegacyDatExportManifest.WritePolicyMetadata(root, policy, retailSeed, legacyDat);

                Assert.True(LegacyDatExportManifest.TryLoadPolicyFromJson(root, out var parsed));
                Assert.Equal(LegacyDatContentSource.Dm, parsed.Shell.CharGen);
                Assert.True(parsed.Shell.PatchInGameScreenStrings);
                Assert.Equal(retailSeed, LegacyDatExportManifest.TryLoadRetailSeedDirectory(root));
                Assert.Equal(legacyDat, LegacyDatExportManifest.TryLoadLegacyDatDirectory(root));

                string manifest = File.ReadAllText(Path.Combine(root, "worldbuilder_legacy_conversion_manifest.txt"));
                Assert.Contains("export_preset=FullDm", manifest, StringComparison.Ordinal);
                Assert.Contains("[audit]", manifest, StringComparison.Ordinal);
                Assert.Contains("kept", manifest, StringComparison.Ordinal);
            }
            finally {
                Directory.Delete(root, recursive: true);
            }
        }

        [Fact]
        public void ClientDatRepair_ReapplyLocalUiOverrides_RewritesConnectionAndCharacterManagementStrings() {
            string seed = @"C:\Users\chris\Downloads\ac-updates";
            if (!File.Exists(Path.Combine(seed, "client_local_English.dat"))) {
                return;
            }

            string root = Path.Combine(Path.GetTempPath(), $"acme-local-ui-repair-{Guid.NewGuid():N}");
            string outDir = Path.Combine(root, "out");
            Directory.CreateDirectory(outDir);

            try {
                foreach (string name in new[] {
                    "client_local_English.dat",
                    "client_local_English.dat.meta",
                }) {
                    string src = Path.Combine(seed, name);
                    if (File.Exists(src)) {
                        File.Copy(src, Path.Combine(outDir, name), overwrite: true);
                    }
                }

                var policy = new LegacyDatExportPolicy {
                    Shell = new LegacyDatClientShellPolicy {
                        Ui = LegacyDatContentSource.DmPortalStrings,
                        ConnectionLoginUiShell = true,
                        PatchConnectionScreenStrings = true,
                    },
                };

                LegacyDatClientDatRepair.ReapplyLocalUiOverrides(
                    outDir,
                    policy,
                    legacyDatDirectory: null,
                    onProgress: null);

                Assert.True(File.Exists(Path.Combine(outDir, "dm_ui_string_patch_report.txt")));

                using var writer = new DefaultDatReaderWriter(
                    outDir,
                    DatAccessType.Read,
                    FileCachingStrategy.Never,
                    IndexCachingStrategy.OnDemand);

                Assert.True(writer.Local().TryGet<StringTable>(0x23000002, out var labels) && labels?.Strings != null);
                Assert.True(labels!.Strings.TryGetValue(184855186, out var restoreRow) && restoreRow != null);
                Assert.Equal(
                    "Restore Character",
                    string.Join(" ", restoreRow!.Strings));

                Assert.True(writer.TryGet<StringTable>(0x23000010, out var errors) && errors?.Strings != null);
                Assert.True(errors!.Strings.TryGetValue(164627218, out var logonRow) && logonRow != null);
                Assert.StartsWith(
                    "Client:",
                    string.Join(" ", logonRow!.Strings),
                    StringComparison.Ordinal);
            }
            finally {
                Directory.Delete(root, recursive: true);
            }
        }

        [Fact]
        public void CellWorldPruner_KeepIdsMatchLegacyCellCatalog() {
            string dir = CreateLegacyDatFixture(
                includeLanguageDat: false,
                includeWorldReference: true);

            try {
                using var source = new LegacyDatReader(dir);
                var keepIds = LegacyDatCellWorldPruner.CollectKeepIds(source);

                Assert.Contains(0x7D64FFFEu, keepIds);
                Assert.Single(keepIds);
                Assert.DoesNotContain(0x1234FFFFu, keepIds);
            }
            finally {
                Directory.Delete(dir, recursive: true);
            }
        }

        [Fact]
        public void WorldReferenceClosure_IncludesReferencedPortalAssetsOnly() {
            string dir = CreateLegacyDatFixture(
                includeLanguageDat: false,
                includeEnvironment: false,
                includeWorldReference: true);

            try {
                using var source = new LegacyDatReader(dir);
                var closure = LegacyDatWorldReferenceCollector.Collect(source);

                Assert.True(closure.Contains("Region", 0x13000000u));
                Assert.True(closure.Contains("GfxObj", 0x01000022u));
                Assert.True(closure.Contains("SurfaceTexture", 0x05000042u));
                Assert.False(closure.Contains("GfxObj", 0x01009999u));
            }
            finally {
                Directory.Delete(dir, recursive: true);
            }
        }

        [Fact]
        public void Convert_RejectsOutputDirectoryThatMatchesRetailSeed() {
            string legacyDir = CreateLegacyDatFixture(includeLanguageDat: false);
            string retailSeedDir = Path.Combine(Path.GetTempPath(), $"acme-retail-seed-{Guid.NewGuid():N}");
            Directory.CreateDirectory(retailSeedDir);

            try {
                foreach (string datFile in DatProjectModeInfo.RetailDatFiles) {
                    File.WriteAllBytes(Path.Combine(retailSeedDir, datFile), Array.Empty<byte>());
                }

                var ex = Assert.Throws<InvalidOperationException>(() => LegacyDatConversionService.Convert(
                    new LegacyToRetailConversionOptions {
                        LegacyDatDirectory = legacyDir,
                        RetailSeedDirectory = retailSeedDir,
                        OutputDirectory = retailSeedDir,
                    }));

                Assert.Contains("output directory must be different from the retail seed directory", ex.Message, StringComparison.OrdinalIgnoreCase);
            }
            finally {
                Directory.Delete(legacyDir, recursive: true);
                Directory.Delete(retailSeedDir, recursive: true);
            }
        }

        [Fact]
        public void RenderSurfaceHelpers_RemapSyntheticIdsIntoRetailRangeAndRetailizeIndexedSourceData() {
            uint syntheticRenderSurfaceId = LegacyDatDecoders.CreateSyntheticRenderSurfaceId(0x05000042u);

            bool ok = LegacyDatDecoders.TryDecodeSurfaceTexture(
                BuildLegacySurfaceTextureBytes(includeExtraMip: true),
                LegacyDatVersion.DarkMajesty,
                syntheticRenderSurfaceId,
                out var surfaceTexture,
                out var renderSurface);

            Assert.True(ok);
            Assert.NotNull(surfaceTexture);
            Assert.NotNull(renderSurface);
            Assert.Equal(syntheticRenderSurfaceId, (uint)surfaceTexture!.Textures[0]);
            Assert.Equal(syntheticRenderSurfaceId, renderSurface!.Id);
            Assert.Equal(new byte[] { 3, 0, 9, 0, 11, 0 }, renderSurface.SourceData);

            LegacyDatConversionService.RemapSurfaceTextureRenderSurfaceIdsForRetail(surfaceTexture);
            LegacyDatConversionService.RemapRenderSurfaceForRetail(renderSurface);

            uint retailRenderSurfaceId = 0x06000042u;
            Assert.Equal(retailRenderSurfaceId, (uint)surfaceTexture.Textures[0]);
            Assert.Equal(retailRenderSurfaceId, renderSurface.Id);
            Assert.Equal(retailRenderSurfaceId, LegacyDatConversionService.ConvertRenderSurfaceIdToRetail(syntheticRenderSurfaceId));
            Assert.False(LegacyDatReader.IsSyntheticRenderSurfaceId(retailRenderSurfaceId));
            Assert.Equal(0x04000077u, renderSurface.DefaultPaletteId);
            Assert.Equal(new byte[] { 24, 0, 72, 0 }, renderSurface.SourceData);
        }

        [Fact]
        public void RenderSurfaceHelpers_TrimArgbSourceDataToTopLevelImageBeforeRetailSave() {
            uint syntheticRenderSurfaceId = LegacyDatDecoders.CreateSyntheticRenderSurfaceId(0x05000043u);

            bool ok = LegacyDatDecoders.TryDecodeSurfaceTexture(
                BuildLegacyArgbSurfaceTextureBytes(includeExtraMip: true),
                LegacyDatVersion.DarkMajesty,
                syntheticRenderSurfaceId,
                out _,
                out var renderSurface);

            Assert.True(ok);
            Assert.NotNull(renderSurface);
            Assert.Equal((uint)PixelFormat.PFID_A8R8G8B8, renderSurface!.Format);
            Assert.Equal(12, renderSurface.SourceData.Length);

            LegacyDatConversionService.RemapRenderSurfaceForRetail(renderSurface);

            Assert.Equal(0x06000043u, renderSurface.Id);
            Assert.Equal(8, renderSurface.SourceData.Length);
            Assert.Equal(new byte[] {
                0x10, 0x20, 0x30, 0x40,
                0x50, 0x60, 0x70, 0x80,
            }, renderSurface.SourceData);
        }

        [Fact]
        public void RenderSurfaceHelpers_ReusesPreferredRetailRenderSurfaceSlotForSyntheticTextures() {
            uint syntheticRenderSurfaceId = LegacyDatDecoders.CreateSyntheticRenderSurfaceId(0x05000042u);

            var map = LegacyDatConversionService.BuildRenderSurfaceIdMap(new uint[] { syntheticRenderSurfaceId });

            Assert.True(map.TryGetValue(syntheticRenderSurfaceId, out uint remapped));
            Assert.Equal(0x06000042u, remapped);
        }

        [Fact]
        public void RenderSurfaceHelpers_AllocateSyntheticIdsAwayFromDirectRenderSurfacesInSameBatch() {
            uint syntheticRenderSurfaceId = LegacyDatDecoders.CreateSyntheticRenderSurfaceId(0x05000042u);

            var map = LegacyDatConversionService.BuildRenderSurfaceIdMap(
                new uint[] {
                    syntheticRenderSurfaceId,
                    0x06000042u,
                });

            Assert.True(map.TryGetValue(syntheticRenderSurfaceId, out uint remapped));
            Assert.Equal(0x0600FF00u, remapped);
            Assert.NotEqual(0x06000042u, remapped);
        }

        [Fact]
        public void RenderSurfaceHelpers_KeepDirectLegacyRenderSurfacesInRetailBgraForm() {
            var buffer = new byte[64];
            var writer = new DatBinWriter(buffer);
            writer.WriteUInt32(0x06000066);
            writer.WriteInt32(2);
            writer.WriteInt32(1);
            writer.WriteBytes(new byte[] { 0x11, 0x22, 0x33, 0x44, 0x55, 0x66 }, 6);

            bool ok = LegacyDatDecoders.TryDecodeDirectRenderSurface(
                buffer.AsSpan(0, writer.Offset).ToArray(),
                LegacyDatVersion.DarkMajesty,
                out var renderSurface);

            Assert.True(ok);
            Assert.NotNull(renderSurface);

            LegacyDatConversionService.RemapRenderSurfaceForRetail(renderSurface!);

            Assert.Equal(0x06000066u, renderSurface.Id);
            Assert.Equal((uint)PixelFormat.PFID_A8R8G8B8, renderSurface.Format);
            Assert.Equal(new byte[] {
                0x33, 0x22, 0x11, 0xFF,
                0x66, 0x55, 0x44, 0xFF,
            }, renderSurface.SourceData);
        }

        [Fact]
        public void PrimaryPass_DefersPortalDefinitionsUntilRenderSurfaceMapExists() {
            var portalDefinition = new LegacyDatConversionTypeDefinition(
                "RenderSurface",
                "portal.dat",
                LegacyDatConversionPhase.Phase1WorldData,
                "test",
                EnabledForConversion: true,
                _ => Array.Empty<uint>(),
                (_, _) => new LegacyDatConversionClassification(LegacyDatConversionStatus.Supported),
                (_, _, _, _) => true);
            var cellDefinition = new LegacyDatConversionTypeDefinition(
                "LandBlock",
                "cell.dat",
                LegacyDatConversionPhase.Phase1WorldData,
                "test",
                EnabledForConversion: true,
                _ => Array.Empty<uint>(),
                (_, _) => new LegacyDatConversionClassification(LegacyDatConversionStatus.Supported),
                (_, _, _, _) => true);

            Assert.False(LegacyDatConversionService.ShouldConvertDefinitionInPrimaryPass(portalDefinition));
            Assert.True(LegacyDatConversionService.ShouldConvertDefinitionInPrimaryPass(cellDefinition));
        }

        private static string CreateLegacyDatFixture(
            bool includeLanguageDat,
            bool includeEnvironment = false,
            bool includeWorldReference = false) {
            string dir = Path.Combine(Path.GetTempPath(), $"acme-legacy-convert-{Guid.NewGuid():N}");
            Directory.CreateDirectory(dir);

            var cellEntries = new List<(uint Id, byte[] Payload)>();
            if (includeWorldReference) {
                cellEntries.Add((0x7D64FFFEu, BuildLandBlockInfoWithObjectStab(0x01000022u)));
            }

            WriteDarkMajestyDat(
                Path.Combine(dir, "cell.dat"),
                cellEntries);

            var portalEntries = new List<(uint Id, byte[] Payload)>(BuildPortalEntries(includeEnvironment));
            if (includeWorldReference) {
                portalEntries.Add((0x01009999u, BuildLegacyGfxObjBytes(0x01009999u)));
            }

            WriteDarkMajestyDat(
                Path.Combine(dir, "portal.dat"),
                portalEntries);

            if (includeLanguageDat) {
                WriteDarkMajestyDat(
                    Path.Combine(dir, "language.dat"),
                    new[] {
                        (0x23000001u, Encoding.ASCII.GetBytes("deferred-string-table")),
                    });
            }

            return dir;
        }

        private static (uint Id, byte[] Payload)[] BuildPortalEntries(bool includeEnvironment) {
            var entries = new List<(uint Id, byte[] Payload)> {
                (0x05000042u, BuildLegacySurfaceTextureBytes()),
                (0x01000022u, BuildLegacyGfxObjBytes()),
                (0x130F0000u, Encoding.ASCII.GetBytes("bad-region")),
            };

            if (includeEnvironment) {
                entries.Add((0x0D000094u, BuildLegacyEnvironmentBytes()));
            }

            return entries.ToArray();
        }

        private static byte[] BuildLegacySurfaceTextureBytes(bool includeExtraMip = false) {
            var buffer = new byte[64];
            var writer = new DatBinWriter(buffer);
            writer.WriteUInt32(0x05000042);
            writer.WriteUInt32(2); // INDEX8
            writer.WriteInt32(2);
            writer.WriteInt32(1);
            writer.WriteByte(3);
            writer.WriteByte(9);
            if (includeExtraMip) {
                writer.WriteByte(11);
            }
            writer.WriteUInt32(0x04000077);
            return buffer.AsSpan(0, writer.Offset).ToArray();
        }

        private static byte[] BuildLegacyArgbSurfaceTextureBytes(bool includeExtraMip = false) {
            var buffer = new byte[64];
            var writer = new DatBinWriter(buffer);
            writer.WriteUInt32(0x05000043);
            writer.WriteUInt32(5); // A8R8G8B8
            writer.WriteInt32(2);
            writer.WriteInt32(1);
            var basePixels = new byte[] {
                0x10, 0x20, 0x30, 0x40,
                0x50, 0x60, 0x70, 0x80,
            };
            writer.WriteBytes(basePixels, basePixels.Length);
            if (includeExtraMip) {
                var mipPixels = new byte[] {
                    0xAA, 0xBB, 0xCC, 0xDD,
                };
                writer.WriteBytes(mipPixels, mipPixels.Length);
            }
            return buffer.AsSpan(0, writer.Offset).ToArray();
        }

        private static byte[] BuildLegacyGfxObjBytes(uint id = 0x01000022) {
            var buffer = new byte[768];
            var writer = new DatBinWriter(buffer);

            writer.WriteUInt32(id);
            writer.WriteUInt32((uint)(GfxObjFlags.HasPhysics | GfxObjFlags.HasDrawing));
            writer.WriteUInt32(1);
            writer.WriteUInt32(0x08000124);
            writer.WriteInt32((int)VertexType.CSWVertexType);
            writer.WriteUInt32(3);
            writer.WriteUInt16(1);
            writer.WriteUInt16(1);
            writer.WriteVector3(System.Numerics.Vector3.Zero);
            writer.WriteVector3(System.Numerics.Vector3.UnitZ);
            writer.WriteSingle(0f);
            writer.WriteSingle(0f);
            writer.WriteUInt16(2);
            writer.WriteUInt16(1);
            writer.WriteVector3(System.Numerics.Vector3.UnitX);
            writer.WriteVector3(System.Numerics.Vector3.UnitZ);
            writer.WriteSingle(1f);
            writer.WriteSingle(0f);
            writer.WriteUInt16(3);
            writer.WriteUInt16(1);
            writer.WriteVector3(System.Numerics.Vector3.UnitY);
            writer.WriteVector3(System.Numerics.Vector3.UnitZ);
            writer.WriteSingle(0f);
            writer.WriteSingle(1f);

            writer.WriteUInt32(1);
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
            writer.WriteUInt32(0x4C454146);
            writer.WriteInt32(0);
            writer.WriteInt32(1);
            writer.WriteVector3(System.Numerics.Vector3.Zero);
            writer.WriteSingle(1f);
            writer.WriteUInt32(0);
            writer.Align(4);

            writer.WriteVector3(new System.Numerics.Vector3(4f, 5f, 6f));

            writer.WriteUInt32(1);
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
            writer.WriteUInt32(0x4C454146);
            writer.WriteInt32(2);
            writer.Align(4);

            return buffer.AsSpan(0, writer.Offset).ToArray();
        }

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
            WriteLegacyVertex(writer, 0, System.Numerics.Vector3.Zero, 0f, 0f);
            WriteLegacyVertex(writer, 1, System.Numerics.Vector3.UnitX, 1f, 0f);
            WriteLegacyVertex(writer, 2, System.Numerics.Vector3.UnitY, 0f, 1f);

            writer.WriteUInt16(11);
            writer.WriteByte(3);
            writer.WriteByte((byte)StipplingType.NoPos);
            writer.WriteInt32((int)CullMode.None);
            writer.WriteInt16(2);
            writer.WriteInt16(2);
            writer.WriteInt16(0);
            writer.WriteInt16(1);
            writer.WriteInt16(2);
            writer.Align(4);

            writer.WriteUInt16(11);
            writer.Align(4);

            writer.WriteUInt32(0x4C454146);
            writer.WriteInt32(0);
            writer.Align(4);

            writer.WriteUInt32(0x4C454146);
            writer.WriteInt32(0);
            writer.WriteInt32(1);
            writer.WriteVector3(System.Numerics.Vector3.Zero);
            writer.WriteSingle(1f);
            writer.WriteUInt32(0);
            writer.Align(4);

            writer.WriteUInt32(0);
            writer.Align(4);

            return buffer.AsSpan(0, writer.Offset).ToArray();
        }

        private static byte[] BuildLandBlockInfoWithObjectStab(uint stabId) {
            var buffer = new byte[512];
            var writer = new DatBinWriter(buffer);

            writer.WriteUInt32(0x7D64FFFE);
            writer.WriteUInt32(0);
            writer.WriteUInt32(1);
            writer.WriteItem(new Stab {
                Id = stabId,
                Frame = new Frame {
                    Origin = Vector3.Zero,
                    Orientation = Quaternion.Identity,
                },
            });
            writer.WriteUInt16(0);
            writer.WriteUInt16(0);
            writer.Align(4);

            return buffer.AsSpan(0, writer.Offset).ToArray();
        }

        private static void WriteLegacyVertex(DatBinWriter writer, ushort id, System.Numerics.Vector3 origin, float u, float v) {
            writer.WriteUInt16(id);
            writer.WriteUInt16(1);
            writer.WriteVector3(origin);
            writer.WriteVector3(System.Numerics.Vector3.UnitZ);
            writer.WriteSingle(u);
            writer.WriteSingle(v);
        }

        private static void WriteDarkMajestyDat(string path, IReadOnlyList<(uint Id, byte[] Payload)> entries, int iteration = 8) {
            const int HeaderOffset = 0x12C;
            const int RootOffset = 0x200;
            const int BlockSize = 0x404;
            const int DirectoryDataLength = 0x400;
            const int BranchCount = 0x25;

            int payloadOffset = Align(RootOffset + BlockSize, 4);
            using var ms = new MemoryStream();
            using var writer = new BinaryWriter(ms);

            ms.SetLength(payloadOffset);
            ms.Position = RootOffset;
            writer.Write(0u); // single-block directory chain

            byte[] directoryData = new byte[DirectoryDataLength];
            using (var dirStream = new MemoryStream(directoryData))
            using (var dirWriter = new BinaryWriter(dirStream)) {
                for (int i = 0; i < BranchCount; i++) {
                    dirWriter.Write(0u);
                }

                dirWriter.Write((uint)entries.Count);
                int nextOffset = payloadOffset;
                foreach (var (id, payload) in entries) {
                    dirWriter.Write(id);
                    dirWriter.Write((uint)nextOffset);
                    dirWriter.Write((uint)payload.Length);
                    nextOffset = Align(nextOffset + 4 + payload.Length, 4);
                }
            }

            writer.Write(directoryData);

            foreach (var (_, payload) in entries) {
                ms.Position = payloadOffset;
                writer.Write(0u); // no continuation block
                writer.Write(payload);
                payloadOffset = Align(payloadOffset + 4 + payload.Length, 4);
                ms.SetLength(Math.Max(ms.Length, payloadOffset));
            }

            ms.Position = HeaderOffset;
            writer.Write(0x5442u);
            writer.Write((uint)BlockSize);
            writer.Write((uint)ms.Length);
            writer.Write(iteration);
            writer.Write(0u);
            writer.Write(0u);
            writer.Write(0u);
            writer.Write((uint)RootOffset);

            File.WriteAllBytes(path, ms.ToArray());
        }

        private static byte[] BuildLegacySetupBytes(uint setupId, params uint[] parts) {
            using var ms = new MemoryStream();
            using var writer = new BinaryWriter(ms);
            writer.Write(setupId);
            writer.Write(0u);
            writer.Write((uint)parts.Length);
            foreach (uint part in parts) {
                writer.Write(part);
            }

            return ms.ToArray();
        }

        private static ElementDesc FindLayoutElement(LayoutDesc layout, params uint[] path) {
            Assert.NotNull(layout);
            Assert.NotNull(layout.Elements);
            Assert.NotEmpty(path);

            Dictionary<uint, ElementDesc>? current = layout.Elements;
            ElementDesc? element = null;
            foreach (uint id in path) {
                Assert.NotNull(current);
                Assert.True(current!.TryGetValue(id, out element), $"Missing layout element 0x{id:X8}");
                current = element!.Children;
            }

            return element!;
        }

        private static int Align(int value, int alignment) {
            int mask = alignment - 1;
            return (value + mask) & ~mask;
        }

        [Fact]
        public void FilterIdsForExport_SlimMerge_ExportsAllLegacyClothingTablesAndPalSets() {
            var definition = new LegacyDatConversionTypeDefinition(
                "ClothingTable",
                "portal.dat",
                LegacyDatConversionPhase.Phase1WorldData,
                "test",
                true,
                _ => new[] { 0x10000001u, 0x10000002u },
                (_, _) => new LegacyDatConversionClassification(LegacyDatConversionStatus.Supported),
                (_, _, _, _) => true);
            var closure = new LegacyDatWorldReferenceClosure();

            var filtered = LegacyDatConversionService.FilterIdsForExport(
                LegacyDatExportMode.SlimMerge,
                closure,
                definition,
                new[] { 0x10000001u, 0x10000002u });

            Assert.Equal(2, filtered.Count);
        }

        [Fact]
        public void ShouldKeepRetailPortalGlobal_DoesNotForceRetailClothingOrPalSet() {
            Assert.False(LegacyDatPortalBootstrap.ShouldKeepRetailPortalGlobal(0x0F000001u));
            Assert.False(LegacyDatPortalBootstrap.ShouldKeepRetailPortalGlobal(0x10000001u));
            Assert.True(LegacyDatPortalBootstrap.ShouldKeepRetailPortalGlobal(0x0E000002u));
        }
    }
}
