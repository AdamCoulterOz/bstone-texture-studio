using TextureStudio.Core.Formats;
using TextureStudio.Core.Imaging;
using TextureStudio.Core.Model;

namespace TextureStudio.Core.Games.BlakeStone;

/// <summary>Blake Stone's VSWAP container as an <see cref="IGameArchive"/>: the shared
/// Wolfenstein-family chunk table and tile codecs (<see cref="VswapFile"/>,
/// <see cref="TileDecoders"/>) read through this game's own VGA palette.</summary>
public sealed class VswapArchive : IGameArchive
{
    private readonly VswapFile _file;
    private readonly uint[] _palette = BlakeStonePalette.ToRgba();

    public VswapArchive(byte[] bytes, string sourceName)
    {
        _file = new VswapFile(bytes);
        SourceName = sourceName;
    }

    public string SourceName { get; }

    public int WallCount => _file.WallCount;

    public int SpriteCount => _file.SpriteCount;

    public bool IsEmpty(TileRef tile) =>
        _file.IsEmptyChunk(tile.Kind == TileKind.Wall
            ? tile.Index
            : _file.SpriteStart + tile.Index);

    public RgbaImage Decode(TileRef tile) => tile.Kind == TileKind.Wall
        ? TileDecoders.DecodeWall(_file.GetWallData(tile.Index), _palette)
        : TileDecoders.DecodeSprite(_file.GetSpriteData(tile.Index), _palette);
}
