using System.Globalization;
using System.Numerics;
using System.Text.Json;
using Acme.Dat;
using MessagePack;
using WorldBuilder.Shared.Documents;

namespace WorldBuilder.Shared.Lib.MonsterBuilder;

/// <summary>
/// Editable monster. The source Setup and GfxObjs are copied before any edit.
/// Publishing writes only the working ids.
/// </summary>
public sealed class MonsterSession {
    static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    const int UndoLimit = 40;

    readonly List<byte[]> _undo = [];
    readonly List<byte[]> _redo = [];
    int _undoSuspend;

    public MonsterProjectModel Project { get; private set; } = new();
    public Setup? WorkingSetup { get; private set; }
    public Dictionary<uint, GfxObj> GfxById { get; } = [];
    /// <summary>Surfaces created in this session (0x08…). Not part of undo; publishing writes them with the meshes.</summary>
    public Dictionary<uint, Surface> Surfaces { get; } = [];
    public Dictionary<uint, SurfaceTexture> SurfaceTextures { get; } = [];
    public Dictionary<uint, RenderSurface> RenderSurfaces { get; } = [];
    public uint SourceSetupId { get; private set; }
    public bool IsCloned => Project.Cloned;
    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;

    public static MonsterSession Load(IDatReaderWriter dats, uint setupId) {
        if (!dats.TryGet<Setup>(setupId, out var setup) || setup == null)
            throw new InvalidOperationException($"Setup 0x{setupId:X8} was not found.");
        var gfx = new Dictionary<uint, GfxObj>();
        foreach (uint part in setup.Parts.Distinct()) {
            if (!dats.TryGet<GfxObj>(part, out var obj) || obj == null)
                throw new InvalidOperationException($"Setup 0x{setupId:X8} part GfxObj 0x{part:X8} was not found.");
            gfx[part] = obj;
        }
        return FromRecords(setup, gfx);
    }

    public static MonsterSession FromRecords(Setup sourceSetup, IReadOnlyDictionary<uint, GfxObj> sourceGfx) {
        var session = new MonsterSession {
            SourceSetupId = sourceSetup.Id,
            WorkingSetup = Copy(sourceSetup),
        };
        session.Project.SourceSetup = Hex(sourceSetup.Id);
        session.Project.WorkingSetup = Hex(sourceSetup.Id);
        for (int i = 0; i < sourceSetup.Parts.Count; i++) {
            uint id = sourceSetup.Parts[i];
            if (!sourceGfx.TryGetValue(id, out var gfx))
                throw new InvalidOperationException($"Missing GfxObj 0x{id:X8}.");
            if (!session.GfxById.ContainsKey(id))
                session.GfxById[id] = CopyGfx(gfx);
            session.Project.Parts.Add(new MonsterPartModel {
                Index = i,
                SourceGfxObj = Hex(id),
                WorkingGfxObj = Hex(id),
            });
        }
        return session;
    }

    public void CloneNewIds(IEnumerable<uint> existingGfxIds, IEnumerable<uint> existingSetupIds) {
        if (WorkingSetup == null)
            throw new InvalidOperationException("Load a Setup first.");
        RememberUndo();
        var gfxTaken = existingGfxIds.ToHashSet();
        var setupTaken = existingSetupIds.ToHashSet();
        gfxTaken.UnionWith(GfxById.Keys);
        setupTaken.Add(WorkingSetup.Id);

        uint setupId = ObjSingleMeshImporter.AllocateNextId(0x02000000, setupTaken);
        var claimed = new HashSet<uint>();
        var instances = new GfxObj[WorkingSetup.Parts.Count];
        var newIds = new uint[WorkingSetup.Parts.Count];
        for (int i = 0; i < WorkingSetup.Parts.Count; i++) {
            uint oldId = WorkingSetup.Parts[i];
            if (!GfxById.TryGetValue(oldId, out var gfx))
                throw new InvalidOperationException($"Missing GfxObj 0x{oldId:X8}.");
            instances[i] = claimed.Add(oldId) ? gfx : Copy(gfx);
            uint next = ObjSingleMeshImporter.AllocateNextId(0x01000000, gfxTaken);
            gfxTaken.Add(next);
            instances[i].Id = next;
            newIds[i] = next;
            WorkingSetup.Parts[i] = next;
            var part = Project.Parts.FirstOrDefault(item => item.Index == i);
            if (part != null)
                part.WorkingGfxObj = Hex(next);
        }
        GfxById.Clear();
        for (int i = 0; i < instances.Length; i++)
            GfxById[newIds[i]] = instances[i];
        WorkingSetup.Id = setupId;
        WorkingSetup.NumParts = (uint)WorkingSetup.Parts.Count;
        Project.WorkingSetup = Hex(setupId);
        Project.Cloned = true;
        _redo.Clear();
    }

    public GfxObj? GfxForPart(int partIndex) {
        if (WorkingSetup == null || partIndex < 0 || partIndex >= WorkingSetup.Parts.Count)
            return null;
        return GfxById.TryGetValue(WorkingSetup.Parts[partIndex], out var gfx) ? gfx : null;
    }

    public void SetLabel(int partIndex, string? label) {
        var part = Part(partIndex);
        if (part == null) return;
        part.Label = string.IsNullOrWhiteSpace(label) ? null : label.Trim();
    }

    public void SetRole(int partIndex, string? role) {
        var part = Part(partIndex);
        if (part == null) return;
        part.Role = string.IsNullOrWhiteSpace(role) ? null : role;
    }

    public void SetMirrorPartner(int partIndex, int? partner) {
        var part = Part(partIndex);
        if (part == null) return;
        part.MirrorPartner = partner;
    }

    public void SetHidden(int partIndex, bool hidden) {
        var part = Part(partIndex);
        if (part == null) return;
        part.Hidden = hidden;
    }

    public void EditGeometry(int partIndex, Action<GfxObj> edit) {
        var gfx = RequirePart(partIndex);
        RememberUndo();
        edit(gfx);
        _redo.Clear();
    }

    public void EditAttachment(int partIndex, Vector3 origin, Quaternion orientation, Vector3? scale) {
        if (WorkingSetup == null)
            throw new InvalidOperationException("Load a Setup first.");
        RememberUndo();
        var pose = SetupAssembly.PreviewPose(WorkingSetup);
        if (!WorkingSetup.PlacementFrames.TryGetValue(pose, out var placement)) {
            placement = new AnimationFrame {
                Key = (uint)pose,
                HookData = [0, 0, 0, 0],
                Frames = Enumerable.Range(0, WorkingSetup.Parts.Count)
                    .Select(_ => new Frame { Origin = Vector3.Zero, Orientation = Quaternion.Identity })
                    .ToList(),
            };
            WorkingSetup.PlacementFrames[pose] = placement;
        }
        while (placement.Frames.Count <= partIndex)
            placement.Frames.Add(new Frame { Origin = Vector3.Zero, Orientation = Quaternion.Identity });
        placement.Frames[partIndex].Origin = origin;
        placement.Frames[partIndex].Orientation = orientation;
        if (scale.HasValue)
            SetDefaultScale(partIndex, scale.Value);
        _redo.Clear();
    }

    public void ApplyProportion(string? role, Vector3? setupAxis, float? alongFactor, float? perpendicularFactor, float? uniform) {
        if (WorkingSetup == null)
            return;
        var parts = string.IsNullOrWhiteSpace(role)
            ? Enumerable.Range(0, WorkingSetup.Parts.Count)
            : Project.Parts.Where(part => part.Role == role).Select(part => part.Index);
        bool any = false;
        foreach (int index in parts.ToList()) {
            var gfx = GfxForPart(index);
            if (gfx == null)
                continue;
            if (!any) {
                RememberUndo();
                any = true;
            }
            if (uniform.HasValue)
                GfxObjMesh.ScaleUniform(gfx, uniform.Value);
            if (setupAxis.HasValue && alongFactor.HasValue)
                GfxObjMesh.ScaleAlong(gfx, setupAxis.Value, alongFactor.Value);
            else if (alongFactor.HasValue)
                GfxObjMesh.ScaleAlong(gfx, GfxObjMesh.LongestAxis(gfx), alongFactor.Value);
            if (setupAxis.HasValue && perpendicularFactor.HasValue)
                GfxObjMesh.ScalePerpendicular(gfx, setupAxis.Value, perpendicularFactor.Value);
            else if (perpendicularFactor.HasValue)
                GfxObjMesh.ScalePerpendicular(gfx, GfxObjMesh.LongestAxis(gfx), perpendicularFactor.Value);
        }
        if (any)
            _redo.Clear();
    }

    public void ApplyOverall(Vector3 setupAxis, float factor) {
        if (WorkingSetup == null)
            return;
        RememberUndo();
        for (int i = 0; i < WorkingSetup.Parts.Count; i++) {
            var gfx = GfxForPart(i);
            if (gfx == null)
                continue;
            GfxObjMesh.ScaleAlong(gfx, SetupAssembly.AxisInLocal(WorkingSetup, i, setupAxis), factor);
        }
        _redo.Clear();
    }

    public bool ReplacePartFromObj(int partIndex, string objText, bool objIsAssembled, bool deduplicate, out string? error) =>
        ReplacePartFromObj(partIndex, objText, new ObjPartReplaceOptions {
            ObjIsAssembled = objIsAssembled,
            Deduplicate = deduplicate,
        }, out error);

    public bool ReplacePartFromObj(int partIndex, string objText, ObjPartReplaceOptions options, out string? error) {
        error = null;
        var gfx = RequirePart(partIndex);
        if (!ObjPartImport.TryParse(objText, deduplicate: false, partIndex, out var mesh, out error) || mesh == null) {
            // A file whose _partN groups all belong to other parts parses to nothing; try it whole.
            if (!ObjPartImport.TryParse(objText, deduplicate: false, partIndex: null, out mesh, out error) || mesh == null)
                return false;
        }
        if (mesh.Positions.Count == 0) {
            error ??= "OBJ has no faces for this part.";
            return false;
        }
        ObjPartImport.ConvertUpAxis(mesh, options.UpAxis);
        if (options.FlipV)
            ObjPartImport.FlipV(mesh);
        ObjPartImport.ApplyMaterialSurfaces(mesh, options.MaterialSurfaces, options.DefaultSurface);
        if (options.ObjIsAssembled) {
            if (WorkingSetup == null) {
                error = "Load a Setup before converting assembled coordinates.";
                return false;
            }
            ObjPartImport.ConvertAssembledToLocal(mesh, SetupAssembly.LocalToSetup(WorkingSetup, partIndex));
        }
        else if (MathF.Abs(options.YawDegrees) > 1e-4f) {
            ObjPartImport.TransformInPlace(mesh, 1f, options.YawDegrees, Vector3.Zero);
        }
        if (options.FitToPart) {
            var stats = GfxObjMesh.Stats(gfx);
            if (stats.Size.LengthSquared() > 1e-10f)
                ObjPartImport.FitCentered(mesh, stats.Min, stats.Max);
        }
        var surfaces = mesh.Surfaces.Count > 0 ? mesh.Surfaces : gfx.Surfaces.ToList();
        if (surfaces.Count == 0)
            surfaces = [0x08000001u];
        bool deduplicate = options.Deduplicate || mesh.Positions.Count > ushort.MaxValue;
        var target = Copy(gfx);
        try {
            GfxObjMesh.ReplaceTriangles(target, mesh.Positions, mesh.Normals, mesh.Uvs, mesh.SurfaceIndices, surfaces, deduplicate);
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("vertex ids", StringComparison.OrdinalIgnoreCase)) {
            error = $"The OBJ needs more than {ushort.MaxValue} vertices ({mesh.TriangleCount} triangles). Decimate it first.";
            return false;
        }
        RememberUndo();
        GfxById[gfx.Id] = target;
        _redo.Clear();
        return true;
    }

    /// <summary>
    /// Splits one assembled OBJ across every part of the working Setup so the model
    /// animates with the Setup's MotionTable. Group names win; the rest goes to the
    /// part whose current mesh is nearest. One undo step.
    /// </summary>
    public bool ImportMonsterFromObj(string objText, ObjMonsterImportOptions options, out ObjMonsterImportResult? result, out string? error) {
        result = null;
        error = null;
        if (WorkingSetup == null) {
            error = "Load a Setup first. The import needs its skeleton.";
            return false;
        }
        var labels = new Dictionary<string, int>();
        var roles = new Dictionary<string, int>();
        foreach (var part in Project.Parts) {
            if (!string.IsNullOrWhiteSpace(part.Label))
                labels.TryAdd(ObjPartImport.Normalize(part.Label), part.Index);
            if (!string.IsNullOrWhiteSpace(part.Role)) {
                roles.TryAdd(ObjPartImport.Normalize(part.Role), part.Index);
                roles.TryAdd(ObjPartImport.Normalize(MonsterRoles.DisplayName(part.Role)), part.Index);
            }
        }
        if (!ObjMonsterImport.TryPlan(objText, WorkingSetup, GfxForPart, labels, roles, options, out var plan, out error) || plan == null)
            return false;
        if (!ObjMonsterImport.TryBuild(plan, WorkingSetup, GfxForPart, options, out var replacements, out error))
            return false;

        RememberUndo();
        foreach (var (id, gfx) in replacements) {
            gfx.Id = id;
            GfxById[id] = gfx;
        }
        ObjMonsterImport.GrowSpheres(WorkingSetup, id => GfxById.TryGetValue(id, out var gfx) ? gfx : null);
        _redo.Clear();
        result = plan.Result;
        return true;
    }

    /// <summary>
    /// Creates a Surface → SurfaceTexture → RenderSurface chain for an uncompressed BGRA image
    /// and returns the new Surface id. Ids are taken above every id in the DAT and the portal
    /// overlay, so publishing never overwrites retail records.
    /// </summary>
    public uint AddTexture(
        string name,
        byte[] bgra,
        int width,
        int height,
        IEnumerable<uint> existingSurfaceIds,
        IEnumerable<uint> existingSurfaceTextureIds,
        IEnumerable<uint> existingRenderSurfaceIds,
        string? source = null) {
        if (width <= 0 || height <= 0 || bgra.Length != width * height * 4)
            throw new ArgumentException($"Texture '{name}' must be {width}x{height} BGRA ({width * height * 4} bytes), got {bgra.Length}.");
        uint surfaceId = Services.CustomTextureStore.AllocateSurfaceGid(existingSurfaceIds.Concat(Surfaces.Keys));
        uint textureId = ObjSingleMeshImporter.AllocateNextId(0x05000000, existingSurfaceTextureIds.Concat(SurfaceTextures.Keys));
        uint renderId = ObjSingleMeshImporter.AllocateNextId(0x06000000, existingRenderSurfaceIds.Concat(RenderSurfaces.Keys));

        RenderSurfaces[renderId] = new RenderSurface {
            Id = renderId,
            Width = width,
            Height = height,
            Format = (uint)PixelFormat.PFID_A8R8G8B8,
            SourceData = bgra,
        };
        var texture = new SurfaceTexture { Id = textureId, Type = TextureType.Texture2D };
        texture.Textures.Add(renderId);
        SurfaceTextures[textureId] = texture;
        Surfaces[surfaceId] = new Surface {
            Id = surfaceId,
            Type = SurfaceType.Base1Image,
            OrigTextureId = textureId,
            OrigPaletteId = 0,
            Translucency = 0f,
            Luminosity = 0f,
            Diffuse = 1f,
        };
        Project.Textures.Add(new MonsterTextureModel {
            Name = name,
            Surface = Hex(surfaceId),
            SurfaceTexture = Hex(textureId),
            RenderSurface = Hex(renderId),
            Width = width,
            Height = height,
            Source = source,
        });
        return surfaceId;
    }

    /// <summary>Creates a flat-colour Surface (ARGB) for MTL materials that only have a <c>Kd</c>.</summary>
    public uint AddSolidSurface(string name, uint argb, IEnumerable<uint> existingSurfaceIds) {
        uint surfaceId = Services.CustomTextureStore.AllocateSurfaceGid(existingSurfaceIds.Concat(Surfaces.Keys));
        float alpha = ((argb >> 24) & 0xFF) / 255f;
        Surfaces[surfaceId] = new Surface {
            Id = surfaceId,
            Type = SurfaceType.Base1Solid,
            ColorValue = argb | 0xFF000000u,
            Translucency = 1f - alpha,
            Luminosity = 0f,
            Diffuse = 1f,
        };
        Project.Textures.Add(new MonsterTextureModel { Name = name, Surface = Hex(surfaceId), SolidColor = argb });
        return surfaceId;
    }

    public string? TextureName(uint surfaceId) =>
        Project.Textures.FirstOrDefault(texture => MonsterIds.TryParse(texture.Surface, out uint id) && id == surfaceId)?.Name;

    /// <summary>Record lookup for previews: a session texture record by type and id, or null.</summary>
    public IDatRecord? TryGetTextureRecord(Type type, uint id) {
        if (type == typeof(Surface))
            return Surfaces.TryGetValue(id, out var surface) ? surface : null;
        if (type == typeof(SurfaceTexture))
            return SurfaceTextures.TryGetValue(id, out var texture) ? texture : null;
        if (type == typeof(RenderSurface))
            return RenderSurfaces.TryGetValue(id, out var render) ? render : null;
        return null;
    }

    /// <summary>Paints every part, or only <paramref name="partIndices"/>, with one surface. One undo step.</summary>
    public void PaintParts(uint surfaceId, IEnumerable<int>? partIndices) {
        if (WorkingSetup == null)
            throw new InvalidOperationException("Load a Setup first.");
        var targets = (partIndices ?? Enumerable.Range(0, WorkingSetup.Parts.Count)).Distinct().ToList();
        RememberUndo();
        var painted = new HashSet<uint>();
        foreach (int index in targets) {
            var gfx = GfxForPart(index);
            if (gfx == null || !painted.Add(gfx.Id))
                continue;
            // Replace the whole surface list rather than appending, so the record stays tidy.
            gfx.Surfaces = [surfaceId];
            foreach (var poly in gfx.Polygons.Values)
                poly.PosSurface = 0;
            GfxObjMesh.Commit(gfx, topologyChanged: false);
        }
        _redo.Clear();
    }

    public void MirrorToPartner(int partIndex) {
        var part = Part(partIndex) ?? throw new InvalidOperationException("Select a part.");
        if (part.MirrorPartner is not int partner || partner == partIndex)
            throw new InvalidOperationException("Set a mirror partner on this part first.");
        var source = RequirePart(partIndex);
        var destination = RequirePart(partner);
        if (WorkingSetup == null)
            throw new InvalidOperationException("Load a Setup first.");
        RememberUndo();
        GfxObjMesh.CopyMirroredFrom(
            source,
            SetupAssembly.LocalToSetup(WorkingSetup, partIndex),
            destination,
            SetupAssembly.LocalToSetup(WorkingSetup, partner));
        _redo.Clear();
    }

    public MonsterValidationReport Validate(Func<uint, bool>? surfaceExists, IReadOnlySet<uint>? existingDatIds) {
        if (WorkingSetup == null)
            throw new InvalidOperationException("Load a Setup first.");
        var protectedIds = new HashSet<uint> { SourceSetupId };
        foreach (var part in Project.Parts) {
            if (MonsterIds.TryParse(part.SourceGfxObj, out uint source))
                protectedIds.Add(source);
        }
        return MonsterValidation.Check(
            WorkingSetup,
            GfxById,
            surfaceExists,
            Project.Cloned ? protectedIds : protectedIds.Concat(WorkingSetup.Parts).Append(WorkingSetup.Id).ToHashSet(),
            existingDatIds);
    }

    public string PublishSummary(MonsterValidationReport report) {
        if (WorkingSetup == null)
            return "Load a Setup first.";
        var lines = new List<string> {
            Project.Cloned
                ? "Publish adds these records to the project portal overlay. Export DATs to write client_portal.dat."
                : "Clone the monster first. Publish will not overwrite the source Setup.",
            "",
            $"Source Setup: {Project.SourceSetup}",
            $"New Setup: {Hex(WorkingSetup.Id)}",
            "New GfxObjs:",
        };
        foreach (var part in Project.Parts.OrderBy(item => item.Index))
            lines.Add($"  Part {part.Index}: {part.WorkingGfxObj}");
        if (report.Fatals.Any()) {
            lines.Add("");
            lines.Add("Fatal:");
            lines.AddRange(report.Fatals.Select(issue => "  " + issue.Message));
        }
        if (report.Warnings.Any()) {
            lines.Add("");
            lines.Add("Warnings:");
            lines.AddRange(report.Warnings.Select(issue => "  " + issue.Message));
        }
        return string.Join("\n", lines);
    }

    public void Publish(PortalDatDocument portal) {
        if (WorkingSetup == null)
            throw new InvalidOperationException("Load a Setup first.");
        if (!Project.Cloned)
            throw new InvalidOperationException("Clone the monster before publishing. The source Setup is left unchanged.");
        var report = Validate(surfaceExists: null, existingDatIds: null);
        if (report.HasFatal)
            throw new InvalidOperationException(report.Fatals.First().Message);
        foreach (var gfx in GfxById.Values)
            GfxObjMesh.Commit(gfx, topologyChanged: false);
        portal.SetEntry(WorkingSetup.Id, WorkingSetup);
        foreach (var gfx in GfxById.Values)
            portal.SetEntry(gfx.Id, gfx);
        // Only textures still referenced by a part are written.
        var used = GfxById.Values.SelectMany(gfx => gfx.Surfaces).ToHashSet();
        foreach (var (id, surface) in Surfaces) {
            if (!used.Contains(id))
                continue;
            portal.SetEntry(id, surface);
            if (SurfaceTextures.TryGetValue(surface.OrigTextureId, out var texture)) {
                portal.SetEntry(texture.Id, texture);
                foreach (uint renderId in texture.Textures) {
                    if (RenderSurfaces.TryGetValue(renderId, out var render))
                        portal.SetEntry(render.Id, render);
                }
            }
        }
    }

    public string ToJson() {
        if (WorkingSetup == null)
            throw new InvalidOperationException("Load a Setup first.");
        foreach (var gfx in GfxById.Values)
            GfxObjMesh.Commit(gfx, topologyChanged: false);
        Project.SetupRecord = Pack(WorkingSetup);
        Project.GfxRecords = GfxById.ToDictionary(kv => Hex(kv.Key), kv => Pack(kv.Value));
        Project.TextureRecords = Surfaces.ToDictionary(kv => Hex(kv.Key), kv => Pack(kv.Value))
            .Concat(SurfaceTextures.ToDictionary(kv => Hex(kv.Key), kv => Pack(kv.Value)))
            .Concat(RenderSurfaces.ToDictionary(kv => Hex(kv.Key), kv => Pack(kv.Value)))
            .ToDictionary(kv => kv.Key, kv => kv.Value);
        Project.WorkingSetup = Hex(WorkingSetup.Id);
        return JsonSerializer.Serialize(Project, JsonOptions);
    }

    public static MonsterSession FromJson(string json) {
        var model = JsonSerializer.Deserialize<MonsterProjectModel>(json, JsonOptions)
            ?? throw new InvalidOperationException("Monster project JSON is empty.");
        if (string.IsNullOrWhiteSpace(model.SetupRecord))
            throw new InvalidOperationException("Monster project is missing the working Setup record.");
        var session = new MonsterSession {
            Project = model,
            WorkingSetup = Unpack<Setup>(model.SetupRecord),
            SourceSetupId = MonsterIds.TryParse(model.SourceSetup, out uint source) ? source : 0,
        };
        foreach (var (idText, record) in model.GfxRecords) {
            if (!MonsterIds.TryParse(idText, out uint id))
                continue;
            var gfx = Unpack<GfxObj>(record);
            gfx.Id = id;
            session.GfxById[id] = gfx;
        }
        foreach (var (idText, record) in model.TextureRecords) {
            if (!MonsterIds.TryParse(idText, out uint id))
                continue;
            switch (id & 0xFF000000) {
                case 0x08000000: {
                    var surface = Unpack<Surface>(record);
                    surface.Id = id;
                    session.Surfaces[id] = surface;
                    break;
                }
                case 0x05000000: {
                    var texture = Unpack<SurfaceTexture>(record);
                    texture.Id = id;
                    session.SurfaceTextures[id] = texture;
                    break;
                }
                case 0x06000000: {
                    var render = Unpack<RenderSurface>(record);
                    render.Id = id;
                    session.RenderSurfaces[id] = render;
                    break;
                }
            }
        }
        return session;
    }

    public void Batch(Action action) {
        RememberUndo();
        _undoSuspend++;
        try { action(); }
        finally {
            _undoSuspend--;
            _redo.Clear();
        }
    }

    public void Undo() {
        if (_undo.Count == 0)
            return;
        _redo.Add(Capture());
        Restore(_undo[^1]);
        _undo.RemoveAt(_undo.Count - 1);
    }

    public void Redo() {
        if (_redo.Count == 0)
            return;
        _undo.Add(Capture());
        Restore(_redo[^1]);
        _redo.RemoveAt(_redo.Count - 1);
    }

    public string ExportSetupObj() {
        if (WorkingSetup == null)
            throw new InvalidOperationException("Load a Setup first.");
        using var writer = new StringWriter();
        WavefrontMeshExport.WriteSetupParts(WorkingSetup, id => GfxById.TryGetValue(id, out var gfx) ? gfx : null, writer);
        return writer.ToString();
    }

    public string ExportPartObj(int partIndex) {
        var gfx = RequirePart(partIndex);
        using var writer = new StringWriter();
        WavefrontMeshExport.WriteGfxObj(gfx, gfx.Id, writer);
        return writer.ToString();
    }

    void SetDefaultScale(int partIndex, Vector3 scale) {
        if (WorkingSetup == null)
            return;
        if ((WorkingSetup.Flags & SetupAssembly.HasDefaultScale) == 0 || WorkingSetup.DefaultScale.Count != WorkingSetup.Parts.Count) {
            WorkingSetup.Flags |= SetupAssembly.HasDefaultScale;
            WorkingSetup.DefaultScale = Enumerable.Range(0, WorkingSetup.Parts.Count)
                .Select(_ => new[] { 1f, 1f, 1f })
                .ToList();
        }
        WorkingSetup.DefaultScale[partIndex] = [scale.X, scale.Y, scale.Z];
    }

    void RememberUndo() {
        if (_undoSuspend > 0)
            return;
        _undo.Add(Capture());
        if (_undo.Count > UndoLimit)
            _undo.RemoveAt(0);
    }

    byte[] Capture() => MessagePackSerializer.Serialize(new SessionSnapshot {
        ProjectJson = JsonSerializer.Serialize(Project, JsonOptions),
        SetupRecord = WorkingSetup == null ? "" : Pack(WorkingSetup),
        GfxRecords = GfxById.ToDictionary(kv => Hex(kv.Key), kv => Pack(kv.Value)),
    });

    void Restore(byte[] bytes) {
        var snapshot = MessagePackSerializer.Deserialize<SessionSnapshot>(bytes);
        Project = JsonSerializer.Deserialize<MonsterProjectModel>(snapshot.ProjectJson, JsonOptions) ?? new MonsterProjectModel();
        WorkingSetup = string.IsNullOrEmpty(snapshot.SetupRecord) ? null : Unpack<Setup>(snapshot.SetupRecord);
        GfxById.Clear();
        foreach (var (idText, record) in snapshot.GfxRecords) {
            if (!MonsterIds.TryParse(idText, out uint id))
                continue;
            var gfx = Unpack<GfxObj>(record);
            gfx.Id = id;
            GfxById[id] = gfx;
        }
        SourceSetupId = MonsterIds.TryParse(Project.SourceSetup, out uint source) ? source : SourceSetupId;
    }

    MonsterPartModel? Part(int index) => Project.Parts.FirstOrDefault(part => part.Index == index);

    GfxObj RequirePart(int partIndex) =>
        GfxForPart(partIndex) ?? throw new InvalidOperationException($"Part {partIndex} has no GfxObj.");

    static string Pack<T>(T record) where T : class, IDatRecord {
        record.BeforeSerialize();
        return Convert.ToBase64String(MessagePackSerializer.Serialize(record));
    }

    static T Unpack<T>(string base64) where T : class, IDatRecord, new() {
        var record = MessagePackSerializer.Deserialize<T>(Convert.FromBase64String(base64));
        record.AfterDeserialize();
        return record;
    }

    static T Copy<T>(T record) where T : class, IDatRecord => DatNativeRecords.CloneMessagePack(record);

    static GfxObj CopyGfx(GfxObj source) {
        if (source.RenderVertices.Count == 0 && source.VertexArray.Vertices.Count > 0)
            GfxObjMesh.Commit(source, topologyChanged: false);
        return Copy(source);
    }

    static string Hex(uint id) => "0x" + id.ToString("X8", CultureInfo.InvariantCulture);
}

public sealed class ObjPartReplaceOptions {
    /// <summary>The OBJ came from Export Assembled OBJ and is already posed in setup space.</summary>
    public bool ObjIsAssembled { get; init; }
    public bool Deduplicate { get; init; }
    public ObjUpAxis UpAxis { get; init; } = ObjUpAxis.ZUp;
    /// <summary>Scale and centre the OBJ onto the part's current bounding box.</summary>
    public bool FitToPart { get; init; }
    public float YawDegrees { get; init; }
    /// <summary>Flip texture V for OBJs whose UVs have V = 0 at the bottom (Blender, Maya).</summary>
    public bool FlipV { get; init; }
    /// <summary>OBJ material name → Asheron's Call Surface id.</summary>
    public IReadOnlyDictionary<string, uint>? MaterialSurfaces { get; init; }
    /// <summary>Surface for faces with no mapped material and no <c>surface_0x…</c> name.</summary>
    public uint? DefaultSurface { get; init; }
}

[MessagePackObject]
public sealed class SessionSnapshot {
    [Key(0)] public string ProjectJson { get; set; } = "";
    [Key(1)] public string SetupRecord { get; set; } = "";
    [Key(2)] public Dictionary<string, string> GfxRecords { get; set; } = new();
}

public static class MonsterIds {
    public static bool TryParse(string? text, out uint id) {
        id = 0;
        if (string.IsNullOrWhiteSpace(text))
            return false;
        var trimmed = text.Trim();
        var styles = NumberStyles.Integer;
        if (trimmed.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) {
            styles = NumberStyles.HexNumber;
            trimmed = trimmed[2..];
        }
        return uint.TryParse(trimmed, styles, CultureInfo.InvariantCulture, out id);
    }
}
