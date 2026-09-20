using Acme.Dat;
using System.Buffers.Binary;

namespace WorldBuilder.Shared.Lib;

/// <summary>
/// Repairs retail DAT container metadata required by the AC client.
/// See acclient CThreadsafeDiskController::InitFile (LoadIterationList on 0xFFFF0001, version 1),
/// CLBlockAllocator::OpenDataFile (header 0x140 magic 0x5442, transaction 0x100),
/// and Chorizite DatHeader.WriteEmptyTransaction.
/// </summary>
public static class LegacyDatClientExportFixer {
    private const int HeaderOffset = 0x140;
    private const int FileSizeFieldOffset = HeaderOffset + 8;
    private const int BtreeRootFieldOffset = HeaderOffset + 32;
    private const int TransactionOffset = 0x100;
    private const int TransactionLength = 0x40;
    private const int ExpectedMagic = 0x5442; // 21570

    /// <summary>
    /// Retail cell/portal DATs that WorldBuilder opens for writing during conversion.
    /// </summary>
    internal static readonly string[] ClientCatalogDatFiles = {
        "client_cell_1.dat",
        "client_portal.dat",
    };

    /// <summary>
    /// Retail companion DATs copied verbatim from seed and never written by the converter.
    /// The AC client is sensitive to header/free-list patches on these files.
    /// </summary>
    internal static readonly string[] UntouchedRetailCompanionDatFiles = {
        "client_local_English.dat",
        "client_highres.dat",
    };

    /// <summary>
    /// Deprecated SlimMerge archive name kept for audit compatibility only.
    /// </summary>
    [Obsolete("Single converted client_portal.dat is the supported deploy model.")]
    public const string ConvertedPortalArchiveFileName = "client_portal_converted.dat";

    private static readonly string[] ChoriziteCatalogDatFiles = ClientCatalogDatFiles;

    // Chorizite DatHeader.WriteEmptyTransaction() serializes a no-op journal entry.
    private static readonly byte[] EmptyTransactionPrefix = [0x00, 0x50, 0x4C, 0x00];

    public static void FinalizeForClient(
        string retailSeedDirectory,
        string outputDirectory,
        RetailDatIterations iterations,
        int portalIterationOverride,
        Action<string>? onProgress = null,
        LegacyDatExportMode exportMode = LegacyDatExportMode.FullMerge) =>
        FinalizeForClient(
            retailSeedDirectory,
            outputDirectory,
            iterations,
            portalIterationOverride,
            onProgress,
            LegacyDatExportPolicy.FromLegacyMode(exportMode));

    public static void FinalizeForClient(
        string retailSeedDirectory,
        string outputDirectory,
        RetailDatIterations iterations,
        int portalIterationOverride,
        Action<string>? onProgress,
        LegacyDatExportPolicy policy) {
        ArgumentException.ThrowIfNullOrWhiteSpace(retailSeedDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);
        ArgumentNullException.ThrowIfNull(policy);

        foreach (string datFile in ChoriziteCatalogDatFiles) {
            EnsureIterationObjectFromSeed(retailSeedDirectory, outputDirectory, datFile, onProgress);
        }

        onProgress?.Invoke("Syncing DAT iteration metadata...");
        RetailDatIterations.ApplyToOutputDirectory(outputDirectory, iterations, portalIterationOverride, onProgress);

        // Required after slim overlay + prune: mixed Chorizite writes and retail seed copies leave
        // inconsistent per-entry B-tree metadata (retail client init reads portal.dat immediately).
        LegacyDatPortalBootstrap.RepairPortalFileEntryMetadata(outputDirectory, retailSeedDirectory, onProgress);

        // RepairPortal rewrites via Chorizite and can reintroduce 0xCDCDCDCD leaf sentinels (client init crash).
        FixCatalogLeafSentinels(outputDirectory, onProgress);

        foreach (string datFile in ClientCatalogDatFiles) {
            string outputPath = Path.Combine(outputDirectory, datFile);
            if (!File.Exists(outputPath)) {
                continue;
            }

            onProgress?.Invoke($"Finalizing {datFile} for client compatibility...");
            EnsureRetailHeaderMagic(outputPath);

            // Do not truncate slim overlay exports: btree extent walk can clip live payloads
            // (see dmsalive: 4010 portal refs past EOF → server/client AV on read).
            SyncHeaderFileSize(outputPath);

            DatExportFixer.PatchFreeBlocksBeforeExport(outputPath);
        }

        RestoreUntouchedRetailSeedFiles(retailSeedDirectory, outputDirectory, policy, onProgress);
    }

    /// <summary>
    /// After DM StringTable patches, the retail client still mmap's local.dat at init.
    /// </summary>
    public static void FinalizePatchedLocalDat(string outputDirectory, Action<string>? onProgress = null) {
        string localPath = Path.Combine(outputDirectory, "client_local_English.dat");
        if (!File.Exists(localPath)) {
            return;
        }

        onProgress?.Invoke("Finalizing patched client_local_English.dat for client init...");
        DatExportFixer.FixLeafBranchSentinels(localPath);
        EnsureRetailHeaderMagic(localPath);
        EnsureCatalogPayloadsFitFile(localPath);
        SyncHeaderFileSize(localPath);
        DatExportFixer.PatchFreeBlocksBeforeExport(localPath);
        WriteNativeNoOpTransaction(localPath);
    }

    /// <summary>
    /// String/layout patches can leave catalog entries past the logical file size. The client walks
    /// the B-tree at init and reads payloads by offset+size (CLBlockAllocator::OpenDataFile).
    /// </summary>
    internal static void EnsureCatalogPayloadsFitFile(string datPath) {
        if (!File.Exists(datPath)) {
            return;
        }

        using var reader = new DefaultDatReaderWriter(
            Path.GetDirectoryName(datPath)!,
            DatAccessType.ReadWrite,
            FileCachingStrategy.Never,
            IndexCachingStrategy.OnDemand);

        IDatReaderWriter? database = Path.GetFileName(datPath) switch {
            "client_portal.dat" => reader.Portal(),
            "client_cell_1.dat" => reader.Cell(),
            "client_local_English.dat" => reader.Local(),
            _ => null,
        };

        if (database == null) {
            return;
        }

        long requiredLength = 0;
        foreach (var file in database.Catalog().Tree) {
            if (file.Offset > 0 && file.Size > 0) {
                requiredLength = Math.Max(requiredLength, file.Offset + file.Size);
            }
        }

        reader.Dispose();

        if (requiredLength <= 0) {
            return;
        }

        using var fs = new FileStream(datPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        if (fs.Length < requiredLength) {
            fs.SetLength(requiredLength);
        }
    }

    /// <summary>
    /// Resets free-block headers before Chorizite opens a catalog for write. Prevents reusing
    /// retail seed free-chain pointers after overlay exports.
    /// </summary>
    internal static void PatchCatalogFilesForWriting(string outputDirectory, params string[] datFiles) {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);
        foreach (string datFile in datFiles) {
            string path = Path.Combine(outputDirectory, datFile);
            if (!File.Exists(path)) {
                continue;
            }

            ClearEmptyTransaction(path);
            DatExportFixer.PatchFreeBlocksBeforeExport(path);
        }
    }

    internal static void FixCatalogLeafSentinels(string outputDirectory, Action<string>? onProgress = null) {
        foreach (string datFile in ClientCatalogDatFiles) {
            string path = Path.Combine(outputDirectory, datFile);
            if (!File.Exists(path)) {
                continue;
            }

            onProgress?.Invoke($"Fixing leaf sentinels in {datFile}...");
            DatExportFixer.FixLeafBranchSentinels(path);
        }
    }

    /// <summary>
    /// Last pass after all Chorizite writes (portal repair, local string patches).
    /// </summary>
    public static void CompleteClientExport(
        string outputDirectory,
        LegacyDatExportPolicy policy,
        Action<string>? onProgress = null,
        string? retailSeedDirectory = null) {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);
        ArgumentNullException.ThrowIfNull(policy);

        FixCatalogLeafSentinels(outputDirectory, onProgress);

        if (LegacyDatMergeRuleResolver.UsesFullRetailPortalSeed(policy)) {
            LegacyDatPortalCatalogDeduplicator.DeduplicatePortal(
                outputDirectory,
                retailSeedDirectory,
                onProgress);
        }

        if (LegacyDatMergeRuleResolver.ShouldApplyDmPortalStringPatches(policy)
            || LegacyDatMergeRuleResolver.ShouldPatchConnectionScreenStrings(policy)
            || LegacyDatMergeRuleResolver.ShouldPatchDmInGameStrings(policy)
            || LegacyDatMergeRuleResolver.ShouldPatchCharGenLayoutShape(policy)
            || LegacyDatMergeRuleResolver.ShouldPatchInGameLayoutShape(policy)) {
            FinalizePatchedLocalDat(outputDirectory, onProgress);
        }

        foreach (string datFile in ClientCatalogDatFiles) {
            string path = Path.Combine(outputDirectory, datFile);
            if (!File.Exists(path)) {
                continue;
            }

            EnsureRetailHeaderMagic(path);
            EnsureCatalogPayloadsFitFile(path);
            SyncHeaderFileSize(path);
        }

        string localPath = Path.Combine(outputDirectory, "client_local_English.dat");
        if (File.Exists(localPath)) {
            EnsureCatalogPayloadsFitFile(localPath);
            SyncHeaderFileSize(localPath);
        }

        // Chorizite closes with a 4-byte WriteEmptyTransaction prefix; the retail client deserializes
        // the full journal and may replay retail AddObject ops. Always finish with a native no-op journal.
        onProgress?.Invoke("Writing native no-op transaction journals...");
        RestoreClientTransactionJournals(retailSeedDirectory ?? outputDirectory, outputDirectory, policy, onProgress);
    }

    /// <summary>
    /// Chorizite DatReaderWriter closes with WriteEmptyTransaction (4-byte prefix + zeros). The retail
    /// client deserializes the full 64-byte journal via SB_TypeAlloc and rejects blobs missing magic 19536.
    /// Converted portal/cell catalogs must use a native no-op journal: retail seed journals contain
    /// AddObject recovery ops for the retail B-tree layout and AV when replayed after BTree__LoadTree.
    /// </summary>
    internal static void RestoreClientTransactionJournals(
        string retailSeedDirectory,
        string outputDirectory,
        Action<string>? onProgress = null) =>
        RestoreClientTransactionJournals(
            retailSeedDirectory,
            outputDirectory,
            LegacyDatExportPolicy.FromPreset(LegacyDatExportPreset.HalfDm),
            onProgress);

    internal static void RestoreClientTransactionJournals(
        string retailSeedDirectory,
        string outputDirectory,
        LegacyDatExportPolicy policy,
        Action<string>? onProgress = null) {
        ArgumentException.ThrowIfNullOrWhiteSpace(retailSeedDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);
        ArgumentNullException.ThrowIfNull(policy);

        string[] datFiles = [
            "client_portal.dat",
            "client_cell_1.dat",
            "client_local_English.dat",
            "client_highres.dat",
        ];

        bool localWasPatched = LegacyDatMergeRuleResolver.ShouldApplyDmPortalStringPatches(policy)
            || LegacyDatMergeRuleResolver.ShouldPatchConnectionScreenStrings(policy)
            || LegacyDatMergeRuleResolver.ShouldPatchDmInGameStrings(policy)
            || LegacyDatMergeRuleResolver.ShouldPatchCharGenLayoutShape(policy)
            || LegacyDatMergeRuleResolver.ShouldPatchInGameLayoutShape(policy);

        foreach (string datFile in datFiles) {
            string outputPath = Path.Combine(outputDirectory, datFile);
            if (!File.Exists(outputPath)) {
                continue;
            }

            // Always write the native no-op journal for written files.
            onProgress?.Invoke($"Writing native no-op transaction journal in {datFile}...");
            WriteNativeNoOpTransaction(outputPath);
        }
    }

    /// <summary>
    /// Serialized DiskTransactInfo type 0 (no pending ops) with magic 19536. Matches
    /// CLBlockAllocator::ClearTransaction / DiskTransactInfo__DiskTransactInfo.
    /// </summary>
    internal static void WriteNativeNoOpTransaction(string datPath) {
        if (!File.Exists(datPath)) {
            return;
        }

        using var fs = new FileStream(datPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        if (fs.Length < TransactionOffset + TransactionLength) {
            return;
        }

        fs.Position = TransactionOffset;
        fs.Write(NativeNoOpTransaction, 0, NativeNoOpTransaction.Length);
        fs.Flush();
    }

    // SB_TypeAlloc header + magic 0x4C50 (19536). Retail AddObject journals set byte 7 to 0x01.
    private static readonly byte[] NativeNoOpTransaction = CreateNativeNoOpTransaction();

    static byte[] CreateNativeNoOpTransaction() {
        byte[] transaction = new byte[TransactionLength];
        transaction[0] = 0x00;
        transaction[1] = 0x50;
        transaction[2] = 0x4C;
        return transaction;
    }

    /// <summary>
    /// Deprecated launch swap that archived the converted portal and restored retail seed portal.
    /// </summary>
    [Obsolete("Use SingleConvertedCatalog deploy policy instead.")]
    public static void ApplySlimMergeClientLaunchPortal(
        string retailSeedDirectory,
        string outputDirectory,
        Action<string>? onProgress = null) {
        ArgumentException.ThrowIfNullOrWhiteSpace(retailSeedDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);

        onProgress?.Invoke(
            "WARN: ApplySlimMergeClientLaunchPortal is deprecated; exports should ship a single client_portal.dat.");
    }

    internal static void RestoreUntouchedRetailSeedFiles(
        string retailSeedDirectory,
        string outputDirectory,
        Action<string>? onProgress = null) =>
        RestoreUntouchedRetailSeedFiles(
            retailSeedDirectory,
            outputDirectory,
            LegacyDatExportPolicy.FromPreset(LegacyDatExportPreset.HalfDm),
            onProgress);

    internal static void RestoreUntouchedRetailSeedFiles(
        string retailSeedDirectory,
        string outputDirectory,
        LegacyDatExportPolicy policy,
        Action<string>? onProgress = null) {
        ArgumentException.ThrowIfNullOrWhiteSpace(retailSeedDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);

        foreach (string datFile in UntouchedRetailCompanionDatFiles) {
            if (datFile == "client_local_English.dat") {
                if (!LegacyDatMergeRuleResolver.ShouldCopyRetailLocalSeed(policy)) {
                    continue;
                }

                // Patched after convert; do not overwrite with pristine retail seed.
                if (LegacyDatMergeRuleResolver.ShouldApplyDmPortalStringPatches(policy)
                    || LegacyDatMergeRuleResolver.ShouldPatchConnectionScreenStrings(policy)
                    || LegacyDatMergeRuleResolver.ShouldPatchDmInGameStrings(policy)
                    || LegacyDatMergeRuleResolver.ShouldPatchCharGenLayoutShape(policy)
                    || LegacyDatMergeRuleResolver.ShouldPatchInGameLayoutShape(policy)) {
                    onProgress?.Invoke("Keeping patched client_local_English.dat (DM local layout/string overrides).");
                    continue;
                }
            }

            string sourcePath = Path.Combine(retailSeedDirectory, datFile);
            string destinationPath = Path.Combine(outputDirectory, datFile);
            if (!File.Exists(sourcePath)) {
                onProgress?.Invoke($"WARN: retail seed missing untouched companion file {datFile}.");
                continue;
            }

            onProgress?.Invoke($"Restoring untouched retail companion {datFile}...");
            File.Copy(sourcePath, destinationPath, overwrite: true);
        }
    }

    internal static void SyncHeaderFileSize(string datPath) {
        if (!File.Exists(datPath)) {
            return;
        }

        using var fs = new FileStream(datPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        if (fs.Length < FileSizeFieldOffset + 4) {
            return;
        }

        int actualSize = (int)Math.Min(fs.Length, int.MaxValue);
        Span<byte> sizeBytes = stackalloc byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(sizeBytes, actualSize);
        fs.Position = FileSizeFieldOffset;
        fs.Write(sizeBytes);
        fs.Flush();
    }

    internal static int ReadBtreeRoot(string datPath) {
        if (!File.Exists(datPath)) {
            return 0;
        }

        using var fs = new FileStream(datPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (fs.Length < BtreeRootFieldOffset + 4) {
            return 0;
        }

        Span<byte> rootBytes = stackalloc byte[4];
        fs.Position = BtreeRootFieldOffset;
        fs.ReadExactly(rootBytes);
        return BinaryPrimitives.ReadInt32LittleEndian(rootBytes);
    }

    /// <summary>
    /// Forces the export portal catalog root block to match the retail seed after overlay writes.
    /// Required for retail client init when Chorizite rebalances the tree during bulk replace.
    /// </summary>
    internal static void PreserveRetailPortalBtreeRoot(string retailSeedPortalPath, string exportPortalPath) {
        ArgumentException.ThrowIfNullOrWhiteSpace(retailSeedPortalPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(exportPortalPath);
        if (!File.Exists(retailSeedPortalPath) || !File.Exists(exportPortalPath)) {
            return;
        }

        int seedRoot = ReadBtreeRoot(retailSeedPortalPath);
        if (seedRoot <= 0) {
            return;
        }

        using var fs = new FileStream(exportPortalPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        if (fs.Length < BtreeRootFieldOffset + 4) {
            return;
        }

        Span<byte> rootBytes = stackalloc byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(rootBytes, seedRoot);
        fs.Position = BtreeRootFieldOffset;
        fs.Write(rootBytes);
        fs.Flush();
    }

    internal static void ClearEmptyTransaction(string datPath) {
        if (!File.Exists(datPath)) {
            return;
        }

        byte[] transaction = new byte[TransactionLength];
        EmptyTransactionPrefix.CopyTo(transaction, 0);

        using var fs = new FileStream(datPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        if (fs.Length < TransactionOffset + transaction.Length) {
            return;
        }

        fs.Position = TransactionOffset;
        fs.Write(transaction);
        fs.Flush();
    }

    private static void EnsureIterationObjectFromSeed(
        string retailSeedDirectory,
        string outputDirectory,
        string datFileName,
        Action<string>? onProgress) {
        using var seed = new DefaultDatReaderWriter(
            retailSeedDirectory,
            DatAccessType.Read,
            FileCachingStrategy.Never,
            IndexCachingStrategy.OnDemand);
        using var output = new DefaultDatReaderWriter(
            outputDirectory,
            DatAccessType.ReadWrite,
            FileCachingStrategy.Never,
            IndexCachingStrategy.OnDemand);

        var seedDatabase = LegacyDatRetailShellBootstrap.ResolveDatabase(seed, datFileName);
        var outputDatabase = LegacyDatRetailShellBootstrap.ResolveDatabase(output, datFileName);

        if (outputDatabase.Catalog().Tree.HasFile(LegacyDatPortalFileIds.Iteration)) {
            return;
        }

        if (!seedDatabase.TryGetFileBytes(LegacyDatPortalFileIds.Iteration, out byte[]? bytes, false) || bytes == null) {
            onProgress?.Invoke($"WARN: retail seed missing iteration object for {datFileName}.");
            return;
        }

        onProgress?.Invoke($"Restoring iteration object in {datFileName}...");
        outputDatabase.TryWriteFileBytes(LegacyDatPortalFileIds.Iteration, bytes, bytes.Length, 1);
    }

    private static void EnsureRetailHeaderMagic(string datPath) {
        using var fs = new FileStream(datPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        if (fs.Length < HeaderOffset + 4) {
            return;
        }

        Span<byte> magicBytes = stackalloc byte[4];
        fs.Position = HeaderOffset;
        fs.ReadExactly(magicBytes);
        if (BinaryPrimitives.ReadInt32LittleEndian(magicBytes) == ExpectedMagic) {
            return;
        }

        BinaryPrimitives.WriteInt32LittleEndian(magicBytes, ExpectedMagic);
        fs.Position = HeaderOffset;
        fs.Write(magicBytes);
        fs.Flush();
    }
}
