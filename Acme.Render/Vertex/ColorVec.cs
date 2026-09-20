namespace Acme.Render.Vertex {
    public struct ColorVec {
        public static readonly ColorVec White = new(1, 1, 1, 1);
        public static readonly ColorVec Black = new(0, 0, 0, 1);
        public static readonly ColorVec Red = new(1, 0, 0, 1);
        public static readonly ColorVec Green = new(0, 1, 0, 1);
        public static readonly ColorVec Blue = new(0, 0, 1, 1);
        public static readonly ColorVec Yellow = new(1, 1, 0, 1);
        public static readonly ColorVec Magenta = new(1, 0, 1, 1);
        public static readonly ColorVec Cyan = new(0, 1, 1, 1);
        public static readonly ColorVec Transparent = new(0, 0, 0, 0);

        public float R;
        public float G;
        public float B;
        public float A;

        public ColorVec(float red, float green, float blue, float alpha) {
            R = red;
            G = green;
            B = blue;
            A = alpha;
        }
    }
}
