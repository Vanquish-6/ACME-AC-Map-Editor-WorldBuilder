namespace Acme.Render {
    public struct Rectangle {
        public int X;
        public int Y;
        public int Width;
        public int Height;

        public Rectangle(int x, int y, int width, int height) {
            X = x;
            Y = y;
            Width = width;
            Height = height;
        }

        public int Left {
            get => X;
            set => X = value;
        }

        public int Right {
            get => X + Width;
            set => Width = value - X;
        }

        public int Top {
            get => Y;
            set => Y = value;
        }

        public int Bottom {
            get => Y + Height;
            set => Height = value - Y;
        }
    }
}
