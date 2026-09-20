using CommunityToolkit.Mvvm.ComponentModel;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using System.Linq;

namespace WorldBuilder.Editors.Dungeon {
    public partial class CellSurfaceSlot : ObservableObject {
        public int SlotIndex { get; }
        public ushort SurfaceId { get; }
        public string DisplayText { get; }

        [ObservableProperty]
        private WriteableBitmap? _thumbnail;

        public CellSurfaceSlot(int slotIndex, ushort surfaceId, string displayText) {
            SlotIndex = slotIndex;
            SurfaceId = surfaceId;
            DisplayText = displayText;
        }
    }

    public partial class PrefabListEntry : ObservableObject {
        public DungeonPrefab Prefab { get; }
        public string DisplayName { get; }
        public string DetailText => DetailOverride ?? $"{Prefab.Cells.Count} rooms, {Prefab.OpenPortalCount} open, used {Prefab.UsageCount}x";
        public string? DetailOverride { get; set; }
        public int CellCount => Prefab.Cells.Count;
        public int OpenPortals => Prefab.OpenPortalCount;

        /// <summary>Compact cell count badge text like "3c".</summary>
        public string CellCountBadge => $"{Prefab.Cells.Count}c";

        /// <summary>Open doorway count badge like "2d".</summary>
        public string DoorCountBadge => $"{Prefab.OpenPortalCount}d";

        /// <summary>Connection directions summary, e.g. "N+S" or "N+E+Down".</summary>
        public string DirectionSummary => Prefab.ConnectionDirectionSummary;

        /// <summary>True when this prefab is compatible with currently open portals.</summary>
        public bool IsCompatible { get; set; }

        /// <summary>Roof status for display: "Roofed", "Partial Roof", "No Roof".</summary>
        public string RoofStatus {
            get {
                if (Prefab.HasNoRoof) return "No Roof";
                if (Prefab.HasPartialRoof) return "Partial Roof";
                return "";
            }
        }

        public bool ShowRoofWarning => Prefab.HasNoRoof || Prefab.HasPartialRoof;

        /// <summary>Color for the roof status indicator.</summary>
        public IBrush RoofStatusBrush {
            get {
                if (Prefab.HasNoRoof) return new SolidColorBrush(Color.FromRgb(204, 102, 102));
                if (Prefab.HasPartialRoof) return new SolidColorBrush(Color.FromRgb(189, 151, 77));
                return new SolidColorBrush(Color.FromRgb(110, 192, 122));
            }
        }

        /// <summary>Color for the compatible indicator.</summary>
        public IBrush CompatibleBrush => IsCompatible
            ? new SolidColorBrush(Color.FromRgb(110, 192, 122))
            : new SolidColorBrush(Color.FromRgb(90, 74, 110));

        /// <summary>Style tag (e.g. "Sewer", "Cave", "Crypt").</summary>
        public string StyleTag => Prefab.Style;

        /// <summary>Starter-kit role label (Hallway, Corner, T-junction, …).</summary>
        public string KitRole { get; set; } = "";

        /// <summary>True when this piece is from a 1-cell starter kit.</summary>
        public bool IsKitPiece => !string.IsNullOrEmpty(KitRole);

        /// <summary>Readable doorway list, e.g. "Doors: North, South".</summary>
        public string DoorListText {
            get {
                var dirs = DungeonBuildKitBuilder.FriendlyDirections(Prefab.OpenFaceDirections);
                return string.IsNullOrEmpty(dirs)
                    ? $"{OpenPortals} door{(OpenPortals != 1 ? "s" : "")}"
                    : $"Doors: {dirs}";
            }
        }

        /// <summary>Usage count formatted: "17k" or "342".</summary>
        public string UsageText {
            get {
                var u = Prefab.UsageCount;
                return u >= 1000 ? $"{u / 1000}k" : u.ToString();
            }
        }

        [ObservableProperty]
        private Avalonia.Media.Imaging.WriteableBitmap? _thumbnail;

        [ObservableProperty]
        private bool _isFavorite;

        /// <summary>True when this is a user-created custom prefab (from "Save Selection as Prefab").</summary>
        public bool IsCustom => Prefab.Signature.StartsWith("custom_");

        public PrefabListEntry(DungeonPrefab prefab, string displayName) {
            Prefab = prefab;
            DisplayName = displayName;
        }
    }

    public class PortalListEntry {
        public int Index { get; }
        public ushort PolygonId { get; }
        public ushort OtherCellId { get; }
        public bool IsConnected { get; }
        public bool IsOpen => !IsConnected;
        public string DisplayText { get; }

        public ushort OwnerCellNum { get; }
        public ushort OwnerEnvId { get; }
        public ushort OwnerCellStruct { get; }
        public int CompatibleCount { get; set; }
        public string CompatibleHint => CompatibleCount > 0 ? $"{CompatibleCount} fits" : "";

        public PortalListEntry(int index, ushort polygonId, ushort otherCellId, bool isConnected,
            string? connectedRoomName = null,
            ushort ownerCellNum = 0, ushort ownerEnvId = 0, ushort ownerCellStruct = 0,
            int compatibleCount = 0) {
            Index = index;
            PolygonId = polygonId;
            OtherCellId = otherCellId;
            IsConnected = isConnected;
            OwnerCellNum = ownerCellNum;
            OwnerEnvId = ownerEnvId;
            OwnerCellStruct = ownerCellStruct;
            CompatibleCount = compatibleCount;
            if (isConnected) {
                var target = !string.IsNullOrEmpty(connectedRoomName) ? connectedRoomName : $"Room 0x{otherCellId:X4}";
                DisplayText = $"Door {index + 1} \u2192 {target}";
            }
            else {
                DisplayText = compatibleCount > 0
                    ? $"Door (open \u2014 {compatibleCount} compatible)"
                    : "Door (open)";
            }
        }
    }

    /// <summary>Display model for one world-database instance placement (generator/item/portal) in the Instance Placements panel.</summary>
    public class InstancePlacementItemViewModel : ObservableObject {
        public int Index { get; }
        public uint WeenieClassId { get; }
        public ushort CellNumber { get; }
        public string PositionText { get; }
        public string DisplayText => $"0x{WeenieClassId:X} in 0x{CellNumber:X4} @ {PositionText}";

        private bool _isSelected;
        public bool IsSelected {
            get => _isSelected;
            set => SetProperty(ref _isSelected, value);
        }

        public InstancePlacementItemViewModel(int index, uint weenieClassId, ushort cellNumber, string positionText) {
            Index = index;
            WeenieClassId = weenieClassId;
            CellNumber = cellNumber;
            PositionText = positionText;
        }
    }
}
