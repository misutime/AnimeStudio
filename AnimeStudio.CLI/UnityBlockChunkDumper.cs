using System;
using System.Collections.Generic;
using System.IO;

namespace AnimeStudio.CLI
{
    internal static class UnityBlockChunkDumper
    {
        public static void Dump(string inputPath, string outputPath, Game game)
        {
            if (string.IsNullOrWhiteSpace(inputPath))
            {
                throw new ArgumentException("inputPath is required.");
            }
            if (string.IsNullOrWhiteSpace(outputPath))
            {
                throw new ArgumentException("outputPath is required.");
            }

            Directory.CreateDirectory(outputPath);

            using var reader = new FileReader(inputPath).PreProcessing(game);
            var written = reader.FileType switch
            {
                FileType.BlkFile => DumpBlk(reader, outputPath, game),
                FileType.BlockFile => DumpBlockFile(reader, outputPath, game),
                _ => DumpSingleUnityFile(reader, outputPath, game),
            };

            Logger.Info($"Dumped {written} Unity block chunk(s) to {outputPath}.");
        }

        private static int DumpBlk(FileReader reader, string outputPath, Game game)
        {
            if (game is not Blk blk)
            {
                throw new InvalidOperationException($"Game {game.Name} does not provide BLK decrypt keys.");
            }

            var count = 0;
            using var stream = BlkUtils.Decrypt(reader, blk);
            foreach (var offset in stream.GetOffsets(reader.FullPath))
            {
                var dummyPath = Path.Combine(reader.FullPath, offset.ToString("X8"));
                using var subReader = new FileReader(dummyPath, stream, leaveOpen: true);
                count += DumpParsedChunk(subReader, stream, offset, outputPath, game, count);
            }
            return count;
        }

        private static int DumpSingleUnityFile(FileReader reader, string outputPath, Game game)
        {
            if (reader.FileType != FileType.BundleFile && reader.FileType != FileType.MhyFile)
            {
                Logger.Warning($"Input file type {reader.FileType} is not a Unity bundle/MHY block.");
                return 0;
            }

            return DumpParsedChunk(reader, reader.BaseStream, 0, outputPath, game, 0);
        }

        private static int DumpBlockFile(FileReader reader, string outputPath, Game game)
        {
            var count = 0;
            using var stream = new OffsetStream(reader.BaseStream, 0);
            foreach (var offset in stream.GetOffsets(reader.FullPath))
            {
                var dummyPath = Path.Combine(reader.FullPath, offset.ToString("X8"));
                using var subReader = new FileReader(dummyPath, stream, leaveOpen: true);
                count += DumpParsedChunk(subReader, stream, offset, outputPath, game, count);
            }
            return count;
        }

        private static int DumpParsedChunk(FileReader reader, Stream sourceStream, long absoluteOffset, string outputPath, Game game, int index)
        {
            long size;
            string extension;
            string typeName;

            switch (reader.FileType)
            {
                case FileType.BundleFile:
                    using (var bundle = new BundleFile(reader, game))
                    {
                        size = bundle.m_Header.size;
                        extension = ".unityfs";
                        typeName = bundle.m_Header.signature;
                    }
                    break;
                case FileType.MhyFile when game is Mhy mhy:
                    var mhyFile = new MhyFile(reader, mhy);
                    DumpInnerFiles(mhyFile.fileList, Path.Combine(outputPath, $"{index:D3}_{absoluteOffset:X8}_MHY_files"));
                    return 1;
                case FileType.Blb3File:
                    var blb3File = new Blb3File(reader, reader.FullPath);
                    DumpInnerFiles(blb3File.fileList, Path.Combine(outputPath, $"{index:D3}_{absoluteOffset:X8}_BLB3_files"));
                    return 1;
                default:
                    Logger.Warning($"Skipped block at 0x{absoluteOffset:X8}: unsupported inner type {reader.FileType}.");
                    return 0;
            }

            if (size <= 0 || size > GetAvailableBytes(sourceStream, absoluteOffset))
            {
                Logger.Warning($"Skipped block at 0x{absoluteOffset:X8}: invalid size 0x{size:X8} for stream length 0x{sourceStream.Length:X8}.");
                return 0;
            }

            var fileName = $"{index:D3}_{absoluteOffset:X8}_{typeName}{extension}";
            var destination = Path.Combine(outputPath, SanitizeFileName(fileName));
            CopyRange(sourceStream, absoluteOffset, size, destination);
            Logger.Info($"Dumped {typeName} block 0x{absoluteOffset:X8}, size 0x{size:X8}: {destination}");
            return 1;
        }

        private static void DumpInnerFiles(IEnumerable<StreamFile> files, string outputPath)
        {
            Directory.CreateDirectory(outputPath);
            foreach (var file in files)
            {
                var name = string.IsNullOrWhiteSpace(file.fileName) ? "unnamed.bin" : file.fileName;
                var destination = Path.Combine(outputPath, SanitizeFileName(name));
                file.stream.Position = 0;
                using var output = File.Create(destination);
                file.stream.CopyTo(output);
                Logger.Info($"Dumped inner file {name}: {destination}");
            }
        }

        private static void CopyRange(Stream sourceStream, long absoluteOffset, long size, string destination)
        {
            SeekAbsolute(sourceStream, absoluteOffset);
            using var output = File.Create(destination);
            var buffer = new byte[1024 * 1024];
            var remaining = size;
            while (remaining > 0)
            {
                var read = sourceStream.Read(buffer, 0, (int)Math.Min(buffer.Length, remaining));
                if (read <= 0)
                {
                    throw new EndOfStreamException($"Unexpected end of stream while dumping {destination}.");
                }
                output.Write(buffer, 0, read);
                remaining -= read;
            }
        }

        private static long GetAvailableBytes(Stream stream, long absoluteOffset)
        {
            if (stream is OffsetStream offsetStream)
            {
                return offsetStream.BaseLength - absoluteOffset;
            }

            return stream.Length - absoluteOffset;
        }

        private static void SeekAbsolute(Stream stream, long absoluteOffset)
        {
            if (stream is OffsetStream offsetStream)
            {
                offsetStream.Offset = absoluteOffset;
                offsetStream.Position = 0;
                return;
            }

            stream.Position = absoluteOffset;
        }

        private static string SanitizeFileName(string value)
        {
            foreach (var invalid in Path.GetInvalidFileNameChars())
            {
                value = value.Replace(invalid, '_');
            }
            return value;
        }
    }
}
