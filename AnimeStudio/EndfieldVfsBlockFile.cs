using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace AnimeStudio
{
    public class EndfieldVfsBlockFile : IDisposable
    {
        private const int BlockHeadLength = 12;
        private const int ProtoVersion = 3;
        // Endfield 正式 PC 包使用独立 ChaCha key；旧公开 key 是 Android/旧工具链样本。
        private const string ChachaKeyBase64 = "6VsxesT4KFadI6hr8nHctT6Eb6dckk1nHbqOOPTKUuE=";
        private static readonly byte[] ChachaKey = Convert.FromBase64String(ChachaKeyBase64);
        private static readonly object MetadataCacheLock = new();
        private static readonly Dictionary<string, CachedMainInfo> MetadataCache = new(StringComparer.OrdinalIgnoreCase);

        private readonly List<IDisposable> openedStreams = new();

        public BundleFile.Header m_Header;
        public List<StreamFile> fileList = new();

        public EndfieldVfsBlockFile(
            FileReader reader,
            GameType game,
            Func<string, bool> innerFileFilter = null,
            int innerFileLimit = 0,
            bool innerFileFilterIsDiagnostic = true)
        {
            if (!game.IsArknightsEndfieldGroup())
            {
                throw new InvalidCastException("Endfield VFS block list can only be used with Arknights Endfield game types.");
            }

            var blockListPath = reader.FullPath;
            var mainInfo = GetMainInfo(blockListPath);
            if (mainInfo.CodeVersion < ProtoVersion)
            {
                throw new InvalidDataException($"Unsupported Endfield VFS proto version {mainInfo.CodeVersion}.");
            }

            var totalSize = 0L;
            if (!IsUnityBundleBlockType(mainInfo.BlockType))
            {
                m_Header = CreateHeader(totalSize);
                Logger.Verbose($"Endfield VFS {Path.GetFileName(blockListPath)} is {mainInfo.BlockType}; skipped for Unity bundle loading.");
                return;
            }

            var isFiltering = innerFileFilter != null || innerFileLimit > 0;
            var totalUnityBundleFileCount = mainInfo.Chunks
                .SelectMany(x => x.Files)
                .Count(x => IsUnityBundleBlockType(x.BlockType));
            var filteredUnityBundleFileCount = 0;
            var reachedLimit = false;
            var blockDir = Path.GetDirectoryName(blockListPath) ?? throw new InvalidDataException($"Cannot get block directory for {blockListPath}.");
            foreach (var chunk in mainInfo.Chunks)
            {
                var selectedFiles = new List<EndfieldVfsFileInfo>();
                foreach (var file in chunk.Files)
                {
                    if (!IsUnityBundleBlockType(file.BlockType))
                    {
                        continue;
                    }

                    if (innerFileFilter != null && !innerFileFilter(file.FileName))
                    {
                        filteredUnityBundleFileCount++;
                        continue;
                    }

                    if (innerFileLimit > 0 && fileList.Count + selectedFiles.Count >= innerFileLimit)
                    {
                        filteredUnityBundleFileCount++;
                        reachedLimit = true;
                        continue;
                    }

                    selectedFiles.Add(file);
                }

                if (selectedFiles.Count == 0)
                {
                    if (reachedLimit)
                    {
                        break;
                    }
                    continue;
                }

                var chunkPath = Path.Combine(blockDir, chunk.FileName);
                if (!File.Exists(chunkPath))
                {
                    Logger.Warning($"Endfield VFS chunk is missing: {chunkPath}");
                    continue;
                }

                var chunkStream = File.Open(chunkPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                openedStreams.Add(chunkStream);

                foreach (var file in selectedFiles)
                {
                    if (file.Offset < 0 || file.Length < 0 || file.Offset + file.Length > chunkStream.Length)
                    {
                        Logger.Warning($"Endfield VFS file range is invalid: {file.FileName} in {chunk.FileName}");
                        continue;
                    }

                    var stream = CreateFileStream(chunkStream, file);
                    totalSize += stream.Length;
                    fileList.Add(new StreamFile
                    {
                        path = file.FileName,
                        fileName = Path.GetFileName(file.FileName),
                        stream = stream,
                    });
                }

                if (reachedLimit)
                {
                    break;
                }
            }

            m_Header = CreateHeader(totalSize);

            if (isFiltering && innerFileFilterIsDiagnostic)
            {
                Logger.Warning($"Endfield VFS inner file filter is active: exposed {fileList.Count}/{totalUnityBundleFileCount} Unity bundle file(s), filtered {filteredUnityBundleFileCount}. This is diagnostic only; full Library source indexing should leave it unset.");
            }
            Logger.Verbose($"Endfield VFS {Path.GetFileName(blockListPath)} exposed {fileList.Count} Unity bundle file(s).");
        }

        public static IReadOnlyList<UnityBundleEntry> ListUnityBundleFiles(string blockListPath, GameType game)
        {
            if (!game.IsArknightsEndfieldGroup())
            {
                throw new InvalidCastException("Endfield VFS block list can only be used with Arknights Endfield game types.");
            }

            var mainInfo = GetMainInfo(blockListPath);
            if (!IsUnityBundleBlockType(mainInfo.BlockType))
            {
                return Array.Empty<UnityBundleEntry>();
            }

            return mainInfo.Chunks
                .SelectMany(chunk => chunk.Files)
                .Where(file => IsUnityBundleBlockType(file.BlockType))
                .Select(file => new UnityBundleEntry(file.FileName, file.Length))
                .ToArray();
        }

        public static CabLocationScanResult LocateCabFiles(
            string blockListPath,
            Game game,
            ISet<string> targetCabNames,
            Func<string, bool> innerFileFilter = null,
            int innerFileLimit = 0)
        {
            if (game == null || !game.Type.IsArknightsEndfieldGroup())
            {
                throw new InvalidCastException("Endfield CAB locator can only be used with Arknights Endfield game types.");
            }
            if (targetCabNames == null || targetCabNames.Count == 0)
            {
                return new CabLocationScanResult(blockListPath, 0, 0, Array.Empty<CabLocation>(), Array.Empty<CabLocationError>());
            }

            var mainInfo = GetMainInfo(blockListPath);
            if (!IsUnityBundleBlockType(mainInfo.BlockType))
            {
                return new CabLocationScanResult(blockListPath, 0, 0, Array.Empty<CabLocation>(), Array.Empty<CabLocationError>());
            }

            var blockDir = Path.GetDirectoryName(blockListPath) ?? throw new InvalidDataException($"Cannot get block directory for {blockListPath}.");
            var locations = new List<CabLocation>();
            var errors = new List<CabLocationError>();
            var scannedUnityBundles = 0;
            var skippedUnityBundles = 0;
            var reachedLimit = false;

            foreach (var chunk in mainInfo.Chunks)
            {
                var chunkPath = Path.Combine(blockDir, chunk.FileName);
                if (!File.Exists(chunkPath))
                {
                    errors.Add(new CabLocationError(chunk.FileName, string.Empty, "missing_chunk_file"));
                    continue;
                }

                using var chunkStream = File.Open(chunkPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                foreach (var file in chunk.Files)
                {
                    if (!IsUnityBundleBlockType(file.BlockType))
                    {
                        continue;
                    }

                    if (innerFileFilter != null && !innerFileFilter(file.FileName))
                    {
                        skippedUnityBundles++;
                        continue;
                    }

                    if (innerFileLimit > 0 && scannedUnityBundles >= innerFileLimit)
                    {
                        skippedUnityBundles++;
                        reachedLimit = true;
                        break;
                    }

                    scannedUnityBundles++;
                    try
                    {
                        using var stream = CreateFileStream(chunkStream, file);
                        using var reader = new FileReader(Path.Combine(blockDir, file.FileName), stream);
                        foreach (var innerFile in EnumerateNestedCabFiles(reader, game, file.FileName, depth: 0))
                        {
                            if (!targetCabNames.Contains(innerFile.CabName))
                            {
                                continue;
                            }

                            locations.Add(new CabLocation(
                                innerFile.CabName,
                                blockListPath,
                                chunk.FileName,
                                file.FileName,
                                file.Length,
                                innerFile.Path,
                                innerFile.Length));
                        }
                    }
                    catch (Exception e) when (e is IOException || e is InvalidDataException || e is EndOfStreamException || e is ArgumentOutOfRangeException || e is NotSupportedException)
                    {
                        errors.Add(new CabLocationError(chunk.FileName, file.FileName, e.GetType().Name + ": " + e.Message));
                    }
                }

                if (reachedLimit)
                {
                    break;
                }
            }

            return new CabLocationScanResult(blockListPath, scannedUnityBundles, skippedUnityBundles, locations, errors);
        }

        public static CabLocationScanResult ListCabFiles(
            string blockListPath,
            Game game,
            Func<string, bool> innerFileFilter = null,
            int innerFileLimit = 0)
        {
            if (game == null || !game.Type.IsArknightsEndfieldGroup())
            {
                throw new InvalidCastException("Endfield CAB indexer can only be used with Arknights Endfield game types.");
            }

            var mainInfo = GetMainInfo(blockListPath);
            if (!IsUnityBundleBlockType(mainInfo.BlockType))
            {
                return new CabLocationScanResult(blockListPath, 0, 0, Array.Empty<CabLocation>(), Array.Empty<CabLocationError>());
            }

            var blockDir = Path.GetDirectoryName(blockListPath) ?? throw new InvalidDataException($"Cannot get block directory for {blockListPath}.");
            var locations = new List<CabLocation>();
            var errors = new List<CabLocationError>();
            var scannedUnityBundles = 0;
            var skippedUnityBundles = 0;
            var reachedLimit = false;

            foreach (var chunk in mainInfo.Chunks)
            {
                var chunkPath = Path.Combine(blockDir, chunk.FileName);
                if (!File.Exists(chunkPath))
                {
                    errors.Add(new CabLocationError(chunk.FileName, string.Empty, "missing_chunk_file"));
                    continue;
                }

                using var chunkStream = File.Open(chunkPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                foreach (var file in chunk.Files)
                {
                    if (!IsUnityBundleBlockType(file.BlockType))
                    {
                        continue;
                    }

                    if (innerFileFilter != null && !innerFileFilter(file.FileName))
                    {
                        skippedUnityBundles++;
                        continue;
                    }

                    if (innerFileLimit > 0 && scannedUnityBundles >= innerFileLimit)
                    {
                        skippedUnityBundles++;
                        reachedLimit = true;
                        break;
                    }

                    scannedUnityBundles++;
                    try
                    {
                        using var stream = CreateFileStream(chunkStream, file);
                        using var reader = new FileReader(Path.Combine(blockDir, file.FileName), stream);
                        foreach (var innerFile in EnumerateNestedCabFiles(reader, game, file.FileName, depth: 0))
                        {
                            locations.Add(new CabLocation(
                                innerFile.CabName,
                                blockListPath,
                                chunk.FileName,
                                file.FileName,
                                file.Length,
                                innerFile.Path,
                                innerFile.Length));
                        }
                    }
                    catch (Exception e) when (e is IOException || e is InvalidDataException || e is EndOfStreamException || e is ArgumentOutOfRangeException || e is NotSupportedException)
                    {
                        errors.Add(new CabLocationError(chunk.FileName, file.FileName, e.GetType().Name + ": " + e.Message));
                    }
                }

                if (reachedLimit)
                {
                    break;
                }
            }

            return new CabLocationScanResult(blockListPath, scannedUnityBundles, skippedUnityBundles, locations, errors);
        }

        private static IEnumerable<NestedCabFile> EnumerateNestedCabFiles(FileReader reader, Game game, string logicalPath, int depth)
        {
            if (reader == null || depth > 4)
            {
                yield break;
            }

            reader.Position = 0;
            if (reader.FileType == FileType.AssetsFile)
            {
                var cabName = Path.GetFileName(reader.FileName ?? reader.FullPath ?? logicalPath ?? string.Empty);
                if (!string.IsNullOrWhiteSpace(cabName))
                {
                    yield return new NestedCabFile(cabName, logicalPath, reader.Length);
                }
                yield break;
            }

            if (reader.FileType == FileType.BundleFile || reader.FileType == FileType.ENCRFile)
            {
                using var bundle = new BundleFile(reader, game);
                foreach (var innerFile in bundle.fileList ?? Enumerable.Empty<StreamFile>())
                {
                    foreach (var nested in EnumerateNestedCabFiles(innerFile, game, logicalPath, depth))
                    {
                        yield return nested;
                    }
                }
                yield break;
            }

            if (reader.FileType == FileType.VFSFile)
            {
                var vfs = new VFSFile(reader, reader.FullPath, game.Type);
                foreach (var innerFile in vfs.fileList ?? Enumerable.Empty<StreamFile>())
                {
                    foreach (var nested in EnumerateNestedCabFiles(innerFile, game, logicalPath, depth))
                    {
                        yield return nested;
                    }
                }
                yield break;
            }
        }

        private static IEnumerable<NestedCabFile> EnumerateNestedCabFiles(StreamFile innerFile, Game game, string parentLogicalPath, int depth)
        {
            if (innerFile?.stream == null)
            {
                yield break;
            }

            var innerName = innerFile.path ?? innerFile.fileName ?? string.Empty;
            var logicalPath = string.IsNullOrWhiteSpace(parentLogicalPath)
                ? innerName
                : parentLogicalPath + ">" + innerName;
            innerFile.stream.Position = 0;
            using var reader = new FileReader(innerName, innerFile.stream);
            foreach (var nested in EnumerateNestedCabFiles(reader, game, logicalPath, depth + 1))
            {
                yield return nested;
            }
        }

        public static IReadOnlyList<BlockTypeStat> ListBlockTypeStats(string blockListPath, GameType game)
        {
            if (!game.IsArknightsEndfieldGroup())
            {
                throw new InvalidCastException("Endfield VFS block list can only be used with Arknights Endfield game types.");
            }

            var mainInfo = GetMainInfo(blockListPath);
            return mainInfo.Chunks
                .SelectMany(chunk => chunk.Files)
                .GroupBy(file => file.BlockType)
                .OrderByDescending(group => group.Sum(file => file.Length))
                .Select(group => new BlockTypeStat(group.Key.ToString(), (byte)group.Key, group.Count(), group.Sum(file => file.Length)))
                .ToArray();
        }

        public static IReadOnlyList<VfsFileEntry> ListFileEntries(string blockListPath, GameType game)
        {
            if (!game.IsArknightsEndfieldGroup())
            {
                throw new InvalidCastException("Endfield VFS block list can only be used with Arknights Endfield game types.");
            }

            var mainInfo = GetMainInfo(blockListPath);
            return mainInfo.Chunks
                .SelectMany(chunk => chunk.Files.Select(file => new VfsFileEntry(
                    chunk.FileName,
                    file.FileName,
                    file.Length,
                    file.Offset,
                    file.BlockType.ToString(),
                    (byte)file.BlockType,
                    file.UseEncrypt)))
                .ToArray();
        }

        public static void ExtractFiles(string blockListPath, GameType game, string outputDirectory, Func<string, bool> fileFilter = null, int limit = 0)
        {
            if (!game.IsArknightsEndfieldGroup())
            {
                throw new InvalidCastException("Endfield VFS block list can only be used with Arknights Endfield game types.");
            }

            if (string.IsNullOrWhiteSpace(outputDirectory))
            {
                throw new ArgumentException("Output directory is required.", nameof(outputDirectory));
            }

            var mainInfo = GetMainInfo(blockListPath);
            var blockDir = Path.GetDirectoryName(blockListPath) ?? throw new InvalidDataException($"Cannot get block directory for {blockListPath}.");
            Directory.CreateDirectory(outputDirectory);
            var extracted = 0;
            foreach (var chunk in mainInfo.Chunks)
            {
                var chunkPath = Path.Combine(blockDir, chunk.FileName);
                if (!File.Exists(chunkPath))
                {
                    continue;
                }

                using var chunkStream = File.Open(chunkPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                foreach (var file in chunk.Files)
                {
                    if (fileFilter != null && !fileFilter(file.FileName))
                    {
                        continue;
                    }

                    if (limit > 0 && extracted >= limit)
                    {
                        return;
                    }

                    var outputPath = Path.Combine(outputDirectory, file.FileName.Replace('/', Path.DirectorySeparatorChar));
                    Directory.CreateDirectory(Path.GetDirectoryName(outputPath) ?? outputDirectory);
                    using var source = CreateFileStream(chunkStream, file);
                    using var output = File.Create(outputPath);
                    source.CopyTo(output);
                    extracted++;
                }
            }
        }

        private static EndfieldVfsMainInfo GetMainInfo(string blockListPath)
        {
            var fullPath = Path.GetFullPath(blockListPath);
            var fileInfo = new FileInfo(fullPath);
            if (!fileInfo.Exists)
            {
                throw new FileNotFoundException($"Endfield VFS block list not found: {fullPath}", fullPath);
            }

            lock (MetadataCacheLock)
            {
                if (MetadataCache.TryGetValue(fullPath, out var cached)
                    && cached.Length == fileInfo.Length
                    && cached.LastWriteUtc == fileInfo.LastWriteTimeUtc)
                {
                    return cached.MainInfo;
                }
            }

            var blockListBytes = File.ReadAllBytes(fullPath);
            DecryptBlockList(blockListBytes);
            var mainInfo = EndfieldVfsMainInfo.Read(blockListBytes, BlockHeadLength);

            lock (MetadataCacheLock)
            {
                MetadataCache[fullPath] = new CachedMainInfo(fileInfo.Length, fileInfo.LastWriteTimeUtc, mainInfo);
            }

            return mainInfo;
        }

        public void Dispose()
        {
            foreach (var stream in fileList.Select(x => x.stream))
            {
                stream.Dispose();
            }
            foreach (var stream in openedStreams)
            {
                stream.Dispose();
            }
            fileList.Clear();
            openedStreams.Clear();
        }

        private static void DecryptBlockList(byte[] blockListBytes)
        {
            if (blockListBytes.Length <= BlockHeadLength)
            {
                throw new InvalidDataException("Endfield VFS block list is too small.");
            }

            var nonce = blockListBytes.AsSpan(0, BlockHeadLength).ToArray();
            var encrypted = blockListBytes.AsSpan(BlockHeadLength).ToArray();
            var decrypted = EndfieldChacha20.Transform(encrypted, ChachaKey, nonce, 1);
            decrypted.CopyTo(blockListBytes.AsSpan(BlockHeadLength));
        }

        private static BundleFile.Header CreateHeader(long totalSize)
        {
            return new BundleFile.Header
            {
                signature = "EndfieldVFS",
                version = ProtoVersion,
                unityVersion = "5.x.x",
                unityRevision = "2021.3.34f5",
                size = (uint)Math.Min(totalSize, uint.MaxValue),
            };
        }

        private static Stream CreateFileStream(FileStream chunkStream, EndfieldVfsFileInfo file)
        {
            if (!file.UseEncrypt)
            {
                return new OffsetStream(chunkStream, file.Offset, file.Length);
            }

            if (file.Length > int.MaxValue)
            {
                throw new InvalidDataException($"Encrypted Endfield VFS file is too large to buffer: {file.FileName}");
            }

            chunkStream.Position = file.Offset;
            var encrypted = new byte[(int)file.Length];
            ReadExactly(chunkStream, encrypted);

            Span<byte> nonce = stackalloc byte[BlockHeadLength];
            BinaryPrimitives.WriteInt32LittleEndian(nonce, ProtoVersion);
            BinaryPrimitives.WriteInt64LittleEndian(nonce[sizeof(int)..], file.IvSeed);

            var decrypted = EndfieldChacha20.Transform(encrypted, ChachaKey, nonce.ToArray(), 1);
            return new MemoryStream(decrypted, writable: false);
        }

        private static void ReadExactly(Stream stream, byte[] buffer)
        {
            var offset = 0;
            while (offset < buffer.Length)
            {
                var read = stream.Read(buffer, offset, buffer.Length - offset);
                if (read == 0)
                {
                    throw new EndOfStreamException("Unexpected end of Endfield VFS chunk.");
                }
                offset += read;
            }
        }

        private static bool IsUnityBundleBlockType(EndfieldVfsBlockType type)
        {
            return type == EndfieldVfsBlockType.InitBundle
                || type == EndfieldVfsBlockType.Bundle;
        }

        private enum EndfieldVfsBlockType : byte
        {
            All = 0,
            InitAudio = 1,
            InitBundle = 2,
            InitialExtendData = 3,
            BundleManifest = 4,
            IFixPatchOut = 5,
            AuditStreaming = 6,
            AuditDynamicStreaming = 7,
            AuditIV = 8,
            AuditAudio = 9,
            AuditVideo = 10,
            Bundle = 11,
            Audio = 12,
            Video = 13,
            IV = 14,
            Streaming = 15,
            DynamicStreaming = 16,
            Lua = 17,
            Table = 18,
            JsonData = 19,
            ExtendData = 20,
            HotfixAudio = 21,
            Raw = 100,
            AudioChinese = 101,
            AudioEnglish = 102,
            AudioJapanese = 103,
            AudioKorean = 104,
        }

        public sealed class UnityBundleEntry
        {
            public UnityBundleEntry(string fileName, long length)
            {
                FileName = fileName;
                Length = length;
            }

            public string FileName { get; }
            public long Length { get; }
        }

        public sealed class BlockTypeStat
        {
            public BlockTypeStat(string blockType, int blockTypeId, int fileCount, long totalLength)
            {
                BlockType = blockType;
                BlockTypeId = blockTypeId;
                FileCount = fileCount;
                TotalLength = totalLength;
            }

            public string BlockType { get; }
            public int BlockTypeId { get; }
            public int FileCount { get; }
            public long TotalLength { get; }
        }

        public sealed class VfsFileEntry
        {
            public VfsFileEntry(string chunkFileName, string fileName, long length, long offset, string blockType, int blockTypeId, bool useEncrypt)
            {
                ChunkFileName = chunkFileName;
                FileName = fileName;
                Length = length;
                Offset = offset;
                BlockType = blockType;
                BlockTypeId = blockTypeId;
                UseEncrypt = useEncrypt;
            }

            public string ChunkFileName { get; }
            public string FileName { get; }
            public long Length { get; }
            public long Offset { get; }
            public string BlockType { get; }
            public int BlockTypeId { get; }
            public bool UseEncrypt { get; }
        }

        public sealed class CabLocationScanResult
        {
            public CabLocationScanResult(string blockListPath, int scannedUnityBundleCount, int skippedUnityBundleCount, IReadOnlyList<CabLocation> locations, IReadOnlyList<CabLocationError> errors)
            {
                BlockListPath = blockListPath;
                ScannedUnityBundleCount = scannedUnityBundleCount;
                SkippedUnityBundleCount = skippedUnityBundleCount;
                Locations = locations ?? Array.Empty<CabLocation>();
                Errors = errors ?? Array.Empty<CabLocationError>();
            }

            public string BlockListPath { get; }
            public int ScannedUnityBundleCount { get; }
            public int SkippedUnityBundleCount { get; }
            public IReadOnlyList<CabLocation> Locations { get; }
            public IReadOnlyList<CabLocationError> Errors { get; }
        }

        public sealed class CabLocation
        {
            public CabLocation(string cabName, string blockListPath, string chunkFileName, string unityBundleFile, long unityBundleLength, string bundleInnerPath, long bundleInnerLength)
            {
                CabName = cabName;
                BlockListPath = blockListPath;
                ChunkFileName = chunkFileName;
                UnityBundleFile = unityBundleFile;
                UnityBundleLength = unityBundleLength;
                BundleInnerPath = bundleInnerPath;
                BundleInnerLength = bundleInnerLength;
            }

            public string CabName { get; }
            public string BlockListPath { get; }
            public string ChunkFileName { get; }
            public string UnityBundleFile { get; }
            public long UnityBundleLength { get; }
            public string BundleInnerPath { get; }
            public long BundleInnerLength { get; }
        }

        private sealed class NestedCabFile
        {
            public NestedCabFile(string cabName, string path, long length)
            {
                CabName = cabName;
                Path = path;
                Length = length;
            }

            public string CabName { get; }
            public string Path { get; }
            public long Length { get; }
        }

        public sealed class CabLocationError
        {
            public CabLocationError(string chunkFileName, string unityBundleFile, string message)
            {
                ChunkFileName = chunkFileName;
                UnityBundleFile = unityBundleFile;
                Message = message;
            }

            public string ChunkFileName { get; }
            public string UnityBundleFile { get; }
            public string Message { get; }
        }

        private sealed class CachedMainInfo
        {
            public CachedMainInfo(long length, DateTime lastWriteUtc, EndfieldVfsMainInfo mainInfo)
            {
                Length = length;
                LastWriteUtc = lastWriteUtc;
                MainInfo = mainInfo;
            }

            public long Length { get; }
            public DateTime LastWriteUtc { get; }
            public EndfieldVfsMainInfo MainInfo { get; }
        }

        private sealed class EndfieldVfsMainInfo
        {
            public int CodeVersion { get; private set; }
            public int Version { get; private set; }
            public EndfieldVfsBlockType BlockType { get; private set; }
            public List<EndfieldVfsChunkInfo> Chunks { get; } = new();

            public static EndfieldVfsMainInfo Read(byte[] bytes, int offset)
            {
                var info = new EndfieldVfsMainInfo();
                info.CodeVersion = ReadInt32(bytes, ref offset);
                if (info.CodeVersion > 10)
                {
                    info.CodeVersion = ProtoVersion;
                    info.Version = ProtoVersion;
                }
                else
                {
                    info.Version = ReadInt32(bytes, ref offset);
                }

                var groupNameLength = ReadUInt16(bytes, ref offset);
                if (groupNameLength > bytes.Length - offset)
                {
                    throw new InvalidDataException("Unsupported Endfield VFS block list format or decryption key mismatch.");
                }
                offset += groupNameLength;
                offset += sizeof(long); // groupCfgHashName，正式包只有前 4 字节参与目录名，整体仍占 8 字节。
                offset += sizeof(int); // groupFileInfoNum
                offset += sizeof(long); // groupChunksLength
                info.BlockType = (EndfieldVfsBlockType)ReadByte(bytes, ref offset);

                var chunkCount = ReadInt32(bytes, ref offset);
                for (var i = 0; i < chunkCount; i++)
                {
                    var chunk = new EndfieldVfsChunkInfo
                    {
                        FileName = ReadMd5FileName(bytes, ref offset),
                    };
                    offset += 16; // contentMD5
                    chunk.Length = ReadInt64(bytes, ref offset);
                    chunk.BlockType = (EndfieldVfsBlockType)ReadByte(bytes, ref offset);
                    if (info.CodeVersion > ProtoVersion)
                    {
                        offset += sizeof(int); // EVFSFileTag
                    }

                    var fileCount = ReadInt32(bytes, ref offset);
                    for (var fileIndex = 0; fileIndex < fileCount; fileIndex++)
                    {
                        var fileNameLength = ReadUInt16(bytes, ref offset);
                        var fileName = Encoding.UTF8.GetString(bytes.AsSpan(offset, fileNameLength));
                        offset += fileNameLength;

                        offset += sizeof(long); // fileNameHash
                        offset += 16; // fileChunkMD5Name
                        offset += 16; // fileDataMD5

                        var file = new EndfieldVfsFileInfo
                        {
                            FileName = fileName,
                            Offset = ReadInt64(bytes, ref offset),
                            Length = ReadInt64(bytes, ref offset),
                            BlockType = (EndfieldVfsBlockType)ReadByte(bytes, ref offset),
                            UseEncrypt = ReadByte(bytes, ref offset) != 0,
                        };

                        if (file.UseEncrypt)
                        {
                            file.IvSeed = ReadInt64(bytes, ref offset);
                        }
                        if (info.CodeVersion > ProtoVersion)
                        {
                            offset += sizeof(int); // EVFSFileTag
                        }

                        chunk.Files.Add(file);
                    }

                    info.Chunks.Add(chunk);
                }

                return info;
            }

            private static string ReadMd5FileName(byte[] bytes, ref int offset)
            {
                Ensure(bytes, offset, 16);
                var name = Convert.ToHexString(bytes.AsSpan(offset, 16)) + ".chk";
                offset += 16;
                return name;
            }

            private static byte ReadByte(byte[] bytes, ref int offset)
            {
                Ensure(bytes, offset, 1);
                return bytes[offset++];
            }

            private static ushort ReadUInt16(byte[] bytes, ref int offset)
            {
                Ensure(bytes, offset, sizeof(ushort));
                var value = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(offset));
                offset += sizeof(ushort);
                return value;
            }

            private static int ReadInt32(byte[] bytes, ref int offset)
            {
                Ensure(bytes, offset, sizeof(int));
                var value = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(offset));
                offset += sizeof(int);
                return value;
            }

            private static long ReadInt64(byte[] bytes, ref int offset)
            {
                Ensure(bytes, offset, sizeof(long));
                var value = BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(offset));
                offset += sizeof(long);
                return value;
            }

            private static void Ensure(byte[] bytes, int offset, int count)
            {
                if (offset < 0 || count < 0 || offset + count > bytes.Length)
                {
                    throw new InvalidDataException("Endfield VFS block list is truncated.");
                }
            }
        }

        private sealed class EndfieldVfsChunkInfo
        {
            public string FileName { get; set; }
            public long Length { get; set; }
            public EndfieldVfsBlockType BlockType { get; set; }
            public List<EndfieldVfsFileInfo> Files { get; } = new();
        }

        private sealed class EndfieldVfsFileInfo
        {
            public string FileName { get; set; }
            public long Offset { get; set; }
            public long Length { get; set; }
            public EndfieldVfsBlockType BlockType { get; set; }
            public bool UseEncrypt { get; set; }
            public long IvSeed { get; set; }
        }

        private static class EndfieldChacha20
        {
            private static readonly uint[] Sigma =
            {
                0x61707865,
                0x3320646e,
                0x79622d32,
                0x6b206574,
            };

            public static byte[] Transform(byte[] input, byte[] key, byte[] nonce, uint counter)
            {
                if (key.Length != 32)
                    throw new ArgumentException("ChaCha20 key must be 32 bytes.", nameof(key));
                if (nonce.Length != 12)
                    throw new ArgumentException("ChaCha20 nonce must be 12 bytes.", nameof(nonce));

                var output = new byte[input.Length];
                Span<byte> keyStream = stackalloc byte[64];
                var blockCounter = counter;
                for (var offset = 0; offset < input.Length; offset += 64)
                {
                    WriteKeyStreamBlock(keyStream, key, nonce, blockCounter++);
                    var count = Math.Min(64, input.Length - offset);
                    for (var i = 0; i < count; i++)
                    {
                        output[offset + i] = (byte)(input[offset + i] ^ keyStream[i]);
                    }
                }

                return output;
            }

            private static void WriteKeyStreamBlock(Span<byte> output, byte[] key, byte[] nonce, uint counter)
            {
                Span<uint> state = stackalloc uint[16];
                Span<uint> working = stackalloc uint[16];

                Sigma.CopyTo(state);
                for (var i = 0; i < 8; i++)
                {
                    state[4 + i] = BinaryPrimitives.ReadUInt32LittleEndian(key.AsSpan(i * 4, 4));
                }
                state[12] = counter;
                state[13] = BinaryPrimitives.ReadUInt32LittleEndian(nonce.AsSpan(0, 4));
                state[14] = BinaryPrimitives.ReadUInt32LittleEndian(nonce.AsSpan(4, 4));
                state[15] = BinaryPrimitives.ReadUInt32LittleEndian(nonce.AsSpan(8, 4));

                state.CopyTo(working);
                for (var i = 0; i < 10; i++)
                {
                    QuarterRound(working, 0, 4, 8, 12);
                    QuarterRound(working, 1, 5, 9, 13);
                    QuarterRound(working, 2, 6, 10, 14);
                    QuarterRound(working, 3, 7, 11, 15);
                    QuarterRound(working, 0, 5, 10, 15);
                    QuarterRound(working, 1, 6, 11, 12);
                    QuarterRound(working, 2, 7, 8, 13);
                    QuarterRound(working, 3, 4, 9, 14);
                }

                for (var i = 0; i < 16; i++)
                {
                    BinaryPrimitives.WriteUInt32LittleEndian(output.Slice(i * 4, 4), working[i] + state[i]);
                }
            }

            private static void QuarterRound(Span<uint> x, int a, int b, int c, int d)
            {
                x[a] += x[b]; x[d] = RotateLeft(x[d] ^ x[a], 16);
                x[c] += x[d]; x[b] = RotateLeft(x[b] ^ x[c], 12);
                x[a] += x[b]; x[d] = RotateLeft(x[d] ^ x[a], 8);
                x[c] += x[d]; x[b] = RotateLeft(x[b] ^ x[c], 7);
            }

            private static uint RotateLeft(uint value, int count)
            {
                return (value << count) | (value >> (32 - count));
            }
        }
    }
}
