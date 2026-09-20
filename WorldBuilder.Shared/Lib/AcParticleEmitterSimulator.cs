using System;
using System.Collections.Generic;
using System.Numerics;
using Acme.Dat;

namespace WorldBuilder.Shared.Lib {
    /// <summary>
    /// Client-faithful CPU simulation for AC <see cref="ParticleEmitter"/> definitions (placed in-world as editor objects).
    /// </summary>
    public sealed class AcParticleEmitterSimulator {
        static readonly ThreadLocal<Random> Rng = new(() => new Random());

        ParticleEmitter _def = new();
        AcParticleSlot[] _slots = Array.Empty<AcParticleSlot>();
        double _creationTime;
        double _lastEmitTime;
        Vector3 _lastEmitAnchorPos;
        int _totalEmitted;
        bool _stopped;
        bool _loaded;

        public uint EmitterId { get; private set; }

        public static uint ResolveVisualGfxObjectId(ParticleEmitter pe) {
            var hw = pe.HwGfxObjId;
            if (hw != 0) return hw;
            return pe.GfxObjId;
        }

        /// <summary>
        /// Reads a portal <see cref="ParticleEmitter"/> and returns the GfxObj used for drawing/thumbnails.
        /// DatReaderWriter may throw from <c>Unpack</c> on corrupt or non-emitter data — same failure mode as LBI stab IDs.
        /// </summary>
        public static bool TryResolveVisualGfxObjectIdFromPortal(IDatReaderWriter dats, uint emitterDid, out uint gfxId) {
            gfxId = 0;
            try {
                if (!dats.TryGet<ParticleEmitter>(emitterDid, out var pe) || pe == null) return false;
                gfxId = ResolveVisualGfxObjectId(pe);
                return gfxId != 0;
            }
            catch {
                return false;
            }
        }

        public bool TryLoad(IDatReaderWriter dats, uint particleEmitterId) {
            ResetState();
            EmitterId = particleEmitterId;
            try {
                if (!dats.TryGet<ParticleEmitter>(particleEmitterId, out _def)) return false;
            }
            catch {
                return false;
            }
            if (ResolveVisualGfxObjectId(_def) == 0) return false;
            int max = Math.Clamp(_def.MaxParticles, 1, 512);
            _slots = new AcParticleSlot[max];
            _loaded = true;
            return true;
        }

        public uint GfxObjectId => _loaded ? ResolveVisualGfxObjectId(_def) : 0;

        void ResetState() {
            _slots = Array.Empty<AcParticleSlot>();
            _creationTime = 0;
            _lastEmitTime = 0;
            _lastEmitAnchorPos = default;
            _totalEmitted = 0;
            _stopped = false;
            _loaded = false;
            EmitterId = 0;
        }

        public void Begin(double timeSec, in Matrix4x4 parentWorld) {
            if (!_loaded) return;
            _creationTime = timeSec;
            _lastEmitTime = timeSec;
            _lastEmitAnchorPos = parentWorld.Translation;
            _stopped = false;
            _totalEmitted = 0;
            for (int i = 0; i < _slots.Length; i++) _slots[i].Active = false;
            int n0 = Math.Clamp(_def.InitialParticles, 0, _slots.Length);
            for (int i = 0; i < n0; i++) TryEmitParticle(timeSec, parentWorld);
        }

        public void Advance(double timeSec, float deltaTime, in Matrix4x4 parentWorld, List<Matrix4x4> outInstanceWorld) {
            outInstanceWorld.Clear();
            if (!_loaded || _slots.Length == 0) return;

            UpdateStopped(timeSec);

            var anchorDelta = parentWorld.Translation - _lastEmitAnchorPos;
            if (!_stopped && ShouldEmit(timeSec, CountActive(), anchorDelta)) {
                if (TryEmitParticle(timeSec, parentWorld)) {
                    _lastEmitTime = timeSec;
                    _lastEmitAnchorPos = parentWorld.Translation;
                }
            }

            bool persistentUnlimited = _def.TotalParticles <= 0 && _def.TotalSeconds <= 0;

            for (int i = 0; i < _slots.Length; i++) {
                ref var s = ref _slots[i];
                if (!s.Active) continue;

                Matrix4x4 refFrame = _def.IsParentLocal ? parentWorld : s.StartReferenceMatrix;
                double lifetime = ComputeLifetime(ref s, timeSec, persistentUnlimited, deltaTime);

                if (s.Lifespan > 0 && lifetime >= s.Lifespan) {
                    s.Active = false;
                    continue;
                }

                UpdateParticleMotion((ParticleType)_def.ParticleType, ref s, lifetime, refFrame, out var worldPos, out var worldRot, out float uScale, out float uAlpha);

                var mat = Matrix4x4.CreateScale(uScale)
                    * Matrix4x4.CreateFromQuaternion(worldRot)
                    * Matrix4x4.CreateTranslation(worldPos);
                // Encode per-particle alpha in M44 (normally 1.0 for affine transforms).
                // The StaticObject.vert shader reads aInstanceMatrix[3].w as per-instance alpha.
                mat.M44 = uAlpha;
                outInstanceWorld.Add(mat);
            }
        }

        static double ComputeLifetime(ref AcParticleSlot s, double timeSec, bool persistentUnlimited, float deltaTime) {
            if (persistentUnlimited) {
                s.LifetimeAccum += deltaTime;
                return s.LifetimeAccum;
            }
            return timeSec - s.BirthTime;
        }

        void UpdateStopped(double timeSec) {
            if (_stopped) return;
            if (_def.TotalSeconds > 0 && _creationTime + _def.TotalSeconds < timeSec)
                _stopped = true;
            if (_def.TotalParticles > 0 && _totalEmitted >= _def.TotalParticles)
                _stopped = true;
        }

        bool ShouldEmit(double timeSec, int numParticles, Vector3 anchorDelta) {
            if (_def.TotalParticles > 0 && _totalEmitted >= _def.TotalParticles) return false;
            if (numParticles >= _def.MaxParticles) return false;

            if (_def.EmitterType.HasFlag(EmitterType.BirthratePerSec))
                return timeSec - _lastEmitTime > _def.Birthrate;
            if (_def.EmitterType.HasFlag(EmitterType.BirthratePerMeter))
                return anchorDelta.LengthSquared() > 1e-8f;
            return false;
        }

        int CountActive() {
            int n = 0;
            for (int i = 0; i < _slots.Length; i++)
                if (_slots[i].Active) n++;
            return n;
        }

        bool TryEmitParticle(double timeSec, in Matrix4x4 parentWorld) {
            int idx = -1;
            for (int i = 0; i < _slots.Length; i++) {
                if (!_slots[i].Active) { idx = i; break; }
            }
            if (idx < 0) return false;

            ref var s = ref _slots[idx];
            InitSlot(ref s, timeSec, parentWorld);
            _totalEmitted++;
            return true;
        }

        void InitSlot(ref AcParticleSlot s, double timeSec, in Matrix4x4 parentWorld) {
            bool persistentUnlimited = _def.TotalParticles <= 0 && _def.TotalSeconds <= 0;
            DecomposeRigid(parentWorld, out var parentOrigin, out var parentRot);

            s.Active = true;
            s.BirthTime = timeSec;
            s.Lifespan = Math.Max(1e-3, GetRandomLifespan());
            s.PersistentParticle = persistentUnlimited;
            s.LifetimeAccum = 0;
            s.LastPhysicsTime = timeSec;
            s.StartOrigin = parentOrigin;
            s.StartOrientation = parentRot;
            s.StartReferenceMatrix = parentWorld;
            s.ParticleOrientation = Quaternion.Identity;

            var offset = GetRandomOffset();
            var ra = GetRandomA();
            var rb = GetRandomB();
            var rc = GetRandomC();
            s.StartScale = GetRandomStartScale();
            s.FinalScale = GetRandomFinalScale();
            s.StartTrans = GetRandomStartTrans();
            s.FinalTrans = GetRandomFinalTrans();

            var pType = (ParticleType)_def.ParticleType;
            ApplyInitForType(ref s, pType, parentWorld, in offset, in ra, in rb, in rc);
            s.Offset = TransformOffsetAtInit(parentWorld, in offset, pType);

            double lifetime = ComputeLifetime(ref s, timeSec, persistentUnlimited, 0f);
            Matrix4x4 refFrame = _def.IsParentLocal ? parentWorld : s.StartReferenceMatrix;
            UpdateParticleMotion(pType, ref s, lifetime, refFrame, out _, out _, out _, out _);
        }

        static Vector3 TransformOffsetAtInit(in Matrix4x4 parentWorld, in Vector3 localOffset, ParticleType pType) {
            if (pType is ParticleType.ParabolicLVLA or ParticleType.ParabolicLVLALR) {
                var o = localOffset;
                return new Vector3(
                    o.X * parentWorld.M11 + o.Y * parentWorld.M21 + o.Z * parentWorld.M31,
                    o.X * parentWorld.M12 + o.Y * parentWorld.M22 + o.Z * parentWorld.M32,
                    o.X * parentWorld.M13 + o.Y * parentWorld.M23 + o.Z * parentWorld.M33);
            }
            if (pType is ParticleType.ParabolicGVGA or ParticleType.ParabolicGVGAGR)
                return localOffset;
            return Vector3.Transform(localOffset, Quaternion.CreateFromRotationMatrix(parentWorld));
        }

        static void ApplyInitForType(ref AcParticleSlot s, ParticleType pType, in Matrix4x4 parentWorld,
            in Vector3 offset, in Vector3 a, in Vector3 b, in Vector3 c) {
            switch (pType) {
                case ParticleType.Swarm:
                    s.A = Vector3.Transform(a, Quaternion.CreateFromRotationMatrix(parentWorld));
                    s.B = b;
                    s.C = c;
                    break;
                case ParticleType.Explode: {
                    float aa = RollF(-MathF.PI, MathF.PI);
                    float ab = RollF(-MathF.PI, MathF.PI);
                    float ca = MathF.Cos(aa);
                    float sa = MathF.Sin(aa);
                    float cb = MathF.Cos(ab);
                    float sb = MathF.Sin(ab);
                    var cv = new Vector3(ca * c.X * cb, sa * c.Y * cb, sb * c.Z);
                    if (cv.LengthSquared() < 1e-12f) s.C = Vector3.Zero;
                    else s.C = Vector3.Normalize(cv);
                    s.A = a;
                    s.B = b;
                    break;
                }
                case ParticleType.Implode:
                    s.A = a;
                    s.B = b;
                    s.C = new Vector3(c.X * offset.X, c.Y * offset.Y, c.Z * offset.Z);
                    break;
                case ParticleType.ParabolicLVLA:
                    s.B = LocalToGlobalVec(parentWorld, b);
                    s.A = LocalToGlobalVec(parentWorld, a);
                    s.C = c;
                    break;
                case ParticleType.ParabolicLVLALR:
                    s.C = LocalToGlobalVec(parentWorld, c);
                    s.B = LocalToGlobalVec(parentWorld, b);
                    s.A = LocalToGlobalVec(parentWorld, a);
                    break;
                case ParticleType.ParabolicGVGA:
                case ParticleType.ParabolicGVGAGR:
                    s.C = c;
                    s.B = b;
                    s.A = a;
                    break;
                case ParticleType.GlobalVelocity:
                    s.A = a;
                    s.B = b;
                    s.C = c;
                    break;
                default:
                    s.A = a;
                    s.B = b;
                    s.C = c;
                    break;
            }
        }

        static Vector3 LocalToGlobalVec(in Matrix4x4 parentWorld, in Vector3 v) => new(
            v.X * parentWorld.M11 + v.Y * parentWorld.M21 + v.Z * parentWorld.M31,
            v.X * parentWorld.M12 + v.Y * parentWorld.M22 + v.Z * parentWorld.M32,
            v.X * parentWorld.M13 + v.Y * parentWorld.M23 + v.Z * parentWorld.M33);

        static void DecomposeRigid(in Matrix4x4 m, out Vector3 origin, out Quaternion rot) {
            origin = m.Translation;
            rot = Quaternion.CreateFromRotationMatrix(m);
        }

        static void UpdateParticleMotion(ParticleType pType, ref AcParticleSlot s, double lifetime, in Matrix4x4 refFrame,
            out Vector3 worldPos, out Quaternion worldRot, out float uniformScale, out float uniformAlpha) {
            DecomposeRigid(refFrame, out var pOrigin, out var pRot);
            float t = (float)lifetime;
            float v26 = lifetime < s.Lifespan ? (float)(lifetime / s.Lifespan) : 1f;

            worldPos = pOrigin;
            worldRot = pRot;

            switch (pType) {
                case ParticleType.Still:
                    worldPos = pOrigin + s.Offset;
                    break;
                case ParticleType.LocalVelocity:
                case ParticleType.GlobalVelocity:
                    worldPos = pOrigin + s.Offset + s.A * t;
                    break;
                case ParticleType.ParabolicLVGA:
                case ParticleType.ParabolicGVGA:
                case ParticleType.ParabolicLVLA: {
                    float bx = s.B.X * 0.5f;
                    float by = s.B.Y * 0.5f;
                    float bz = s.B.Z * 0.5f;
                    float v11 = t;
                    float originc = bx * v11 * v11;
                    float origin4c = by * v11 * v11;
                    float origin8b = bz * v11 * v11;
                    worldPos = pOrigin + s.Offset + s.A * t + new Vector3(originc, origin4c, origin8b);
                    break;
                }
                case ParticleType.ParabolicLVGAGR:
                case ParticleType.ParabolicLVLALR:
                case ParticleType.ParabolicGVGAGR:
                    UpdateParabolicRotated(ref s, t, refFrame, out worldPos, out worldRot);
                    break;
                case ParticleType.Swarm:
                    worldPos = pOrigin + s.Offset + new Vector3(
                        MathF.Cos(s.B.X * t) * s.C.X + t * s.A.X,
                        MathF.Sin(s.B.Y * t) * s.C.Y + t * s.A.Y,
                        MathF.Cos(s.B.Z * t) * s.C.Z + t * s.A.Z);
                    break;
                case ParticleType.Explode:
                    worldPos = pOrigin + s.Offset + new Vector3(
                        (t * s.B.X + s.C.X * s.A.X) * t,
                        (t * s.B.Y + s.C.Y * s.A.X) * t,
                        (t * s.B.Z + s.C.Z * s.A.X + s.A.Z) * t);
                    break;
                case ParticleType.Implode: {
                    float c = MathF.Cos(s.A.X * t);
                    float t2 = t * t;
                    worldPos = pOrigin + s.Offset + new Vector3(
                        c * s.C.X + t2 * s.B.X,
                        c * s.C.Y + t2 * s.B.Y,
                        c * s.C.Z + t2 * s.B.Z);
                    break;
                }
                default:
                    worldPos = pOrigin + s.Offset;
                    break;
            }

            uniformScale = (s.FinalScale - s.StartScale) * v26 + s.StartScale;
            uniformScale = Math.Clamp(uniformScale, 0.05f, 20f);

            uniformAlpha = (s.FinalTrans - s.StartTrans) * v26 + s.StartTrans;
            uniformAlpha = Math.Clamp(uniformAlpha, 0f, 1f);
        }

        static void UpdateParabolicRotated(ref AcParticleSlot s, float t, in Matrix4x4 refFrame,
            out Vector3 worldPos, out Quaternion worldRot) {
            float origine = s.B.X * 0.5f;
            float origin4d = s.B.Y * 0.5f;
            float origin8c = s.B.Z * 0.5f;
            float v15 = t;
            float wy = origin4d * v15;
            float wz = origin8c * v15;
            float v30 = v15;
            float v16 = wy * v30;
            float v56 = wz * v30;
            float v17 = v15 * s.A.X;
            wy = v15 * s.A.Y;
            wz = v15 * s.A.Z;
            float v18 = v17 + s.Offset.X;
            float ry = wy + s.Offset.Y;
            float rz = wz + s.Offset.Z;
            float wx = v18 + origine * v15 * v30;
            var o = refFrame.Translation;
            o.X += wx;
            o.Y += ry + v16;
            o.Z += rz + v56;
            float v19 = v15 * s.C.X;
            float v21 = v15 * s.C.Y;
            float rz2 = v15 * s.C.Z;
            var w = new Vector3(v19, v21, rz2);
            worldRot = Quaternion.CreateFromRotationMatrix(refFrame);
            FrameRotate(ref worldRot, w);
            worldRot = Quaternion.Normalize(worldRot);
            worldPos = o;
        }

        /// <summary>Matches client Frame::rotate + Frame::grotate (axis = w, angle = |w|).</summary>
        static void FrameRotate(ref Quaternion q, Vector3 w) {
            float lenSq = w.X * w.X + w.Y * w.Y + w.Z * w.Z;
            if (lenSq < 1e-8f * 1e-8f) return;
            float len = MathF.Sqrt(lenSq);
            float half = len * 0.5f;
            float s = MathF.Sin(half);
            float inv = 1f / len;
            float f = s * w.X * inv;
            float g = s * w.Y * inv;
            float vz = s * w.Z * inv;
            float wa = MathF.Cos(half);
            float qx = q.X, qy = q.Y, qz = q.Z, qw = q.W;
            float newX = wa * qx + g * qz + f * qw - vz * qy;
            float newY = wa * qy - f * qz + vz * qx + g * qw;
            float newZ = wa * qz + f * qy + g * qx - vz * qw;
            float newW = wa * qw - f * qx - g * qy - vz * qz;
            q = Quaternion.Normalize(new Quaternion(newX, newY, newZ, newW));
        }

        double GetRandomLifespan() {
            double r = RollD(-1, 1);
            double v = r * _def.LifespanRand + _def.Lifespan;
            return v < 0 ? 0 : v;
        }

        float GetRandomStartScale() {
            float v = (float)(RollD(-1, 1) * _def.ScaleRand + _def.StartScale);
            return Math.Clamp(v, 0.1f, 10f);
        }

        float GetRandomFinalScale() {
            float v = (float)(RollD(-1, 1) * _def.ScaleRand + _def.FinalScale);
            return Math.Clamp(v, 0.1f, 10f);
        }

        float GetRandomStartTrans() {
            float v = (float)(RollD(-1, 1) * _def.TransRand + _def.StartTrans);
            return Math.Clamp(v, 0f, 1f);
        }

        float GetRandomFinalTrans() {
            float v = (float)(RollD(-1, 1) * _def.TransRand + _def.FinalTrans);
            return Math.Clamp(v, 0f, 1f);
        }

        Vector3 GetRandomOffset() {
            double rx = RollD(-1, 1);
            double ry = RollD(-1, 1);
            double rz = RollD(-1, 1);
            var dir = _def.OffsetDir;
            double dot = rx * dir.X + ry * dir.Y + rz * dir.Z;
            var offset = new Vector3(
                (float)(rx - dot * dir.X),
                (float)(ry - dot * dir.Y),
                (float)(rz - dot * dir.Z));
            if (offset.LengthSquared() < 1e-12f) {
                offset = Vector3.Zero;
            }
            else {
                offset = Vector3.Normalize(offset);
                float dist = (float)(RollD(0, 1) * (_def.MaxOffset - _def.MinOffset) + _def.MinOffset);
                offset *= dist;
            }
            return offset;
        }

        Vector3 GetRandomA() {
            float m = (float)(RollD(0, 1) * (_def.MaxA - _def.MinA) + _def.MinA);
            return _def.A * m;
        }

        Vector3 GetRandomB() {
            float m = (float)(RollD(0, 1) * (_def.MaxB - _def.MinB) + _def.MinB);
            return _def.B * m;
        }

        Vector3 GetRandomC() {
            float m = (float)(RollD(0, 1) * (_def.MaxC - _def.MinC) + _def.MinC);
            return _def.C * m;
        }

        static double RollD(double a, double b) => a + (b - a) * Rng.Value!.NextDouble();

        static float RollF(float a, float b) => a + (b - a) * (float)Rng.Value!.NextDouble();
    }

    internal struct AcParticleSlot {
        public bool Active;
        public double BirthTime;
        public double Lifespan;
        public bool PersistentParticle;
        public double LifetimeAccum;
        public double LastPhysicsTime;

        public Vector3 Offset;
        public Vector3 A, B, C;
        public float StartScale, FinalScale, StartTrans, FinalTrans;

        public Matrix4x4 StartReferenceMatrix;
        public Vector3 StartOrigin;
        public Quaternion StartOrientation;

        public Quaternion ParticleOrientation;
    }
}
