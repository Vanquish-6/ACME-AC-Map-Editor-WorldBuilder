using System;
using System.Numerics;
using WorldBuilder.Shared.Documents;
using WorldBuilder.Shared.Models;

namespace WorldBuilder.Utilities {
    public static class StampTransforms {
        /// <summary>
        /// Rotates a stamp 90 degrees clockwise by reindexing the vertex grid.
        /// AC-specific: Works on arbitrary NxM grids.
        /// </summary>
        public static TerrainStamp Rotate90Clockwise(TerrainStamp original) {
            int w = original.WidthInVertices;
            int h = original.HeightInVertices;

            var rotated = new TerrainStamp {
                Name = original.Name + " (Rotated 90°)",
                Description = original.Description,
                WidthInVertices = h,  // Swap dimensions
                HeightInVertices = w,
                Heights = new byte[original.Heights.Length],
                TerrainTypes = new ushort[original.TerrainTypes.Length]
            };

            for (int x = 0; x < w; x++) {
                for (int y = 0; y < h; y++) {
                    int oldIndex = x * h + y;

                    // 90° clockwise rotation: (x, y) → (y, w-1-x)
                    int newX = y;
                    int newY = w - 1 - x;
                    int newIndex = newX * w + newY;

                    rotated.Heights[newIndex] = original.Heights[oldIndex];
                    rotated.TerrainTypes[newIndex] = original.TerrainTypes[oldIndex];
                }
            }

            // Rotate objects
            foreach (var obj in original.Objects) {
                var rotatedObj = RotateObject90(obj, w, h);
                rotated.Objects.Add(rotatedObj);
            }

            return rotated;
        }

        public static TerrainStamp Rotate180(TerrainStamp original) {
            return Rotate90Clockwise(Rotate90Clockwise(original));
        }

        public static TerrainStamp Rotate270Clockwise(TerrainStamp original) {
            return Rotate90Clockwise(Rotate90Clockwise(Rotate90Clockwise(original)));
        }

        private static StaticObject RotateObject90(StaticObject obj, int gridWidth, int gridHeight) {
            // Rotate position within grid (assuming 24-unit spacing)
            float cellSize = 24f;
            float localX = obj.Origin.X;
            float localY = obj.Origin.Y;

            // 90° clockwise rotation
            // New X is old Y
            float newX = localY;
            // New Y is Width - 1 - old X
            float newY = ((gridWidth - 1) * cellSize) - localX;

            // Rotate orientation 90° clockwise around Z-axis (-90 degrees)
            var rotation90 = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, -MathF.PI / 2f);
            var newOrientation = obj.Orientation * rotation90;

            return new StaticObject {
                Id = obj.Id,
                IsSetup = obj.IsSetup,
                Origin = new Vector3(newX, newY, obj.Origin.Z),
                Orientation = newOrientation,
                Scale = obj.Scale
            };
        }
    }
}
