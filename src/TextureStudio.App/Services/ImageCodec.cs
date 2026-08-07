using Microsoft.JSInterop;
using TextureStudio.Core.Imaging;

namespace TextureStudio.App.Services;

/// <summary>PNG encode/decode, clipboard, and download via browser interop.</summary>
public sealed class ImageCodec(IJSRuntime js)
{
    public async Task<RgbaImage> DecodePngAsync(byte[] png)
    {
        var result = await js.InvokeAsync<DecodedImage>("studioInterop.decodePng", (object)png);
        return new RgbaImage(result.Width, result.Height, result.Pixels);
    }

    public async Task<byte[]> EncodePngAsync(RgbaImage image) =>
        await js.InvokeAsync<byte[]>(
            "studioInterop.encodePng", image.Width, image.Height, (object)image.Pixels);

    public async Task<string> ToDataUrlAsync(RgbaImage image) =>
        "data:image/png;base64," + Convert.ToBase64String(await EncodePngAsync(image));

    public async Task CopyPngToClipboardAsync(byte[] png) =>
        await js.InvokeVoidAsync("studioInterop.copyPngToClipboard", (object)png);

    public async Task DownloadAsync(string name, byte[] bytes, string mime = "application/octet-stream") =>
        await js.InvokeVoidAsync("studioInterop.downloadFile", name, (object)bytes, mime);

    public async Task RegisterPasteHandlerAsync<T>(DotNetObjectReference<T> reference) where T : class =>
        await js.InvokeVoidAsync("studioInterop.registerPasteHandler", reference);

    /// <summary>Compose a sheet in the browser (canvas NN-scaling; no big marshals).</summary>
    public async Task<byte[]> ComposeSheetAsync(
        int width, int height, string background,
        IEnumerable<(int X, int Y, int Size, int SrcSize, byte[] Rgba, bool Matte)> tiles) =>
        await js.InvokeAsync<byte[]>("studioInterop.composeSheet", new
        {
            width,
            height,
            background,
            tiles = tiles.Select(t => new
            {
                x = t.X, y = t.Y, size = t.Size, srcSize = t.SrcSize, rgba = t.Rgba, matte = t.Matte,
            }).ToArray(),
        });

    /// <summary>Downscaled preview data URL; full-size pixels never enter .NET.</summary>
    public async Task<string> PreviewDataUrlAsync(byte[] png, int maxWidth) =>
        await js.InvokeAsync<string>("studioInterop.pngPreviewDataUrl", (object)png, maxWidth);

    /// <summary>Square grid composed from already-encoded PNGs (identity references).</summary>
    public async Task<byte[]> ComposePngGridAsync(IEnumerable<byte[]> pngs, int tilePx) =>
        await js.InvokeAsync<byte[]>("studioInterop.composePngGrid", pngs.ToArray(), tilePx);

    /// <summary>Image dimensions — PNG header fast-path, decoder fallback for other formats
    /// (Gemini sometimes returns JPEG/WebP despite the PNG request).</summary>
    public async Task<(int Width, int Height)> PngSizeAsync(byte[] png)
    {
        var size = await js.InvokeAsync<PngDimensions>("studioInterop.imageSize", (object)png);
        return (size.Width, size.Height);
    }

    /// <summary>Stroke revision outlines onto a PNG browser-side.</summary>
    public async Task<byte[]> AnnotatePngAsync(
        byte[] png, IEnumerable<(int X, int Y, int W, int H, string Color)> regions) =>
        await js.InvokeAsync<byte[]>("studioInterop.annotatePng", (object)png,
            regions.Select(r => new { x = r.X, y = r.Y, w = r.W, h = r.H, color = r.Color }).ToArray());

    private sealed record PngDimensions(int Width, int Height);

    private sealed record DecodedImage(int Width, int Height, byte[] Pixels);
}
