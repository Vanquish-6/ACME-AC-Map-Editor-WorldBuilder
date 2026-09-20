using System;
using System.Collections.Generic;
using System.Numerics;
using WorldBuilder.Lib;
using WorldBuilder.Shared.Documents;
using WorldBuilder.Shared.Models;

namespace WorldBuilder.Editors.Landscape {
    /// <summary>
    /// Manages terrain editing state and modifications
    /// </summary>
    public class TerrainEditingContext {
        private readonly TerrainDocument _terrainDoc;
        private readonly TerrainSystem _terrainSystem;
        private BaseDocument? _currentLayerDoc;

        private readonly HashSet<uint> _modifiedLandblocks = new();

        /// <summary>
        /// Set of active vertices being edited (in world coordinates)
        /// </summary>
        public HashSet<Vector2> ActiveVertices { get; } = new();

        /// <summary>
        /// Whether the brush preview should be rendered on the terrain
        /// </summary>
        public bool BrushActive { get; set; } = false;

        /// <summary>
        /// World-space center of the brush preview (XY)
        /// </summary>
        public Vector2 BrushCenter { get; set; } = Vector2.Zero;

        /// <summary>
        /// Radius of the brush preview in world units
        /// </summary>
        public float BrushRadius { get; set; } = 0f;

        /// <summary>
        /// 0 = hard-edged disk, 1 = smooth falloff from center to edge.
        /// </summary>
        public float BrushFalloff { get; set; } = 0.65f;

        /// <summary>
        /// When true, terrain tools refuse to paint (locked layer).
        /// </summary>
        public bool LayerLocked { get; set; }

        /// <summary>
        /// Texture atlas layer index for the preview texture.
        /// -1 means no preview. Set by brush/fill tools to show a WYSIWYG
        /// texture preview on the terrain via the shader.
        /// </summary>
        public int PreviewTextureAtlasIndex { get; set; } = -1;

        /// <summary>
        /// Gets the modified landblock IDs since last clear
        /// </summary>
        public IEnumerable<uint> ModifiedLandblocks => _modifiedLandblocks;

        /// <summary>
        /// Gets or sets the current layer document for editing (TerrainDocument or LayerDocument)
        /// </summary>
        public BaseDocument? CurrentLayerDoc {
            get => _currentLayerDoc;
            set => _currentLayerDoc = value;
        }

        public TerrainEditingContext(DocumentManager docManager, TerrainSystem terrainSystem, Project? project = null) {
            var terrainDoc = docManager.GetOrCreateDocumentAsync<TerrainDocument>("terrain").Result;
            _terrainDoc = terrainDoc ?? throw new ArgumentNullException(nameof(terrainDoc));
            _terrainSystem = terrainSystem ?? throw new ArgumentNullException(nameof(terrainSystem));
            _currentLayerDoc = _terrainDoc; // Default to base layer
            Project = project;
        }

        /// <summary>The active project, providing access to OutdoorInstancePlacements.</summary>
        public Project? Project { get; }

        /// <summary>Fired when OutdoorInstancePlacements are added or removed, so the editor can refresh rendering.</summary>
        public event Action? OutdoorInstancesChanged;

        /// <summary>Raises <see cref="OutdoorInstancesChanged"/> from within or outside this class.</summary>
        public void NotifyOutdoorInstancesChanged() => OutdoorInstancesChanged?.Invoke();

        /// <summary>
        /// Marks a landblock as modified and queues it for GPU update
        /// </summary>
        public void MarkLandblockModified(ushort landblockId) {
            _modifiedLandblocks.Add(landblockId);
            _terrainSystem.Scene.DataManager.MarkLandblocksDirty(new HashSet<ushort> { landblockId });

            // Apply changes to the current layer
            var changes = new Dictionary<ushort, Dictionary<byte, uint>>();
            var currentLayer = _currentLayerDoc ?? _terrainDoc;
            if (currentLayer is TerrainDocument terrainDoc) {
                terrainDoc.UpdateLandblocksBatchInternal(changes, out var modifiedLandblocks);
            }
            else if (currentLayer is LayerDocument layerDoc) {
                layerDoc.UpdateLandblocksBatchInternal(TerrainField.Type, changes, out var modifiedLandblocks);
            }
        }

        /// <summary>
        /// Marks multiple landblocks as modified
        /// </summary>
        public void MarkLandblocksModified(HashSet<ushort> landblockIds) {
            foreach (var id in landblockIds) {
                _modifiedLandblocks.Add(id);
            }
            _terrainSystem.Scene.DataManager.MarkLandblocksDirty(landblockIds);

            // Apply changes to the current layer
            var changes = new Dictionary<ushort, Dictionary<byte, uint>>();
            var currentLayer = _currentLayerDoc ?? _terrainDoc;
            if (currentLayer is TerrainDocument terrainDoc) {
                terrainDoc.UpdateLandblocksBatchInternal(changes, out var modifiedLandblocks);
            }
            else if (currentLayer is LayerDocument layerDoc) {
                layerDoc.UpdateLandblocksBatchInternal(TerrainField.Type, changes, out var modifiedLandblocks);
            }
        }

        /// <summary>
        /// Clears the modified landblocks set (called after GPU updates)
        /// </summary>
        public void ClearModifiedLandblocks() {
            _modifiedLandblocks.Clear();
        }

        /// <summary>
        /// Gets height at a world position using bilinear interpolation
        /// </summary>
        public float GetHeightAtPosition(float x, float y) {
            return _terrainSystem.Scene.DataManager.GetHeightAtPosition(x, y);
        }

        /// <summary>
        /// Computes the terrain surface normal at a world position by sampling neighboring heights.
        /// </summary>
        public Vector3 GetTerrainNormal(float x, float y) {
            const float step = 1.0f;
            float hL = _terrainSystem.Scene.DataManager.GetHeightAtPosition(x - step, y);
            float hR = _terrainSystem.Scene.DataManager.GetHeightAtPosition(x + step, y);
            float hD = _terrainSystem.Scene.DataManager.GetHeightAtPosition(x, y - step);
            float hU = _terrainSystem.Scene.DataManager.GetHeightAtPosition(x, y + step);
            var normal = new Vector3(hL - hR, hD - hU, 2f * step);
            return Vector3.Normalize(normal);
        }

        /// <summary>
        /// Computes a quaternion that aligns an object's up-vector (+Z) to the given surface normal.
        /// Works for floors, walls, ceilings, and any arbitrary surface orientation.
        /// </summary>
        public static Quaternion AlignToNormal(Vector3 surfaceNormal) {
            if (surfaceNormal.LengthSquared() < 1e-10f) return Quaternion.Identity;
            surfaceNormal = Vector3.Normalize(surfaceNormal);

            var up = Vector3.UnitZ;
            float dot = Vector3.Dot(up, surfaceNormal);

            if (dot > 0.9999f) return Quaternion.Identity;
            if (dot < -0.9999f) return Quaternion.CreateFromAxisAngle(Vector3.UnitX, MathF.PI);

            var axis = Vector3.Cross(up, surfaceNormal);
            if (axis.LengthSquared() < 1e-10f) return Quaternion.Identity;
            axis = Vector3.Normalize(axis);
            float angle = MathF.Acos(Math.Clamp(dot, -1f, 1f));
            return Quaternion.CreateFromAxisAngle(axis, angle);
        }

        /// <summary>
        /// Computes a full orientation quaternion that places an object on a surface:
        /// the object's +Z axis aligns to the surface normal (object faces away from surface),
        /// while preserving a yaw angle around the normal. Works for floors, walls, and ceilings.
        /// </summary>
        public static Quaternion AlignToSurfaceWithYaw(Vector3 surfaceNormal, float yaw) {
            if (surfaceNormal.LengthSquared() < 1e-10f) return Quaternion.Identity;
            surfaceNormal = Vector3.Normalize(surfaceNormal);

            var forward = surfaceNormal;
            float absDotZ = MathF.Abs(Vector3.Dot(forward, Vector3.UnitZ));

            Vector3 referenceUp;
            if (absDotZ > 0.95f) {
                // Floor or ceiling: use yaw-rotated Y axis as reference
                referenceUp = new Vector3(MathF.Sin(yaw), MathF.Cos(yaw), 0f);
            } else {
                // Wall or angled surface: world Z is usable as a reference
                referenceUp = Vector3.UnitZ;
            }

            var right = Vector3.Cross(referenceUp, forward);
            if (right.LengthSquared() < 1e-10f) {
                referenceUp = Vector3.UnitX;
                right = Vector3.Cross(referenceUp, forward);
            }
            right = Vector3.Normalize(right);
            var up = Vector3.Normalize(Vector3.Cross(forward, right));

            // Apply yaw rotation around the surface normal for non-floor/ceiling surfaces
            if (absDotZ <= 0.95f) {
                var yawRot = Quaternion.CreateFromAxisAngle(forward, yaw);
                right = Vector3.Transform(right, yawRot);
                up = Vector3.Transform(up, yawRot);
            }

            var rotMatrix = new Matrix4x4(
                right.X, right.Y, right.Z, 0,
                up.X, up.Y, up.Z, 0,
                forward.X, forward.Y, forward.Z, 0,
                0, 0, 0, 1);

            return Quaternion.Normalize(Quaternion.CreateFromRotationMatrix(rotMatrix));
        }

        /// <summary>
        /// Adds a vertex to the active set
        /// </summary>
        public void AddActiveVertex(Vector2 vertex) {
            ActiveVertices.Add(vertex);
        }

        /// <summary>
        /// Removes a vertex from the active set
        /// </summary>
        public void RemoveActiveVertex(Vector2 vertex) {
            ActiveVertices.Remove(vertex);
        }

        /// <summary>
        /// Clears all active vertices
        /// </summary>
        public void ClearActiveVertices() {
            ActiveVertices.Clear();
        }

        /// <summary>
        /// Gets the terrain document (base layer)
        /// </summary>
        public TerrainDocument TerrainDocument => _terrainDoc;

        /// <summary>
        /// Gets the terrain system
        /// </summary>
        public TerrainSystem TerrainSystem => _terrainSystem;

        /// <summary>
        /// Gets the object selection state for the selector tool
        /// </summary>
        public ObjectSelectionState ObjectSelection { get; } = new();
    }
}