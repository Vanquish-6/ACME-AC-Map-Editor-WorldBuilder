using Avalonia.Input;

namespace WorldBuilder.Lib {
    /// <summary>Drag-and-drop format names for palette items placed into a 3D view.</summary>
    public static class EditorDragFormats {
        public const string DungeonPrefab = "AcmeDungeonPrefab";
        public const string ObjectId = "AcmeObjectId";
        public const string ObjectIsSetup = "AcmeObjectIsSetup";
        public const string ObjectWeenieClassId = "AcmeObjectWcid";
        public const string DockablePanelId = "DockablePanelId";

        public static DataObject ForPrefab(string signature) {
            var data = new DataObject();
            data.Set(DungeonPrefab, signature);
            return data;
        }

        public static DataObject ForObject(uint id, bool isSetup, uint? weenieClassId = null) {
            var data = new DataObject();
            data.Set(ObjectId, id.ToString());
            data.Set(ObjectIsSetup, isSetup ? "1" : "0");
            if (weenieClassId.HasValue)
                data.Set(ObjectWeenieClassId, weenieClassId.Value.ToString());
            return data;
        }

        public static bool TryGetPrefab(IDataObject data, out string signature) {
            signature = data.Get(DungeonPrefab) as string ?? "";
            return !string.IsNullOrEmpty(signature);
        }

        public static bool TryGetObject(IDataObject data, out uint id, out bool isSetup, out uint? wcid) {
            id = 0;
            isSetup = false;
            wcid = null;
            if (data.Get(ObjectId) is not string idText || !uint.TryParse(idText, out id))
                return false;
            isSetup = data.Get(ObjectIsSetup) as string == "1";
            if (data.Get(ObjectWeenieClassId) is string wcidText && uint.TryParse(wcidText, out var parsed))
                wcid = parsed;
            return true;
        }
    }
}
