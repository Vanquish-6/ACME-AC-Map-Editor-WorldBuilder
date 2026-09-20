using System;
using System.Collections.Generic;
using System.Numerics;
using Silk.NET.OpenGL;
using Acme.Render;
using WorldBuilder.Lib;

namespace WorldBuilder.Editors.Landscape {
    public enum GizmoMode { Translate, Rotate, Scale }
    public enum GizmoAxis { None, X, Y, Z, XY, XZ, YZ, All, ViewAxis }

    public class TransformGizmo : IDisposable {
        private const float ArrowLength = 1.5f;
        private const float ShaftRadius = 0.035f;
        private const float TipLength = 0.35f;
        private const float TipRadius = 0.12f;
        private const float PlaneHandleSize = 0.32f;
        private const float PlaneHandleOffset = 0.50f;
        private const float RingRadius = 1.15f;
        private const float RingTubeRadius = 0.025f;
        private const float ViewRingRadius = 1.30f;
        private const float CubeHalfSize = 0.10f;
        private const float CenterHandleRadius = 0.14f;
        private const int ConeSides = 24;
        private const int ShaftSides = 12;
        private const int RingSegments = 64;
        private const int TubeSides = 8;
        private const float HitThresholdPx = 18f;
        private const float ScreenScaleFactor = 0.14f;

        private static readonly Vector3 Red = new(0.90f, 0.18f, 0.18f);
        private static readonly Vector3 Green = new(0.40f, 0.90f, 0.18f);
        private static readonly Vector3 Blue = new(0.20f, 0.45f, 0.95f);
        private static readonly Vector3 Yellow = new(1.0f, 0.95f, 0.20f);
        private static readonly Vector3 Magenta = new(0.95f, 0.20f, 0.95f);
        private static readonly Vector3 Cyan = new(0.20f, 0.95f, 0.95f);
        private static readonly Vector3 White = new(0.92f, 0.92f, 0.92f);
        private static readonly Vector3 HighlightYellow = new(1.0f, 0.98f, 0.60f);
        private static readonly Vector3 HighlightWhite = new(1.0f, 1.0f, 1.0f);

        public GizmoMode Mode { get; set; } = GizmoMode.Translate;
        public GizmoAxis HoveredAxis { get; private set; } = GizmoAxis.None;
        public GizmoAxis ActiveAxis { get; private set; } = GizmoAxis.None;
        public bool IsDragging => ActiveAxis != GizmoAxis.None;
        public bool UseLocalSpace { get; set; }

        public float CurrentRotationAngle { get; private set; }

        private uint _vao, _vbo, _ebo;
        private uint _dynVAO, _dynVBO, _dynEBO;
        private bool _initialized;
        private IShader? _shader;

        private float[] _translateVerts = Array.Empty<float>();
        private uint[] _translateIndices = Array.Empty<uint>();
        private AxisSection[] _translateSections = Array.Empty<AxisSection>();

        private float[] _rotateVerts = Array.Empty<float>();
        private uint[] _rotateIndices = Array.Empty<uint>();
        private AxisSection[] _rotateSections = Array.Empty<AxisSection>();

        private float[] _scaleVerts = Array.Empty<float>();
        private uint[] _scaleIndices = Array.Empty<uint>();
        private AxisSection[] _scaleSections = Array.Empty<AxisSection>();

        private float[] _boxVerts = Array.Empty<float>();
        private uint[] _boxIndices = Array.Empty<uint>();
        private uint _boxVAO, _boxVBO, _boxEBO;

        private Vector3 _dragPlaneOrigin;
        private Vector3 _dragPlaneNormal;
        private Vector3 _dragAxisDir;
        private Vector3 _dragStartHit;
        private float _dragStartAngle;

        struct AxisSection {
            public GizmoAxis Axis;
            public int IndexOffset;
            public int IndexCount;
            public float BaseAlpha;
        }

        public unsafe void Initialize(GL gl, IShader shader) {
            _shader = shader;

            BuildTranslateGeometry();
            BuildRotateGeometry();
            BuildScaleGeometry();

            gl.GenVertexArrays(1, out _vao);
            gl.GenBuffers(1, out _vbo);
            gl.GenBuffers(1, out _ebo);

            gl.BindVertexArray(_vao);
            gl.BindBuffer(GLEnum.ArrayBuffer, _vbo);
            gl.BindBuffer(GLEnum.ElementArrayBuffer, _ebo);
            SetupVertexAttribs(gl);
            gl.BindVertexArray(0);

            gl.GenVertexArrays(1, out _boxVAO);
            gl.GenBuffers(1, out _boxVBO);
            gl.GenBuffers(1, out _boxEBO);

            gl.BindVertexArray(_boxVAO);
            gl.BindBuffer(GLEnum.ArrayBuffer, _boxVBO);
            gl.BindBuffer(GLEnum.ElementArrayBuffer, _boxEBO);
            SetupVertexAttribs(gl);
            gl.BindVertexArray(0);

            gl.GenVertexArrays(1, out _dynVAO);
            gl.GenBuffers(1, out _dynVBO);
            gl.GenBuffers(1, out _dynEBO);

            gl.BindVertexArray(_dynVAO);
            gl.BindBuffer(GLEnum.ArrayBuffer, _dynVBO);
            gl.BindBuffer(GLEnum.ElementArrayBuffer, _dynEBO);
            SetupVertexAttribs(gl);
            gl.BindVertexArray(0);

            _initialized = true;
        }

        private static unsafe void SetupVertexAttribs(GL gl) {
            uint stride = 6 * sizeof(float);
            gl.EnableVertexAttribArray(0);
            gl.VertexAttribPointer(0, 3, GLEnum.Float, false, stride, (void*)0);
            gl.EnableVertexAttribArray(1);
            gl.VertexAttribPointer(1, 3, GLEnum.Float, false, stride, (void*)(3 * sizeof(float)));
        }

        public unsafe void Render(GL gl, Matrix4x4 viewProjection, ICamera camera,
            Vector3 gizmoCenter, Quaternion gizmoOrientation, bool hasSelection) {
            if (!_initialized || _shader == null || !hasSelection) return;

            float gizmoScale = ComputeScale(camera, gizmoCenter);
            var orientMatrix = UseLocalSpace
                ? Matrix4x4.CreateFromQuaternion(gizmoOrientation)
                : Matrix4x4.Identity;
            var model = Matrix4x4.CreateScale(gizmoScale) * orientMatrix
                * Matrix4x4.CreateTranslation(gizmoCenter);

            float[] verts;
            uint[] indices;
            AxisSection[] sections;
            GetModeGeometry(out verts, out indices, out sections);

            gl.Disable(EnableCap.DepthTest);
            gl.Enable(EnableCap.Blend);
            gl.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);
            gl.Disable(EnableCap.CullFace);

            _shader.Bind();
            _shader.SetUniform("uViewProjection", viewProjection);
            _shader.SetUniform("uModel", model);

            gl.BindVertexArray(_vao);
            gl.BindBuffer(GLEnum.ArrayBuffer, _vbo);
            fixed (float* vp = verts) {
                gl.BufferData(GLEnum.ArrayBuffer, (nuint)(verts.Length * sizeof(float)), vp, GLEnum.DynamicDraw);
            }
            gl.BindBuffer(GLEnum.ElementArrayBuffer, _ebo);
            fixed (uint* ip = indices) {
                gl.BufferData(GLEnum.ElementArrayBuffer, (nuint)(indices.Length * sizeof(uint)), ip, GLEnum.DynamicDraw);
            }

            foreach (var section in sections) {
                bool isHovered = section.Axis == HoveredAxis && !IsDragging;
                bool isActive = section.Axis == ActiveAxis && IsDragging;
                bool isDimmed = IsDragging && section.Axis != ActiveAxis;

                float brightness;
                Vector3 highlightColor;
                float highlightMix;
                float alpha = section.BaseAlpha;

                if (isActive) {
                    brightness = 1.0f;
                    highlightColor = HighlightWhite;
                    highlightMix = 0.45f;
                    alpha = 1.0f;
                } else if (isHovered) {
                    brightness = 1.2f;
                    highlightColor = HighlightYellow;
                    highlightMix = 0.30f;
                    alpha = MathF.Min(alpha + 0.2f, 1.0f);
                } else if (isDimmed) {
                    brightness = 0.45f;
                    highlightColor = Vector3.Zero;
                    highlightMix = 0.0f;
                    alpha *= 0.5f;
                } else {
                    brightness = 1.0f;
                    highlightColor = Vector3.Zero;
                    highlightMix = 0.0f;
                }

                _shader.SetUniform("uBrightness", brightness);
                _shader.SetUniform("uAlpha", alpha);
                _shader.SetUniform("uHighlightColor", highlightColor);
                _shader.SetUniform("uHighlightMix", highlightMix);
                gl.DrawElements(GLEnum.Triangles, (uint)section.IndexCount, GLEnum.UnsignedInt,
                    (void*)(section.IndexOffset * sizeof(uint)));
            }

            if (IsDragging && Mode == GizmoMode.Rotate) {
                RenderRotationArc(gl, viewProjection, camera, gizmoCenter, gizmoOrientation, gizmoScale);
            }

            gl.BindVertexArray(0);
            gl.UseProgram(0);
            gl.Enable(EnableCap.DepthTest);
            gl.Enable(EnableCap.CullFace);
            gl.Disable(EnableCap.Blend);
        }

        private unsafe void RenderRotationArc(GL gl, Matrix4x4 viewProjection, ICamera camera,
            Vector3 gizmoCenter, Quaternion gizmoOrientation, float gizmoScale) {
            if (_shader == null || MathF.Abs(CurrentRotationAngle) < 0.001f) return;

            var orient = UseLocalSpace
                ? Matrix4x4.CreateFromQuaternion(gizmoOrientation)
                : Matrix4x4.Identity;
            var axisDir = GetAxisDirection(ActiveAxis, orient);
            var perp1 = GetPerpendicular(axisDir);
            var perp2 = Vector3.Cross(axisDir, perp1);

            float arcRadius = RingRadius * gizmoScale;
            float angle = CurrentRotationAngle;
            int arcSegments = Math.Max(3, (int)(MathF.Abs(angle) / (MathF.PI / 32f)));
            arcSegments = Math.Min(arcSegments, 128);

            var verts = new List<float>();
            var indices = new List<uint>();

            uint centerIdx = 0;
            AddVertex(verts, gizmoCenter, GetAxisColor(ActiveAxis) * 0.6f);

            for (int i = 0; i <= arcSegments; i++) {
                float t = (float)i / arcSegments;
                float a = t * angle;
                var pt = gizmoCenter + (perp1 * MathF.Cos(a) + perp2 * MathF.Sin(a)) * arcRadius;
                AddVertex(verts, pt, GetAxisColor(ActiveAxis) * 0.6f);
            }

            for (int i = 0; i < arcSegments; i++) {
                indices.Add(centerIdx);
                if (angle >= 0) {
                    indices.Add(centerIdx + 1 + (uint)i);
                    indices.Add(centerIdx + 2 + (uint)i);
                } else {
                    indices.Add(centerIdx + 2 + (uint)i);
                    indices.Add(centerIdx + 1 + (uint)i);
                }
            }

            if (verts.Count == 0 || indices.Count == 0) return;

            var vertsArr = verts.ToArray();
            var indicesArr = indices.ToArray();

            _shader.SetUniform("uModel", Matrix4x4.Identity);
            _shader.SetUniform("uBrightness", 1.0f);
            _shader.SetUniform("uAlpha", 0.35f);
            _shader.SetUniform("uHighlightColor", Vector3.Zero);
            _shader.SetUniform("uHighlightMix", 0.0f);

            gl.BindVertexArray(_dynVAO);
            gl.BindBuffer(GLEnum.ArrayBuffer, _dynVBO);
            fixed (float* vp = vertsArr) {
                gl.BufferData(GLEnum.ArrayBuffer, (nuint)(vertsArr.Length * sizeof(float)), vp, GLEnum.DynamicDraw);
            }
            gl.BindBuffer(GLEnum.ElementArrayBuffer, _dynEBO);
            fixed (uint* ip = indicesArr) {
                gl.BufferData(GLEnum.ElementArrayBuffer, (nuint)(indicesArr.Length * sizeof(uint)), ip, GLEnum.DynamicDraw);
            }

            gl.DrawElements(GLEnum.Triangles, (uint)indicesArr.Length, GLEnum.UnsignedInt, null);
            gl.BindVertexArray(0);
        }

        private static void AddVertex(List<float> verts, Vector3 pos, Vector3 color) {
            verts.Add(pos.X); verts.Add(pos.Y); verts.Add(pos.Z);
            verts.Add(color.X); verts.Add(color.Y); verts.Add(color.Z);
        }

        private static Vector3 GetAxisColor(GizmoAxis axis) => axis switch {
            GizmoAxis.X => Red,
            GizmoAxis.Y => Green,
            GizmoAxis.Z => Blue,
            GizmoAxis.XY => Yellow,
            GizmoAxis.XZ => Magenta,
            GizmoAxis.YZ => Cyan,
            GizmoAxis.ViewAxis => White,
            _ => White
        };

        public unsafe void RenderSelectionBox(GL gl, Matrix4x4 viewProjection, ICamera camera,
            Vector3 min, Vector3 max, Vector3 color) {
            if (!_initialized || _shader == null) return;

            BuildBoxGeometry(min, max, color);

            gl.Enable(EnableCap.DepthTest);
            gl.Enable(EnableCap.Blend);
            gl.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);
            gl.Disable(EnableCap.CullFace);

            _shader.Bind();
            _shader.SetUniform("uViewProjection", viewProjection);
            _shader.SetUniform("uModel", Matrix4x4.Identity);
            _shader.SetUniform("uAlpha", 0.85f);
            _shader.SetUniform("uBrightness", 1.0f);
            _shader.SetUniform("uHighlightColor", Vector3.Zero);
            _shader.SetUniform("uHighlightMix", 0.0f);

            gl.BindVertexArray(_boxVAO);
            gl.BindBuffer(GLEnum.ArrayBuffer, _boxVBO);
            fixed (float* vp = _boxVerts) {
                gl.BufferData(GLEnum.ArrayBuffer, (nuint)(_boxVerts.Length * sizeof(float)), vp, GLEnum.DynamicDraw);
            }
            gl.BindBuffer(GLEnum.ElementArrayBuffer, _boxEBO);
            fixed (uint* ip = _boxIndices) {
                gl.BufferData(GLEnum.ElementArrayBuffer, (nuint)(_boxIndices.Length * sizeof(uint)), ip, GLEnum.DynamicDraw);
            }

            gl.DrawElements(GLEnum.Triangles, (uint)_boxIndices.Length, GLEnum.UnsignedInt, null);

            gl.BindVertexArray(0);
            gl.UseProgram(0);
            gl.Enable(EnableCap.CullFace);
            gl.Disable(EnableCap.Blend);
        }

        public void UpdateHover(Vector2 mousePos, ICamera camera, Matrix4x4 viewProjection,
            Vector3 gizmoCenter, Quaternion gizmoOrientation) {
            if (IsDragging) return;
            HoveredAxis = HitTestScreen(mousePos, camera, viewProjection, gizmoCenter, gizmoOrientation);
        }

        public GizmoAxis HitTestScreen(Vector2 mousePos, ICamera camera, Matrix4x4 viewProjection,
            Vector3 gizmoCenter, Quaternion gizmoOrientation) {

            float gizmoScale = ComputeScale(camera, gizmoCenter);
            var orient = UseLocalSpace
                ? Matrix4x4.CreateFromQuaternion(gizmoOrientation)
                : Matrix4x4.Identity;
            float sw = camera.ScreenSize.X, sh = camera.ScreenSize.Y;

            var centerScreen = WorldToScreen(gizmoCenter, viewProjection, sw, sh);
            if (!centerScreen.HasValue) return GizmoAxis.None;

            if (Mode == GizmoMode.Translate || Mode == GizmoMode.Scale) {
                var axes = new[] { Vector3.UnitX, Vector3.UnitY, Vector3.UnitZ };
                var axisIds = new[] { GizmoAxis.X, GizmoAxis.Y, GizmoAxis.Z };

                if (Mode == GizmoMode.Translate) {
                    var centerScreenDist = Vector2.Distance(mousePos, centerScreen.Value);
                    if (centerScreenDist < HitThresholdPx * 1.0f) {
                        return GizmoAxis.All;
                    }

                    var planeAxes = new[] {
                        (GizmoAxis.XY, Vector3.UnitX, Vector3.UnitY),
                        (GizmoAxis.XZ, Vector3.UnitX, Vector3.UnitZ),
                        (GizmoAxis.YZ, Vector3.UnitY, Vector3.UnitZ)
                    };
                    foreach (var (axis, a1, a2) in planeAxes) {
                        var d1 = Vector3.Transform(a1, orient);
                        var d2 = Vector3.Transform(a2, orient);
                        var handleCenter = gizmoCenter + (d1 + d2) * (PlaneHandleOffset * gizmoScale);
                        var hScreen = WorldToScreen(handleCenter, viewProjection, sw, sh);
                        if (hScreen.HasValue && Vector2.Distance(mousePos, hScreen.Value) < HitThresholdPx * 1.4f)
                            return axis;
                    }
                }

                float closestDist = float.MaxValue;
                GizmoAxis closestAxis = GizmoAxis.None;
                for (int i = 0; i < 3; i++) {
                    var dir = Vector3.Transform(axes[i], orient);
                    var endWorld = gizmoCenter + dir * (ArrowLength * gizmoScale);
                    var endScreen = WorldToScreen(endWorld, viewProjection, sw, sh);
                    if (endScreen == null) continue;
                    float dist = PointToSegmentDist(mousePos, centerScreen.Value, endScreen.Value);
                    if (dist < HitThresholdPx && dist < closestDist) {
                        closestDist = dist;
                        closestAxis = axisIds[i];
                    }
                }
                return closestAxis;
            }

            if (Mode == GizmoMode.Rotate) {
                float viewRingScreenRadius = 0f;
                var viewRingSample = gizmoCenter + camera.Right * (ViewRingRadius * gizmoScale);
                var viewRingScreen = WorldToScreen(viewRingSample, viewProjection, sw, sh);
                if (viewRingScreen.HasValue) {
                    viewRingScreenRadius = Vector2.Distance(centerScreen.Value, viewRingScreen.Value);
                    float viewRingDist = MathF.Abs(Vector2.Distance(mousePos, centerScreen.Value) - viewRingScreenRadius);
                    if (viewRingDist < HitThresholdPx)
                        return GizmoAxis.ViewAxis;
                }

                var axes = new[] { Vector3.UnitX, Vector3.UnitY, Vector3.UnitZ };
                var axisIds = new[] { GizmoAxis.X, GizmoAxis.Y, GizmoAxis.Z };
                float closestDist = float.MaxValue;
                GizmoAxis closestAxis = GizmoAxis.None;

                for (int i = 0; i < 3; i++) {
                    var axisDir = Vector3.Transform(axes[i], orient);
                    var perp1 = GetPerpendicular(axisDir);
                    var perp2 = Vector3.Cross(axisDir, perp1);

                    float bestSegDist = float.MaxValue;
                    for (int s = 0; s < RingSegments; s++) {
                        float a1 = 2f * MathF.PI * s / RingSegments;
                        float a2 = 2f * MathF.PI * (s + 1) / RingSegments;
                        var p1 = gizmoCenter + (perp1 * MathF.Cos(a1) + perp2 * MathF.Sin(a1)) * (RingRadius * gizmoScale);
                        var p2 = gizmoCenter + (perp1 * MathF.Cos(a2) + perp2 * MathF.Sin(a2)) * (RingRadius * gizmoScale);
                        var s1 = WorldToScreen(p1, viewProjection, sw, sh);
                        var s2 = WorldToScreen(p2, viewProjection, sw, sh);
                        if (s1 == null || s2 == null) continue;
                        float d = PointToSegmentDist(mousePos, s1.Value, s2.Value);
                        if (d < bestSegDist) bestSegDist = d;
                    }
                    if (bestSegDist < HitThresholdPx && bestSegDist < closestDist) {
                        closestDist = bestSegDist;
                        closestAxis = axisIds[i];
                    }
                }
                return closestAxis;
            }

            return GizmoAxis.None;
        }

        public bool StartDrag(GizmoAxis axis, Vector2 mousePos, ICamera camera, Matrix4x4 viewProjection,
            Vector3 gizmoCenter, Quaternion gizmoOrientation) {
            if (axis == GizmoAxis.None) return false;
            ActiveAxis = axis;
            CurrentRotationAngle = 0f;

            float gizmoScale = ComputeScale(camera, gizmoCenter);
            var orient = UseLocalSpace
                ? Matrix4x4.CreateFromQuaternion(gizmoOrientation)
                : Matrix4x4.Identity;

            if (Mode == GizmoMode.Translate || Mode == GizmoMode.Scale) {
                if (axis == GizmoAxis.All) {
                    var viewDir = Vector3.Normalize(gizmoCenter - camera.Position);
                    _dragPlaneNormal = -viewDir;
                    _dragAxisDir = Vector3.Zero;
                } else {
                    _dragAxisDir = GetAxisDirection(axis, orient);
                    var viewDir = Vector3.Normalize(gizmoCenter - camera.Position);

                    if (axis == GizmoAxis.XY || axis == GizmoAxis.XZ || axis == GizmoAxis.YZ) {
                        _dragPlaneNormal = GetPlaneNormal(axis, orient);
                    } else {
                        var cross = Vector3.Cross(viewDir, _dragAxisDir);
                        _dragPlaneNormal = Vector3.Normalize(Vector3.Cross(_dragAxisDir, cross));
                    }
                }

                _dragPlaneOrigin = gizmoCenter;
                var ray = BuildRay(mousePos, camera);
                var hit = RayPlaneIntersect(ray.origin, ray.dir, _dragPlaneOrigin, _dragPlaneNormal);
                _dragStartHit = hit ?? gizmoCenter;
            }

            if (Mode == GizmoMode.Rotate) {
                if (axis == GizmoAxis.ViewAxis) {
                    _dragAxisDir = Vector3.Normalize(camera.Position - gizmoCenter);
                } else {
                    _dragAxisDir = GetAxisDirection(axis, orient);
                }
                _dragPlaneNormal = _dragAxisDir;
                _dragPlaneOrigin = gizmoCenter;
                var ray = BuildRay(mousePos, camera);
                var hit = RayPlaneIntersect(ray.origin, ray.dir, _dragPlaneOrigin, _dragPlaneNormal);
                if (hit.HasValue) {
                    var offset = hit.Value - gizmoCenter;
                    _dragStartAngle = MathF.Atan2(
                        Vector3.Dot(offset, Vector3.Cross(_dragAxisDir, GetPerpendicular(_dragAxisDir))),
                        Vector3.Dot(offset, GetPerpendicular(_dragAxisDir)));
                }
            }

            return true;
        }

        public Vector3 ComputeTranslateDelta(Vector2 mousePos, ICamera camera, Vector3 gizmoCenter) {
            if (!IsDragging || Mode != GizmoMode.Translate) return Vector3.Zero;
            var ray = BuildRay(mousePos, camera);
            var hit = RayPlaneIntersect(ray.origin, ray.dir, _dragPlaneOrigin, _dragPlaneNormal);
            if (!hit.HasValue) return Vector3.Zero;

            var delta = hit.Value - _dragStartHit;

            if (ActiveAxis == GizmoAxis.All) {
                return delta;
            }

            if (ActiveAxis == GizmoAxis.X || ActiveAxis == GizmoAxis.Y || ActiveAxis == GizmoAxis.Z) {
                return _dragAxisDir * Vector3.Dot(delta, _dragAxisDir);
            }
            return delta;
        }

        public float ComputeRotationAngle(Vector2 mousePos, ICamera camera, Vector3 gizmoCenter) {
            if (!IsDragging || Mode != GizmoMode.Rotate) return 0f;
            var ray = BuildRay(mousePos, camera);
            var hit = RayPlaneIntersect(ray.origin, ray.dir, _dragPlaneOrigin, _dragPlaneNormal);
            if (!hit.HasValue) return CurrentRotationAngle;

            var perp = GetPerpendicular(_dragAxisDir);
            var offset = hit.Value - gizmoCenter;
            float angle = MathF.Atan2(
                Vector3.Dot(offset, Vector3.Cross(_dragAxisDir, perp)),
                Vector3.Dot(offset, perp));
            CurrentRotationAngle = angle - _dragStartAngle;
            return CurrentRotationAngle;
        }

        public Quaternion GetRotationAxis() {
            if (!IsDragging || Mode != GizmoMode.Rotate) return Quaternion.Identity;
            return Quaternion.Identity;
        }

        public Vector3 GetRotationAxisDirection() => _dragAxisDir;

        public Vector3 ComputeScaleDelta(Vector2 mousePos, ICamera camera, Vector3 gizmoCenter) {
            if (!IsDragging || Mode != GizmoMode.Scale) return Vector3.Zero;
            var ray = BuildRay(mousePos, camera);
            var hit = RayPlaneIntersect(ray.origin, ray.dir, _dragPlaneOrigin, _dragPlaneNormal);
            if (!hit.HasValue) return Vector3.Zero;

            var delta = hit.Value - _dragStartHit;
            float axisDelta = Vector3.Dot(delta, _dragAxisDir);
            float scaleFactor = axisDelta / ComputeScale(camera, gizmoCenter);

            return ActiveAxis switch {
                GizmoAxis.X => new Vector3(scaleFactor, 0, 0),
                GizmoAxis.Y => new Vector3(0, scaleFactor, 0),
                GizmoAxis.Z => new Vector3(0, 0, scaleFactor),
                GizmoAxis.All => new Vector3(scaleFactor, scaleFactor, scaleFactor),
                _ => Vector3.Zero
            };
        }

        public void EndDrag() {
            ActiveAxis = GizmoAxis.None;
            CurrentRotationAngle = 0f;
        }

        public void CancelDrag() {
            ActiveAxis = GizmoAxis.None;
            CurrentRotationAngle = 0f;
        }

        public Vector3 GetLocalAxisDirection(GizmoAxis axis, Quaternion orientation) {
            var orient = UseLocalSpace
                ? Matrix4x4.CreateFromQuaternion(orientation)
                : Matrix4x4.Identity;
            return GetAxisDirection(axis, orient);
        }

        private void GetModeGeometry(out float[] verts, out uint[] indices, out AxisSection[] sections) {
            switch (Mode) {
                case GizmoMode.Translate:
                    verts = _translateVerts; indices = _translateIndices; sections = _translateSections; break;
                case GizmoMode.Rotate:
                    verts = _rotateVerts; indices = _rotateIndices; sections = _rotateSections; break;
                case GizmoMode.Scale:
                    verts = _scaleVerts; indices = _scaleIndices; sections = _scaleSections; break;
                default:
                    verts = _translateVerts; indices = _translateIndices; sections = _translateSections; break;
            }
        }

        private float ComputeScale(ICamera camera, Vector3 center) {
            if (camera is OrthographicTopDownCamera ortho)
                return ortho.OrthographicSize * 0.15f;
            return Vector3.Distance(camera.Position, center) * ScreenScaleFactor;
        }

        #region Geometry Building

        private void BuildTranslateGeometry() {
            var verts = new List<float>();
            var indices = new List<uint>();
            var sections = new List<AxisSection>();

            var axesData = new[] {
                (Vector3.UnitX, Red, GizmoAxis.X),
                (Vector3.UnitY, Green, GizmoAxis.Y),
                (Vector3.UnitZ, Blue, GizmoAxis.Z)
            };

            foreach (var (axis, color, gizmoAxis) in axesData) {
                int idxOff = indices.Count;
                var shaftEnd = axis * ArrowLength;
                var tipEnd = axis * (ArrowLength + TipLength);
                AddCylinder(verts, indices, Vector3.Zero, shaftEnd, ShaftRadius, color, ShaftSides);
                AddCone(verts, indices, shaftEnd, tipEnd, TipRadius, color, ConeSides);
                sections.Add(new AxisSection { Axis = gizmoAxis, IndexOffset = idxOff, IndexCount = indices.Count - idxOff, BaseAlpha = 1.0f });
            }

            var planeData = new[] {
                (GizmoAxis.XY, Vector3.UnitX, Vector3.UnitY, Yellow),
                (GizmoAxis.XZ, Vector3.UnitX, Vector3.UnitZ, Magenta),
                (GizmoAxis.YZ, Vector3.UnitY, Vector3.UnitZ, Cyan)
            };
            foreach (var (gizmoAxis, a1, a2, color) in planeData) {
                int idxOff = indices.Count;
                var center = (a1 + a2) * PlaneHandleOffset;
                float hs = PlaneHandleSize * 0.5f;
                var corners = new[] {
                    center + (a1 + a2) * hs,
                    center + (a1 - a2) * hs,
                    center + (-a1 - a2) * hs,
                    center + (-a1 + a2) * hs,
                };
                AddQuad(verts, indices, corners[0], corners[1], corners[2], corners[3], color);
                AddQuadOutline(verts, indices, corners[0], corners[1], corners[2], corners[3], color * 1.3f, 0.015f);
                sections.Add(new AxisSection { Axis = gizmoAxis, IndexOffset = idxOff, IndexCount = indices.Count - idxOff, BaseAlpha = 0.45f });
            }

            int centerOff = indices.Count;
            AddSphere(verts, indices, Vector3.Zero, CenterHandleRadius, White, 12, 8);
            sections.Add(new AxisSection { Axis = GizmoAxis.All, IndexOffset = centerOff, IndexCount = indices.Count - centerOff, BaseAlpha = 0.85f });

            _translateVerts = verts.ToArray();
            _translateIndices = indices.ToArray();
            _translateSections = sections.ToArray();
        }

        private void BuildRotateGeometry() {
            var verts = new List<float>();
            var indices = new List<uint>();
            var sections = new List<AxisSection>();

            var axesData = new[] {
                (Vector3.UnitX, Red, GizmoAxis.X),
                (Vector3.UnitY, Green, GizmoAxis.Y),
                (Vector3.UnitZ, Blue, GizmoAxis.Z)
            };

            foreach (var (axis, color, gizmoAxis) in axesData) {
                int idxOff = indices.Count;
                AddTorus(verts, indices, axis, RingRadius, RingTubeRadius, color, RingSegments, TubeSides);
                sections.Add(new AxisSection { Axis = gizmoAxis, IndexOffset = idxOff, IndexCount = indices.Count - idxOff, BaseAlpha = 0.90f });
            }

            int viewOff = indices.Count;
            AddTorus(verts, indices, Vector3.UnitZ, ViewRingRadius, RingTubeRadius * 0.7f, White * 0.7f, RingSegments, TubeSides);
            sections.Add(new AxisSection { Axis = GizmoAxis.ViewAxis, IndexOffset = viewOff, IndexCount = indices.Count - viewOff, BaseAlpha = 0.40f });

            _rotateVerts = verts.ToArray();
            _rotateIndices = indices.ToArray();
            _rotateSections = sections.ToArray();
        }

        private void BuildScaleGeometry() {
            var verts = new List<float>();
            var indices = new List<uint>();
            var sections = new List<AxisSection>();

            var axesData = new[] {
                (Vector3.UnitX, Red, GizmoAxis.X),
                (Vector3.UnitY, Green, GizmoAxis.Y),
                (Vector3.UnitZ, Blue, GizmoAxis.Z)
            };

            foreach (var (axis, color, gizmoAxis) in axesData) {
                int idxOff = indices.Count;
                var shaftEnd = axis * ArrowLength;
                AddCylinder(verts, indices, Vector3.Zero, shaftEnd, ShaftRadius, color, ShaftSides);
                AddCube(verts, indices, shaftEnd, CubeHalfSize, color);
                sections.Add(new AxisSection { Axis = gizmoAxis, IndexOffset = idxOff, IndexCount = indices.Count - idxOff, BaseAlpha = 1.0f });
            }

            int centerOff = indices.Count;
            AddCube(verts, indices, Vector3.Zero, CubeHalfSize * 0.8f, White);
            sections.Add(new AxisSection { Axis = GizmoAxis.All, IndexOffset = centerOff, IndexCount = indices.Count - centerOff, BaseAlpha = 0.90f });

            _scaleVerts = verts.ToArray();
            _scaleIndices = indices.ToArray();
            _scaleSections = sections.ToArray();
        }

        private void BuildBoxGeometry(Vector3 min, Vector3 max, Vector3 color) {
            var verts = new List<float>();
            var indices = new List<uint>();
            float t = 0.02f * Vector3.Distance(min, max);
            t = MathF.Max(t, 0.05f);

            var corners = new Vector3[8];
            corners[0] = new(min.X, min.Y, min.Z);
            corners[1] = new(max.X, min.Y, min.Z);
            corners[2] = new(max.X, max.Y, min.Z);
            corners[3] = new(min.X, max.Y, min.Z);
            corners[4] = new(min.X, min.Y, max.Z);
            corners[5] = new(max.X, min.Y, max.Z);
            corners[6] = new(max.X, max.Y, max.Z);
            corners[7] = new(min.X, max.Y, max.Z);

            var edges = new[] {
                (0,1),(1,2),(2,3),(3,0),
                (4,5),(5,6),(6,7),(7,4),
                (0,4),(1,5),(2,6),(3,7)
            };
            foreach (var (a, b) in edges)
                AddCylinder(verts, indices, corners[a], corners[b], t, color, 6);

            _boxVerts = verts.ToArray();
            _boxIndices = indices.ToArray();
        }

        private static void AddCylinder(List<float> verts, List<uint> indices,
            Vector3 from, Vector3 to, float radius, Vector3 color, int sides) {
            var dir = to - from;
            float len = dir.Length();
            if (len < 1e-6f) return;
            dir /= len;
            var p1 = GetPerpendicular(dir);
            var p2 = Vector3.Cross(dir, p1);

            uint b = (uint)(verts.Count / 6);

            for (int i = 0; i < sides; i++) {
                float a = 2f * MathF.PI * i / sides;
                float cos = MathF.Cos(a);
                float sin = MathF.Sin(a);
                var offset = p1 * cos * radius + p2 * sin * radius;
                AddVertex(verts, from + offset, color);
                AddVertex(verts, to + offset, color);
            }

            for (int i = 0; i < sides; i++) {
                uint cur = b + (uint)(i * 2);
                uint next = b + (uint)(((i + 1) % sides) * 2);
                indices.Add(cur); indices.Add(next); indices.Add(cur + 1);
                indices.Add(next); indices.Add(next + 1); indices.Add(cur + 1);
            }

            uint capBase = (uint)(verts.Count / 6);
            AddVertex(verts, from, color);
            AddVertex(verts, to, color);
            for (int i = 0; i < sides; i++) {
                uint cur = b + (uint)(i * 2);
                uint next = b + (uint)(((i + 1) % sides) * 2);
                indices.Add(capBase); indices.Add(next); indices.Add(cur);
                indices.Add(capBase + 1); indices.Add(cur + 1); indices.Add(next + 1);
            }
        }

        private static void AddCone(List<float> verts, List<uint> indices,
            Vector3 baseCenter, Vector3 tip, float radius, Vector3 color, int sides) {
            var dir = Vector3.Normalize(tip - baseCenter);
            var p1 = GetPerpendicular(dir);
            var p2 = Vector3.Cross(dir, p1);

            uint b = (uint)(verts.Count / 6);
            AddVertex(verts, baseCenter, color);
            AddVertex(verts, tip, color * 1.15f);

            for (int i = 0; i < sides; i++) {
                float a = 2f * MathF.PI * i / sides;
                var pt = baseCenter + p1 * MathF.Cos(a) * radius + p2 * MathF.Sin(a) * radius;
                AddVertex(verts, pt, color);
            }

            for (int i = 0; i < sides; i++) {
                int next = (i + 1) % sides;
                indices.Add(b); indices.Add(b + 2 + (uint)i); indices.Add(b + 2 + (uint)next);
                indices.Add(b + 1); indices.Add(b + 2 + (uint)next); indices.Add(b + 2 + (uint)i);
            }
        }

        private static void AddCube(List<float> verts, List<uint> indices,
            Vector3 center, float halfSize, Vector3 color) {
            uint b = (uint)(verts.Count / 6);
            var offsets = new Vector3[] {
                new(-1,-1,-1), new(1,-1,-1), new(1,1,-1), new(-1,1,-1),
                new(-1,-1, 1), new(1,-1, 1), new(1,1, 1), new(-1,1, 1)
            };
            foreach (var o in offsets) AddVertex(verts, center + o * halfSize, color);

            var faces = new uint[] {
                0,1,2,0,2,3, 4,6,5,4,7,6,
                0,4,5,0,5,1, 2,6,7,2,7,3,
                0,3,7,0,7,4, 1,5,6,1,6,2
            };
            foreach (var f in faces) indices.Add(b + f);
        }

        private static void AddSphere(List<float> verts, List<uint> indices,
            Vector3 center, float radius, Vector3 color, int slices, int stacks) {
            uint b = (uint)(verts.Count / 6);

            AddVertex(verts, center + new Vector3(0, 0, radius), color);

            for (int i = 1; i < stacks; i++) {
                float phi = MathF.PI * i / stacks;
                for (int j = 0; j < slices; j++) {
                    float theta = 2f * MathF.PI * j / slices;
                    var pt = center + new Vector3(
                        MathF.Sin(phi) * MathF.Cos(theta),
                        MathF.Sin(phi) * MathF.Sin(theta),
                        MathF.Cos(phi)
                    ) * radius;
                    AddVertex(verts, pt, color);
                }
            }

            AddVertex(verts, center + new Vector3(0, 0, -radius), color);

            for (int j = 0; j < slices; j++) {
                uint next = (uint)((j + 1) % slices);
                indices.Add(b); indices.Add(b + 1 + (uint)j); indices.Add(b + 1 + next);
            }

            for (int i = 0; i < stacks - 2; i++) {
                for (int j = 0; j < slices; j++) {
                    uint cur = b + 1 + (uint)(i * slices + j);
                    uint next = b + 1 + (uint)(i * slices + (j + 1) % slices);
                    uint curBelow = b + 1 + (uint)((i + 1) * slices + j);
                    uint nextBelow = b + 1 + (uint)((i + 1) * slices + (j + 1) % slices);
                    indices.Add(cur); indices.Add(curBelow); indices.Add(next);
                    indices.Add(next); indices.Add(curBelow); indices.Add(nextBelow);
                }
            }

            uint bottomPole = (uint)(verts.Count / 6 - 1);
            uint lastRingStart = b + 1 + (uint)((stacks - 2) * slices);
            for (int j = 0; j < slices; j++) {
                uint next = (uint)((j + 1) % slices);
                indices.Add(bottomPole); indices.Add(lastRingStart + next); indices.Add(lastRingStart + (uint)j);
            }
        }

        private static void AddTorus(List<float> verts, List<uint> indices,
            Vector3 axis, float majorRadius, float minorRadius, Vector3 color, int majorSegments, int minorSegments) {
            var perp1 = GetPerpendicular(axis);
            var perp2 = Vector3.Cross(axis, perp1);

            uint b = (uint)(verts.Count / 6);

            for (int i = 0; i < majorSegments; i++) {
                float majorAngle = 2f * MathF.PI * i / majorSegments;
                var ringCenter = perp1 * MathF.Cos(majorAngle) * majorRadius + perp2 * MathF.Sin(majorAngle) * majorRadius;
                var ringTangent = Vector3.Normalize(-perp1 * MathF.Sin(majorAngle) + perp2 * MathF.Cos(majorAngle));
                var ringNormal = Vector3.Normalize(ringCenter);
                var ringBinormal = Vector3.Cross(ringTangent, ringNormal);

                for (int j = 0; j < minorSegments; j++) {
                    float minorAngle = 2f * MathF.PI * j / minorSegments;
                    var pt = ringCenter + (ringNormal * MathF.Cos(minorAngle) + ringBinormal * MathF.Sin(minorAngle)) * minorRadius;
                    AddVertex(verts, pt, color);
                }
            }

            for (int i = 0; i < majorSegments; i++) {
                int nextI = (i + 1) % majorSegments;
                for (int j = 0; j < minorSegments; j++) {
                    int nextJ = (j + 1) % minorSegments;
                    uint a = b + (uint)(i * minorSegments + j);
                    uint bv = b + (uint)(i * minorSegments + nextJ);
                    uint c = b + (uint)(nextI * minorSegments + j);
                    uint d = b + (uint)(nextI * minorSegments + nextJ);
                    indices.Add(a); indices.Add(c); indices.Add(bv);
                    indices.Add(bv); indices.Add(c); indices.Add(d);
                }
            }
        }

        private static void AddQuad(List<float> verts, List<uint> indices,
            Vector3 a, Vector3 b, Vector3 c, Vector3 d, Vector3 color) {
            uint baseIdx = (uint)(verts.Count / 6);
            AddVertex(verts, a, color);
            AddVertex(verts, b, color);
            AddVertex(verts, c, color);
            AddVertex(verts, d, color);
            indices.Add(baseIdx); indices.Add(baseIdx + 1); indices.Add(baseIdx + 2);
            indices.Add(baseIdx); indices.Add(baseIdx + 2); indices.Add(baseIdx + 3);
            indices.Add(baseIdx + 2); indices.Add(baseIdx + 1); indices.Add(baseIdx);
            indices.Add(baseIdx + 3); indices.Add(baseIdx + 2); indices.Add(baseIdx);
        }

        private static void AddQuadOutline(List<float> verts, List<uint> indices,
            Vector3 a, Vector3 b, Vector3 c, Vector3 d, Vector3 color, float thickness) {
            AddCylinder(verts, indices, a, b, thickness, color, 4);
            AddCylinder(verts, indices, b, c, thickness, color, 4);
            AddCylinder(verts, indices, c, d, thickness, color, 4);
            AddCylinder(verts, indices, d, a, thickness, color, 4);
        }

        #endregion

        #region Math Helpers

        private static Vector3 GetPerpendicular(Vector3 v) {
            v = Vector3.Normalize(v);
            var candidate = MathF.Abs(Vector3.Dot(v, Vector3.UnitY)) < 0.9f ? Vector3.UnitY : Vector3.UnitX;
            return Vector3.Normalize(Vector3.Cross(v, candidate));
        }

        private static Vector2? WorldToScreen(Vector3 world, Matrix4x4 vp, float sw, float sh) {
            var clip = Vector4.Transform(new Vector4(world, 1f), vp);
            if (clip.W <= 0.001f) return null;
            float ndcX = clip.X / clip.W;
            float ndcY = clip.Y / clip.W;
            return new Vector2((ndcX + 1f) * 0.5f * sw, (ndcY + 1f) * 0.5f * sh);
        }

        private static float PointToSegmentDist(Vector2 p, Vector2 a, Vector2 b) {
            var ab = b - a;
            float len2 = ab.LengthSquared();
            if (len2 < 1e-6f) return Vector2.Distance(p, a);
            float t = Math.Clamp(Vector2.Dot(p - a, ab) / len2, 0f, 1f);
            var proj = a + ab * t;
            return Vector2.Distance(p, proj);
        }

        private static Vector3 GetAxisDirection(GizmoAxis axis, Matrix4x4 orient) => axis switch {
            GizmoAxis.X => Vector3.Normalize(Vector3.Transform(Vector3.UnitX, orient)),
            GizmoAxis.Y => Vector3.Normalize(Vector3.Transform(Vector3.UnitY, orient)),
            GizmoAxis.Z => Vector3.Normalize(Vector3.Transform(Vector3.UnitZ, orient)),
            _ => Vector3.UnitZ
        };

        private static Vector3 GetPlaneNormal(GizmoAxis axis, Matrix4x4 orient) => axis switch {
            GizmoAxis.XY => Vector3.Normalize(Vector3.Transform(Vector3.UnitZ, orient)),
            GizmoAxis.XZ => Vector3.Normalize(Vector3.Transform(Vector3.UnitY, orient)),
            GizmoAxis.YZ => Vector3.Normalize(Vector3.Transform(Vector3.UnitX, orient)),
            _ => Vector3.UnitZ
        };

        private static Vector3? RayPlaneIntersect(Vector3 origin, Vector3 dir, Vector3 planePoint, Vector3 planeNormal) {
            float denom = Vector3.Dot(planeNormal, dir);
            if (MathF.Abs(denom) < 1e-6f) return null;
            float t = Vector3.Dot(planePoint - origin, planeNormal) / denom;
            if (t < 0) return null;
            return origin + dir * t;
        }

        private static (Vector3 origin, Vector3 dir) BuildRay(Vector2 mousePos, ICamera camera) {
            float sw = camera.ScreenSize.X, sh = camera.ScreenSize.Y;
            float ndcX = 2f * mousePos.X / sw - 1f;
            float ndcY = 2f * mousePos.Y / sh - 1f;
            var vp = camera.GetViewMatrix() * camera.GetProjectionMatrix();
            if (!Matrix4x4.Invert(vp, out var vpInv))
                return (camera.Position, camera.Front);

            var nearW = Vector4.Transform(new Vector4(ndcX, ndcY, -1f, 1f), vpInv);
            var farW = Vector4.Transform(new Vector4(ndcX, ndcY, 1f, 1f), vpInv);
            nearW /= nearW.W;
            farW /= farW.W;
            var origin = new Vector3(nearW.X, nearW.Y, nearW.Z);
            var dir = Vector3.Normalize(new Vector3(farW.X, farW.Y, farW.Z) - origin);
            return (origin, dir);
        }

        #endregion

        public void Dispose() { }

        public void Dispose(GL gl) {
            if (_vao != 0) gl.DeleteVertexArray(_vao);
            if (_vbo != 0) gl.DeleteBuffer(_vbo);
            if (_ebo != 0) gl.DeleteBuffer(_ebo);
            if (_boxVAO != 0) gl.DeleteVertexArray(_boxVAO);
            if (_boxVBO != 0) gl.DeleteBuffer(_boxVBO);
            if (_boxEBO != 0) gl.DeleteBuffer(_boxEBO);
            if (_dynVAO != 0) gl.DeleteVertexArray(_dynVAO);
            if (_dynVBO != 0) gl.DeleteBuffer(_dynVBO);
            if (_dynEBO != 0) gl.DeleteBuffer(_dynEBO);
        }
    }
}
