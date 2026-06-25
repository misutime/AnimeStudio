using System;
using System.Collections.Generic;
using System.IO;

namespace AnimeStudio
{
    public class ObjectReader : EndianBinaryReader
    {
        private const int UnknownClassIdWarningLimit = 5;
        private static readonly object unknownClassIdWarningLock = new();
        private static readonly Dictionary<string, int> unknownClassIdWarningCounts = new(StringComparer.OrdinalIgnoreCase);

        public SerializedFile assetsFile;
        public Game Game;
        public long m_PathID;
        public long byteStart;
        public uint byteSize;
        public ClassIDType type;
        public int rawClassID;
        public SerializedType serializedType;
        public BuildTarget platform;
        public SerializedFileFormatVersion m_Version;

        public int[] version => assetsFile.version;
        public BuildType buildType => assetsFile.buildType;

        public ObjectReader(EndianBinaryReader reader, SerializedFile assetsFile, ObjectInfo objectInfo, Game game) : base(reader.BaseStream, reader.Endian)
        {
            this.assetsFile = assetsFile;
            Game = game;
            m_PathID = objectInfo.m_PathID;
            byteStart = objectInfo.byteStart;
            byteSize = objectInfo.byteSize;
            rawClassID = objectInfo.classID;
            if (Enum.IsDefined(typeof(ClassIDType), objectInfo.classID))
            {
                type = (ClassIDType)objectInfo.classID;
            }
            else
            {
                type = ClassIDType.UnknownType;
                LogUnknownClassID(objectInfo.classID, m_PathID, assetsFile.fileName);
            }
            serializedType = objectInfo.serializedType;
            platform = assetsFile.m_TargetPlatform;
            m_Version = assetsFile.header.m_Version;

            Logger.Verbose($"Initialized reader for {type} object with {m_PathID} in file {assetsFile.fileName} !!");
        }

        private static void LogUnknownClassID(int classID, long pathID, string fileName)
        {
            var key = $"{fileName}|{classID}";
            int count;
            lock (unknownClassIdWarningLock)
            {
                unknownClassIdWarningCounts.TryGetValue(key, out count);
                count++;
                unknownClassIdWarningCounts[key] = count;
            }

            var message = $"Unknown ClassIDType {classID} for object with PathID {pathID} in file {fileName}. The object is kept as UnknownType placeholder.";
            if (count <= UnknownClassIdWarningLimit)
            {
                Logger.Warning(message);
                return;
            }

            if (count == UnknownClassIdWarningLimit + 1)
            {
                // Naraka 这类游戏会在同一个 CAB 里放大量自定义脚本对象。
                // 保留前几条明细，再汇总限流，避免真正的模型/材质错误被日志刷屏淹没。
                Logger.Info($"Further Unknown ClassIDType {classID} warnings in file {fileName} are suppressed after {UnknownClassIdWarningLimit} sample object(s); affected objects are kept as UnknownType placeholders.");
                return;
            }

            Logger.Debug(message);
        }

        public override int Read(byte[] buffer, int index, int count)
        {
            var pos = Position - byteStart;
            if (pos + count > byteSize)
            {
                throw new EndOfStreamException("Unable to read beyond the end of the stream.");
            }
            return base.Read(buffer, index, count);
        }

        public void Reset()
        {
            Logger.Verbose($"Resetting reader position to object offset 0x{byteStart:X8}...");
            Position = byteStart;
        }

        public int BytesLeft()
        {
            return (int)(byteSize - (Position - byteStart));
        }

        public Vector3 ReadVector3()
        {
            if (version[0] > 5 || (version[0] == 5 && version[1] >= 4))
            {
                return new Vector3(ReadSingle(), ReadSingle(), ReadSingle());
            }
            else
            {
                return new Vector4(ReadSingle(), ReadSingle(), ReadSingle(), ReadSingle());
            }
        }

        public XForm ReadXForm()
        {
            var t = ReadVector3();
            var q = ReadQuaternion();
            var s = ReadVector3();

            return new XForm(t, q, s);
        }

        public XForm ReadXForm4()
        {
            var t = ReadVector4();
            var q = ReadQuaternion();
            var s = ReadVector4();

            return new XForm(t, q, s);
        }

        public Vector3[] ReadVector3Array(int length = 0)
        {
            if (length == 0)
            {
                length = ReadInt32();
            }
            return ReadArray(ReadVector3, length);
        }

        public XForm[] ReadXFormArray()
        {
            return ReadArray(ReadXForm, ReadInt32());
        }
    }
}
