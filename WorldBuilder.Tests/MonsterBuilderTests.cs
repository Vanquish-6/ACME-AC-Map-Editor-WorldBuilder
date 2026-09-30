using System.Numerics;
using Acme.Dat;
using WorldBuilder.Shared.Lib.MonsterBuilder;

namespace WorldBuilder.Tests;

public class MonsterBuilderTests {
    [Fact]
    public void GfxObj_RoundTrip_KeepsVerticesFacesSurfacesUvsAndNormals() {
        var gfx = TriangleGfx();
        GfxObjMesh.Commit(gfx, topologyChanged: true);

        var packed = DatNativeRecords.Pack(gfx);
        var read = DatNativeRecords.Unpack<GfxObj>(packed);

        Assert.Equal(gfx.Surfaces, read.Surfaces);
        Assert.Equal(gfx.VertexEntryOrder, read.VertexEntryOrder);
        Assert.Equal(gfx.DrawingPolygonEntryOrder, read.DrawingPolygonEntryOrder);
        foreach (ushort id in gfx.VertexEntryOrder) {
            var before = gfx.VertexArray.Vertices[id];
            var after = read.VertexArray.Vertices[id];
            Assert.Equal(before.Origin, after.Origin);
            Assert.Equal(before.Normal, after.Normal);
            Assert.Equal(before.UVs[0].U, after.UVs[0].U);
            Assert.Equal(before.UVs[0].V, after.UVs[0].V);
        }
        Assert.Equal(gfx.Polygons[0].VertexIds, read.Polygons[0].VertexIds);
        Assert.Equal(gfx.Polygons[0].PosSurface, read.Polygons[0].PosSurface);
    }

    [Fact]
    public void Setup_RoundTrip_KeepsHierarchyAndPlacement() {
        var setup = TwoPartSetup(0x01000010, 0x01000011);
        var packed = DatNativeRecords.Pack(setup);
        var read = DatNativeRecords.Unpack<Setup>(packed);

        Assert.Equal(setup.Parts, read.Parts);
        Assert.Equal(setup.ParentIndex, read.ParentIndex);
        Assert.Equal(setup.DefaultScale[1], read.DefaultScale[1]);
        var before = setup.PlacementFrames[Placement.Resting].Frames[1];
        var after = read.PlacementFrames[Placement.Resting].Frames[1];
        Assert.Equal(before.Origin, after.Origin);
        Assert.Equal(before.Orientation, after.Orientation);
        Assert.Equal(setup.DefaultMtableId, read.DefaultMtableId);
    }

    [Fact]
    public void Clone_WithoutEdits_MatchesSourceGeometryAndUsesNewIds() {
        var setup = TwoPartSetup(0x01000010, 0x01000011);
        var gfxA = TriangleGfx(0x01000010);
        var gfxB = TriangleGfx(0x01000011);
        gfxB.VertexArray.Vertices[0].Origin = new Vector3(2, 0, 0);
        GfxObjMesh.Commit(gfxA, topologyChanged: true);
        GfxObjMesh.Commit(gfxB, topologyChanged: true);
        var sourceA = DatNativeRecords.CloneMessagePack(gfxA);
        var session = MonsterSession.FromRecords(setup, new Dictionary<uint, GfxObj> {
            [0x01000010] = gfxA,
            [0x01000011] = gfxB,
        });

        session.CloneNewIds([], []);

        Assert.NotEqual(setup.Id, session.WorkingSetup!.Id);
        Assert.NotEqual(0x01000010u, session.WorkingSetup.Parts[0]);
        Assert.Equal(sourceA.VertexArray.Vertices[0].Origin, session.GfxForPart(0)!.VertexArray.Vertices[0].Origin);
        Assert.Equal(new Vector3(2, 0, 0), session.GfxForPart(1)!.VertexArray.Vertices[0].Origin);
        Assert.Equal(setup.ParentIndex, session.WorkingSetup.ParentIndex);
        Assert.Equal(setup.PlacementFrames[Placement.Resting].Frames[1].Origin,
            session.WorkingSetup.PlacementFrames[Placement.Resting].Frames[1].Origin);
        Assert.Equal(0x01000010u, gfxA.Id);
    }

    [Fact]
    public void GeometryScale_DoesNotMovePlacementFrame() {
        var setup = TwoPartSetup(0x01000010, 0x01000011);
        var session = MonsterSession.FromRecords(setup, new Dictionary<uint, GfxObj> {
            [0x01000010] = TriangleGfx(0x01000010),
            [0x01000011] = TriangleGfx(0x01000011),
        });
        var origin = session.WorkingSetup!.PlacementFrames[Placement.Resting].Frames[0].Origin;
        session.EditGeometry(0, gfx => GfxObjMesh.ScaleUniform(gfx, 2f));

        Assert.Equal(origin, session.WorkingSetup.PlacementFrames[Placement.Resting].Frames[0].Origin);
        Assert.Equal(new Vector3(2, 0, 0), session.GfxForPart(0)!.VertexArray.Vertices[1].Origin);
    }

    [Fact]
    public void ObjImport_KeepsTriangleSoupUntilDedupRequested() {
        const string obj = """
            v 0 0 0
            v 1 0 0
            v 0 1 0
            v 0 0 0
            v 1 0 0
            v 0 0 1
            vn 0 0 1
            vn 0 0 1
            vn 0 0 1
            vt 0 0
            vt 1 0
            vt 0 1
            usemtl surface_0x08000001
            f 1/1/1 2/2/2 3/3/3
            f 4//1 5//1 6//1
            """;
        Assert.True(ObjPartImport.TryParse(obj, deduplicate: false, out var mesh, out var error), error);
        Assert.NotNull(mesh);
        Assert.Equal(6, mesh!.Positions.Count);
        Assert.Equal(2, mesh.SourceTriangles);
        Assert.Equal(0x08000001u, mesh.Surfaces[0]);

        var gfx = TriangleGfx();
        GfxObjMesh.ReplaceTriangles(gfx, mesh.Positions, mesh.Normals, mesh.Uvs, mesh.SurfaceIndices, mesh.Surfaces, deduplicate: false);
        Assert.Equal(6, gfx.VertexArray.Vertices.Count);
        GfxObjMesh.Deduplicate(gfx);
        Assert.True(gfx.VertexArray.Vertices.Count < 6);
    }

    [Fact]
    public void AssembledObj_ConvertsBackToGfxObjLocal() {
        var setup = TwoPartSetup(0x01000010, 0x01000011);
        var mesh = new ObjPartMesh();
        mesh.Positions.Add(new Vector3(11, 0, 0));
        mesh.Normals.Add(Vector3.UnitX);
        mesh.Uvs.Add(Vector2.Zero);
        mesh.SurfaceIndices.Add(0);
        ObjPartImport.ConvertAssembledToLocal(mesh, SetupAssembly.LocalToSetup(setup, 1));
        Assert.Equal(1f, mesh.Positions[0].X, 3);
        Assert.Equal(0f, mesh.Positions[0].Y, 3);
    }

    [Fact]
    public void Mirror_UsesSetupSpaceRatherThanLocalXOnly() {
        var setup = TwoPartSetup(0x01000010, 0x01000011);
        var left = TriangleGfx(0x01000010);
        var right = TriangleGfx(0x01000011);
        var session = MonsterSession.FromRecords(setup, new Dictionary<uint, GfxObj> {
            [0x01000010] = left,
            [0x01000011] = right,
        });
        session.SetMirrorPartner(0, 1);
        session.MirrorToPartner(0);

        var mirrored = session.GfxForPart(1)!;
        Assert.Contains(mirrored.VertexArray.Vertices.Values, vertex =>
            Vector3.Distance(vertex.Origin, new Vector3(-10, 0, 0)) < 0.001f);
    }

    [Fact]
    public void Validation_FatalOnBadVertex_WarnsOnDegenerate() {
        var gfx = TriangleGfx();
        gfx.Polygons[0].VertexIds[2] = 99;
        var setup = TwoPartSetup(gfx.Id, gfx.Id);
        var report = MonsterValidation.Check(setup, new Dictionary<uint, GfxObj> { [gfx.Id] = gfx }, null, null, null);
        Assert.Contains(report.Fatals, issue => issue.Message.Contains("vertex 99", StringComparison.Ordinal));

        var flat = TriangleGfx();
        flat.VertexArray.Vertices[2].Origin = flat.VertexArray.Vertices[0].Origin;
        var flatSetup = TwoPartSetup(flat.Id, flat.Id);
        var warning = MonsterValidation.Check(flatSetup, new Dictionary<uint, GfxObj> { [flat.Id] = flat }, null, null, null);
        Assert.Contains(warning.Warnings, issue => issue.Message.Contains("degenerate", StringComparison.Ordinal));
        Assert.DoesNotContain(warning.Fatals, issue => issue.Message.Contains("degenerate", StringComparison.Ordinal));
    }

    [Fact]
    public void ProjectJson_RoundTripsWorkingMeshWithoutDatIdsChangingSource() {
        var setup = TwoPartSetup(0x01000010, 0x01000011);
        var session = MonsterSession.FromRecords(setup, new Dictionary<uint, GfxObj> {
            [0x01000010] = TriangleGfx(0x01000010),
            [0x01000011] = TriangleGfx(0x01000011),
        });
        session.CloneNewIds([], []);
        session.SetRole(0, MonsterRoles.Head);
        session.EditGeometry(0, gfx => GfxObjMesh.Translate(gfx, new Vector3(0, 0, 0.25f)));
        var restored = MonsterSession.FromJson(session.ToJson());

        Assert.Equal(MonsterRoles.Head, restored.Project.Parts[0].Role);
        Assert.Equal(session.WorkingSetup!.Id, restored.WorkingSetup!.Id);
        Assert.Equal(new Vector3(0, 0, 0.25f), restored.GfxForPart(0)!.VertexArray.Vertices[0].Origin);
    }

    [Fact]
    public void ObjImport_KeepsGroupNamesAndComputesFaceNormalsWhenMissing() {
        const string obj = """
            o head
            v 0 0 0
            v 1 0 0
            v 0 1 0
            f 1 2 3
            g setup_02000034_part1_0x01000011
            v 0 0 1
            v 1 0 1
            v 0 0 2
            f 4 5 6
            """;
        Assert.True(ObjPartImport.TryParse(obj, deduplicate: false, out var mesh, out var error), error);
        Assert.Equal(new[] { "head", "setup_02000034_part1_0x01000011" }, mesh!.GroupNames);
        Assert.Equal(0, mesh.GroupIndices[0]);
        Assert.Equal(1, mesh.GroupIndices[3]);
        Assert.False(mesh.HadNormals);
        Assert.Equal(Vector3.UnitZ, mesh.Normals[0]);
        Assert.Equal(1f, MathF.Abs(mesh.Normals[3].Y), 3);
    }

    [Fact]
    public void ObjImport_YUpBecomesAcZUp() {
        const string obj = """
            v 0 0 0
            v 1 0 0
            v 0 1 0
            vn 0 1 0
            f 1//1 2//1 3//1
            """;
        Assert.True(ObjPartImport.TryParse(obj, deduplicate: false, out var mesh, out _));
        ObjPartImport.ConvertUpAxis(mesh!, ObjUpAxis.YUp);
        Assert.Equal(new Vector3(0, 0, 1), mesh!.Positions[2]);
        Assert.Equal(Vector3.UnitZ, mesh.Normals[0]);
        Assert.Equal(ObjUpAxis.YUp, ObjPartImport.DetectUpAxis(obj));
        Assert.Equal(ObjUpAxis.ZUp, ObjPartImport.DetectUpAxis("# AC Setup → Wavefront OBJ (WorldBuilder)\n" + obj));
        Assert.Equal(ObjUpAxis.ZUp, ObjPartImport.DetectUpAxis("o setup_02000034_part0_0x01000010\n" + obj));
    }

    [Fact]
    public void GroupNames_ResolveToParts() {
        var labels = new Dictionary<string, int> { ["leftclaw"] = 5 };
        var roles = new Dictionary<string, int> { ["head"] = 2, ["upperarml"] = 3 };
        Assert.True(ObjPartImport.TryResolveGroup("setup_02000034_part7_0x0100ABCD", 10, labels, roles, out int part) && part == 7);
        Assert.True(ObjPartImport.TryResolveGroup("part_4", 10, labels, roles, out part) && part == 4);
        Assert.True(ObjPartImport.TryResolveGroup("Head.001", 10, labels, roles, out part) && part == 2);
        Assert.True(ObjPartImport.TryResolveGroup("Upper Arm L", 10, labels, roles, out part) && part == 3);
        Assert.True(ObjPartImport.TryResolveGroup("Left Claw", 10, labels, roles, out part) && part == 5);
        Assert.False(ObjPartImport.TryResolveGroup("Cube", 10, labels, roles, out _));
        Assert.False(ObjPartImport.TryResolveGroup("part99", 10, labels, roles, out _));
    }

    [Fact]
    public void WholeMonsterImport_SplitsByNearestOriginalPartAndConvertsToLocal() {
        // Part 0 sits at the origin, part 1 is attached 10 units along X.
        var setup = TwoPartSetup(0x01000010, 0x01000011);
        var session = MonsterSession.FromRecords(setup, new Dictionary<uint, GfxObj> {
            [0x01000010] = TriangleGfx(0x01000010),
            [0x01000011] = TriangleGfx(0x01000011),
        });
        // Two unnamed triangles in assembled space: one near part 0, one near part 1.
        const string obj = """
            v 0 0 0
            v 1 0 0
            v 0 1 0
            v 10 0 0
            v 11 0 0
            v 10 1 0
            f 1 2 3
            f 4 5 6
            """;
        var options = new ObjMonsterImportOptions { UpAxis = ObjUpAxis.ZUp, FitToCreature = false, Deduplicate = false };
        Assert.True(session.ImportMonsterFromObj(obj, options, out var result, out var error), error);
        Assert.True(result!.UsedPositionMapping);
        Assert.Equal(1, result.TrianglesPerPart[0]);
        Assert.Equal(1, result.TrianglesPerPart[1]);
        // Part 1's triangle was converted back into its local frame (x = 10 -> 0).
        var part1 = session.GfxForPart(1)!;
        Assert.Contains(part1.VertexArray.Vertices.Values, v => Vector3.Distance(v.Origin, Vector3.Zero) < 1e-4f);
        Assert.Contains(part1.VertexArray.Vertices.Values, v => Vector3.Distance(v.Origin, new Vector3(1, 0, 0)) < 1e-4f);
        Assert.True(session.CanUndo);
        session.Undo();
        Assert.Equal(new Vector3(1, 0, 0), session.GfxForPart(0)!.VertexArray.Vertices[1].Origin);
    }

    [Fact]
    public void WholeMonsterImport_UsesGroupNamesFitsHeightAndEmptiesUnmatchedParts() {
        var setup = TwoPartSetup(0x01000010, 0x01000011);
        var gfxA = TriangleGfx(0x01000010);
        gfxA.VertexArray.Vertices[2].Origin = new Vector3(0, 0, 2); // creature is 2 units tall
        var session = MonsterSession.FromRecords(setup, new Dictionary<uint, GfxObj> {
            [0x01000010] = gfxA,
            [0x01000011] = TriangleGfx(0x01000011),
        });
        session.SetLabel(0, "Body");
        // Y-up file, 20 units tall, all in a group named after part 0's label.
        const string obj = """
            o Body
            v 0 0 0
            v 4 0 0
            v 0 20 0
            f 1 2 3
            """;
        var options = new ObjMonsterImportOptions { UpAxis = ObjUpAxis.YUp, FitToCreature = true, ClearUnmatchedParts = true };
        Assert.True(session.ImportMonsterFromObj(obj, options, out var result, out var error), error);
        Assert.Contains("Body", result!.NamedGroups);
        Assert.False(result.UsedPositionMapping);
        Assert.Equal(0.1f, result.Scale, 3);
        Assert.Equal(new[] { 1 }, result.ClearedParts);

        var body = session.GfxForPart(0)!;
        var stats = GfxObjMesh.Stats(body);
        Assert.Equal(2f, stats.Size.Z, 3);
        Assert.Equal(0f, stats.Min.Z, 3);
        var emptied = GfxObjMesh.Stats(session.GfxForPart(1)!);
        Assert.Equal(1, emptied.TriangleCount);
        Assert.True(emptied.Size.Length() < 0.01f);
        Assert.Equal(0, emptied.DegenerateTriangles);

        var report = session.Validate(null, null);
        Assert.DoesNotContain(report.Fatals, issue => issue.Message.Contains("no vertices", StringComparison.Ordinal));
    }

    [Fact]
    public void WholeMonsterImport_AutoWeldsWhenTriangleSoupWouldOverflow() {
        var setup = TwoPartSetup(0x01000010, 0x01000011);
        var session = MonsterSession.FromRecords(setup, new Dictionary<uint, GfxObj> {
            [0x01000010] = TriangleGfx(0x01000010),
            [0x01000011] = TriangleGfx(0x01000011),
        });
        // 24,000 triangles sharing a small vertex pool: 72,000 corners > 65,535, but few unique vertices.
        var text = new System.Text.StringBuilder("o part0\n");
        for (int i = 0; i < 30; i++)
            text.Append(System.Globalization.CultureInfo.InvariantCulture, $"v {i * 0.01f} 0 0\nv {i * 0.01f} 0.01 0\nv {i * 0.01f} 0 0.01\n");
        for (int i = 0; i < 24000; i++) {
            int b = (i % 30) * 3 + 1;
            text.Append(System.Globalization.CultureInfo.InvariantCulture, $"f {b} {b + 1} {b + 2}\n");
        }
        var options = new ObjMonsterImportOptions { UpAxis = ObjUpAxis.ZUp, FitToCreature = false, Deduplicate = false, ClearUnmatchedParts = false };
        Assert.True(session.ImportMonsterFromObj(text.ToString(), options, out var result, out var error), error);
        Assert.Equal(24000, result!.TrianglesPerPart[0]);
        Assert.True(session.GfxForPart(0)!.VertexArray.Vertices.Count <= 90);
    }

    [Fact]
    public void MaterialLibrary_ReadsDiffuseMapsAndColours() {
        const string mtl = """
            # Blender MTL File
            newmtl Skin
            Kd 0.800000 0.200000 0.100000
            d 1.000000
            map_Kd -s 1 1 1 -clamp on textures/skin atlas.png

            newmtl Eyes
            Kd 0.1 0.2 0.9
            d 0.5

            newmtl Claws
            map_Ka claws.jpg
            """;
        var materials = ObjMaterialLibrary.Parse(mtl);
        Assert.Equal("textures/skin atlas.png", materials["Skin"].DiffuseMap);
        Assert.Equal(0xFFCC331Au, materials["Skin"].DiffuseColor);
        Assert.Null(materials["Eyes"].DiffuseMap);
        Assert.Equal(0x801A33E6u, materials["Eyes"].DiffuseColor);
        Assert.Equal("claws.jpg", materials["Claws"].DiffuseMap);
    }

    [Fact]
    public void ObjImport_MapsMaterialsToSurfacesAndFlipsV() {
        const string obj = """
            mtllib monster.mtl
            v 0 0 0
            v 1 0 0
            v 0 1 0
            vt 0 0.25
            vt 1 0.25
            vt 0 1
            usemtl Skin
            f 1/1 2/2 3/3
            usemtl Eyes
            f 1/1 2/2 3/3
            f 3/3 2/2 1/1
            """;
        Assert.True(ObjPartImport.TryParse(obj, deduplicate: false, out var mesh, out var error), error);
        Assert.Equal(new[] { "monster.mtl" }, mesh!.MaterialLibraries);
        Assert.Equal(new[] { "Skin", "Eyes" }, mesh.MaterialNames);
        Assert.Equal(0, mesh.MaterialIndices[0]);
        Assert.Equal(1, mesh.MaterialIndices[3]);
        var (libraries, used) = ObjPartImport.ScanMaterials(obj);
        Assert.Equal(mesh.MaterialLibraries, libraries);
        Assert.Equal(mesh.MaterialNames, used);

        Assert.True(ObjPartImport.ApplyMaterialSurfaces(mesh, new Dictionary<string, uint> { ["Skin"] = 0x0800F001 }, defaultSurface: 0x0800F002));
        Assert.Equal(new[] { 0x0800F001u, 0x0800F002u }, mesh.Surfaces);
        Assert.Equal(0, mesh.SurfaceIndices[0]);
        Assert.Equal(1, mesh.SurfaceIndices[3]);

        ObjPartImport.FlipV(mesh);
        Assert.Equal(0.75f, mesh.Uvs[0].Y, 4);
        Assert.Equal(0f, mesh.Uvs[2].Y, 4);
    }

    [Fact]
    public void Session_AddTexture_PaintsPartsAndRoundTripsThroughJson() {
        var setup = TwoPartSetup(0x01000010, 0x01000011);
        var session = MonsterSession.FromRecords(setup, new Dictionary<uint, GfxObj> {
            [0x01000010] = TriangleGfx(0x01000010),
            [0x01000011] = TriangleGfx(0x01000011),
        });
        var bgra = new byte[32 * 32 * 4];
        bgra[0] = 0x11; bgra[1] = 0x22; bgra[2] = 0x33; bgra[3] = 0xFF;
        uint surface = session.AddTexture("skin", bgra, 32, 32, [0x0800F001u], [0x05FF0003u], [], source: "skin.png");

        Assert.Equal(0x0800F002u, surface);
        var record = session.Surfaces[surface];
        Assert.Equal(SurfaceType.Base1Image, record.Type);
        Assert.Equal(0x05FF0004u, record.OrigTextureId);
        var texture = session.SurfaceTextures[record.OrigTextureId];
        Assert.Single(texture.Textures);
        var render = session.RenderSurfaces[texture.Textures[0]];
        Assert.Equal(0x06FF0001u, render.Id);
        Assert.Equal(32, render.Width);
        Assert.Equal((uint)PixelFormat.PFID_A8R8G8B8, render.Format);
        Assert.Same(render, session.TryGetTextureRecord(typeof(RenderSurface), render.Id));

        session.PaintParts(surface, [1]);
        Assert.Equal(new[] { surface }, session.GfxForPart(1)!.Surfaces);
        Assert.Equal(new[] { 0x08000001u }, session.GfxForPart(0)!.Surfaces);
        Assert.True(session.CanUndo);

        uint solid = session.AddSolidSurface("eyes", 0x80FF0000u, []);
        Assert.Equal(SurfaceType.Base1Solid, session.Surfaces[solid].Type);
        Assert.Equal(0.5f, session.Surfaces[solid].Translucency, 2);

        var restored = MonsterSession.FromJson(session.ToJson());
        Assert.Equal("skin", restored.TextureName(surface));
        Assert.Equal(0x11, restored.RenderSurfaces[render.Id].SourceData[0]);
        Assert.Equal(record.OrigTextureId, restored.Surfaces[surface].OrigTextureId);
        Assert.Equal(new[] { surface }, restored.GfxForPart(1)!.Surfaces);
        Assert.Equal(2, restored.Project.Textures.Count);
    }

    [Fact]
    public void ReferenceSetupObj_ImportsWhenPresent() {
        string[] candidates = [
            @"C:\ACME\0x02000034_setup.obj",
            @"C:\ACME-Projects\0x02000034_setup.obj",
            @"C:\Users\chris\Downloads\0x02000034_setup.obj",
        ];
        var path = candidates.FirstOrDefault(File.Exists);
        if (path == null)
            return;
        var text = File.ReadAllText(path);
        Assert.True(ObjPartImport.TryParse(text, deduplicate: false, out var mesh, out var error), error);
        Assert.NotNull(mesh);
        Assert.True(mesh!.SourceTriangles > 0);
        Assert.Equal(mesh.SourceTriangles * 3, mesh.Positions.Count);
    }

    static GfxObj TriangleGfx(uint id = 0x01000010) {
        var gfx = new GfxObj {
            Id = id,
            Surfaces = [0x08000001],
            VertexArray = new VertexArray {
                VertexType = VertexType.CSWVertexType,
                Vertices = new Dictionary<ushort, SWVertex> {
                    [0] = Vertex(new Vector3(0, 0, 0), new Vector2(0, 0)),
                    [1] = Vertex(new Vector3(1, 0, 0), new Vector2(1, 0)),
                    [2] = Vertex(new Vector3(0, 1, 0), new Vector2(0, 1)),
                },
            },
            Polygons = new Dictionary<ushort, Polygon> {
                [0] = GfxObjMesh.Triangle(0, 1, 2, 0),
            },
        };
        return gfx;
    }

    static SWVertex Vertex(Vector3 origin, Vector2 uv) => new() {
        Origin = origin,
        Normal = Vector3.UnitZ,
        UVs = [new Vec2Duv { U = uv.X, V = uv.Y }],
    };

    static Setup TwoPartSetup(uint gfxA, uint gfxB) {
        var setup = new Setup {
            Id = 0x02000034,
            Flags = SetupAssembly.HasParent | SetupAssembly.HasDefaultScale,
            NumParts = 2,
            Parts = [gfxA, gfxB],
            ParentIndex = [uint.MaxValue, 0],
            DefaultScale = [[1f, 1f, 1f], [1f, 1f, 1f]],
            DefaultMtableId = 0x09000001,
            PlacementFrames = new Dictionary<Placement, SetupPlacement> {
                [Placement.Resting] = new SetupPlacement {
                    Key = (uint)Placement.Resting,
                    HookData = [0, 0, 0, 0],
                    Frames = [
                        new Frame { Origin = Vector3.Zero, Orientation = Quaternion.Identity },
                        new Frame { Origin = new Vector3(10, 0, 0), Orientation = Quaternion.Identity },
                    ],
                },
            },
        };
        return setup;
    }
}
