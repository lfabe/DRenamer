using System.Runtime.InteropServices;

namespace DrawingRenamer;

public sealed record Raster(int Width, int Height, byte[] Pixels)
{
    internal Raster ReadScanField(Area field, Area frame, double slope, bool filterAnnotations)
    {
        var box = new Area((field.X - frame.X) * Width / frame.Width, (field.Y - frame.Y) * Height / frame.Height,
            field.Width * Width / frame.Width, field.Height * Height / frame.Height);
        var w = Math.Max(1, (int)Math.Round(box.Width));
        var h = Math.Max(1, (int)Math.Round(box.Height));
        var cx = box.X + box.Width / 2 - .5;
        var cy = box.Y + box.Height / 2 - .5;
        var result = new Raster(w, h, new byte[4 * w * h]);
        var ink = 0;
        var colored = 0;
        for (var y = 0; y < h; y++)
        for (var x = 0; x < w; x++)
        {
            var u = x - (w - 1) * .5;
            var v = y - (h - 1) * .5;
            var ix = Math.Clamp((int)Math.Round(cx + u - slope * v), 0, Width - 1);
            var iy = Math.Clamp((int)Math.Round(cy + v + slope * u), 0, Height - 1);
            var pixel = Pixels.AsSpan(4 * (ix + iy * Width), 4);
            pixel.CopyTo(result.Pixels.AsSpan(4 * (x + y * w)));
            if (filterAnnotations && pixel[0] + pixel[1] + pixel[2] < 560)
            {
                ink++;
                if (pixel[0] - pixel[2] > 20) colored++;
            }
        }
        // Blue pen from a signature can cross a cell containing otherwise monochrome lettering.
        if (colored > 0 && colored < ink * .20)
            for (var y = 0; y < h; y++)
            for (var x = 0; x < w / 16; x++)
            {
                var p = 4 * (y * w + x);
                if (result.Pixels[p] - result.Pixels[p + 2] > 20) result.Pixels.AsSpan(p, 3).Fill(255);
            }
        return result;
    }

    internal Raster Orient(int turns, double horizontalShear = 0)
    {
        turns &= 3;
        if (turns == 0 && horizontalShear == 0) return this;
        var rotatedWidth = (turns & 1) == 0 ? Width : Height;
        var rotatedHeight = (turns & 1) == 0 ? Height : Width;
        var width = rotatedWidth + (int)Math.Round((rotatedHeight - 1) * horizontalShear);
        var result = new Raster(width, rotatedHeight, new byte[4 * width * rotatedHeight]);
        if (horizontalShear != 0) result.Pixels.AsSpan().Fill(255);
        for (var y = 0; y < Height; y++)
        for (var x = 0; x < Width; x++)
        {
            var (dx, dy) = turns switch
            {
                1 => (Height - 1 - y, x),
                2 => (Width - 1 - x, Height - 1 - y),
                3 => (y, Width - 1 - x),
                _ => (x, y)
            };
            var from = 4 * (y * Width + x);
            var shift = (int)Math.Round(dy * horizontalShear);
            var to = 4 * (dy * width + dx + shift);
            Pixels.AsSpan(from, 4).CopyTo(result.Pixels.AsSpan(to, 4));
        }
        return result;
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

    public int CountPages(Stream input, CancellationToken token) =>
        WithDocument(input, Native.FPDF_GetPageCount, token);

    internal (double Width, double Height) PageSize(Stream input, int number, CancellationToken token) =>
        WithDocument<(double, double)>(input, document =>
        {
            var page = Native.FPDF_LoadPage(document, number - 1);
            if (page == IntPtr.Zero) throw new InvalidDataException("Не удалось открыть страницу PDF.");
            try { return (Native.FPDF_GetPageWidthF(page), Native.FPDF_GetPageHeightF(page)); }
            finally { Native.FPDF_ClosePage(page); }
        }, token);

    public Raster Render(string path, int number, Area crop, int maxEdge, CancellationToken token) =>
        Render(path, number, crop, maxEdge, token, 0);

    internal Raster Render(string path, int number, Area crop, int maxEdge, CancellationToken token, int turn)
    {
        if (!crop.IsValid || number < 1 || maxEdge is < 32 or > 4096)
            throw new ArgumentOutOfRangeException(nameof(crop));
        token.ThrowIfCancellationRequested();
        // Open with a managed stream so filenames use Windows Unicode path handling.
        using var input = File.OpenRead(path);
        return WithDocument(input, document => RenderPage(document, number, crop, maxEdge, token, turn), token);
    }

    public PageAlignment? FindPageAlignment(Stream input, int number, IReadOnlyList<TitleTemplate>? templates, CancellationToken token)
    {
        var raster = WithDocument(input, document => RenderPage(document, number, Area.Full, 2300, token, 0), token);
        var (width, height) = PageSize(input, number, token);
        return FindPageAlignment(raster, width, height, templates, token);
    }

    internal static PageAlignment? FindPageAlignment(Raster raster, double width, double height,
        IReadOnlyList<TitleTemplate>? templates, CancellationToken token)
    {
        var slope = ScanDeskew.FindFrameSlope(raster, token);
        if (slope is null)
        {
            var stampSlopes = ScanTitleLocator.Find(raster, width, height, templates ?? TitleTemplate.Defaults,
                turn => raster.Orient(turn), token).Select(stamp => stamp.Slope).Distinct().ToArray();
            if (stampSlopes.Length == 0 || stampSlopes.Max() - stampSlopes.Min() > .006) return null;
            slope = stampSlopes.Average();
        }
        return PageAlignment.ForScan(raster, -Math.Atan(slope.Value) * 180 / Math.PI, token);
    }

    public PageCrop? FindPageCrop(Stream input, int number, int turns, PageAlignment alignment, Area? stamp, CancellationToken token)
    {
        var raster = WithDocument(input, document => RenderPage(document, number, Area.Full, 2300, token, turns), token);
        var (width, height) = PageSize(input, number, token);
        if ((turns & 1) != 0) (width, height) = (height, width);
        var upright = PageCrop.Resample(raster, Area.Full, raster.Width, raster.Height, alignment, token);
        var block = stamp is { IsValid: true } hint ? alignment.Bounds(hint.Rotate(turns), raster.Width, raster.Height) : (Area?)null;
        return DrawingFrameCrop.Find(upright, width, height, turns, block, token);
    }

    private static unsafe void AlignPage(IntPtr document, int number, PageAlignment alignment, int turns)
    {
        if (!double.IsFinite(alignment.Angle) || Math.Abs(alignment.Angle) > 4 ||
            !double.IsFinite(alignment.Scale) || alignment.Scale is <= 0 or > 1)
            throw new InvalidDataException("Указаны недопустимые параметры выравнивания листа.");
        var page = Native.FPDF_LoadPage(document, number - 1);
        if (page == IntPtr.Zero) throw new InvalidDataException("Не удалось открыть страницу PDF.");
        try
        {
            if (Native.FPDF_GetPageBoundingBox(page, out var box) == 0)
                throw new InvalidDataException("Не удалось определить границы страницы PDF.");
            if (alignment.Crop is { } crop)
            {
                CropPage(page, box, alignment, crop);
                return;
            }
            var x = ((double)box.Left + box.Right) / 2;
            var y = ((double)box.Bottom + box.Top) / 2;
            // PDF coordinates point upward, opposite to the clockwise angle in the preview.
            var (sin, cos) = Math.SinCos(-alignment.Angle * Math.PI / 180);
            sin *= alignment.Scale;
            cos *= alignment.Scale;
            var matrix = new Native.Matrix((float)cos, (float)sin, (float)-sin, (float)cos,
                (float)(x - cos * x + sin * y), (float)(y - sin * x - cos * y));
            // Clip both before and after the transform, preserving the source page's visible crop.
            if (Native.FPDFPage_TransFormWithClip(page, null, in box) == 0 ||
                Native.FPDFPage_TransFormWithClip(page, &matrix, in box) == 0)
                throw new InvalidDataException($"Не удалось выровнять содержимое листа {number}.");
            Native.FPDFPage_TransformAnnots(page, matrix.A, matrix.B, matrix.C, matrix.D, matrix.E, matrix.F);
            if (turns != 0) Native.FPDFPage_SetRotation(page, (Native.FPDFPage_GetRotation(page) + turns) % 4);
        }
        finally { Native.FPDF_ClosePage(page); }
    }

    private static unsafe void CropPage(IntPtr page, Native.PageBox box, PageAlignment alignment, PageCrop crop)
    {
        var bounds = crop.Bounds;
        if (crop.WidthMm is < 100 or > 3000 || crop.HeightMm is < 100 or > 3000 || crop.Turns is < 0 or > 3 ||
            !double.IsFinite(bounds.X) || !double.IsFinite(bounds.Y) || !double.IsFinite(bounds.Width) ||
            !double.IsFinite(bounds.Height) || bounds.X is < -.4 or > .5 || bounds.Y is < -.4 or > .5 ||
            bounds.Width is < .35 or > 1.4 || bounds.Height is < .35 or > 1.4)
            throw new InvalidDataException("Недопустимые границы обрезки.");
        var width = (double)box.Right - box.Left;
        var height = (double)box.Top - box.Bottom;
        var centerX = box.Left + width / 2;
        var centerY = box.Bottom + height / 2;
        var quarter = (Native.FPDFPage_GetRotation(page) + crop.Turns) & 3;
        var (viewW, viewH) = (quarter & 1) == 0 ? (width, height) : (height, width);
        var outputW = (float)(crop.WidthMm * PageCrop.PointsPerMm);
        var outputH = (float)(crop.HeightMm * PageCrop.PointsPerMm);
        var (sin, cos) = Math.SinCos((quarter * 90 + alignment.Angle) * Math.PI / 180);
        var scaleX = outputW / (bounds.Width * viewW);
        var scaleY = outputH / (bounds.Height * viewH);
        var a = cos * alignment.Scale * scaleX;
        var b = -sin * alignment.Scale * scaleY;
        var c = sin * alignment.Scale * scaleX;
        var d = cos * alignment.Scale * scaleY;
        var matrix = new Native.Matrix((float)a, (float)b, (float)c, (float)d,
            (float)((.5 - bounds.X) * viewW * scaleX - a * centerX - c * centerY),
            (float)(outputH - (.5 - bounds.Y) * viewH * scaleY - b * centerX - d * centerY));
        var outputBox = new Native.PageBox(0, outputH, outputW, 0);
        if (Native.FPDFPage_TransFormWithClip(page, null, in box) == 0 ||
            Native.FPDFPage_TransFormWithClip(page, &matrix, in outputBox) == 0)
            throw new InvalidDataException("Не удалось обрезать содержимое страницы PDF.");
        Native.FPDFPage_TransformAnnots(page, matrix.A, matrix.B, matrix.C, matrix.D, matrix.E, matrix.F);
        Native.FPDFPage_SetRotation(page, 0);
        Native.FPDFPage_SetMediaBox(page, 0, 0, outputW, outputH);
        Native.FPDFPage_SetCropBox(page, 0, 0, outputW, outputH);
        Native.FPDFPage_SetTrimBox(page, 0, 0, outputW, outputH);
        Native.FPDFPage_SetBleedBox(page, 0, 0, outputW, outputH);
        Native.FPDFPage_SetArtBox(page, 0, 0, outputW, outputH);
    }

    internal static void SavePageEdits(Stream input, Stream output, IReadOnlyDictionary<int, int>? rotations,
        IReadOnlyDictionary<int, PageAlignment>? alignments)
    {
        WithDocument(input, document =>
        {
            const uint modifyContents = 1 << 3;
            const uint assemblePages = 1 << 10;
            var permissions = Native.FPDF_GetDocPermissions(document);
            var rotationPermission = Native.FPDF_GetSecurityHandlerRevision(document) < 3 ? modifyContents : assemblePages;
            if ((rotations?.Count > 0 && (permissions & rotationPermission) == 0) ||
                (alignments?.Count > 0 && (permissions & modifyContents) == 0))
                throw new InvalidDataException("Параметры защиты PDF запрещают менять листы.");

            var count = Native.FPDF_GetPageCount(document);
            foreach (var number in (rotations?.Keys ?? []).Union(alignments?.Keys ?? []))
            {
                var turns = rotations?.GetValueOrDefault(number) ?? 0;
                if (number < 1 || number > count || turns is < 0 or > 3)
                    throw new InvalidDataException("Указан недопустимый номер листа или угол поворота.");
                if (alignments?.TryGetValue(number, out var alignment) == true)
                {
                    AlignPage(document, number, alignment, turns);
                    continue;
                }
                var page = Native.FPDF_LoadPage(document, number - 1);
                if (page == IntPtr.Zero) throw new InvalidDataException("Не удалось открыть страницу PDF.");
                try { Native.FPDFPage_SetRotation(page, (Native.FPDFPage_GetRotation(page) + turns) % 4); }
                finally { Native.FPDF_ClosePage(page); }
            }

            const uint incremental = 1;
            return Save(document, output, incremental);
        });
    }

    public static string[] SplitPages(string path)
    {
        path = Path.GetFullPath(path);
        using var input = File.OpenRead(path);
        return WithDocument(input, source =>
        {
            var count = Native.FPDF_GetPageCount(source);
            if (count < 1) throw new InvalidDataException("В PDF нет страниц.");
            if (count == 1) return [path];
            var stem = Path.Combine(Path.GetDirectoryName(path)!, Path.GetFileNameWithoutExtension(path));
            var pages = Enumerable.Range(1, count).Select(number => $"{stem}_{number}.pdf").ToArray();
            var collision = pages.FirstOrDefault(File.Exists);
            if (collision is not null) throw new IOException($"Файл уже существует: {Path.GetFileName(collision)}.");
            var temporary = pages.Select(_ => Path.Combine(Path.GetDirectoryName(path)!,
                $".drawing-renamer-{Guid.NewGuid():N}.tmp")).ToArray();
            var written = new List<string>();
            try
            {
                // Stage alongside the source so published pages can be renamed atomically.
                for (var index = 0; index < count; index++)
                {
                    using var output = new FileStream(temporary[index], FileMode.CreateNew);
                    CreatePdf(output, target => ImportPage(target, source, index, 0, 0));
                    output.Flush(flushToDisk: true);
                }
                for (var index = 0; index < count; index++)
                {
                    File.Move(temporary[index], pages[index]);
                    written.Add(pages[index]);
                }
                return pages;
            }
            catch
            {
                foreach (var page in written) File.Delete(page);
                throw;
            }
            finally { foreach (var file in temporary) File.Delete(file); }
        });
    }

    public static void MergePages(string path, IReadOnlyList<(string Path, string Hash, int Turn, PageAlignment? Alignment)> pages)
    {
        if (pages.Count < 2) throw new ArgumentException("Выберите хотя бы два листа PDF.", nameof(pages));
        path = Path.GetFullPath(path);
        var temporary = Path.Combine(Path.GetDirectoryName(path)!, $".drawing-renamer-{Guid.NewGuid():N}.tmp");
        try
        {
            using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                WithLibrary(() => CreatePdf(output, target =>
                {
                    for (var index = 0; index < pages.Count; index++)
                    {
                        var (sourcePath, hash, turn, alignment) = pages[index];
                        if (turn is < 0 or > 3) throw new InvalidDataException("Указан недопустимый угол поворота листа.");
                        using var input = File.OpenRead(sourcePath);
                        if (RenameService.Hash(input) != hash)
                            throw new InvalidDataException($"PDF изменился: {Path.GetFileName(sourcePath)}. Распознайте его повторно.");
                        LoadDocument(input, source =>
                        {
                            if (Native.FPDF_GetPageCount(source) != 1)
                                throw new InvalidDataException("Для объединения выберите одностраничные PDF.");
                            if (alignment is not null && (Native.FPDF_GetDocPermissions(source) & (1u << 3)) == 0)
                                throw new InvalidDataException("Параметры защиты PDF запрещают менять листы.");
                            ImportPage(target, source, 0, index, alignment?.Crop is null ? turn : 0);
                            if (alignment is { } adjusted) AlignPage(target, index + 1, adjusted, 0);
                            return true;
                        });
                    }
                }), default);
                output.Flush(flushToDisk: true);
            }
            File.Move(temporary, path);
        }
        finally { File.Delete(temporary); }
    }

    private static int CreatePdf(Stream output, Action<IntPtr> populate)
    {
        var document = Native.FPDF_CreateNewDocument();
        if (document == IntPtr.Zero) throw new InvalidDataException("Не удалось создать PDF.");
        try
        {
            populate(document);
            return Save(document, output, 0);
        }
        finally { Native.FPDF_CloseDocument(document); }
    }

    private static void ImportPage(IntPtr target, IntPtr source, int sourceIndex, int targetIndex, int turn)
    {
        const uint assemblePages = 1 << 10;
        const uint modifyContents = 1 << 3;
        var permission = Native.FPDF_GetSecurityHandlerRevision(source) < 3 ? modifyContents : assemblePages;
        if ((Native.FPDF_GetDocPermissions(source) & permission) == 0)
            throw new InvalidDataException("Защита PDF запрещает извлекать листы.");
        if (Native.FPDF_ImportPagesByIndex(target, source, in sourceIndex, 1, targetIndex) == 0)
            throw new InvalidDataException("Не удалось скопировать лист PDF.");
        if (turn == 0) return;
        var page = Native.FPDF_LoadPage(target, targetIndex);
        if (page == IntPtr.Zero) throw new InvalidDataException("Не удалось повернуть лист PDF.");
        try { Native.FPDFPage_SetRotation(page, (Native.FPDFPage_GetRotation(page) + turn) % 4); }
        finally { Native.FPDF_ClosePage(page); }
    }

    private static int Save(IntPtr document, Stream output, uint flags)
    {
        Exception? writeError = null;
        Native.WriteBlock write = (_, data, size) =>
        {
            try { return WriteBlock(output, data, size); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                writeError = ex;
                return 0;
            }
        };
        var writer = new Native.FileWrite(1, write);
        var saved = Native.FPDF_SaveAsCopy(document, ref writer, flags);
        GC.KeepAlive(write);
        if (writeError is not null) throw new IOException("Не удалось записать PDF.", writeError);
        if (saved == 0) throw new InvalidDataException("PDFium не смог сохранить документ.");
        return saved;
    }

    private static T WithDocument<T>(Stream input, Func<IntPtr, T> action, CancellationToken token = default) =>
        WithLibrary(() => LoadDocument(input, action), token);

    private static T WithLibrary<T>(Func<T> action, CancellationToken token)
    {
        lock (Gate)
        {
            token.ThrowIfCancellationRequested();
            Native.FPDF_InitLibrary();
            try { return action(); }
            finally { Native.FPDF_DestroyLibrary(); }
        }
    }

    private static T LoadDocument<T>(Stream input, Func<IntPtr, T> action)
    {
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
        try { return action(document); }
        finally
        {
            Native.FPDF_CloseDocument(document);
            GC.KeepAlive(read);
        }
    }

    private static unsafe int ReadBlock(Stream stream, IntPtr buffer, uint size)
    {
        if (size > int.MaxValue) return 0;
        stream.ReadExactly(new Span<byte>((void*)buffer, (int)size));
        return 1;
    }

    private static unsafe int WriteBlock(Stream stream, IntPtr buffer, uint size)
    {
        if (size > int.MaxValue) return 0;
        stream.Write(new ReadOnlySpan<byte>((void*)buffer, (int)size));
        return 1;
    }

    private static Raster RenderPage(IntPtr document, int number, Area crop, int maxEdge,
        CancellationToken token, int turn)
    {
        var page = Native.FPDF_LoadPage(document, number - 1);
        if (page == IntPtr.Zero) throw new InvalidDataException("Не удалось открыть страницу PDF.");
        try
        {
            var pageWidth = Native.FPDF_GetPageWidthF(page);
            var pageHeight = Native.FPDF_GetPageHeightF(page);
            if ((turn & 1) != 0) (pageWidth, pageHeight) = (pageHeight, pageWidth);
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
                    (int)Math.Ceiling(pageHeight * scale), turn, 0);
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
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate int WriteBlock(IntPtr writer, IntPtr buffer, uint size);
        [StructLayout(LayoutKind.Sequential)]
        public readonly record struct FileWrite(int Version, WriteBlock Write);
        [StructLayout(LayoutKind.Sequential)]
        public readonly record struct PageBox(float Left, float Top, float Right, float Bottom);
        [StructLayout(LayoutKind.Sequential)]
        public readonly record struct Matrix(float A, float B, float C, float D, float E, float F);
        [DllImport(Dll)] public static extern void FPDF_InitLibrary();
        [DllImport(Dll)] public static extern void FPDF_DestroyLibrary();
        [DllImport(Dll)] public static extern IntPtr FPDF_LoadCustomDocument(ref FileAccess access, IntPtr password);
        [DllImport(Dll)] public static extern uint FPDF_GetLastError();
        [DllImport(Dll)] public static extern void FPDF_CloseDocument(IntPtr document);
        [DllImport(Dll)] public static extern uint FPDF_GetDocPermissions(IntPtr document);
        [DllImport(Dll)] public static extern int FPDF_GetSecurityHandlerRevision(IntPtr document);
        [DllImport(Dll)] public static extern int FPDF_GetPageCount(IntPtr document);
        [DllImport(Dll)] public static extern IntPtr FPDF_LoadPage(IntPtr document, int index);
        [DllImport(Dll)] public static extern void FPDF_ClosePage(IntPtr page);
        [DllImport(Dll)] public static extern int FPDFPage_GetRotation(IntPtr page);
        [DllImport(Dll)] public static extern void FPDFPage_SetRotation(IntPtr page, int turns);
        [DllImport(Dll)] public static extern void FPDFPage_SetMediaBox(IntPtr page, float left, float bottom, float right, float top);
        [DllImport(Dll)] public static extern void FPDFPage_SetCropBox(IntPtr page, float left, float bottom, float right, float top);
        [DllImport(Dll)] public static extern void FPDFPage_SetTrimBox(IntPtr page, float left, float bottom, float right, float top);
        [DllImport(Dll)] public static extern void FPDFPage_SetBleedBox(IntPtr page, float left, float bottom, float right, float top);
        [DllImport(Dll)] public static extern void FPDFPage_SetArtBox(IntPtr page, float left, float bottom, float right, float top);
        [DllImport(Dll)] public static extern int FPDF_GetPageBoundingBox(IntPtr page, out PageBox box);
        [DllImport(Dll)] public static extern unsafe int FPDFPage_TransFormWithClip(IntPtr page, Matrix* matrix, in PageBox box);
        [DllImport(Dll)] public static extern void FPDFPage_TransformAnnots(IntPtr page, double a, double b, double c,
            double d, double e, double f);
        [DllImport(Dll)] public static extern int FPDF_SaveAsCopy(IntPtr document, ref FileWrite writer, uint flags);
        [DllImport(Dll)] public static extern IntPtr FPDF_CreateNewDocument();
        [DllImport(Dll)] public static extern int FPDF_ImportPagesByIndex(IntPtr target, IntPtr source,
            in int pageIndex, uint length, int targetIndex);
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
