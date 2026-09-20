using System.Numerics;

namespace Acme.Render {
    public struct BoundingBox {
        public Vector3 Min;
        public Vector3 Max;

        public BoundingBox(Vector3 min, Vector3 max) {
            Min = min;
            Max = max;
        }

        public Vector3 Center => (Min + Max) * 0.5f;
        public Vector3 Size => Max - Min;

        public bool Intersects(BoundingBox other) {
            return Min.X <= other.Max.X && Max.X >= other.Min.X
                && Min.Y <= other.Max.Y && Max.Y >= other.Min.Y
                && Min.Z <= other.Max.Z && Max.Z >= other.Min.Z;
        }
    }
}
