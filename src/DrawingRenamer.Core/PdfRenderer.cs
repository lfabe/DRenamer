using System.Buffers.Binary;
using System.Runtime.InteropServices;

namespace DrawingRenamer;

public sealed record Raster(int Width, int Height, byte[] Pixels)
{
    // PDFium returns BGRx. BMP also gives Leptonica an in-memory input with no temporary files.
    internal byte[] ToBmp(int turns)
    {
        var width = (turns & 1) == 0 ? Width : Height;
        var height = (turns & 1) == 0 ? Height : Width;
        var bmp = new byte[54 + Pixels.Length];
        bmp[0] = (byte)'B';
        bmp[1] = (byte)'M';
        BinaryPrimitives.WriteInt32LittleEndian(bmp.AsSpan(2), bmp.Length);
        bmp[10] = 54;
        bmp[14] = 40;
        BinaryPrimitives.WriteInt32LittleEndian(bmp.AsSpan(18), width);
        BinaryPrimitives.WriteInt32LittleEndian(bmp.AsSpan(22), height);
        bmp[26] = 1;
        bmp[28] = 32;
        BinaryPrimitives.WriteInt32LittleEndian(bmp.AsSpan(38), 11811);
        BinaryPrimitives.WriteInt32LittleEndian(bmp.AsSpan(42), 11811);
        for (var y = 0; y < Height; y++)
        for (var x = 0; x < Width; x++)
        {
            var (dx, dy) = (turns & 3) switch
            {
                1 => (Height - 1 - y, x),
                2 => (Width - 1 - x, Height - 1 - y),
                3 => (y, Width - 1 - x),
                _ => (x, y)
            };
            var from = 4 * (y * Width + x);
            var to = 54 + 4 * ((height - 1 - dy) * width + dx);
            Pixels.AsSpan(from, 3).CopyTo(bmp.AsSpan(to, 3));
        }
        return bmp;
    }

    internal IEnumerable<Segment> FindLines(Area frame, CancellationToken token)
    {
        // Skip short runs so glyph strokes and drawing details do not dominate the table search.
        return Scan(horizontal: true).Concat(Scan(horizontal: false));

        IEnumerable<Segment> Scan(bool horizontal)
        {
            var rows = horizontal ? Height : Width;
            var columns = horizontal ? Width : Height;
            for (var row = 0; row < rows; row++)
            {
                token.ThrowIfCancellationRequested();
                var start = -1;
                var last = -1;
                for (var col = 0; col <= columns; col++)
                {
                    if (col < columns)
                    {
                        var index = horizontal ? row * Width + col : col * Width + row;
                        var i = index * 4;
                        if (Pixels[i] + Pixels[i + 1] + Pixels[i + 2] < 560)
                        {
                            if (start < 0) start = col;
                            last = col;
                            continue;
                        }
                    }
                    if (start < 0 || (col - last <= 2 && col < columns)) continue;
                    if (last - start >= 18)
                        yield return new(Map(start), Map(last));
                    start = -1;
                }

                PointD Map(int col) => horizontal
                    ? new(frame.X + col * frame.Width / Width, frame.Y + row * frame.Height / Height)
                    : new(frame.X + row * frame.Width / Width, frame.Y + col * frame.Height / Height);
            }
        }
    }
}

public sealed class PdfRenderer
{
    private static readonly object Gate = new();

    public Raster Render(string path, int number, Area crop, int maxEdge, CancellationToken token)
    {
        if (!crop.IsValid || number < 1 || maxEdge is < 32 or > 4096)
            throw new ArgumentOutOfRangeException(nameof(crop));
        lock (Gate)
        {
            token.ThrowIfCancellationRequested();
            Native.FPDF_InitLibrary();
            try
            {
                // Open with a managed stream so filenames use Windows Unicode path handling.
                using var input = File.OpenRead(path);
                if (input.Length > uint.MaxValue)
                    throw new InvalidDataException("PDF превышает 4 ГиБ.");
                Native.ReadBlock read = (_, position, buffer, size) =>
                {
                    try
                    {
                        input.Position = position;
                        return ReadBlock(input, buffer, size);
                    }
                    catch (IOException) { return 0; }
                };
                var access = new Native.FileAccess((uint)input.Length, read, IntPtr.Zero);
                var document = Native.FPDF_LoadCustomDocument(ref access, IntPtr.Zero);
                if (document == IntPtr.Zero)
                    throw new InvalidDataException(Native.FPDF_GetLastError() == 4
                        ? "PDF защищён паролем." : "PDFium не смог открыть документ.");
                try { return RenderPage(document, number, crop, maxEdge, token); }
                finally
                {
                    Native.FPDF_CloseDocument(document);
                    GC.KeepAlive(read);
                }
            }
            finally { Native.FPDF_DestroyLibrary(); }
        }
    }

    private static unsafe int ReadBlock(Stream stream, IntPtr buffer, uint size)
    {
        if (size > int.MaxValue) return 0;
        stream.ReadExactly(new Span<byte>((void*)buffer, (int)size));
        return 1;
    }

    private static Raster RenderPage(IntPtr document, int number, Area crop, int maxEdge,
        CancellationToken token)
    {
        var page = Native.FPDF_LoadPage(document, number - 1);
        if (page == IntPtr.Zero) throw new InvalidDataException("Не удалось открыть страницу PDF.");
        try
        {
            var pageWidth = Native.FPDF_GetPageWidthF(page);
            var pageHeight = Native.FPDF_GetPageHeightF(page);
            if (!float.IsFinite(pageWidth) || !float.IsFinite(pageHeight) || pageWidth <= 0 || pageHeight <= 0)
                throw new InvalidDataException("Некорректные размеры страницы.");
            var scale = Math.Min(maxEdge / Math.Max(pageWidth * crop.Width, pageHeight * crop.Height),
                100000 / Math.Max(pageWidth, pageHeight));
            var width = Math.Max(1, (int)Math.Round(pageWidth * crop.Width * scale));
            var height = Math.Max(1, (int)Math.Round(pageHeight * crop.Height * scale));
            var bitmap = Native.FPDFBitmap_Create(width, height, 0);
            if (bitmap == IntPtr.Zero) throw new InvalidOperationException("PDFium не выделил память.");
            try
            {
                Native.FPDFBitmap_FillRect(bitmap, 0, 0, width, height, 0xffffffff);
                Native.FPDF_RenderPageBitmap(bitmap, page, -(int)Math.Round(crop.X * pageWidth * scale),
                    -(int)Math.Round(crop.Y * pageHeight * scale), (int)Math.Ceiling(pageWidth * scale),
                    (int)Math.Ceiling(pageHeight * scale), 0, 0);
                token.ThrowIfCancellationRequested();
                var stride = Native.FPDFBitmap_GetStride(bitmap);
                var buffer = Native.FPDFBitmap_GetBuffer(bitmap);
                var pixels = new byte[width * height * 4];
                for (var y = 0; y < height; y++)
                    Marshal.Copy(buffer + y * stride, pixels, y * width * 4, width * 4);
                return new(width, height, pixels);
            }
            finally { Native.FPDFBitmap_Destroy(bitmap); }
        }
        finally { Native.FPDF_ClosePage(page); }
    }

    private static class Native
    {
        private const string Dll = "pdfium";
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate int ReadBlock(IntPtr state, uint position, IntPtr buffer, uint size);
        [StructLayout(LayoutKind.Sequential)]
        public readonly record struct FileAccess(uint Length, ReadBlock Read, IntPtr State);
        [DllImport(Dll)] public static extern void FPDF_InitLibrary();
        [DllImport(Dll)] public static extern void FPDF_DestroyLibrary();
        [DllImport(Dll)] public static extern IntPtr FPDF_LoadCustomDocument(ref FileAccess access, IntPtr password);
        [DllImport(Dll)] public static extern uint FPDF_GetLastError();
        [DllImport(Dll)] public static extern void FPDF_CloseDocument(IntPtr document);
        [DllImport(Dll)] public static extern IntPtr FPDF_LoadPage(IntPtr document, int index);
        [DllImport(Dll)] public static extern void FPDF_ClosePage(IntPtr page);
        [DllImport(Dll)] public static extern float FPDF_GetPageWidthF(IntPtr page);
        [DllImport(Dll)] public static extern float FPDF_GetPageHeightF(IntPtr page);
        [DllImport(Dll)] public static extern IntPtr FPDFBitmap_Create(int width, int height, int alpha);
        [DllImport(Dll)] public static extern int FPDFBitmap_FillRect(IntPtr bitmap, int x, int y, int width, int height, uint color);
        [DllImport(Dll)] public static extern void FPDF_RenderPageBitmap(IntPtr bitmap, IntPtr page, int x, int y,
            int width, int height, int rotation, int flags);
        [DllImport(Dll)] public static extern int FPDFBitmap_GetStride(IntPtr bitmap);
        [DllImport(Dll)] public static extern IntPtr FPDFBitmap_GetBuffer(IntPtr bitmap);
        [DllImport(Dll)] public static extern void FPDFBitmap_Destroy(IntPtr bitmap);
    }
}
