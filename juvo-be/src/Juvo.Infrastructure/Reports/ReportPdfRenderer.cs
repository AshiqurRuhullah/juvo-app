using System.Globalization;
using Juvo.Application.Reports;
using Juvo.Infrastructure.Billing;
using SkiaSharp;

namespace Juvo.Infrastructure.Reports;

/// <summary>
/// Draws a report as an A4 PDF (landscape when a table has many columns) with the same SkiaSharp and HarfBuzz text drawing as the invoices, so Bengali shapes correctly. Fonts are the
/// embedded Noto Sans and Noto Sans Bengali; Skia subsets them into each PDF, so a typical report file is tens of kilobytes, not megabytes. Title, shop name, the filters it was run
/// with, each table with its totals row, and the notes are drawn; tables continue on the next page with the header repeated.
/// </summary>
internal sealed class ReportPdfRenderer : IReportPdfRenderer
{
    private static readonly SKColor Red = new(0xDB, 0x01, 0x00);
    private static readonly SKColor Ink = new(0x1C, 0x08, 0x07);
    private static readonly SKColor Cream = new(0xEB, 0xE5, 0xDB);
    private static readonly SKColor Muted = new(0x6B, 0x5C, 0x58);
    private const float Margin = 36f;
    private const float RowHeight = 17f;

    public byte[] Render(ReportDoc doc, string shopName, string lang)
    {
        var landscape = doc.Sections.Any(s => s.Headers.Count > 7);
        var pageW = landscape ? 842f : 595f;
        var pageH = landscape ? 595f : 842f;
        var bottom = pageH - 48f;
        using var output = new MemoryStream();
        using var pen = new SkiaPdfRenderer.TextPen();
        using (var document = SKDocument.CreatePdf(output, new SKDocumentPdfMetadata { Title = doc.Title, Author = shopName, Creator = "Juvo BD", Subject = doc.Report, Creation = doc.GeneratedAt.UtcDateTime, Modified = doc.GeneratedAt.UtcDateTime, EncodingQuality = 90 }))
        {
            var page = 0;
            SKCanvas? canvas = null;
            var y = 0f;

            void NewPage()
            {
                if (canvas is not null)
                {
                    Footer(canvas, pen, pageW, pageH, page, shopName, doc);
                    document.EndPage();
                }

                canvas = document.BeginPage(pageW, pageH);
                page++;
                y = Margin;
                if (page == 1)
                {
                    pen.Draw(canvas, shopName, Margin, y + 10, 10, true, Red);
                    y += 26;
                    pen.Draw(canvas, doc.Title, Margin, y + 8, 18, true, Ink);
                    y += 28;
                    foreach (var f in doc.Filters)
                    {
                        pen.Draw(canvas, f.Key + ": " + f.Value, Margin, y + 8, 9, false, Muted);
                        y += 13;
                    }

                    pen.Draw(canvas, ReportText.T("Generated", lang) + ": " + doc.GeneratedAt.ToOffset(TimeSpan.FromHours(6)).ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) + " (Asia/Dhaka)", Margin, y + 8, 9, false, Muted);
                    y += 22;
                }
            }

            NewPage();
            foreach (var section in doc.Sections)
            {
                if (y + RowHeight * 3 > bottom)
                {
                    NewPage();
                }

                pen.Draw(canvas!, section.Title, Margin, y + 10, 12, true, Ink);
                y += 22;
                var widths = Widths(pen, section, pageW - 2 * Margin);

                void Header()
                {
                    using var fill = new SKPaint { Color = Cream };
                    canvas!.DrawRect(Margin, y, pageW - 2 * Margin, RowHeight, fill);
                    DrawRow(canvas, pen, section.Headers, widths, section, y, true);
                    y += RowHeight;
                }

                Header();
                foreach (var row in section.Rows)
                {
                    if (y + RowHeight > bottom)
                    {
                        NewPage();
                        Header();
                    }

                    DrawRow(canvas!, pen, row, widths, section, y, false);
                    y += RowHeight;
                }

                if (section.Totals is not null)
                {
                    if (y + RowHeight > bottom)
                    {
                        NewPage();
                        Header();
                    }

                    using var line = new SKPaint { Color = Ink, StrokeWidth = 0.8f };
                    canvas!.DrawLine(Margin, y, pageW - Margin, y, line);
                    DrawRow(canvas, pen, section.Totals, widths, section, y, true);
                    y += RowHeight;
                }

                y += 14;
            }

            if (doc.Notes.Count > 0)
            {
                if (y + 30 > bottom)
                {
                    NewPage();
                }

                pen.Draw(canvas!, ReportText.T("Notes", lang), Margin, y + 9, 9, true, Muted);
                y += 14;
                foreach (var note in doc.Notes)
                {
                    foreach (var line in pen.Wrap(note, 8, false, pageW - 2 * Margin, 3))
                    {
                        if (y + 11 > bottom)
                        {
                            NewPage();
                        }

                        pen.Draw(canvas!, line, Margin, y + 8, 8, false, Muted);
                        y += 11;
                    }
                }
            }

            Footer(canvas!, pen, pageW, pageH, page, shopName, doc);
            document.EndPage();
            document.Close();
        }

        return output.ToArray();
    }

    private static float[] Widths(SkiaPdfRenderer.TextPen pen, ReportSection s, float available)
    {
        var count = s.Headers.Count;
        var natural = new float[count];
        for (var c = 0; c < count; c++)
        {
            var w = pen.Width(s.Headers[c], 8f, true);
            foreach (var row in s.Rows.Take(80))
            {
                if (c < row.Count && row[c] is { Length: > 0 } t)
                {
                    w = Math.Max(w, pen.Width(t, 8f, false));
                }
            }

            if (s.Totals is not null && c < s.Totals.Count && s.Totals[c] is { Length: > 0 } tt)
            {
                w = Math.Max(w, pen.Width(tt, 8f, true));
            }

            natural[c] = Math.Min(w, 200f) + 10f;
        }

        var sum = natural.Sum();
        var scale = sum > available ? available / sum : 1f;
        var widths = natural.Select(w => w * scale).ToArray();
        if (scale >= 1f)
        {
            var extra = (available - sum) / count;
            widths = [.. widths.Select(w => w + extra)];
        }

        return widths;
    }

    private static void DrawRow(SKCanvas canvas, SkiaPdfRenderer.TextPen pen, IReadOnlyList<string?> cells, float[] widths, ReportSection section, float y, bool bold)
    {
        var x = Margin;
        for (var c = 0; c < widths.Length; c++)
        {
            var text = c < cells.Count ? cells[c] ?? string.Empty : string.Empty;
            var numeric = section.NumericColumns?.Contains(section.Headers[c]) == true;
            var fit = text.Length == 0 ? [] : pen.Wrap(text, 8f, bold, widths[c] - 8f, 1);
            if (fit.Count > 0)
            {
                if (numeric)
                {
                    pen.Draw(canvas, fit[0], x + widths[c] - 4f, y + 12f, 8f, bold, Ink, SKTextAlign.Right);
                }
                else
                {
                    pen.Draw(canvas, fit[0], x + 4f, y + 12f, 8f, bold, Ink);
                }
            }

            x += widths[c];
        }
    }

    private static void Footer(SKCanvas canvas, SkiaPdfRenderer.TextPen pen, float pageW, float pageH, int page, string shop, ReportDoc doc)
    {
        pen.Draw(canvas, shop + " - " + doc.Title, Margin, pageH - 24f, 8f, false, Muted);
        pen.Draw(canvas, page.ToString(CultureInfo.InvariantCulture), pageW - Margin, pageH - 24f, 8f, false, Muted, SKTextAlign.Right);
    }
}
