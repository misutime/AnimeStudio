using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using K4os.Hash.xxHash;

namespace AnimeStudio
{
    public static class Texture2DExtensions
    {
        private static Configuration _configuration;

        static Texture2DExtensions()
        {
            _configuration = Configuration.Default.Clone();
            _configuration.PreferContiguousImageBuffers = true;
        }

        public static string GetImageHash(this Texture2D m_Texture2D)
        {
            var image = m_Texture2D.ConvertToImage(true);
            var hashstring = "";
            if (image != null)
            {
                try
                {
                    // TODO: be not only for png's since people may not always use that format, but in the end the hash is still unique and different from the raw data one
                    using var ms = new MemoryStream();
                    image.Save(ms, new PngEncoder());
                    ms.Position = 0;
                    Span<byte> span = ms.GetBuffer().AsSpan(0, (int)ms.Length);
                    var hash = XXH64.DigestOf(span);
                    hashstring = hash.ToString("x");
                }
                catch
                {
                    hashstring = "";
                }

                image.Dispose();
            }

            return hashstring;
        }

        public static Image<Bgra32> ConvertToImage(this Texture2D m_Texture2D, bool flip)
        {
            return ConvertToImage(m_Texture2D, flip, null, null);
        }

        public static Image<Bgra32> ConvertToImage(this Texture2D m_Texture2D, bool flip, Func<string, IDictionary<string, object>, IDisposable> profileMeasure, IDictionary<string, object> profileData)
        {
            var converter = new Texture2DConverter(m_Texture2D);
            byte[] buff = ArrayPool<byte>.Shared.Rent(m_Texture2D.m_Width * m_Texture2D.m_Height * 4);
            try
            {
                var decoded = false;
                using (Measure(profileMeasure, "model_texture_decode", profileData))
                {
                    decoded = converter.DecodeTexture2D(buff);
                }

                if (decoded)
                {
                    Image<Bgra32> image;
                    using (Measure(profileMeasure, "model_texture_image_load", profileData))
                    {
                        image = Image.LoadPixelData<Bgra32>(_configuration, buff, m_Texture2D.m_Width, m_Texture2D.m_Height);
                    }
                    if (flip)
                    {
                        using (Measure(profileMeasure, "model_texture_flip", profileData))
                        {
                            image.Mutate(x => x.Flip(FlipMode.Vertical));
                        }
                    }
                    return image;
                }
                return null;
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buff, true);
            }
        }

        public static MemoryStream ConvertToStream(this Texture2D m_Texture2D, ImageFormat imageFormat, bool flip)
        {
            return ConvertToStream(m_Texture2D, imageFormat, flip, null, null);
        }

        public static MemoryStream ConvertToStream(this Texture2D m_Texture2D, ImageFormat imageFormat, bool flip, Func<string, IDictionary<string, object>, IDisposable> profileMeasure, IDictionary<string, object> profileData)
        {
            var image = ConvertToImage(m_Texture2D, flip, profileMeasure, profileData);
            if (image != null)
            {
                using (image)
                {
                    var encodeData = CopyProfileData(profileData);
                    encodeData["imageFormat"] = imageFormat.ToString();
                    var stage = "model_texture_encode_" + imageFormat.ToString().ToLowerInvariant();
                    using (Measure(profileMeasure, stage, encodeData))
                    {
                        var stream = image.ConvertToStream(imageFormat);
                        encodeData["outputBytes"] = stream.Length;
                        return stream;
                    }
                }
            }
            return null;
        }

        public static MemoryStream ConvertRawTextureToStream(byte[] rawData, int width, int height, TextureFormat textureFormat, ImageFormat imageFormat, bool flip, int[] version = null, BuildTarget platform = BuildTarget.NoTarget)
        {
            using var image = ConvertRawTextureToImage(rawData, width, height, textureFormat, flip, version, platform);
            return image?.ConvertToStream(imageFormat);
        }

        public static Image<Bgra32> ConvertRawTextureToImage(byte[] rawData, int width, int height, TextureFormat textureFormat, bool flip, int[] version = null, BuildTarget platform = BuildTarget.NoTarget)
        {
            return ConvertRawTextureToImage(rawData, width, height, textureFormat, flip, version, platform, null, null);
        }

        public static Image<Bgra32> ConvertRawTextureToImage(byte[] rawData, int width, int height, TextureFormat textureFormat, bool flip, int[] version = null, BuildTarget platform = BuildTarget.NoTarget, Func<string, IDictionary<string, object>, IDisposable> profileMeasure = null, IDictionary<string, object> profileData = null)
        {
            var converter = new Texture2DConverter(rawData, width, height, textureFormat, version, platform);
            byte[] buff = ArrayPool<byte>.Shared.Rent(width * height * 4);
            try
            {
                var decoded = false;
                using (Measure(profileMeasure, "model_texture_decode", profileData))
                {
                    decoded = converter.DecodeTexture2D(buff);
                }

                if (decoded)
                {
                    Image<Bgra32> image;
                    using (Measure(profileMeasure, "model_texture_image_load", profileData))
                    {
                        image = Image.LoadPixelData<Bgra32>(_configuration, buff, width, height);
                    }
                    if (flip)
                    {
                        using (Measure(profileMeasure, "model_texture_flip", profileData))
                        {
                            image.Mutate(x => x.Flip(FlipMode.Vertical));
                        }
                    }
                    return image;
                }
                return null;
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buff, true);
            }
        }

        private static IDisposable Measure(Func<string, IDictionary<string, object>, IDisposable> profileMeasure, string stage, IDictionary<string, object> profileData)
        {
            return profileMeasure?.Invoke(stage, profileData);
        }

        private static Dictionary<string, object> CopyProfileData(IDictionary<string, object> profileData)
        {
            return profileData == null
                ? new Dictionary<string, object>()
                : new Dictionary<string, object>(profileData);
        }
    }
}
