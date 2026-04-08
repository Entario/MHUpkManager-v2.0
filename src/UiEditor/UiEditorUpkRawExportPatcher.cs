using UpkManager.Models.UpkFile;
using UpkManager.Models.UpkFile.Compression;
using UpkManager.Models.UpkFile.Tables;

namespace MHUpkManager.UiEditor;

internal sealed class UpkRawExportPatcher
{
    public async Task<byte[]> GetLogicalPackageBytesAsync(string upkPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(upkPath);
        UnrealHeader header = await LoadHeaderAsync(upkPath).ConfigureAwait(false);
        byte[] originalBytes = await File.ReadAllBytesAsync(upkPath).ConfigureAwait(false);
        return header.CompressedChunks.Count > 0 ? DecompressPackageWithHeader(originalBytes, header) : originalBytes;
    }

    public async Task PatchExportsAsync(string upkPath, IReadOnlyDictionary<int, byte[]> replacementsByExportIndex, string outputUpkPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(upkPath);
        ArgumentNullException.ThrowIfNull(replacementsByExportIndex);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputUpkPath);

        byte[] originalBytes = await File.ReadAllBytesAsync(upkPath).ConfigureAwait(false);
        UnrealHeader header = await LoadHeaderAsync(upkPath).ConfigureAwait(false);
        List<ExportReplacement> exportBuffers = header.ExportTable
            .Select(export => new ExportReplacement(export.TableIndex, export.UnrealObjectReader?.GetBytes() ?? Array.Empty<byte>(), Array.Empty<BulkDataPatch>()))
            .ToList();

        foreach ((int exportIndex, byte[] replacementBytes) in replacementsByExportIndex)
        {
            if (exportIndex <= 0 || exportIndex > exportBuffers.Count)
                throw new ArgumentOutOfRangeException(nameof(replacementsByExportIndex), $"Export index {exportIndex} is outside the package export table.");
            exportBuffers[exportIndex - 1] = new ExportReplacement(exportIndex, replacementBytes, Array.Empty<BulkDataPatch>());
        }

        byte[] repacked = header.CompressedChunks.Count > 0
            ? RepackCompressedFile(DecompressPackageWithHeader(originalBytes, header), originalBytes, header, exportBuffers)
            : RepackFile(originalBytes, header, exportBuffers);
        await File.WriteAllBytesAsync(outputUpkPath, repacked).ConfigureAwait(false);
    }

    public async Task PatchLogicalOffsetsAsync(string upkPath, IReadOnlyDictionary<int, byte[]> replacementsByLogicalOffset, string outputUpkPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(upkPath);
        ArgumentNullException.ThrowIfNull(replacementsByLogicalOffset);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputUpkPath);

        byte[] originalBytes = await File.ReadAllBytesAsync(upkPath).ConfigureAwait(false);
        UnrealHeader header = await LoadHeaderAsync(upkPath).ConfigureAwait(false);
        byte[] logicalBytes = header.CompressedChunks.Count > 0 ? DecompressPackageWithHeader(originalBytes, header) : (byte[])originalBytes.Clone();

        foreach ((int logicalOffset, byte[] replacementBytes) in replacementsByLogicalOffset)
        {
            if (logicalOffset < 0 || logicalOffset > logicalBytes.Length - replacementBytes.Length)
                throw new ArgumentOutOfRangeException(nameof(replacementsByLogicalOffset), $"Logical offset {logicalOffset} is outside the package buffer.");
            Buffer.BlockCopy(replacementBytes, 0, logicalBytes, logicalOffset, replacementBytes.Length);
        }

        List<ExportReplacement> exportBuffers = header.ExportTable
            .Select(export => new ExportReplacement(export.TableIndex, export.UnrealObjectReader?.GetBytes() ?? Array.Empty<byte>(), Array.Empty<BulkDataPatch>()))
            .ToList();

        byte[] repacked = header.CompressedChunks.Count > 0
            ? RepackCompressedFile(logicalBytes, originalBytes, header, exportBuffers)
            : RepackFile(logicalBytes, header, exportBuffers);
        await File.WriteAllBytesAsync(outputUpkPath, repacked).ConfigureAwait(false);
    }

    private static async Task<UnrealHeader> LoadHeaderAsync(string upkPath)
    {
        UpkManager.Repository.UpkFileRepository repository = new();
        UnrealHeader header = await repository.LoadUpkFile(upkPath).ConfigureAwait(false);
        await header.ReadHeaderAsync(null).ConfigureAwait(false);
        return header;
    }

    private static byte[] RepackFile(byte[] originalBytes, UnrealHeader header, IReadOnlyList<ExportReplacement> exportBuffers)
    {
        int headerSize = header.Size;
        byte[] repacked = new byte[headerSize + exportBuffers.Sum(static replacement => replacement.Buffer.Length)];
        Buffer.BlockCopy(originalBytes, 0, repacked, 0, Math.Min(headerSize, originalBytes.Length));
        List<int> entryOffsets = LocateExportTableOffsets(header);
        int cursor = headerSize;
        for (int index = 0; index < exportBuffers.Count; index++)
        {
            ExportReplacement exportReplacement = exportBuffers[index];
            byte[] exportData = exportReplacement.Buffer;
            Buffer.BlockCopy(exportData, 0, repacked, cursor, exportData.Length);
            foreach (BulkDataPatch patch in exportReplacement.BulkDataPatches)
                WriteInt32(repacked, cursor + patch.OffsetFieldPosition, cursor + patch.DataStartPosition);
            WriteInt32(repacked, entryOffsets[index] + 32, exportData.Length);
            WriteInt32(repacked, entryOffsets[index] + 36, cursor);
            cursor += exportData.Length;
        }
        WriteInt32(repacked, 8, headerSize);
        return repacked;
    }

    private static byte[] RepackCompressedFile(byte[] logicalBytes, byte[] originalBytes, UnrealHeader header, IReadOnlyList<ExportReplacement> exportBuffers)
    {
        HeaderPatchOffsets offsets = LocateHeaderPatchOffsets(originalBytes);
        int compressionTableOffset = offsets.CompressionCountOffset + sizeof(int);
        int compressionTableLength = header.CompressionTableCount * 16;
        int compressedDataStart = header.CompressedChunks.Min(static chunk => chunk.CompressedOffset);
        Buffer.BlockCopy(originalBytes, 0, logicalBytes, 0, Math.Min(compressionTableOffset, Math.Min(originalBytes.Length, logicalBytes.Length)));
        int shiftedHeaderSourceOffset = compressionTableOffset + compressionTableLength;
        int shiftedHeaderLength = Math.Max(0, compressedDataStart - shiftedHeaderSourceOffset);
        if (shiftedHeaderLength > 0)
        {
            Buffer.BlockCopy(originalBytes, shiftedHeaderSourceOffset, logicalBytes, compressionTableOffset,
                Math.Min(shiftedHeaderLength, Math.Min(originalBytes.Length - shiftedHeaderSourceOffset, logicalBytes.Length - compressionTableOffset)));
        }
        ClearCompressionHeaderFlags(logicalBytes);
        WriteInt32(logicalBytes, offsets.CompressionCountOffset, 0);
        return RepackFile(logicalBytes, header, exportBuffers);
    }

    private static byte[] DecompressFullPackage(UnrealHeader header)
    {
        int start = header.CompressedChunks.Min(static chunk => chunk.UncompressedOffset);
        int totalSize = header.CompressedChunks.SelectMany(static chunk => chunk.Header.Blocks).Sum(static block => block.UncompressedSize) + start;
        byte[] data = new byte[totalSize];
        foreach (UnrealCompressedChunk chunk in header.CompressedChunks)
        {
            int localOffset = 0;
            foreach (UnrealCompressedChunkBlock block in chunk.Header.Blocks)
            {
                byte[] decompressed = block.CompressedData.Decompress(block.UncompressedSize);
                Buffer.BlockCopy(decompressed, 0, data, chunk.UncompressedOffset + localOffset, decompressed.Length);
                localOffset += block.UncompressedSize;
            }
        }
        return data;
    }

    private static byte[] DecompressPackageWithHeader(byte[] originalBytes, UnrealHeader header)
    {
        byte[] logicalBytes = DecompressFullPackage(header);
        HeaderPatchOffsets offsets = LocateHeaderPatchOffsets(originalBytes);
        int compressionTableOffset = offsets.CompressionCountOffset + sizeof(int);
        int compressionTableLength = header.CompressionTableCount * 16;
        int compressedDataStart = header.CompressedChunks.Min(static chunk => chunk.CompressedOffset);
        Buffer.BlockCopy(originalBytes, 0, logicalBytes, 0, Math.Min(compressionTableOffset, Math.Min(originalBytes.Length, logicalBytes.Length)));
        int shiftedHeaderSourceOffset = compressionTableOffset + compressionTableLength;
        int shiftedHeaderLength = Math.Max(0, compressedDataStart - shiftedHeaderSourceOffset);
        if (shiftedHeaderLength > 0)
        {
            Buffer.BlockCopy(originalBytes, shiftedHeaderSourceOffset, logicalBytes, compressionTableOffset,
                Math.Min(shiftedHeaderLength, Math.Min(originalBytes.Length - shiftedHeaderSourceOffset, logicalBytes.Length - compressionTableOffset)));
        }
        return logicalBytes;
    }

    private static void ClearCompressionHeaderFlags(byte[] bytes)
    {
        HeaderPatchOffsets offsets = LocateHeaderPatchOffsets(bytes);
        WriteUInt32(bytes, offsets.PackageFlagsOffset, ReadUInt32(bytes, offsets.PackageFlagsOffset) & ~(uint)(EPackageFlags.Compressed | EPackageFlags.FullyCompressed));
        WriteUInt32(bytes, offsets.CompressionFlagsOffset, 0);
    }

    private static HeaderPatchOffsets LocateHeaderPatchOffsets(byte[] bytes)
    {
        using MemoryStream stream = new(bytes, writable: false);
        using BinaryReader reader = new(stream);
        stream.Position = 8;
        _ = reader.ReadInt32();
        int groupSize = reader.ReadInt32();
        if (groupSize < 0) stream.Position += -groupSize * 2L;
        else if (groupSize > 0) stream.Position += groupSize;
        int packageFlagsOffset = checked((int)stream.Position);
        stream.Position += sizeof(uint);
        stream.Position += sizeof(int) * 11L;
        stream.Position += 16;
        int generationCount = reader.ReadInt32();
        stream.Position += generationCount * 12L;
        stream.Position += sizeof(uint) * 2L;
        int compressionFlagsOffset = checked((int)stream.Position);
        return new HeaderPatchOffsets(packageFlagsOffset, compressionFlagsOffset, compressionFlagsOffset + sizeof(uint));
    }

    private static List<int> LocateExportTableOffsets(UnrealHeader header)
    {
        List<int> offsets = new(header.ExportTable.Count);
        int cursor = header.ExportTableOffset;
        foreach (UnrealExportTableEntry export in header.ExportTable)
        {
            offsets.Add(cursor);
            cursor += 68 + (export.NetObjects.Count * sizeof(int));
        }
        return offsets;
    }

    private static uint ReadUInt32(byte[] buffer, int offset) => BitConverter.ToUInt32(buffer, offset);
    private static void WriteUInt32(byte[] buffer, int offset, uint value) => Buffer.BlockCopy(BitConverter.GetBytes(value), 0, buffer, offset, sizeof(uint));
    private static void WriteInt32(byte[] buffer, int offset, int value) => Buffer.BlockCopy(BitConverter.GetBytes(value), 0, buffer, offset, sizeof(int));

    private readonly record struct HeaderPatchOffsets(int PackageFlagsOffset, int CompressionFlagsOffset, int CompressionCountOffset);
    private sealed record ExportReplacement(int ExportIndex, byte[] Buffer, IReadOnlyList<BulkDataPatch> BulkDataPatches);
}

internal sealed record BulkDataPatch(int OffsetFieldPosition, int DataStartPosition);
