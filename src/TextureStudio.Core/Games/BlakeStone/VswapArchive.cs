using TextureStudio.Core.Formats;
using TextureStudio.Core.Imaging;

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
        // Walls first, then sprites, each in engine index order — the studio treats this
        // order as canonical when sorting an item's frames.
        var tiles = new List<GameTile>();
        for (var i = 0; i < _file.WallCount; i++)
        {
            if (!_file.IsEmptyChunk(i))
            {
                tiles.Add(new GameTile(BlakeStoneTiles.WallId(i), Model.TileKind.Full));
            }
        }
        for (var i = 0; i < _file.SpriteCount; i++)
        {
            if (!_file.IsEmptyChunk(_file.SpriteStart + i))
            {
                tiles.Add(new GameTile(BlakeStoneTiles.SpriteId(i), Model.TileKind.Cutout));
            }
        }
        Tiles = tiles;
    }

    public string SourceName { get; }

    public IReadOnlyList<GameTile> Tiles { get; }

    /// <summary>Sprite count as the archive holds it, including empty slots — the edition
    /// heuristic counts addressable chunks, not the ones that happen to have art.</summary>
    internal int RawSpriteCount => _file.SpriteCount;

    public RgbaImage Decode(string tileId)
    {
        var index = BlakeStoneTiles.IndexOf(tileId);
        if (index < 0)
        {
            throw new InvalidDataException($"'{tileId}' is not a Blake Stone tile id.");
        }
        return BlakeStoneTiles.IsWall(tileId)
            ? TileDecoders.DecodeWall(_file.GetWallData(index), _palette)
            : TileDecoders.DecodeSprite(_file.GetSpriteData(index), _palette);
    }
}
