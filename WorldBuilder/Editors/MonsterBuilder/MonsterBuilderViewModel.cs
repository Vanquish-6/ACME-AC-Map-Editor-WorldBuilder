using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform.Storage;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using Acme.Dat;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using WorldBuilder.Lib;
using WorldBuilder.Shared.Documents;
using WorldBuilder.Shared.Lib;
using WorldBuilder.Shared.Lib.MonsterBuilder;
using WorldBuilder.Shared.Models;
using WorldBuilder.ViewModels;

namespace WorldBuilder.Editors.MonsterBuilder;

public sealed class SurfaceRow {
    public uint Id { get; init; }
    public string Label { get; init; } = "";
    public string Detail { get; init; } = "";
}

public sealed class DonorPartRow {
    public int Index { get; init; }
    public uint GfxId { get; init; }
    public string Title { get; init; } = "";
    public string GfxLabel { get; init; } = "";
}

public partial class MonsterPartRow : ObservableObject {
    public int Index { get; init; }
    public int Depth { get; init; }
    [ObservableProperty] private string _title = "";
    public string GfxLabel { get; init; } = "";
    public string ParentLabel { get; init; } = "";
    [ObservableProperty] private string _roleLabel = "";
    public Action<MonsterPartRow>? ToggleRequested { get; set; }
    [ObservableProperty] private bool _hidden;

    [RelayCommand]
    private void Toggle() => ToggleRequested?.Invoke(this);
}

/// <summary>
/// Edits a cloned Setup by changing GfxObj vertices in local space.
/// Animation playback is not included: ACME does not evaluate MotionTables.
/// </summary>
public partial class MonsterBuilderViewModel : ViewModelBase {
    Project? _project;
    IDatReaderWriter? _dats;
    PortalDatDocument? _portal;
    MonsterSession? _session;
    bool _loadingFields;
    uint[]? _setupIds;
    readonly Dictionary<uint, GfxObj> _donorGfx = [];

    public MonsterBuilderViewModel() { }

    public IReadOnlyList<string> Roles { get; } = MonsterRoles.All;
    public IReadOnlyList<string> PrimitiveKinds { get; } = Enum.GetNames<PrimitiveKind>();
    public IReadOnlyList<string> ObjUpAxisNames { get; } = [UpAxisAuto, UpAxisY, UpAxisZ];
    const string UpAxisAuto = "Auto (ACME exports are Z up, others Y up)";
    const string UpAxisY = "Y up (Blender, Maya, downloads)";
    const string UpAxisZ = "Z up (ACME export, AC native)";

    [ObservableProperty] private string _setupIdText = "0x02000034";
    [ObservableProperty] private string _setupFilter = "";
    [ObservableProperty] private ObservableCollection<string> _setupMatches = [];
    [ObservableProperty] private string? _selectedSetupMatch;
    [ObservableProperty] private string _donorSetupText = "";
    [ObservableProperty] private ObservableCollection<DonorPartRow> _donorParts = [];
    [ObservableProperty] private DonorPartRow? _selectedDonorPart;
    [ObservableProperty] private ObservableCollection<SurfaceRow> _partSurfaces = [];
    [ObservableProperty] private ObservableCollection<SurfaceRow> _creatureSurfaces = [];
    [ObservableProperty] private SurfaceRow? _selectedPartSurface;
    [ObservableProperty] private SurfaceRow? _selectedPaintSurface;
    [ObservableProperty] private string _statusText = "Enter a Setup ID and load it. Clone before you edit so the source monster stays unchanged.";
    [ObservableProperty] private string _monsterTitle = "No Setup loaded";
    [ObservableProperty] private ObservableCollection<MonsterPartRow> _parts = [];
    [ObservableProperty] private MonsterPartRow? _selectedPart;
    [ObservableProperty] private int _previewRevision;
    [ObservableProperty] private int _selectedPartIndex = -1;
    [ObservableProperty] private bool _showSolid = true;
    [ObservableProperty] private bool _showWireframe;
    [ObservableProperty] private bool _showNormals;
    [ObservableProperty] private bool _showBounds;
    [ObservableProperty] private bool _showOrigins = true;
    [ObservableProperty] private bool _showHierarchy = true;
    [ObservableProperty] private int _cameraRequest;
    [ObservableProperty] private string _cameraPreset = "";
    [ObservableProperty] private bool _transformGeometry = true;

    [ObservableProperty] private string _partSummary = "Select a part.";
    [ObservableProperty] private string _partLabel = "";
    [ObservableProperty] private string _selectedRole = "";
    [ObservableProperty] private string _mirrorPartnerText = "";
    [ObservableProperty] private string _geometryPosition = "0, 0, 0";
    [ObservableProperty] private string _geometryRotation = "0, 0, 0";
    [ObservableProperty] private string _geometryScale = "1, 1, 1";
    [ObservableProperty] private string _uniformScale = "1";
    [ObservableProperty] private string _attachmentPosition = "0, 0, 0";
    [ObservableProperty] private string _attachmentRotation = "0, 0, 0";
    [ObservableProperty] private string _attachmentScale = "1, 1, 1";
    [ObservableProperty] private string _surfaceText = "";
    [ObservableProperty] private string _replacementSurface = "";
    [ObservableProperty] private string _faceList = "";
    [ObservableProperty] private string _deformFactor = "1.2";
    [ObservableProperty] private string _primitiveKindName = "Horn";
    [ObservableProperty] private string _primitiveSegments = "6";
    [ObservableProperty] private string _primitiveRadius = "0.06";
    [ObservableProperty] private string _primitiveLength = "0.25";
    [ObservableProperty] private string _primitivePosition = "0, 0, 0";
    [ObservableProperty] private string _primitiveRotation = "0, 0, 0";
    [ObservableProperty] private string _primitiveScale = "1, 1, 1";
    [ObservableProperty] private bool _objIsAssembled;
    [ObservableProperty] private bool _deduplicateObj = true;
    [ObservableProperty] private string _objUpAxisName = UpAxisAuto;
    [ObservableProperty] private bool _objFitToCreature = true;
    [ObservableProperty] private bool _objFitToPart;
    [ObservableProperty] private string _objYawDegrees = "0";
    [ObservableProperty] private bool _objClearUnmatched = true;
    [ObservableProperty] private bool _objUseGroupNames = true;
    [ObservableProperty] private bool _objUseMaterials = true;
    [ObservableProperty] private bool _objFlipV = true;
    [ObservableProperty] private string _objImportSummary = "";
    [ObservableProperty] private string _overallHeight = "1";
    [ObservableProperty] private string _overallWidth = "1";
    [ObservableProperty] private string _headScale = "1";
    [ObservableProperty] private string _torsoWidth = "1";
    [ObservableProperty] private string _torsoHeight = "1";
    [ObservableProperty] private string _armLength = "1";
    [ObservableProperty] private string _armThickness = "1";
    [ObservableProperty] private string _handSize = "1";
    [ObservableProperty] private string _legLength = "1";
    [ObservableProperty] private string _legThickness = "1";
    [ObservableProperty] private string _footSize = "1";
    [ObservableProperty] private bool _hasHead;
    [ObservableProperty] private bool _hasTorso;
    [ObservableProperty] private bool _hasArms;
    [ObservableProperty] private bool _hasHands;
    [ObservableProperty] private bool _hasLegs;
    [ObservableProperty] private bool _hasFeet;
    [ObservableProperty] private string _publishSummary = "";
    [ObservableProperty] private bool _publishReady;
    [ObservableProperty] private bool _canUndo;
    [ObservableProperty] private bool _canRedo;

    public MonsterSession? Session => _session;

    internal void Init(Project project) {
        _project = project;
        _dats = project.DatReaderWriter;
        if (_dats.CanWrite) {
            _portal = project.DocumentManager
                .GetOrCreateDocumentAsync<PortalDatDocument>(PortalDatDocument.DocumentId)
                .GetAwaiter().GetResult();
        }
    }

    partial void OnSelectedSetupMatchChanged(string? value) {
        if (!string.IsNullOrWhiteSpace(value))
            SetupIdText = value;
    }

    partial void OnSelectedPartChanged(MonsterPartRow? value) {
        SelectedPartIndex = value?.Index ?? -1;
        LoadSelectedFields();
        PreviewRevision++;
    }

    partial void OnSelectedRoleChanged(string value) {
        if (_loadingFields || _session == null || SelectedPartIndex < 0) return;
        _session.SetRole(SelectedPartIndex, string.IsNullOrWhiteSpace(value) ? null : value);
        RefreshProportionFlags();
        if (SelectedPart != null)
            SelectedPart.RoleLabel = MonsterRoles.DisplayName(string.IsNullOrWhiteSpace(value) ? null : value);
    }

    partial void OnPartLabelChanged(string value) {
        if (_loadingFields || _session == null || SelectedPartIndex < 0) return;
        _session.SetLabel(SelectedPartIndex, value);
        if (SelectedPart != null)
            SelectedPart.Title = string.IsNullOrWhiteSpace(value) ? $"Part {SelectedPartIndex}" : value;
    }

    [RelayCommand]
    private void FindSetups() {
        if (_dats == null) { StatusText = "Open a project first."; return; }
        _setupIds ??= _dats.GetAllIdsOfType<Setup>().OrderBy(id => id).ToArray();
        var filter = SetupFilter.Trim();
        var matches = _setupIds.Where(id => {
            if (filter.Length == 0) return true;
            var hex = "0x" + id.ToString("X8", CultureInfo.InvariantCulture);
            return hex.Contains(filter, StringComparison.OrdinalIgnoreCase)
                || id.ToString(CultureInfo.InvariantCulture).Contains(filter, StringComparison.Ordinal);
        }).Take(200).Select(id => "0x" + id.ToString("X8", CultureInfo.InvariantCulture));
        SetupMatches = new ObservableCollection<string>(matches);
        StatusText = SetupMatches.Count == 0
            ? "No Setup IDs matched that filter."
            : $"Showing {SetupMatches.Count} Setup IDs. Select one, then Load.";
    }

    [RelayCommand]
    private void LoadDonor() {
        if (_dats == null) { StatusText = "Open a project first."; return; }
        if (!MonsterIds.TryParse(DonorSetupText, out uint id)) {
            StatusText = "Enter the donor Setup ID, then Load donor.";
            return;
        }
        if (!_dats.TryGet<Setup>(id, out var setup) || setup == null) {
            StatusText = $"Donor Setup {MonsterHex(id)} was not found.";
            return;
        }
        _donorGfx.Clear();
        var rows = new ObservableCollection<DonorPartRow>();
        for (int i = 0; i < setup.Parts.Count; i++) {
            uint gfxId = setup.Parts[i];
            if (!_donorGfx.ContainsKey(gfxId) && _dats.TryGet<GfxObj>(gfxId, out var gfx) && gfx != null)
                _donorGfx[gfxId] = gfx;
            rows.Add(new DonorPartRow {
                Index = i,
                GfxId = gfxId,
                Title = $"Part {i}",
                GfxLabel = MonsterHex(gfxId),
            });
        }
        DonorParts = rows;
        SelectedDonorPart = rows.FirstOrDefault();
        StatusText = $"Donor {MonsterHex(id)} has {rows.Count} parts. Select your monster part on the left, select a donor part, then Use donor mesh.";
    }

    [RelayCommand]
    private void UseDonorMesh() {
        if (!EnsureCloned() || !TryPart(out int index) || SelectedDonorPart == null) {
            if (SelectedDonorPart == null)
                StatusText = "Load a donor Setup and select one of its parts.";
            return;
        }
        if (!_donorGfx.TryGetValue(SelectedDonorPart.GfxId, out var donor)) {
            StatusText = "That donor part has no mesh.";
            return;
        }
        if (RunGeometry(index, gfx => GfxObjMesh.CopyFrom(gfx, donor)))
            StatusText = $"Part {index} now uses the mesh from donor part {SelectedDonorPart.Index}. The pose attachment was not moved.";
    }

    [RelayCommand]
    private void PaintPart() {
        if (!EnsureCloned() || !TryPart(out int index) || SelectedPaintSurface == null) {
            if (SelectedPaintSurface == null)
                StatusText = "Select a surface from this monster, then Paint part.";
            return;
        }
        uint surface = SelectedPaintSurface.Id;
        if (RunGeometry(index, gfx => GfxObjMesh.ApplySurface(gfx, surface, null)))
            StatusText = $"Painted part {index} with {MonsterHex(surface)}.";
    }

    [RelayCommand]
    private void ReplacePartSurface() {
        if (!EnsureCloned() || !TryPart(out int index) || _session == null) return;
        if (SelectedPartSurface == null || SelectedPaintSurface == null) {
            StatusText = "Select the surface to replace on this part, and the surface to paint with.";
            return;
        }
        var gfx = _session.GfxForPart(index);
        if (gfx == null) return;
        int slot = gfx.Surfaces.IndexOf(SelectedPartSurface.Id);
        if (slot < 0) {
            StatusText = "That surface is not on the selected part.";
            return;
        }
        var faces = gfx.Polygons.Where(poly => poly.Value.PosSurface == slot).Select(poly => poly.Key).ToList();
        uint replacement = SelectedPaintSurface.Id;
        uint previous = SelectedPartSurface.Id;
        if (RunGeometry(index, target => GfxObjMesh.ApplySurface(target, replacement, faces)))
            StatusText = $"Replaced {MonsterHex(previous)} with {MonsterHex(replacement)} on part {index}.";
    }

    [RelayCommand]
    private void LoadSetup() {
        if (_dats == null) { StatusText = "Open a project first."; return; }
        if (!MonsterIds.TryParse(SetupIdText, out uint id)) { StatusText = "Enter a Setup ID such as 0x02000034."; return; }
        try {
            _session = MonsterSession.Load(_dats, id);
            PublishReady = false;
            PublishSummary = "";
            StatusText = $"Loaded Setup {MonsterHex(id)}. Clone it before editing. The source records are not changed.";
            RebuildParts(0);
            CameraPreset = "reset";
            CameraRequest++;
            PreviewRevision++;
        }
        catch (Exception ex) { StatusText = ex.Message; }
    }

    [RelayCommand]
    private void CloneMonster() {
        if (_session == null || _dats == null) { StatusText = "Load a Setup first."; return; }
        try {
            var gfxIds = _dats.GetAllIdsOfType<GfxObj>().Concat(_portal?.GetEntryIds().Where(id => (id & 0xFF000000) == 0x01000000) ?? []);
            var setupIds = _dats.GetAllIdsOfType<Setup>().Concat(_portal?.GetEntryIds().Where(id => (id & 0xFF000000) == 0x02000000) ?? []);
            _session.CloneNewIds(gfxIds, setupIds);
            StatusText = $"Cloned to Setup {_session.Project.WorkingSetup}. Edits stay on the clone.";
            RebuildParts(SelectedPartIndex);
            PreviewRevision++;
            RefreshHistory();
        }
        catch (Exception ex) { StatusText = ex.Message; }
    }

    [RelayCommand]
    private void ApplyGeometry() {
        if (!TryPart(out var index)) return;
        if (!TryVec3(GeometryPosition, out var position) || !TryVec3(GeometryRotation, out var rotation)
            || !TryVec3(GeometryScale, out var scale) || !TryFloat(UniformScale, out float uniform)) {
            StatusText = "Geometry values must be numbers. Use X, Y, Z and a uniform scale.";
            return;
        }
        var radians = rotation * (MathF.PI / 180f);
        var quat = Quaternion.CreateFromAxisAngle(Vector3.UnitX, radians.X)
            * Quaternion.CreateFromAxisAngle(Vector3.UnitY, radians.Y)
            * Quaternion.CreateFromAxisAngle(Vector3.UnitZ, radians.Z);
        RunGeometry(index, gfx => {
            if (position != Vector3.Zero) GfxObjMesh.Translate(gfx, position);
            if (rotation != Vector3.Zero) GfxObjMesh.Rotate(gfx, quat);
            var combined = scale * uniform;
            if (combined != Vector3.One) GfxObjMesh.ScaleAboutOrigin(gfx, combined);
        });
        GeometryPosition = "0, 0, 0";
        GeometryRotation = "0, 0, 0";
        GeometryScale = "1, 1, 1";
        UniformScale = "1";
        StatusText = "Scaled, moved, or rotated the part in GfxObj local space. The Setup attachment was not moved.";
    }

    [RelayCommand]
    private void ApplyAttachment() {
        if (_session?.WorkingSetup == null || !TryPart(out int index)) return;
        if (!TryVec3(AttachmentPosition, out var position) || !TryVec3(AttachmentRotation, out var rotation)
            || !TryVec3(AttachmentScale, out var scale)) {
            StatusText = "Attachment values must be numbers.";
            return;
        }
        var radians = rotation * (MathF.PI / 180f);
        var quat = Quaternion.CreateFromAxisAngle(Vector3.UnitX, radians.X)
            * Quaternion.CreateFromAxisAngle(Vector3.UnitY, radians.Y)
            * Quaternion.CreateFromAxisAngle(Vector3.UnitZ, radians.Z);
        try {
            _session.EditAttachment(index, position, quat, scale);
            StatusText = "Updated the resting placement frame and Setup default scale. Geometry vertices were not baked into that pose.";
            AfterEdit();
        }
        catch (Exception ex) { StatusText = ex.Message; }
    }

    [RelayCommand] private void MirrorX() => Deform(gfx => GfxObjMesh.MirrorLocal(gfx, 0), "Mirrored the part on local X.");
    [RelayCommand] private void MirrorY() => Deform(gfx => GfxObjMesh.MirrorLocal(gfx, 1), "Mirrored the part on local Y.");
    [RelayCommand] private void MirrorZ() => Deform(gfx => GfxObjMesh.MirrorLocal(gfx, 2), "Mirrored the part on local Z.");
    [RelayCommand] private void Widen() => Deform(gfx => GfxObjMesh.ScaleAboutOrigin(gfx, new Vector3(Factor(), 1, 1)), "Widened local X.");
    [RelayCommand] private void Narrow() => Deform(gfx => GfxObjMesh.ScaleAboutOrigin(gfx, new Vector3(1f / Factor(), 1, 1)), "Narrowed local X.");
    [RelayCommand] private void Lengthen() => Deform(gfx => GfxObjMesh.ScaleAboutOrigin(gfx, new Vector3(1, 1, Factor())), "Lengthened local Z.");
    [RelayCommand] private void Shorten() => Deform(gfx => GfxObjMesh.ScaleAboutOrigin(gfx, new Vector3(1, 1, 1f / Factor())), "Shortened local Z.");
    [RelayCommand] private void Taper() => Deform(gfx => GfxObjMesh.Taper(gfx, Vector3.UnitZ, Factor()), "Tapered along local Z.");
    [RelayCommand] private void Bulge() => Deform(gfx => GfxObjMesh.Bulge(gfx, Vector3.UnitZ, Factor() - 1f), "Bulged along local Z.");
    [RelayCommand] private void Flatten() => Deform(gfx => GfxObjMesh.ScaleAboutOrigin(gfx, new Vector3(1, Factor() < 1f ? Factor() : 1f / Factor(), 1)), "Flattened local Y.");

    [RelayCommand]
    private void Deduplicate() => Deform(GfxObjMesh.Deduplicate, "Welded matching vertices.");

    [RelayCommand]
    private void ApplySurfaceToPart() {
        if (!TryPart(out int index) || !MonsterIds.TryParse(ReplacementSurface, out uint surface)) {
            StatusText = "Enter a surface ID such as 0x08000001.";
            return;
        }
        RunGeometry(index, gfx => GfxObjMesh.ApplySurface(gfx, surface, null));
        StatusText = $"Applied surface {MonsterHex(surface)} to the whole part.";
    }

    [RelayCommand]
    private void ApplySurfaceToFaces() {
        if (!TryPart(out int index) || _session == null || !MonsterIds.TryParse(ReplacementSurface, out uint surface)) {
            StatusText = "Enter a surface ID.";
            return;
        }
        var faces = ParseFaceList(FaceList);
        if (faces.Count == 0 && MonsterIds.TryParse(SurfaceText, out uint current)) {
            var gfx = _session.GfxForPart(index);
            if (gfx != null) {
                int slot = gfx.Surfaces.IndexOf(current);
                faces = gfx.Polygons.Where(poly => poly.Value.PosSurface == slot).Select(poly => poly.Key).ToList();
            }
        }
        if (faces.Count == 0) { StatusText = "Enter face indices, or select a surface that is already on some faces."; return; }
        RunGeometry(index, gfx => GfxObjMesh.ApplySurface(gfx, surface, faces));
        StatusText = $"Applied surface {MonsterHex(surface)} to {faces.Count} faces.";
    }

    [RelayCommand]
    private void AddPrimitive() {
        if (!TryPart(out int index)) return;
        if (!Enum.TryParse<PrimitiveKind>(PrimitiveKindName, out var kind)) kind = PrimitiveKind.Horn;
        if (!TryVec3(PrimitivePosition, out var position) || !TryVec3(PrimitiveRotation, out var rotation)
            || !TryVec3(PrimitiveScale, out var scale)) {
            StatusText = "Primitive position, rotation, and scale must be numeric.";
            return;
        }
        var radians = rotation * (MathF.PI / 180f);
        var args = new PrimitiveArgs {
            Segments = (int)ParseOr(PrimitiveSegments, 6),
            Radius = ParseOr(PrimitiveRadius, 0.06f),
            Length = ParseOr(PrimitiveLength, 0.25f),
            Position = position,
            Rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitX, radians.X)
                * Quaternion.CreateFromAxisAngle(Vector3.UnitY, radians.Y)
                * Quaternion.CreateFromAxisAngle(Vector3.UnitZ, radians.Z),
            Scale = scale,
            SurfaceDid = MonsterIds.TryParse(ReplacementSurface, out uint surface) ? surface : 0,
            Width = ParseOr(PrimitiveRadius, 0.1f) * 2f,
            Depth = ParseOr(PrimitiveRadius, 0.1f) * 2f,
            Height = ParseOr(PrimitiveLength, 0.1f),
        };
        RunGeometry(index, gfx => GfxObjMesh.AddPrimitive(gfx, kind, args));
        StatusText = $"Added a {kind} in the part's local space.";
    }

    [RelayCommand]
    private void MirrorToPartner() {
        if (_session == null || !TryPart(out int index)) return;
        if (!int.TryParse(MirrorPartnerText, NumberStyles.Integer, CultureInfo.InvariantCulture, out int partner)) {
            StatusText = "Enter the partner part index.";
            return;
        }
        try {
            _session.SetMirrorPartner(index, partner);
            _session.MirrorToPartner(index);
            StatusText = $"Copied part {index} onto part {partner}, mirrored through setup X = 0 and converted into the partner's local space.";
            AfterEdit();
        }
        catch (Exception ex) { StatusText = ex.Message; }
    }

    [RelayCommand]
    private void ApplyProportions() {
        if (_session?.WorkingSetup == null) { StatusText = "Load a Setup first."; return; }
        if (!Differs(OverallHeight, out _) && !Differs(OverallWidth, out _) && !Differs(HeadScale, out _)
            && !Differs(TorsoWidth, out _) && !Differs(TorsoHeight, out _) && !Differs(ArmLength, out _)
            && !Differs(ArmThickness, out _) && !Differs(HandSize, out _) && !Differs(LegLength, out _)
            && !Differs(LegThickness, out _) && !Differs(FootSize, out _)) {
            StatusText = "Set a proportion other than 1. Assign roles before the named controls do anything.";
            return;
        }
        int edits = 0;
        _session.Batch(() => edits = ApplyProportionEdits());
        if (edits == 0) { StatusText = "Set a proportion other than 1. Assign roles before the named controls do anything."; return; }
        StatusText = "Applied proportions in each part's local space.";
        AfterEdit();
    }

    int ApplyProportionEdits() {
        int edits = 0;
        void Uniform(string role, string text) {
            if (!Differs(text, out float factor)) return;
            _session.ApplyProportion(role, null, null, null, factor);
            edits++;
        }
        void Along(string role, Vector3? axis, string text) {
            if (!Differs(text, out float factor)) return;
            _session.ApplyProportion(role, axis, factor, null, null);
            edits++;
        }
        void Thick(string role, Vector3? axis, string text) {
            if (!Differs(text, out float factor)) return;
            _session.ApplyProportion(role, axis, null, factor, null);
            edits++;
        }
        if (Differs(OverallHeight, out float height)) { _session.ApplyOverall(Vector3.UnitZ, height); edits++; }
        if (Differs(OverallWidth, out float width)) { _session.ApplyOverall(Vector3.UnitX, width); edits++; }
        Uniform(MonsterRoles.Head, HeadScale);
        Thick(MonsterRoles.Torso, null, TorsoWidth);
        Along(MonsterRoles.Torso, null, TorsoHeight);
        Along(MonsterRoles.UpperArmL, null, ArmLength);
        Along(MonsterRoles.UpperArmR, null, ArmLength);
        Along(MonsterRoles.ForearmL, null, ArmLength);
        Along(MonsterRoles.ForearmR, null, ArmLength);
        Thick(MonsterRoles.UpperArmL, null, ArmThickness);
        Thick(MonsterRoles.UpperArmR, null, ArmThickness);
        Thick(MonsterRoles.ForearmL, null, ArmThickness);
        Thick(MonsterRoles.ForearmR, null, ArmThickness);
        Uniform(MonsterRoles.HandL, HandSize);
        Uniform(MonsterRoles.HandR, HandSize);
        Along(MonsterRoles.ThighL, null, LegLength);
        Along(MonsterRoles.ThighR, null, LegLength);
        Along(MonsterRoles.ShinL, null, LegLength);
        Along(MonsterRoles.ShinR, null, LegLength);
        Thick(MonsterRoles.ThighL, null, LegThickness);
        Thick(MonsterRoles.ThighR, null, LegThickness);
        Thick(MonsterRoles.ShinL, null, LegThickness);
        Thick(MonsterRoles.ShinR, null, LegThickness);
        Uniform(MonsterRoles.FootL, FootSize);
        Uniform(MonsterRoles.FootR, FootSize);
        return edits;
    }

    [RelayCommand]
    private async Task ReplaceFromObj() {
        if (!TryPart(out int index) || _session == null) return;
        var file = await PickOpen("Replace part from OBJ", "*.obj");
        if (file == null) return;
        if (!EnsureCloned()) return;
        try {
            var text = await ReadText(file);
            var upAxis = ResolveUpAxis(text);
            var materials = ResolveMaterials(file, text);
            var options = new ObjPartReplaceOptions {
                ObjIsAssembled = ObjIsAssembled,
                Deduplicate = DeduplicateObj,
                UpAxis = upAxis,
                FitToPart = ObjFitToPart && !ObjIsAssembled,
                YawDegrees = ParseOr(ObjYawDegrees, 0f),
                FlipV = ObjFlipV && upAxis == ObjUpAxis.YUp,
                MaterialSurfaces = materials.Map,
                DefaultSurface = materials.Default,
            };
            if (!_session.ReplacePartFromObj(index, text, options, out var error)) {
                StatusText = error ?? "OBJ import failed.";
                return;
            }
            StatusText = (ObjIsAssembled
                ? $"Replaced part {index} and converted the OBJ from setup space back into GfxObj local space."
                : options.FitToPart
                    ? $"Replaced part {index} and fitted the OBJ to the part's old size and centre."
                    : $"Replaced part {index}. Vertices were stored in GfxObj local space around the part's pivot.")
                + materials.Note;
            AfterEdit();
        }
        catch (Exception ex) { StatusText = ex.Message; }
    }

    [RelayCommand]
    private async Task ImportTextureForMonster() => await ImportTexture(allParts: true);

    [RelayCommand]
    private async Task ImportTextureForPart() => await ImportTexture(allParts: false);

    async Task ImportTexture(bool allParts) {
        if (_session?.WorkingSetup == null) { StatusText = "Load a Setup first."; return; }
        if (!allParts && !TryPart(out _)) return;
        var file = await PickOpen(allParts ? "Texture for the whole monster" : "Texture for the selected part", "*.png", "*.jpg", "*.jpeg", "*.bmp", "*.tga");
        if (file == null) return;
        var path = file.TryGetLocalPath();
        if (path == null) { StatusText = "Pick a file on this computer."; return; }
        if (!EnsureCloned()) return;
        try {
            uint surface = AddTextureFromFile(path);
            _session.PaintParts(surface, allParts ? null : [SelectedPartIndex]);
            StatusText = allParts
                ? $"Painted every part with {MonsterHex(surface)} ({Path.GetFileName(path)}). It is written with the monster on publish."
                : $"Painted part {SelectedPartIndex} with {MonsterHex(surface)} ({Path.GetFileName(path)}). Use Paint with to put it on other parts.";
            AfterEdit();
            SelectedPaintSurface = CreatureSurfaces.FirstOrDefault(row => row.Id == surface) ?? SelectedPaintSurface;
        }
        catch (Exception ex) { StatusText = ex.Message; }
    }

    /// <summary>Decodes an image, snaps it to a power-of-two size (max 1024), and registers it as a session Surface.</summary>
    uint AddTextureFromFile(string path) {
        if (_session == null) throw new InvalidOperationException("Load a Setup first.");
        var (bgra, width, height) = LoadBgra(path);
        uint surface = _session.AddTexture(
            Path.GetFileNameWithoutExtension(path),
            bgra, width, height,
            ExistingIdsOfType<Surface>(0x08000000),
            ExistingIdsOfType<SurfaceTexture>(0x05000000),
            ExistingIdsOfType<RenderSurface>(0x06000000),
            source: Path.GetFileName(path));
        RememberSessionTextureIds();
        return surface;
    }

    void RememberSessionTextureIds() {
        if (_session == null) return;
        if (_cachedSurfaceIds != null) {
            foreach (uint id in _session.Surfaces.Keys)
                _cachedSurfaceIds.Add(id);
        }
        if (_cachedSurfaceTextureIds != null) {
            foreach (uint id in _session.SurfaceTextures.Keys)
                _cachedSurfaceTextureIds.Add(id);
        }
        if (_cachedRenderSurfaceIds != null) {
            foreach (uint id in _session.RenderSurfaces.Keys)
                _cachedRenderSurfaceIds.Add(id);
        }
    }

    static (byte[] Bgra, int Width, int Height) LoadBgra(string path) {
        using var image = Image.Load<Rgba32>(path);
        int width = PowerOfTwo(image.Width);
        int height = PowerOfTwo(image.Height);
        if (width != image.Width || height != image.Height)
            image.Mutate(x => x.Resize(width, height));
        var bgra = new byte[width * height * 4];
        image.ProcessPixelRows(accessor => {
            for (int y = 0; y < accessor.Height; y++) {
                var row = accessor.GetRowSpan(y);
                for (int x = 0; x < row.Length; x++) {
                    int i = (y * width + x) * 4;
                    bgra[i] = row[x].B;
                    bgra[i + 1] = row[x].G;
                    bgra[i + 2] = row[x].R;
                    bgra[i + 3] = row[x].A;
                }
            }
        });
        return (bgra, width, height);

        static int PowerOfTwo(int size) {
            int result = 32;
            while (result < size && result < 1024)
                result <<= 1;
            return result;
        }
    }

    sealed record MaterialResolution(Dictionary<string, uint> Map, uint? Default, string Note);

    /// <summary>
    /// Follows <c>mtllib</c> → <c>map_Kd</c> to images next to the OBJ and turns each one into a
    /// session Surface, so <c>usemtl</c> faces come in already textured. Materials with only a
    /// <c>Kd</c> colour become flat-colour surfaces. Without an MTL, an image with the OBJ's own
    /// name in the same folder is used for everything.
    /// </summary>
    MaterialResolution ResolveMaterials(IStorageFile file, string objText) {
        var empty = new MaterialResolution([], null, "");
        if (!ObjUseMaterials || _session == null)
            return empty;
        var objPath = file.TryGetLocalPath();
        if (objPath == null)
            return empty;
        var folder = Path.GetDirectoryName(objPath) ?? "";
        var (libraries, used) = ObjPartImport.ScanMaterials(objText);
        var map = new Dictionary<string, uint>(StringComparer.Ordinal);
        var imageSurfaces = new Dictionary<string, uint>(StringComparer.OrdinalIgnoreCase);
        var notes = new List<string>();
        int textures = 0, solids = 0, missing = 0;

        foreach (var library in libraries) {
            var mtlPath = ResolvePath(folder, library);
            if (mtlPath == null) { notes.Add($"MTL '{library}' not found next to the OBJ."); continue; }
            var mtlFolder = Path.GetDirectoryName(mtlPath) ?? folder;
            foreach (var material in ObjMaterialLibrary.Parse(File.ReadAllText(mtlPath)).Values) {
                if (!used.Contains(material.Name) || map.ContainsKey(material.Name))
                    continue;
                if (material.DiffuseMap != null) {
                    var imagePath = ResolvePath(mtlFolder, material.DiffuseMap) ?? ResolvePath(folder, Path.GetFileName(material.DiffuseMap));
                    if (imagePath == null) { missing++; notes.Add($"Image '{material.DiffuseMap}' for material '{material.Name}' not found."); }
                    else {
                        if (!imageSurfaces.TryGetValue(imagePath, out uint surface)) {
                            surface = AddTextureFromFile(imagePath);
                            imageSurfaces[imagePath] = surface;
                            textures++;
                        }
                        map[material.Name] = surface;
                        continue;
                    }
                }
                if (material.DiffuseColor is uint colour) {
                    map[material.Name] = _session.AddSolidSurface(material.Name, colour, ExistingIdsOfType<Surface>(0x08000000));
                    RememberSessionTextureIds();
                    solids++;
                }
            }
        }

        uint? fallback = null;
        if (map.Count == 0) {
            // No usable MTL: look for monster.png beside monster.obj.
            var stem = Path.GetFileNameWithoutExtension(objPath);
            foreach (var ext in new[] { ".png", ".jpg", ".jpeg", ".bmp", ".tga" }) {
                var candidate = Path.Combine(folder, stem + ext);
                if (File.Exists(candidate)) {
                    fallback = AddTextureFromFile(candidate);
                    textures++;
                    notes.Add($"Used {Path.GetFileName(candidate)} for the whole model.");
                    break;
                }
            }
        }
        else if (imageSurfaces.Count == 1 && missing == 0) {
            // One atlas for the model: faces without a usemtl get it too.
            fallback = imageSurfaces.Values.First();
        }

        var summary = new List<string>();
        if (textures > 0) summary.Add($"{textures} texture{(textures == 1 ? "" : "s")}");
        if (solids > 0) summary.Add($"{solids} flat colour{(solids == 1 ? "" : "s")}");
        var note = summary.Count > 0 ? $" Materials: {string.Join(", ", summary)} imported." : "";
        if (notes.Count > 0) note += " " + string.Join(" ", notes);
        return new MaterialResolution(map, fallback, note);

        static string? ResolvePath(string folder, string relative) {
            if (string.IsNullOrWhiteSpace(relative)) return null;
            var cleaned = relative.Trim().Trim('"').Replace('/', Path.DirectorySeparatorChar);
            var candidate = Path.IsPathRooted(cleaned) ? cleaned : Path.Combine(folder, cleaned);
            return File.Exists(candidate) ? Path.GetFullPath(candidate) : null;
        }
    }

    HashSet<uint>? _cachedSurfaceIds;
    HashSet<uint>? _cachedSurfaceTextureIds;
    HashSet<uint>? _cachedRenderSurfaceIds;

    IEnumerable<uint> ExistingIdsOfType<T>(uint range) where T : class, IDatRecord, new() {
        HashSet<uint>? cache = range switch {
            0x08000000 => _cachedSurfaceIds,
            0x05000000 => _cachedSurfaceTextureIds,
            0x06000000 => _cachedRenderSurfaceIds,
            _ => throw new ArgumentOutOfRangeException(nameof(range)),
        };
        if (cache != null)
            return cache;

        cache = _dats == null
            ? new HashSet<uint>()
            : _dats.GetAllIdsOfType<T>().ToHashSet();
        if (_portal != null) {
            foreach (uint id in _portal.GetEntryIds()) {
                if ((id & 0xFF000000) == range)
                    cache.Add(id);
            }
        }

        switch (range) {
            case 0x08000000: _cachedSurfaceIds = cache; break;
            case 0x05000000: _cachedSurfaceTextureIds = cache; break;
            case 0x06000000: _cachedRenderSurfaceIds = cache; break;
        }
        return cache;
    }

    [RelayCommand]
    private async Task ImportMonsterObj() {
        if (_session?.WorkingSetup == null) { StatusText = "Load the creature whose skeleton and animations the model should use, then import."; return; }
        var file = await PickOpen("Import OBJ as whole monster", "*.obj");
        if (file == null) return;
        if (!EnsureCloned()) return;
        try {
            var text = await ReadText(file);
            var upAxis = ResolveUpAxis(text);
            var materials = ResolveMaterials(file, text);
            var options = new ObjMonsterImportOptions {
                UpAxis = upAxis,
                FitToCreature = ObjFitToCreature,
                YawDegrees = ParseOr(ObjYawDegrees, 0f),
                ClearUnmatchedParts = ObjClearUnmatched,
                Deduplicate = DeduplicateObj,
                UseGroupNames = ObjUseGroupNames,
                FlipV = ObjFlipV && upAxis == ObjUpAxis.YUp,
                MaterialSurfaces = materials.Map,
                DefaultSurface = materials.Default,
            };
            if (!_session.ImportMonsterFromObj(text, options, out var result, out var error) || result == null) {
                StatusText = error ?? "OBJ import failed.";
                return;
            }
            ObjImportSummary = BuildImportSummary(result) + materials.Note;
            StatusText = result.Summary() + materials.Note + " Undo reverts the whole import.";
            RebuildParts(SelectedPartIndex);
            AfterEdit();
        }
        catch (Exception ex) { StatusText = ex.Message; }
    }

    string BuildImportSummary(ObjMonsterImportResult result) {
        var lines = new List<string> { result.Summary(), "" };
        foreach (var (part, count) in result.TrianglesPerPart.OrderBy(pair => pair.Key)) {
            var model = _session?.Project.Parts.FirstOrDefault(item => item.Index == part);
            string name = string.IsNullOrWhiteSpace(model?.Label) ? $"Part {part}" : model!.Label!;
            lines.Add($"{name}: {count} triangles");
        }
        if (result.ClearedParts.Count > 0)
            lines.Add("Emptied: " + string.Join(", ", result.ClearedParts));
        if (result.KeptParts.Count > 0)
            lines.Add("Kept old mesh: " + string.Join(", ", result.KeptParts));
        if (result.PositionedGroups.Count > 0)
            lines.Add("Groups placed by position: " + string.Join(", ", result.PositionedGroups.Select(g => g.Length == 0 ? "(unnamed)" : g)));
        return string.Join("\n", lines);
    }

    ObjUpAxis ResolveUpAxis(string objText) => ObjUpAxisName switch {
        UpAxisY => ObjUpAxis.YUp,
        UpAxisZ => ObjUpAxis.ZUp,
        _ => ObjPartImport.DetectUpAxis(objText),
    };

    static async Task<string> ReadText(IStorageFile file) {
        await using var stream = await file.OpenReadAsync();
        using var reader = new StreamReader(stream);
        return await reader.ReadToEndAsync();
    }

    [RelayCommand]
    private async Task ExportPartObj() {
        if (_session == null || !TryPart(out int index)) return;
        var file = await PickSave($"part{index}.obj", "*.obj");
        if (file == null) return;
        await WriteText(file, _session.ExportPartObj(index));
        StatusText = "Exported the part in GfxObj local space.";
    }

    [RelayCommand]
    private async Task ExportAssembledObj() {
        if (_session == null) return;
        var file = await PickSave("setup.obj", "*.obj");
        if (file == null) return;
        await WriteText(file, _session.ExportSetupObj());
        StatusText = "Exported the assembled resting pose. Importing this file back requires Convert from assembled space.";
    }

    [RelayCommand]
    private async Task SaveProject() {
        if (_session == null) { StatusText = "Load a Setup first."; return; }
        var file = await PickSave("monster-project.json", "*.json");
        if (file == null) return;
        await WriteText(file, _session.ToJson());
        StatusText = "Saved the Monster Builder project. Portal DAT records were not modified.";
    }

    [RelayCommand]
    private async Task ExportProject() => await SaveProject();

    [RelayCommand]
    private async Task ImportProject() {
        var file = await PickOpen("Import monster project", "*.json");
        if (file == null) return;
        try {
            await using var stream = await file.OpenReadAsync();
            using var reader = new StreamReader(stream);
            _session = MonsterSession.FromJson(await reader.ReadToEndAsync());
            SetupIdText = _session.Project.SourceSetup;
            StatusText = "Imported the monster project. Publish is still a separate step.";
            RebuildParts(0);
            PreviewRevision++;
        }
        catch (Exception ex) { StatusText = ex.Message; }
    }

    [RelayCommand]
    private void ReviewPublish() {
        if (_session == null) { StatusText = "Load a Setup first."; return; }
        var report = _session.Validate(SurfaceExists, ExistingIds());
        PublishSummary = _session.PublishSummary(report);
        PublishReady = _session.IsCloned && !report.HasFatal && _dats?.CanWrite == true && _portal != null;
        StatusText = PublishReady
            ? "Review the summary, then confirm publish."
            : report.HasFatal ? "Fix the fatal errors before publishing." : "Clone the monster before publishing.";
    }

    [RelayCommand]
    private void ConfirmPublish() {
        if (_session == null || _portal == null || !PublishReady) {
            StatusText = "Review publish first. The project must be able to write portal records.";
            return;
        }
        try {
            _session.Publish(_portal);
            PublishReady = false;
            StatusText = $"Published Setup {_session.Project.WorkingSetup} into the project portal overlay. Export DATs to write client_portal.dat.";
        }
        catch (Exception ex) { StatusText = ex.Message; }
    }

    [RelayCommand]
    private void Undo() {
        if (_session == null || !_session.CanUndo) return;
        int index = SelectedPartIndex;
        _session.Undo();
        RebuildParts(index);
        PreviewRevision++;
        RefreshHistory();
        StatusText = "Undid the last monster edit.";
    }

    [RelayCommand]
    private void Redo() {
        if (_session == null || !_session.CanRedo) return;
        int index = SelectedPartIndex;
        _session.Redo();
        RebuildParts(index);
        PreviewRevision++;
        RefreshHistory();
        StatusText = "Redid the monster edit.";
    }

    [RelayCommand] private void ResetCamera() => RequestCamera("reset");
    [RelayCommand] private void ViewFront() => RequestCamera("front");
    [RelayCommand] private void ViewBack() => RequestCamera("back");
    [RelayCommand] private void ViewLeft() => RequestCamera("left");
    [RelayCommand] private void ViewRight() => RequestCamera("right");
    [RelayCommand] private void ViewTop() => RequestCamera("top");

    [RelayCommand]
    private void ToggleHidden(MonsterPartRow? row) {
        if (_session == null || row == null) return;
        row.Hidden = !row.Hidden;
        _session.SetHidden(row.Index, row.Hidden);
        PreviewRevision++;
    }

    public HashSet<int> HiddenParts() =>
        _session == null
            ? []
            : _session.Project.Parts.Where(part => part.Hidden).Select(part => part.Index).ToHashSet();

    void RequestCamera(string preset) {
        CameraPreset = preset;
        CameraRequest++;
    }

    void Deform(Action<GfxObj> edit, string message) {
        if (!TryPart(out int index)) return;
        RunGeometry(index, edit);
        StatusText = message;
    }

    bool RunGeometry(int index, Action<GfxObj> edit) {
        if (_session == null) return false;
        if (!_session.IsCloned)
            StatusText = "These edits are on an in-memory copy. Clone before publish so the source Setup ID is not reused.";
        try {
            _session.EditGeometry(index, edit);
            AfterEdit();
            return true;
        }
        catch (Exception ex) {
            StatusText = ex.Message;
            return false;
        }
    }

    void AfterEdit() {
        LoadSelectedFields();
        RefreshHistory();
        PreviewRevision++;
    }

    void RefreshHistory() {
        CanUndo = _session?.CanUndo == true;
        CanRedo = _session?.CanRedo == true;
    }

    bool EnsureCloned() {
        if (_session == null) { StatusText = "Load a Setup first."; return false; }
        if (_session.IsCloned) return true;
        CloneMonster();
        return _session.IsCloned;
    }

    bool TryPart(out int index) {
        index = SelectedPartIndex;
        if (_session == null) { StatusText = "Load a Setup first."; return false; }
        if (index < 0) { StatusText = "Select a part."; return false; }
        return true;
    }

    void RebuildParts(int selectIndex) {
        if (_session?.WorkingSetup == null) {
            Parts = [];
            MonsterTitle = "No Setup loaded";
            return;
        }
        var setup = _session.WorkingSetup;
        MonsterTitle = _session.IsCloned
            ? $"Clone {_session.Project.WorkingSetup}  from  {_session.Project.SourceSetup}"
            : $"Source {_session.Project.SourceSetup}";
        var rows = new ObservableCollection<MonsterPartRow>();
        foreach (var part in _session.Project.Parts.OrderBy(item => item.Index)) {
            int parent = -1;
            SetupAssembly.HasParentLink(setup, part.Index, out parent);
            string name = string.IsNullOrWhiteSpace(part.Label) ? $"Part {part.Index}" : part.Label!;
            var row = new MonsterPartRow {
                Index = part.Index,
                Depth = SetupAssembly.Depth(setup, part.Index),
                Title = name,
                GfxLabel = part.WorkingGfxObj ?? "",
                ParentLabel = parent >= 0 ? $"Parent {parent}" : "Root",
                RoleLabel = MonsterRoles.DisplayName(part.Role),
                Hidden = part.Hidden,
            };
            row.ToggleRequested = part => ToggleHidden(part);
            rows.Add(row);
        }
        Parts = rows;
        SelectedPart = rows.FirstOrDefault(row => row.Index == selectIndex) ?? rows.FirstOrDefault();
        RefreshProportionFlags();
        RefreshHistory();
    }

    void LoadSelectedFields() {
        _loadingFields = true;
        try {
            if (_session?.WorkingSetup == null || SelectedPartIndex < 0) {
                PartSummary = "Select a part.";
                return;
            }
            var part = _session.Project.Parts.FirstOrDefault(item => item.Index == SelectedPartIndex);
            var gfx = _session.GfxForPart(SelectedPartIndex);
            PartLabel = part?.Label ?? "";
            SelectedRole = part?.Role ?? "";
            MirrorPartnerText = part?.MirrorPartner?.ToString(CultureInfo.InvariantCulture) ?? "";
            if (gfx == null) {
                PartSummary = "This part has no GfxObj.";
                return;
            }
            var stats = GfxObjMesh.Stats(gfx);
            var surfaces = string.Join(", ", gfx.Surfaces.Select(MonsterHex));
            SurfaceText = gfx.Surfaces.Count > 0 ? MonsterHex(gfx.Surfaces[0]) : "";
            int parent = -1;
            SetupAssembly.HasParentLink(_session.WorkingSetup, SelectedPartIndex, out parent);
            PartSummary = $"Part {SelectedPartIndex}\nGfxObj {part?.WorkingGfxObj}\nParent {(parent >= 0 ? parent.ToString() : "none")}\nVertices {stats.VertexCount}\nTriangles {stats.TriangleCount}\nSurfaces {surfaces}\nSize {stats.Size.X:0.###} x {stats.Size.Y:0.###} x {stats.Size.Z:0.###}";
            SetupAssembly.TryGetPartFrame(_session.WorkingSetup, SelectedPartIndex, out var frame);
            AttachmentPosition = Format(frame.Origin);
            AttachmentRotation = Format(EulerDegrees(frame.Orientation));
            AttachmentScale = Format(SetupAssembly.DefaultScale(_session.WorkingSetup, SelectedPartIndex));
            RefreshSurfaceLists(gfx);
        }
        finally { _loadingFields = false; }
    }

    void RefreshSurfaceLists(GfxObj? selected) {
        PartSurfaces = new ObservableCollection<SurfaceRow>(SurfaceRows(selected));
        var all = new Dictionary<uint, int>();
        if (_session != null) {
            foreach (var gfx in _session.GfxById.Values) {
                if (gfx.Surfaces.Count == 0) continue;
                var faceCounts = new int[gfx.Surfaces.Count];
                foreach (var poly in gfx.Polygons.Values) {
                    int slot = poly.PosSurface;
                    if ((uint)slot < (uint)faceCounts.Length)
                        faceCounts[slot]++;
                }
                for (int i = 0; i < gfx.Surfaces.Count; i++)
                    all[gfx.Surfaces[i]] = all.GetValueOrDefault(gfx.Surfaces[i]) + faceCounts[i];
            }
            // Imported textures stay listed even before any face uses them.
            foreach (uint id in _session.Surfaces.Keys)
                all.TryAdd(id, 0);
        }
        CreatureSurfaces = new ObservableCollection<SurfaceRow>(all.OrderBy(pair => pair.Key).Select(pair => new SurfaceRow {
            Id = pair.Key,
            Label = SurfaceLabel(pair.Key),
            Detail = pair.Value == 0 ? "imported, not on any part yet" : $"{pair.Value} faces on this monster",
        }));
        SelectedPartSurface = PartSurfaces.FirstOrDefault();
        SelectedPaintSurface ??= CreatureSurfaces.FirstOrDefault();
    }

    IEnumerable<SurfaceRow> SurfaceRows(GfxObj? gfx) {
        if (gfx == null) yield break;
        var faceCounts = new int[gfx.Surfaces.Count];
        foreach (var poly in gfx.Polygons.Values) {
            int slot = poly.PosSurface;
            if ((uint)slot < (uint)faceCounts.Length)
                faceCounts[slot]++;
        }
        for (int i = 0; i < gfx.Surfaces.Count; i++) {
            yield return new SurfaceRow {
                Id = gfx.Surfaces[i],
                Label = SurfaceLabel(gfx.Surfaces[i]),
                Detail = $"{faceCounts[i]} faces",
            };
        }
    }

    void RefreshProportionFlags() {
        bool Has(params string[] roles) => _session?.Project.Parts.Any(part => part.Role != null && roles.Contains(part.Role)) == true;
        HasHead = Has(MonsterRoles.Head);
        HasTorso = Has(MonsterRoles.Torso);
        HasArms = Has(MonsterRoles.UpperArmL, MonsterRoles.UpperArmR, MonsterRoles.ForearmL, MonsterRoles.ForearmR);
        HasHands = Has(MonsterRoles.HandL, MonsterRoles.HandR);
        HasLegs = Has(MonsterRoles.ThighL, MonsterRoles.ThighR, MonsterRoles.ShinL, MonsterRoles.ShinR);
        HasFeet = Has(MonsterRoles.FootL, MonsterRoles.FootR);
    }

    bool SurfaceExists(uint id) =>
        _session?.Surfaces.ContainsKey(id) == true
        || (_dats != null && _dats.TryGet<Surface>(id, out var surface) && surface != null);

    string SurfaceLabel(uint id) {
        var name = _session?.TextureName(id);
        return name == null ? MonsterHex(id) : $"{MonsterHex(id)}  {name}";
    }

    HashSet<uint>? ExistingIds() {
        if (_dats == null) return null;
        var ids = _dats.GetAllIdsOfType<GfxObj>().Concat(_dats.GetAllIdsOfType<Setup>()).ToHashSet();
        if (_portal != null) ids.UnionWith(_portal.GetEntryIds());
        return ids;
    }

    float Factor() => ParseOr(DeformFactor, 1.2f);
    static float ParseOr(string text, float fallback) =>
        float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out float value) ? value : fallback;

    static bool Differs(string text, out float factor) {
        factor = ParseOr(text, 1f);
        return MathF.Abs(factor - 1f) > 0.0001f;
    }

    static bool TryFloat(string text, out float value) =>
        float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value);

    static bool TryVec3(string text, out Vector3 value) {
        value = default;
        var parts = text.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 3) return false;
        if (!TryFloat(parts[0], out float x) || !TryFloat(parts[1], out float y) || !TryFloat(parts[2], out float z))
            return false;
        value = new Vector3(x, y, z);
        return true;
    }

    static List<ushort> ParseFaceList(string text) {
        var faces = new List<ushort>();
        foreach (var token in text.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)) {
            var range = token.Split('-', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            if (range.Length == 1 && ushort.TryParse(range[0], out ushort one))
                faces.Add(one);
            else if (range.Length == 2
                && ushort.TryParse(range[0], out ushort start)
                && ushort.TryParse(range[1], out ushort end)) {
                for (int i = start; i <= end; i++)
                    faces.Add((ushort)i);
            }
        }
        return faces;
    }

    static string Format(Vector3 value) =>
        string.Create(CultureInfo.InvariantCulture, $"{value.X:0.###}, {value.Y:0.###}, {value.Z:0.###}");

    static Vector3 EulerDegrees(Quaternion q) {
        var m = Matrix4x4.CreateFromQuaternion(q);
        float pitch = MathF.Asin(Math.Clamp(-m.M31, -1f, 1f));
        float yaw;
        float roll;
        if (MathF.Abs(m.M31) < 0.999f) {
            yaw = MathF.Atan2(m.M21, m.M11);
            roll = MathF.Atan2(m.M32, m.M33);
        }
        else {
            yaw = MathF.Atan2(-m.M12, m.M22);
            roll = 0;
        }
        return new Vector3(roll, pitch, yaw) * (180f / MathF.PI);
    }

    static string MonsterHex(uint id) => "0x" + id.ToString("X8", CultureInfo.InvariantCulture);

    static async Task<IStorageFile?> PickOpen(string title, params string[] patterns) {
        if (Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop
            || desktop.MainWindow == null)
            return null;
        var picked = await desktop.MainWindow.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions {
            Title = title,
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType(title) { Patterns = patterns }],
        });
        return picked.Count > 0 ? picked[0] : null;
    }

    static async Task<IStorageFile?> PickSave(string name, string pattern) {
        if (Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop
            || desktop.MainWindow == null)
            return null;
        return await desktop.MainWindow.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions {
            Title = "Save",
            SuggestedFileName = name,
            FileTypeChoices = [new FilePickerFileType(name) { Patterns = [pattern] }],
        });
    }

    static async Task WriteText(IStorageFile file, string text) {
        await using var stream = await file.OpenWriteAsync();
        stream.SetLength(0);
        await using var writer = new StreamWriter(stream);
        await writer.WriteAsync(text);
    }
}
