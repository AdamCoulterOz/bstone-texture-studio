// VSWAP parsing ported from bstone (bstone_vswap.cpp, GPL-2.0-or-later,
// Apogee Entertainment / bstone contributors).
//
// File format:
//   u16 chunk_count
//   u16 sprite_start
//   u16 sound_start
//   u32 chunk_offsets[chunk_count]
//   u16 chunk_sizes[chunk_count]
//   u8  data[...]
namespace TextureStudio.Core.Formats;

public sealed class VswapFile
{
    private readonly byte[] _bytes;
    private readonly int[] _offsets;
    private readonly int[] _sizes;

    public int ChunkCount { get; }
    public int SpriteStart { get; }
    public int SoundStart { get; }
    public int WallCount => SpriteStart;
    public int SpriteCount => SoundStart - SpriteStart;

    public VswapFile(byte[] bytes)
    {
        if (bytes.Length < 6)
        {
            throw new InvalidDataException("File too small for a VSWAP header.");
        }
        _bytes = bytes;
        ChunkCount = ReadU16(0);
        SpriteStart = ReadU16(2);
        SoundStart = ReadU16(4);
        if (ChunkCount is < 0 or > 1400 || SpriteStart > SoundStart || SoundStart > ChunkCount)
        {
            throw new InvalidDataException("Invalid VSWAP header — is this a VSWAP.BS6/VSWAP.VSI file?");
        }
        var dataStart = 6 + ChunkCount * 4 + ChunkCount * 2;
        if (dataStart > bytes.Length)
        {
            throw new InvalidDataException("VSWAP chunk table exceeds file size.");
        }
        _offsets = new int[ChunkCount];
        _sizes = new int[ChunkCount];
        for (var i = 0; i < ChunkCount; i++)
        {
            _offsets[i] = ReadS32(6 + i * 4);
            _sizes[i] = ReadU16(6 + ChunkCount * 4 + i * 2);
            if (_sizes[i] > 0 &&
                (_offsets[i] < dataStart || _offsets[i] + _sizes[i] > bytes.Length))
            {
                throw new InvalidDataException($"Chunk {i} boundaries are outside the file.");
            }
        }
    }

    public ReadOnlySpan<byte> GetWallData(int index) => GetChunk(index);

    public ReadOnlySpan<byte> GetSpriteData(int index) => GetChunk(SpriteStart + index);

    /// <summary>Empty chunks (size 0) exist in the table; callers should skip them.</summary>
    public bool IsEmptyChunk(int index) => _sizes[index] == 0;

    private ReadOnlySpan<byte> GetChunk(int index) =>
        _bytes.AsSpan(_offsets[index], _sizes[index]);

    private int ReadU16(int offset) => _bytes[offset] | (_bytes[offset + 1] << 8);

    private int ReadS32(int offset) =>
        _bytes[offset] | (_bytes[offset + 1] << 8) | (_bytes[offset + 2] << 16) | (_bytes[offset + 3] << 24);
}
