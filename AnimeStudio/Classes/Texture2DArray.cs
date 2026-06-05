using System.Collections.Generic;

namespace AnimeStudio
{
    public sealed class Texture2DArray : Texture
    {
        public int m_Width;
        public int m_Height;
        public int m_Depth;
        public GraphicsFormat m_Format;
        public int m_MipCount;
        public uint m_DataSize;
        public GLTextureSettings m_TextureSettings;
        public int m_ColorSpace;
        public ResourceReader image_data;
        public StreamingInfo m_StreamData;
        public List<Texture2D> TextureList;

        public Texture2DArray(ObjectReader reader) : base(reader)
        {
            if (version[0] >= 2019)
            {
                m_ColorSpace = reader.ReadInt32();
                m_Format = (GraphicsFormat)reader.ReadInt32();
            }

            m_Width = reader.ReadInt32();
            m_Height = reader.ReadInt32();
            m_Depth = reader.ReadInt32();

            if (version[0] < 2019)
            {
                m_Format = (GraphicsFormat)reader.ReadInt32();
            }

            m_MipCount = reader.ReadInt32();
            if (version[0] > 2023 || (version[0] == 2023 && version[1] >= 2))
            {
                var m_MipsStripped = reader.ReadInt32();
            }

            m_DataSize = reader.ReadUInt32();
            m_TextureSettings = new GLTextureSettings(reader);

            if (version[0] < 2019)
            {
                m_ColorSpace = reader.ReadInt32();
            }

            if (version[0] > 2020 || (version[0] == 2020 && version[1] >= 2))
            {
                var m_UsageMode = reader.ReadInt32();
            }

            var m_IsReadable = reader.ReadBoolean();
            if (version[0] > 2023 || (version[0] == 2023 && version[1] > 2))
            {
                var m_IgnoreMipmapLimit = reader.ReadBoolean();
                reader.AlignStream();
                var m_MipmapLimitGroupName = reader.ReadAlignedString();
            }
            else
            {
                reader.AlignStream();
            }

            var imageDataSize = reader.ReadInt32();
            if (imageDataSize == 0 && (version[0] > 5 || (version[0] == 5 && version[1] >= 6)))
            {
                m_StreamData = new StreamingInfo(reader);
            }

            image_data = !string.IsNullOrEmpty(m_StreamData?.path)
                ? new ResourceReader(m_StreamData.path, assetsFile, m_StreamData.offset, m_StreamData.size)
                : new ResourceReader(reader, reader.BaseStream.Position, imageDataSize);

            TextureList = new List<Texture2D>();
            BuildLayerImages();
        }

        private void BuildLayerImages()
        {
            if (m_Depth <= 0 || m_DataSize == 0)
            {
                return;
            }

            for (var layer = 0; layer < m_Depth; layer++)
            {
                TextureList.Add(new Texture2D(this, layer));
            }
        }
    }
}
