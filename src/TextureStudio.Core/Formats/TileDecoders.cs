// Wall and sprite decoding ported from bstone (bstone_sprite.cpp / image extractor,
// GPL-2.0-or-later, Apogee Entertainment / bstone contributors).
using TextureStudio.Core.Imaging;

namespace TextureStudio.Core.Formats;

public static class TileDecoders
{
    public const int Dimension = 64;

    /// <summary>Walls are 64x64 column-major 8-bit palette indices, 4096 bytes.</summary>
    public static RgbaImage DecodeWall(ReadOnlySpan<byte> data, uint[] palette)
    {
        if (data.Length < Dimension * Dimension)
        {
            throw new InvalidDataException($"Wall chunk is {data.Length} bytes; expected {Dimension * Dimension}.");
        }
        var img = new RgbaImage(Dimension, Dimension);
        for (var x = 0; x < Dimension; x++)
        {
            for (var y = 0; y < Dimension; y++)
            {
                WritePalettePixel(img, x, y, palette[data[x * Dimension + y]]);
            }
        }
        return img;
    }

    /// <summary>Sprites are column posts: u16 left, u16 right, u16 columnOffsets[width],
    /// then per column repeated (u16 end*2, u16 pixelsOffset, u16 start*2) until end==0.
    /// Untouched pixels are transparent. Returns a full 64x64 RGBA with alpha.</summary>
    public static RgbaImage DecodeSprite(ReadOnlySpan<byte> data, uint[] palette)
    {
        var left = ReadU16(data, 0);
        var right = ReadU16(data, 2);
        if (left > right || left >= Dimension || right >= Dimension)
        {
            throw new InvalidDataException("Invalid sprite edges.");
        }
        var img = new RgbaImage(Dimension, Dimension);
        var width = right - left + 1;
        for (var i = 0; i < width; i++)
        {
            var commandsOffset = ReadU16(data, 4 + i * 2);
            var pos = commandsOffset;
            while (true)
            {
                var end = ReadU16(data, pos) / 2;
                if (end == 0)
                {
                    break;
                }
                var pixelsOffset = ReadU16(data, pos + 2);
                var start = ReadU16(data, pos + 4) / 2;
                pos += 6;
                var count = end - start;
                var srcOffset = (pixelsOffset + start) % 0x10000;
                if (srcOffset + count > data.Length || end > Dimension)
                {
                    throw new InvalidDataException("Sprite post outside chunk bounds.");
                }
                for (var y = start; y < end; y++)
                {
                    WritePalettePixel(img, left + i, y, palette[data[srcOffset + (y - start)]]);
                }
            }
        }
        return img;
    }

    private static void WritePalettePixel(RgbaImage img, int x, int y, uint rgba)
    {
        var o = img.Offset(x, y);
        img.Pixels[o] = (byte)rgba;
        img.Pixels[o + 1] = (byte)(rgba >> 8);
        img.Pixels[o + 2] = (byte)(rgba >> 16);
        img.Pixels[o + 3] = (byte)(rgba >> 24);
    }

    private static int ReadU16(ReadOnlySpan<byte> data, int offset) =>
        data[offset] | (data[offset + 1] << 8);
}
