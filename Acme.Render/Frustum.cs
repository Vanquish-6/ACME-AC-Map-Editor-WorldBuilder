using System.Numerics;

namespace Acme.Render {
    public struct Frustum {
        public Plane[] Planes;

        public Frustum(Matrix4x4 viewProjection) {
            Planes = new Plane[6];
            var m = viewProjection;
            Planes[0] = Plane.Normalize(new Plane(m.M14 + m.M11, m.M24 + m.M21, m.M34 + m.M31, m.M44 + m.M41));
            Planes[1] = Plane.Normalize(new Plane(m.M14 - m.M11, m.M24 - m.M21, m.M34 - m.M31, m.M44 - m.M41));
            Planes[2] = Plane.Normalize(new Plane(m.M14 + m.M12, m.M24 + m.M22, m.M34 + m.M32, m.M44 + m.M42));
            Planes[3] = Plane.Normalize(new Plane(m.M14 - m.M12, m.M24 - m.M22, m.M34 - m.M32, m.M44 - m.M42));
            Planes[4] = Plane.Normalize(new Plane(m.M14 + m.M13, m.M24 + m.M23, m.M34 + m.M33, m.M44 + m.M43));
            Planes[5] = Plane.Normalize(new Plane(m.M14 - m.M13, m.M24 - m.M23, m.M34 - m.M33, m.M44 - m.M43));
        }

        public bool IntersectsBoundingBox(BoundingBox box) {
            foreach (var plane in Planes) {
                var p = box.Min;
                if (plane.Normal.X >= 0) p.X = box.Max.X;
                if (plane.Normal.Y >= 0) p.Y = box.Max.Y;
                if (plane.Normal.Z >= 0) p.Z = box.Max.Z;
                if (Plane.DotCoordinate(plane, p) < 0)
                    return false;
            }
            return true;
        }
    }
}
