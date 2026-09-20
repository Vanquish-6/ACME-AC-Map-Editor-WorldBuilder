using Acme.Dat;

namespace WorldBuilder.Shared.Lib;

public static class LegacyDatDmLayoutValidator {
    public readonly record struct ValidationReport(
        int TrackedLayouts,
        int ReadableLayouts,
        int MissingLayouts,
        int BaseRefIssues,
        int OperationIssues,
        IReadOnlyList<string> Lines);

    public static ValidationReport ValidateSpecAgainstLocalDat(
        DefaultDatReaderWriter reader,
        LegacyDatDmLayoutSpec spec,
        IList<string>? warnings = null) {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(spec);

        var localDb = reader.Local()
            ?? throw new InvalidOperationException("Output writer has no local DAT database loaded.");

        var lines = new List<string>();
        int readableLayouts = 0;
        int missingLayouts = 0;
        int baseRefIssues = 0;
        int operationIssues = 0;
        var layoutCache = new Dictionary<uint, LayoutDesc>();

        lines.Add($"Validate layout spec: {spec.Name ?? "unnamed"} (v{spec.Version})");
        lines.Add("Tracked layouts:");
        foreach (var tracked in spec.Layouts) {
            uint layoutId = tracked.LayoutIdValue;
            if (TryLoadLayout(localDb, layoutCache, layoutId, out LayoutDesc? layout)) {
                readableLayouts++;
                lines.Add($"  [ok] 0x{layoutId:X8} {tracked.Role}");
                baseRefIssues += ValidateBaseReferences(localDb, layoutCache, layout!, lines, warnings);
            }
            else {
                missingLayouts++;
                string message = $"Layout spec: missing layout 0x{layoutId:X8} ({tracked.Role}).";
                warnings?.Add(message);
                lines.Add($"  [missing] 0x{layoutId:X8} {tracked.Role}");
            }
        }

        lines.Add("Operations:");
        foreach (var op in spec.Operations) {
            if (op.Disabled) {
                lines.Add($"  [disabled] {op.Type} on {op.LayoutId}");
                continue;
            }

            if (!TryLoadLayout(localDb, layoutCache, op.LayoutIdValue, out LayoutDesc? layout)) {
                operationIssues++;
                lines.Add($"  [missing-layout] {op.Type} on {op.LayoutId}");
                continue;
            }

            if (!TryFindElement(layout!.Elements, op.ElementIdValue, out _)) {
                operationIssues++;
                string message =
                    $"Layout spec: operation {op.Type} targets missing element 0x{op.ElementIdValue:X8} in 0x{op.LayoutIdValue:X8}.";
                warnings?.Add(message);
                lines.Add($"  [missing-element] {op.Type} on {op.LayoutId} element {op.ElementId}");
            }
            else {
                lines.Add($"  [ok] {op.Type} on {op.LayoutId} element {op.ElementId}");
            }
        }

        return new ValidationReport(
            spec.Layouts.Count,
            readableLayouts,
            missingLayouts,
            baseRefIssues,
            operationIssues,
            lines);
    }

    private static int ValidateBaseReferences(
        IDatReaderWriter localDb,
        Dictionary<uint, LayoutDesc> layoutCache,
        LayoutDesc layout,
        List<string> lines,
        IList<string>? warnings) {
        int issues = 0;
        if (layout.Elements == null) {
            return 0;
        }

        ValidateElementTree(localDb, layoutCache, layout.Id, layout.Elements, ref issues, lines, warnings);
        return issues;
    }

    private static void ValidateElementTree(
        IDatReaderWriter localDb,
        Dictionary<uint, LayoutDesc> layoutCache,
        uint ownerLayoutId,
        Dictionary<uint, ElementDesc> elements,
        ref int issues,
        List<string> lines,
        IList<string>? warnings) {
        foreach (var (elementId, element) in elements) {
            if (element == null) {
                continue;
            }

            if (element.BaseLayoutId != 0) {
                if (!TryLoadLayout(localDb, layoutCache, element.BaseLayoutId, out LayoutDesc? baseLayout)) {
                    issues++;
                    string message =
                        $"Layout 0x{ownerLayoutId:X8} element 0x{elementId:X8} references missing base layout 0x{element.BaseLayoutId:X8}.";
                    warnings?.Add(message);
                    lines.Add($"  [bad-base-layout] {message}");
                }
                else if (!TryFindElement(baseLayout!.Elements, element.BaseElement, out _)) {
                    issues++;
                    string message =
                        $"Layout 0x{ownerLayoutId:X8} element 0x{elementId:X8} references missing base element 0x{element.BaseElement:X8} in 0x{element.BaseLayoutId:X8}.";
                    warnings?.Add(message);
                    lines.Add($"  [bad-base-element] {message}");
                }
            }

            if (element.Children != null) {
                ValidateElementTree(localDb, layoutCache, ownerLayoutId, element.Children, ref issues, lines, warnings);
            }
        }
    }

    private static bool TryLoadLayout(
        IDatReaderWriter localDb,
        Dictionary<uint, LayoutDesc> layoutCache,
        uint layoutId,
        out LayoutDesc? layout) {
        if (layoutCache.TryGetValue(layoutId, out layout)) {
            return true;
        }

        if (!localDb.TryGet<LayoutDesc>(layoutId, out layout) || layout == null) {
            layout = null;
            return false;
        }

        layoutCache[layoutId] = layout;
        return true;
    }

    private static bool TryFindElement(
        Dictionary<uint, ElementDesc>? elements,
        uint elementId,
        out ElementDesc? found) {
        found = null;
        if (elements == null) {
            return false;
        }

        if (elements.TryGetValue(elementId, out found) && found != null) {
            return true;
        }

        foreach (var child in elements.Values) {
            if (child?.Children == null) {
                continue;
            }

            if (TryFindElement(child.Children, elementId, out found)) {
                return true;
            }
        }

        found = null;
        return false;
    }
}
