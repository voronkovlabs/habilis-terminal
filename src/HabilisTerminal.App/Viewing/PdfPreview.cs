using System.IO;
using Avalonia.Media.Imaging;
using Windows.Data.Pdf;
using Windows.Storage.Streams;

namespace HabilisTerminal.App.Viewing;

/// <summary>
/// Ctrl+Q on a PDF (issue #110): pages rendered to bitmaps by Windows' own PDF engine, off the UI thread, so moving the
/// cursor over PDF files stays quick. Password-protected and broken files fail with the engine's exception.
/// </summary>
public sealed class PdfPreview : IDisposable
{
    private readonly FileStream _file;
    private readonly PdfDocument _document;

    private PdfPreview(FileStream file, PdfDocument document)
    {
        _file = file;
        _document = document;
    }

    public int PageCount => (int)_document.PageCount;

    public static Task<PdfPreview> OpenAsync(string path) => Task.Run(async () =>
    {
        // Shared for writing and deleting: previewing must not lock a file another program is working on.
        var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        try
        {
            return new PdfPreview(file, await PdfDocument.LoadFromStreamAsync(file.AsRandomAccessStream()));
        }
        catch
        {
            await file.DisposeAsync();
            throw;
        }
    });

    /// <summary>The page's size at 100%, in device-independent units (1/96 inch).</summary>
    public Avalonia.Size PageSize(int index)
    {
        using var page = _document.GetPage((uint)index);
        return new Avalonia.Size(page.Size.Width, page.Size.Height);
    }

    /// <summary>The page fitted into the given pixels, keeping its proportions.</summary>
    public Task<Bitmap> RenderAsync(int index, double width, double height) => Task.Run<Bitmap>(async () =>
    {
        using var page = _document.GetPage((uint)index);
        var scale = Math.Min(width / page.Size.Width, height / page.Size.Height);
        var options = new PdfPageRenderOptions
        {
            DestinationWidth = (uint)Math.Max(1, page.Size.Width * scale),
            DestinationHeight = (uint)Math.Max(1, page.Size.Height * scale),
        };
        using var output = new InMemoryRandomAccessStream();
        await page.RenderToStreamAsync(output, options);
        output.Seek(0);
        using var png = output.AsStreamForRead();
        return new Bitmap(png);
    });

    public void Dispose() => _file.Dispose();
}
