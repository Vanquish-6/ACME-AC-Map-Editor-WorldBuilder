using System.Text;
using Acme.Dat;

namespace WorldBuilder.Shared.Lib;

public static class LegacyDatDmLayoutExporter {
    public readonly record struct LayoutPatchReport(
        string SpecName,
        int ValidatedLayouts,
        int MissingLayouts,
        int PatchedLayouts,
        int PatchedElements,
        int SkippedOperations,
        int BlockedFeatures);

    public static LayoutPatchReport ApplyLayoutSpec(
        DefaultDatReaderWriter outputWriter,
        List<string>? warnings = null,
        string? reportPath = null,
        string specName = LegacyDatDmLayoutSpecNames.CharGenWizard,
        bool persistChanges = true,
        IDictionary<uint, LayoutDesc>? patchedLayoutSnapshots = null,
        bool appendReport = false) {
        ArgumentNullException.ThrowIfNull(outputWriter);

        var localDb = outputWriter.Local()
            ?? throw new InvalidOperationException("Output writer has no local DAT database loaded.");
        var spec = LegacyDatDmLayoutSpec.LoadEmbeddedSpec(specName);
        var validation = LegacyDatDmLayoutValidator.ValidateSpecAgainstLocalDat(outputWriter, spec, warnings);
        var report = new StringBuilder();
        report.AppendLine($"DM layout spec: {spec.Name ?? specName} (v{spec.Version})");
        if (!string.IsNullOrWhiteSpace(spec.Description)) {
            report.AppendLine(spec.Description);
        }

        report.AppendLine();
        foreach (string line in validation.Lines) {
            report.AppendLine(line);
        }

        int validatedLayouts = validation.ReadableLayouts;
        int missingLayouts = validation.MissingLayouts;
        var workingLayouts = new Dictionary<uint, LayoutDesc>();
        var changedLayouts = new HashSet<uint>();
        int patchedElements = 0;
        int skippedOperations = 0;

        if (validation.BaseRefIssues > 0 || validation.OperationIssues > 0) {
            report.AppendLine();
            report.AppendLine(
                $"Validation summary: base refs={validation.BaseRefIssues}, operation targets={validation.OperationIssues}");
        }

        report.AppendLine();
        report.AppendLine("Apply operations:");
        if (spec.Operations.Count == 0) {
            report.AppendLine("  none (spec is report-first; no automatic layout edits are enabled yet)");
        }

        foreach (var op in spec.Operations) {
            if (op.Disabled) {
                report.AppendLine($"  [disabled] {op.Type} on {op.LayoutId}");
                continue;
            }

            if (!TryApplyOperation(outputWriter, localDb, workingLayouts, changedLayouts, op, out string detail)) {
                skippedOperations++;
                string message = $"DM layout spec {specName}: skipped {op.Type} on {op.LayoutId}: {detail}";
                warnings?.Add(message);
                report.AppendLine($"  [skip] {op.Type} on {op.LayoutId} :: {detail}");
                continue;
            }

            patchedElements++;
            report.AppendLine($"  [ok] {op.Type} on {op.LayoutId} :: {detail}");
        }

        foreach (uint layoutId in changedLayouts.OrderBy(id => id)) {
            if (!workingLayouts.TryGetValue(layoutId, out var layout)) {
                continue;
            }

            layout.Id = layoutId;
            if (patchedLayoutSnapshots != null) {
                patchedLayoutSnapshots[layoutId] = layout;
            }
        }

        int patchedLayouts = 0;
        if (!persistChanges) {
            patchedLayouts = changedLayouts.Count;
            report.AppendLine();
            report.AppendLine("Save:");
            report.AppendLine("  preview only; modified layouts were not written to disk");
        }
        else {
            foreach (uint layoutId in changedLayouts.OrderBy(id => id)) {
                if (!workingLayouts.TryGetValue(layoutId, out var layout)) {
                    continue;
                }

                layout.Id = layoutId;
                if (outputWriter.TrySave(layout)) {
                    patchedLayouts++;
                    continue;
                }

                string message = $"DM layout spec {specName}: failed to save layout 0x{layoutId:X8}.";
                warnings?.Add(message);
                report.AppendLine($"  [save-failed] 0x{layoutId:X8}");
            }
        }

        report.AppendLine();
        report.AppendLine("Blocked features:");
        if (spec.BlockedFeatures.Count == 0) {
            report.AppendLine("  none");
        }
        else {
            foreach (var blocked in spec.BlockedFeatures) {
                report.AppendLine($"  - {blocked.Area}: {blocked.Reason} ({blocked.Source ?? "source n/a"})");
            }
        }

        if (!string.IsNullOrWhiteSpace(reportPath)) {
            if (appendReport && File.Exists(reportPath)) {
                File.AppendAllText(
                    reportPath,
                    $"{System.Environment.NewLine}{System.Environment.NewLine}{report}");
            }
            else {
                File.WriteAllText(reportPath, report.ToString());
            }
        }

        return new LayoutPatchReport(
            spec.Name ?? specName,
            validatedLayouts,
            missingLayouts,
            patchedLayouts,
            patchedElements,
            skippedOperations,
            spec.BlockedFeatures.Count);
    }

    private static bool TryApplyOperation(
        DefaultDatReaderWriter outputWriter,
        IDatReaderWriter localDb,
        Dictionary<uint, LayoutDesc> workingLayouts,
        HashSet<uint> changedLayouts,
        LegacyDatDmLayoutOperation op,
        out string detail) {
        detail = string.Empty;

        if (!TryLoadLayout(outputWriter, localDb, workingLayouts, op.LayoutIdValue, out var layout, out string? loadError)) {
            detail = loadError ?? "layout not found";
            return false;
        }

        if (layout?.Elements == null || !TryFindElement(layout.Elements, op.ElementIdValue, out var element) || element == null) {
            detail = $"element 0x{op.ElementIdValue:X8} not found";
            return false;
        }

        bool changed = op.Type switch {
            "set_bounds" => TrySetBounds(element, op, out detail),
            "set_read_order" => TrySetReadOrder(element, op, out detail),
            "set_base_ref" => TrySetBaseRef(element, op, out detail),
            _ => Unsupported(op, out detail),
        };

        if (!changed) {
            return false;
        }

        changedLayouts.Add(op.LayoutIdValue);
        return true;
    }

    private static bool TryLoadLayout(
        DefaultDatReaderWriter outputWriter,
        IDatReaderWriter localDb,
        Dictionary<uint, LayoutDesc> workingLayouts,
        uint layoutId,
        out LayoutDesc? layout,
        out string? error) {
        if (workingLayouts.TryGetValue(layoutId, out layout)) {
            error = null;
            return true;
        }

        if (!localDb.TryGet<LayoutDesc>(layoutId, out var source) || source == null) {
            layout = null;
            error = "layout is not readable from client_local_English.dat";
            return false;
        }

        layout = LayoutDescBinary.Clone(source, layoutId);
        workingLayouts[layoutId] = layout;
        error = null;
        return true;
    }

    private static bool TrySetBounds(ElementDesc element, LegacyDatDmLayoutOperation op, out string detail) {
        bool changed = false;
        if (op.X is uint x && element.X != x) {
            element.X = x;
            changed = true;
        }

        if (op.Y is uint y && element.Y != y) {
            element.Y = y;
            changed = true;
        }

        if (op.Width is uint width && element.Width != width) {
            element.Width = width;
            changed = true;
        }

        if (op.Height is uint height && element.Height != height) {
            element.Height = height;
            changed = true;
        }

        if (!changed) {
            detail = "bounds already match";
            return false;
        }

        EnsureGeometryIncorporation(element);
        detail = $"element 0x{op.ElementIdValue:X8} bounds updated";
        return true;
    }

    private static bool TrySetReadOrder(ElementDesc element, LegacyDatDmLayoutOperation op, out string detail) {
        if (op.ReadOrder is not uint readOrder) {
            detail = "read_order operation requires ReadOrder";
            return false;
        }

        if (element.ReadOrder == readOrder) {
            detail = "read order already matches";
            return false;
        }

        element.ReadOrder = readOrder;
        detail = $"element 0x{op.ElementIdValue:X8} read order -> {readOrder}";
        return true;
    }

    private static bool TrySetBaseRef(ElementDesc element, LegacyDatDmLayoutOperation op, out string detail) {
        if (op.BaseLayoutIdValue is not uint baseLayoutId || op.BaseElementIdValue is not uint baseElementId) {
            detail = "set_base_ref requires BaseLayoutId and BaseElementId";
            return false;
        }

        if (element.BaseLayoutId == baseLayoutId && element.BaseElement == baseElementId) {
            detail = "base reference already matches";
            return false;
        }

        element.BaseLayoutId = baseLayoutId;
        element.BaseElement = baseElementId;
        detail = $"element 0x{op.ElementIdValue:X8} base -> 0x{baseLayoutId:X8}/0x{baseElementId:X8}";
        return true;
    }

    private static bool Unsupported(LegacyDatDmLayoutOperation op, out string detail) {
        detail = $"unsupported operation type '{op.Type}'";
        return false;
    }

    private static bool TryFindElement(
        Dictionary<uint, ElementDesc> elements,
        uint elementId,
        out ElementDesc? found) {
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

    private static void EnsureGeometryIncorporation(ElementDesc element) {
        element.StateDesc ??= new StateDesc();
        element.StateDesc.IncorporationFlags |= IncorporationFlags.X
                                               | IncorporationFlags.Y
                                               | IncorporationFlags.Width
                                               | IncorporationFlags.Height
                                               | IncorporationFlags.ZLevel;
    }
}
