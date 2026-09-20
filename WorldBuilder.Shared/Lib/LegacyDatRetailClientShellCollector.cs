using Acme.Dat;
using System.Reflection;

namespace WorldBuilder.Shared.Lib;

public static class LegacyDatRetailClientShellCollector {
    public const uint CharGenId = 0x0E000002u;

    public static HashSet<uint> CollectPortalIds(string retailSeedDirectory) {
        ArgumentException.ThrowIfNullOrWhiteSpace(retailSeedDirectory);
        using var seed = new DefaultDatReaderWriter(retailSeedDirectory, DatAccessType.Read);
        return CollectPortalIds(seed);
    }

    public static HashSet<uint> CollectPortalIds(IDatReaderWriter portal) {
        ArgumentNullException.ThrowIfNull(portal);

        var keep = new HashSet<uint>();
        var pending = new Queue<uint>();

        void Enqueue(uint id) {
            if (id == 0 || !keep.Add(id)) {
                return;
            }

            pending.Enqueue(id);
        }

        Enqueue(CharGenId);
        if (portal.TryGet(CharGenId, out CharGen? charGen) && charGen != null) {
            foreach (var group in charGen.HeritageGroups.Values) {
                Enqueue(group.SetupId);
                Enqueue(group.EnvironmentSetupId);
            }
        }

        while (pending.Count > 0) {
            LegacyDatRetailPortalReferenceExpander.ExpandPortalReference(portal, pending.Dequeue(), Enqueue);
        }

        return keep;
    }
}

public static class LegacyDatRetailUiShellCollector {
    public static HashSet<uint> CollectPortalIds(string retailSeedDirectory) {
        using var seed = new DefaultDatReaderWriter(retailSeedDirectory, DatAccessType.Read);
        return CollectPortalIds(seed);
    }

    public static HashSet<uint> CollectPortalIds(IDatReaderWriter dats) {
        var keep = new HashSet<uint>();
        var pending = new Queue<uint>();

        void Enqueue(uint id) {
            byte band = (byte)(id >> 24);
            if (id == 0
                || band is not (0x04 or 0x05 or 0x06 or 0x08)
                || !dats.ContainsFile(DatArchive.Portal, id)
                || !keep.Add(id)) {
                return;
            }

            pending.Enqueue(id);
        }

        foreach (uint layoutId in dats.GetAllIdsOfType<LayoutDesc>()) {
            if (!dats.TryGet(layoutId, out LayoutDesc? layout) || layout == null) {
                continue;
            }

            foreach (uint id in LegacyDatRetailPortalReferenceExpander.CollectNestedPortalIds(layout)) {
                Enqueue(id);
            }
        }

        while (pending.Count > 0) {
            LegacyDatRetailPortalReferenceExpander.ExpandPortalReference(dats, pending.Dequeue(), Enqueue);
        }

        return keep;
    }

    public static HashSet<uint> CollectPortalIds(IDatReaderWriter local, IDatReaderWriter portal) {
        _ = local;
        return CollectPortalIds(portal);
    }
}

public static class LegacyDatRetailHighresShellCollector {
    public static HashSet<uint> CollectPortalIds(string retailSeedDirectory) {
        using var seed = new DefaultDatReaderWriter(retailSeedDirectory, DatAccessType.Read);
        return CollectPortalIds(seed);
    }

    public static HashSet<uint> CollectPortalIds(IDatReaderWriter dats) {
        var keep = new HashSet<uint>();
        foreach (uint id in dats.ListFileIds(DatArchive.Highres)) {
            if (id != DatRecordTable.IterationId) {
                keep.Add(id);
            }
        }

        var pending = new Queue<uint>(keep);
        void Enqueue(uint id) {
            if (id == 0 || !dats.ContainsFile(DatArchive.Portal, id) || !keep.Add(id)) {
                return;
            }

            pending.Enqueue(id);
        }

        while (pending.Count > 0) {
            LegacyDatRetailPortalReferenceExpander.ExpandPortalReference(dats, pending.Dequeue(), Enqueue);
        }

        return keep;
    }

    public static HashSet<uint> CollectPortalIds(IDatReaderWriter highres, IDatReaderWriter portal) {
        _ = portal;
        return CollectPortalIds(highres);
    }
}

public static class LegacyDatRetailAppearanceShellCollector {
    public static HashSet<uint> CollectPortalIds(string retailSeedDirectory) {
        using var seed = new DefaultDatReaderWriter(retailSeedDirectory, DatAccessType.Read);
        return CollectPortalIds(seed);
    }

    public static HashSet<uint> CollectPortalIds(IDatReaderWriter dats) {
        var keep = new HashSet<uint>();
        var pending = new Queue<uint>();
        void Enqueue(uint id) {
            if (id == 0 || !keep.Add(id)) {
                return;
            }

            pending.Enqueue(id);
        }

        foreach (uint id in dats.GetAllIdsOfType<ClothingTable>()) Enqueue(id);
        foreach (uint id in dats.GetAllIdsOfType<PalSet>()) Enqueue(id);
        while (pending.Count > 0) {
            LegacyDatRetailPortalReferenceExpander.ExpandPortalReference(dats, pending.Dequeue(), Enqueue);
        }

        return keep;
    }
}

internal static class LegacyDatRetailPortalReferenceExpander {
    internal static void ExpandPortalReference(IDatReaderWriter dats, uint id, Action<uint> enqueue) {
        if (TryGetPortalObject(dats, id, out object? portalObject) && portalObject != null) {
            foreach (uint nestedId in CollectNestedPortalIds(portalObject)) {
                enqueue(nestedId);
            }
            return;
        }

        if (((id >> 24) is 0x03 or 0x09)
            && dats.TryGetFileBytes(DatArchive.Portal, id, out var bytes)
            && bytes != null) {
            ScanRawPortalIds(bytes, enqueue);
        }
    }

    private static bool TryGetPortalObject(IDatReaderWriter dats, uint id, out object? value) {
        value = null;
        switch (id >> 24) {
            case 0x01 when dats.TryGet(id, out GfxObj? gfxObj) && gfxObj != null:
                value = gfxObj; return true;
            case 0x02 when dats.TryGet(id, out Setup? setup) && setup != null:
                value = setup; return true;
            case 0x04 when dats.TryGet(id, out Palette? palette) && palette != null:
                value = palette; return true;
            case 0x05 when dats.TryGet(id, out SurfaceTexture? surfaceTexture) && surfaceTexture != null:
                value = surfaceTexture; return true;
            case 0x06 when dats.TryGet(id, out RenderSurface? renderSurface) && renderSurface != null:
                value = renderSurface; return true;
            case 0x08 when dats.TryGet(id, out Surface? surface) && surface != null:
                value = surface; return true;
            case 0x0E when id == LegacyDatRetailClientShellCollector.CharGenId
                && dats.TryGet(id, out CharGen? charGen) && charGen != null:
                value = charGen; return true;
            case 0x0F when dats.TryGet(id, out PalSet? palSet) && palSet != null:
                value = palSet; return true;
            case 0x10 when dats.TryGet(id, out ClothingTable? clothingTable) && clothingTable != null:
                value = clothingTable; return true;
            default:
                return false;
        }
    }

    internal static HashSet<uint> CollectNestedPortalIds(object root) {
        var found = new HashSet<uint>();
        CollectNestedPortalIds(root, found);
        return found;
    }

    private static void CollectNestedPortalIds(object? value, HashSet<uint> found) {
        if (value == null) {
            return;
        }

        switch (value) {
            case uint id when IsPortalId(id):
                found.Add(id);
                return;
            case int i when i > 0:
                uint asUint = (uint)i;
                if (IsPortalId(asUint)) {
                    found.Add(asUint);
                }
                return;
            case string:
                return;
        }

        Type type = value.GetType();
        if (type.IsPrimitive || type.IsEnum) {
            return;
        }

        if (value is System.Collections.IEnumerable enumerable) {
            foreach (object? item in enumerable) {
                CollectNestedPortalIds(item, found);
            }
            return;
        }

        foreach (FieldInfo field in type.GetFields(BindingFlags.Public | BindingFlags.Instance)) {
            CollectNestedPortalIds(field.GetValue(value), found);
        }

        foreach (PropertyInfo property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance)) {
            if (!property.CanRead || property.GetIndexParameters().Length > 0) {
                continue;
            }

            CollectNestedPortalIds(property.GetValue(value), found);
        }
    }

    static void ScanRawPortalIds(byte[] bytes, Action<uint> enqueue) {
        for (int i = 0; i + 4 <= bytes.Length; i += 4) {
            uint id = BitConverter.ToUInt32(bytes, i);
            if (IsPortalId(id)) {
                enqueue(id);
            }
        }
    }

    internal static bool IsPortalId(uint id) =>
        id >= 0x01000000u && (id >> 24) < 0x20;
}
