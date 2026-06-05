namespace AnimeStudio
{
    public enum GraphicsFormat
    {
        None = 0,
        R8_SRGB = 1,
        R8G8_SRGB = 2,
        R8G8B8_SRGB = 3,
        R8G8B8A8_SRGB = 4,
        R8_UNorm = 5,
        R8G8_UNorm = 6,
        R8G8B8_UNorm = 7,
        R8G8B8A8_UNorm = 8,
        R8_UInt = 13,
        R8G8_UInt = 14,
        R8G8B8_UInt = 15,
        R8G8B8A8_UInt = 16,
        R16_UNorm = 21,
        R16G16_UNorm = 22,
        R16G16B16_UNorm = 23,
        R16G16B16A16_UNorm = 24,
        R16_UInt = 29,
        R16G16_UInt = 30,
        R16G16B16_UInt = 31,
        R16G16B16A16_UInt = 32,
        R16_SFloat = 45,
        R16G16_SFloat = 46,
        R16G16B16_SFloat = 47,
        R16G16B16A16_SFloat = 48,
        R32_SFloat = 49,
        R32G32_SFloat = 50,
        R32G32B32_SFloat = 51,
        R32G32B32A32_SFloat = 52,
        B8G8R8A8_SRGB = 53,
        B8G8R8A8_UNorm = 55,
        B8G8R8A8_UInt = 59,
        E5B9G9R9_UFloatPack32 = 69,
        RGBA_DXT1_SRGB = 88,
        RGBA_DXT1_UNorm = 89,
        RGBA_DXT3_SRGB = 90,
        RGBA_DXT3_UNorm = 91,
        RGBA_DXT5_SRGB = 92,
        RGBA_DXT5_UNorm = 93,
        R_BC4_UNorm = 94,
        RG_BC5_UNorm = 96,
        RGB_BC6H_UFloat = 98,
        RGB_BC6H_SFloat = 99,
        RGBA_BC7_SRGB = 100,
        RGBA_BC7_UNorm = 101,
        RGB_ETC_UNorm = 110,
        RGB_ETC2_SRGB = 111,
        RGB_ETC2_UNorm = 112,
        RGB_A1_ETC2_SRGB = 113,
        RGB_A1_ETC2_UNorm = 114,
        RGBA_ETC2_SRGB = 115,
        RGBA_ETC2_UNorm = 116,
        R_EAC_UNorm = 117,
        R_EAC_SNorm = 118,
        RG_EAC_UNorm = 119,
        RG_EAC_SNorm = 120,
        RGBA_ASTC4X4_SRGB = 121,
        RGBA_ASTC4X4_UNorm = 122,
        RGBA_ASTC5X5_SRGB = 123,
        RGBA_ASTC5X5_UNorm = 124,
        RGBA_ASTC6X6_SRGB = 125,
        RGBA_ASTC6X6_UNorm = 126,
        RGBA_ASTC8X8_SRGB = 127,
        RGBA_ASTC8X8_UNorm = 128,
        RGBA_ASTC10X10_SRGB = 129,
        RGBA_ASTC10X10_UNorm = 130,
        RGBA_ASTC12X12_SRGB = 131,
        RGBA_ASTC12X12_UNorm = 132,
        YUV2 = 133,
        VideoAuto = 144,
    }

    public static class GraphicsFormatExtensions
    {
        public static TextureFormat ToTextureFormat(this GraphicsFormat graphicsFormat)
        {
            switch (graphicsFormat)
            {
                case GraphicsFormat.R8_SRGB:
                case GraphicsFormat.R8_UInt:
                case GraphicsFormat.R8_UNorm:
                    return TextureFormat.R8;
                case GraphicsFormat.R8G8_SRGB:
                case GraphicsFormat.R8G8_UInt:
                case GraphicsFormat.R8G8_UNorm:
                    return TextureFormat.RG16;
                case GraphicsFormat.R8G8B8_SRGB:
                case GraphicsFormat.R8G8B8_UInt:
                case GraphicsFormat.R8G8B8_UNorm:
                    return TextureFormat.RGB24;
                case GraphicsFormat.R8G8B8A8_SRGB:
                case GraphicsFormat.R8G8B8A8_UInt:
                case GraphicsFormat.R8G8B8A8_UNorm:
                    return TextureFormat.RGBA32;
                case GraphicsFormat.R16_UInt:
                case GraphicsFormat.R16_UNorm:
                    return TextureFormat.R16;
                case GraphicsFormat.R16G16_UInt:
                case GraphicsFormat.R16G16_UNorm:
                    return TextureFormat.RG32;
                case GraphicsFormat.R16G16B16_UInt:
                case GraphicsFormat.R16G16B16_UNorm:
                    return TextureFormat.RGB48;
                case GraphicsFormat.R16G16B16A16_UInt:
                case GraphicsFormat.R16G16B16A16_UNorm:
                    return TextureFormat.RGBA64;
                case GraphicsFormat.R16_SFloat:
                    return TextureFormat.RHalf;
                case GraphicsFormat.R16G16_SFloat:
                    return TextureFormat.RGHalf;
                case GraphicsFormat.R16G16B16_SFloat:
                case GraphicsFormat.R16G16B16A16_SFloat:
                    return TextureFormat.RGBAHalf;
                case GraphicsFormat.R32_SFloat:
                    return TextureFormat.RFloat;
                case GraphicsFormat.R32G32_SFloat:
                    return TextureFormat.RGFloat;
                case GraphicsFormat.R32G32B32_SFloat:
                case GraphicsFormat.R32G32B32A32_SFloat:
                    return TextureFormat.RGBAFloat;
                case GraphicsFormat.B8G8R8A8_SRGB:
                case GraphicsFormat.B8G8R8A8_UInt:
                case GraphicsFormat.B8G8R8A8_UNorm:
                    return TextureFormat.BGRA32;
                case GraphicsFormat.E5B9G9R9_UFloatPack32:
                    return TextureFormat.RGB9e5Float;
                case GraphicsFormat.RGBA_DXT1_SRGB:
                case GraphicsFormat.RGBA_DXT1_UNorm:
                    return TextureFormat.DXT1;
                case GraphicsFormat.RGBA_DXT3_SRGB:
                case GraphicsFormat.RGBA_DXT3_UNorm:
                    return TextureFormat.DXT3;
                case GraphicsFormat.RGBA_DXT5_SRGB:
                case GraphicsFormat.RGBA_DXT5_UNorm:
                    return TextureFormat.DXT5;
                case GraphicsFormat.R_BC4_UNorm:
                    return TextureFormat.BC4;
                case GraphicsFormat.RG_BC5_UNorm:
                    return TextureFormat.BC5;
                case GraphicsFormat.RGB_BC6H_SFloat:
                case GraphicsFormat.RGB_BC6H_UFloat:
                    return TextureFormat.BC6H;
                case GraphicsFormat.RGBA_BC7_SRGB:
                case GraphicsFormat.RGBA_BC7_UNorm:
                    return TextureFormat.BC7;
                case GraphicsFormat.RGB_ETC_UNorm:
                    return TextureFormat.ETC_RGB4;
                case GraphicsFormat.RGB_ETC2_SRGB:
                case GraphicsFormat.RGB_ETC2_UNorm:
                    return TextureFormat.ETC2_RGB;
                case GraphicsFormat.RGB_A1_ETC2_SRGB:
                case GraphicsFormat.RGB_A1_ETC2_UNorm:
                    return TextureFormat.ETC2_RGBA1;
                case GraphicsFormat.RGBA_ETC2_SRGB:
                case GraphicsFormat.RGBA_ETC2_UNorm:
                    return TextureFormat.ETC2_RGBA8;
                case GraphicsFormat.R_EAC_UNorm:
                    return TextureFormat.EAC_R;
                case GraphicsFormat.R_EAC_SNorm:
                    return TextureFormat.EAC_R_SIGNED;
                case GraphicsFormat.RG_EAC_UNorm:
                    return TextureFormat.EAC_RG;
                case GraphicsFormat.RG_EAC_SNorm:
                    return TextureFormat.EAC_RG_SIGNED;
                case GraphicsFormat.RGBA_ASTC4X4_SRGB:
                case GraphicsFormat.RGBA_ASTC4X4_UNorm:
                    return TextureFormat.ASTC_RGBA_4x4;
                case GraphicsFormat.RGBA_ASTC5X5_SRGB:
                case GraphicsFormat.RGBA_ASTC5X5_UNorm:
                    return TextureFormat.ASTC_RGBA_5x5;
                case GraphicsFormat.RGBA_ASTC6X6_SRGB:
                case GraphicsFormat.RGBA_ASTC6X6_UNorm:
                    return TextureFormat.ASTC_RGBA_6x6;
                case GraphicsFormat.RGBA_ASTC8X8_SRGB:
                case GraphicsFormat.RGBA_ASTC8X8_UNorm:
                    return TextureFormat.ASTC_RGBA_8x8;
                case GraphicsFormat.RGBA_ASTC10X10_SRGB:
                case GraphicsFormat.RGBA_ASTC10X10_UNorm:
                    return TextureFormat.ASTC_RGBA_10x10;
                case GraphicsFormat.RGBA_ASTC12X12_SRGB:
                case GraphicsFormat.RGBA_ASTC12X12_UNorm:
                    return TextureFormat.ASTC_RGBA_12x12;
                case GraphicsFormat.YUV2:
                case GraphicsFormat.VideoAuto:
                    return TextureFormat.YUY2;
                default:
                    return 0;
            }
        }
    }
}
