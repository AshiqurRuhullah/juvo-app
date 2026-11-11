using System.Globalization;
using System.Reflection;
using System.Text;
using Juvo.Application.Billing;
using SkiaSharp;
using SkiaSharp.HarfBuzz;

namespace Juvo.Infrastructure.Billing;

/// <summary>
/// Draws invoices and credit notes as A4 PDFs with SkiaSharp (MIT) and shapes the text with HarfBuzz (MIT), so Bengali conjuncts and vowel signs come out right. Latin text uses
/// Noto Sans and Bengali text uses Noto Sans Bengali (both SIL Open Font License 1.1, embedded in the assembly and subset into each PDF by Skia). Brand colours come from the logo
/// tokens (red #DB0100, ink #1C0807, cream #EBE5DB). The renderer never touches the database or the network.
/// </summary>
internal sealed class SkiaPdfRenderer : IDocumentPdfRenderer
{
    private const float PageWidth = 595f;
    private const float PageHeight = 842f;
    private const float Margin = 40f;
    private const float BottomLimit = PageHeight - 70f;

    private static readonly SKColor Red = new(0xDB, 0x01, 0x00);
    private static readonly SKColor Ink = new(0x1C, 0x08, 0x07);
    private static readonly SKColor Cream = new(0xEB, 0xE5, 0xDB);
    private static readonly SKColor Muted = new(0x6B, 0x5C, 0x58);

    private static readonly Lazy<Typefaces> Fonts = new(LoadFonts);

    private sealed record Typefaces(SKTypeface Latin, SKTypeface LatinBold, SKTypeface Bengali, SKTypeface BengaliBold);

    private static SKTypeface Load(string file)
    {
        var assembly = typeof(SkiaPdfRenderer).Assembly;
        var name = assembly.GetManifestResourceNames().Single(n => n.EndsWith(file, StringComparison.Ordinal));
        using var stream = assembly.GetManifestResourceStream(name) ?? throw new InvalidOperationException($"The font {file} is missing from the assembly.");
        using var data = SKData.Create(stream);
        return SKTypeface.FromData(data) ?? throw new InvalidOperationException($"The font {file} could not be read.");
    }

    private static Typefaces LoadFonts() => new(Load("NotoSans-Regular.ttf"), Load("NotoSans-Bold.ttf"), Load("NotoSansBengali-Regular.ttf"), Load("NotoSansBengali-Bold.ttf"));

    private static bool IsBengali(Rune r) => r.Value is >= 0x0980 and <= 0x09FF or 0x200C or 0x200D;

    /// <summary>Text drawing with run splitting: Bengali letters use the Bengali font, everything else the Latin font. Shapers are made once per document.</summary>
    private sealed class TextPen : IDisposable
    {
        private readonly Typefaces _faces = Fonts.Value;
        private readonly Dictionary<(bool Bengali, bool Bold), SKShaper> _shapers = [];
        private readonly Dictionary<(bool Bengali, bool Bold, float Size), SKFont> _fonts = [];

        private SKShaper Shaper(bool bengali, bool bold)
        {
            if (!_shapers.TryGetValue((bengali, bold), out var shaper))
            {
                shaper = new SKShaper(Face(bengali, bold));
                _shapers[(bengali, bold)] = shaper;
            }

            return shaper;
        }

        private SKTypeface Face(bool bengali, bool bold) => bengali ? (bold ? _faces.BengaliBold : _faces.Bengali) : (bold ? _faces.LatinBold : _faces.Latin);

        private SKFont Font(bool bengali, bool bold, float size)
        {
            if (!_fonts.TryGetValue((bengali, bold, size), out var font))
            {
                font = new SKFont(Face(bengali, bold), size) { Subpixel = true };
                _fonts[(bengali, bold, size)] = font;
            }

            return font;
        }

        public static List<(string Text, bool Bengali)> Runs(string text)
        {
            var runs = new List<(string, bool)>();
            var current = new StringBuilder();
            bool? kind = null;
            foreach (var rune in text.EnumerateRunes())
            {
                var bn = IsBengali(rune);
                if (kind is not null && kind != bn)
                {
                    runs.Add((current.ToString(), kind.Value));
                    current.Clear();
                }

                kind = bn;
                current.Append(rune.ToString());
            }

            if (current.Length > 0)
            {
                runs.Add((current.ToString(), kind!.Value));
            }

            return runs;
        }

        public float Width(string text, float size, bool bold)
        {
            var total = 0f;
            foreach (var (run, bengali) in Runs(text))
            {
                total += Shaper(bengali, bold).Shape(run, Font(bengali, bold, size)).Width;
            }

            return total;
        }

        public void Draw(SKCanvas canvas, string text, float x, float y, float size, bool bold, SKColor color, SKTextAlign align = SKTextAlign.Left)
        {
            if (string.IsNullOrEmpty(text))
            {
                return;
            }

            var width = Width(text, size, bold);
            if (align == SKTextAlign.Right)
            {
                x -= width;
            }
            else if (align == SKTextAlign.Center)
            {
                x -= width / 2;
            }

            using var paint = new SKPaint { Color = color, IsAntialias = true };
            foreach (var (run, bengali) in Runs(text))
            {
                var font = Font(bengali, bold, size);
                var shaper = Shaper(bengali, bold);
                canvas.DrawShapedText(shaper, run, x, y, SKTextAlign.Left, font, paint);
                x += shaper.Shape(run, font).Width;
            }
        }

        /// <summary>Breaks the text into lines no wider than <paramref name="maxWidth"/> (at spaces; a very long word is cut).</summary>
        public List<string> Wrap(string text, float size, bool bold, float maxWidth, int maxLines)
        {
            var lines = new List<string>();
            var line = new StringBuilder();
            foreach (var word in text.Replace("\r", string.Empty, StringComparison.Ordinal).Split([' ', '\n'], StringSplitOptions.RemoveEmptyEntries))
            {
                var candidate = line.Length == 0 ? word : line + " " + word;
                if (Width(candidate, size, bold) <= maxWidth)
                {
                    line.Clear().Append(candidate);
                    continue;
                }

                if (line.Length > 0)
                {
                    lines.Add(line.ToString());
                    line.Clear();
                }

                var rest = word;
                while (Width(rest, size, bold) > maxWidth && rest.Length > 1)
                {
                    var cut = rest.Length - 1;
                    while (cut > 1 && Width(rest[..cut], size, bold) > maxWidth)
                    {
                        cut--;
                    }

                    lines.Add(rest[..cut]);
                    rest = rest[cut..];
                }

                line.Append(rest);
            }

            if (line.Length > 0)
            {
                lines.Add(line.ToString());
            }

            if (lines.Count > maxLines)
            {
                lines = lines.Take(maxLines).ToList();
                lines[^1] = lines[^1].TrimEnd() + "...";
            }

            return lines;
        }

        public void Dispose()
        {
            foreach (var shaper in _shapers.Values)
            {
                shaper.Dispose();
            }

            foreach (var font in _fonts.Values)
            {
                font.Dispose();
            }
        }
    }

    private static string Money(string currency, decimal amount, bool bn) =>
        (bn && currency == "BDT" ? "৳ " : currency + " ") + amount.ToString("N2", CultureInfo.InvariantCulture);

    private static DateTimeOffset Local(DateTimeOffset at)
    {
        try
        {
            return TimeZoneInfo.ConvertTime(at, TimeZoneInfo.FindSystemTimeZoneById("Asia/Dhaka"));
        }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            return at.ToOffset(TimeSpan.FromHours(6));
        }
    }

    public byte[] Render(PdfDocumentModel m)
    {
        var bn = m.Language == "bn";
        string L(string en, string bangla) => bn ? bangla : en;
        using var pen = new TextPen();
        using var output = new MemoryStream();
        using (var document = SKDocument.CreatePdf(output, new SKDocumentPdfMetadata
        {
            Title = $"{m.Title} {m.Number}",
            Author = m.Seller.Name,
            Creator = "Juvo BD",
            Subject = $"Order {m.OrderNumber}",
            Creation = m.IssuedAt.UtcDateTime,
            Modified = m.IssuedAt.UtcDateTime,
            EncodingQuality = 90,
        }))
        {
            SKCanvas? canvas = null;
            float y = 0;
            var pageNumber = 0;

            void NewPage()
            {
                if (canvas is not null)
                {
                    Footer();
                    document.EndPage();
                }

                canvas = document.BeginPage(PageWidth, PageHeight);
                pageNumber++;
                using var bar = new SKPaint { Color = Red, IsAntialias = true };
                canvas.DrawRect(0, 0, PageWidth, 8, bar);
                pen.Draw(canvas, "JUVO BD", Margin, 48, 22, true, Red);
                pen.Draw(canvas, m.Title, PageWidth - Margin, 48, 18, true, Ink, SKTextAlign.Right);
                y = 72;
                if (pageNumber > 1)
                {
                    pen.Draw(canvas, $"{m.Number}  ({L("continued", "পরবর্তী পৃষ্ঠা")})", Margin, y, 9, false, Muted);
                    y += 22;
                }
            }

            void Footer()
            {
                using var line = new SKPaint { Color = Cream, StrokeWidth = 1 };
                canvas!.DrawLine(Margin, PageHeight - 52, PageWidth - Margin, PageHeight - 52, line);
                pen.Draw(canvas, L("Thank you for shopping with Juvo BD.", "জুভো বিডি-তে কেনাকাটার জন্য ধন্যবাদ।"), Margin, PageHeight - 36, 8.5f, false, Muted);
                pen.Draw(canvas, $"{pageNumber}", PageWidth - Margin, PageHeight - 36, 8.5f, false, Muted, SKTextAlign.Right);
            }

            NewPage();
            var c = canvas!;

            // Number, date and order reference
            var local = Local(m.IssuedAt);
            pen.Draw(c, $"{L("No.", "নং")} {m.Number}", PageWidth - Margin, y, 11, true, Ink, SKTextAlign.Right);
            pen.Draw(c, $"{L("Date", "তারিখ")}: {local:dd/MM/yyyy HH:mm}", PageWidth - Margin, y + 15, 9.5f, false, Ink, SKTextAlign.Right);
            pen.Draw(c, $"{L("Order", "অর্ডার")}: {m.OrderNumber}", PageWidth - Margin, y + 30, 9.5f, false, Ink, SKTextAlign.Right);
            if (m.ReferenceNumber is not null)
            {
                pen.Draw(c, $"{L("Invoice", "ইনভয়েস")}: {m.ReferenceNumber}", PageWidth - Margin, y + 45, 9.5f, false, Ink, SKTextAlign.Right);
            }

            // Seller (left)
            var sy = y;
            pen.Draw(c, L("From", "বিক্রেতা"), Margin, sy, 8.5f, true, Muted);
            sy += 14;
            pen.Draw(c, m.Seller.Name, Margin, sy, 11, true, Ink);
            sy += 14;
            foreach (var part in new[] { m.Seller.Address, m.Seller.Phone, m.Seller.Email, m.Seller.TaxId is null ? null : $"{L("BIN/TIN", "বিআইএন/টিআইএন")}: {m.Seller.TaxId}" }.Where(s => !string.IsNullOrWhiteSpace(s)))
            {
                foreach (var wrapped in pen.Wrap(part!, 9.5f, false, 230, 3))
                {
                    pen.Draw(c, wrapped, Margin, sy, 9.5f, false, Ink);
                    sy += 12.5f;
                }
            }

            y = Math.Max(sy, y + 62) + 10;

            // Customer
            pen.Draw(c, m.IsCreditNote ? L("Credited to", "ক্রেডিট প্রাপক") : L("Bill to", "ক্রেতা"), Margin, y, 8.5f, true, Muted);
            y += 14;
            pen.Draw(c, m.CustomerName, Margin, y, 11, true, Ink);
            y += 14;
            foreach (var part in new[] { m.CustomerMobile, m.CustomerEmail, m.BillingAddress }.Where(s => !string.IsNullOrWhiteSpace(s)))
            {
                foreach (var wrapped in pen.Wrap(part!, 9.5f, false, 330, 3))
                {
                    pen.Draw(c, wrapped, Margin, y, 9.5f, false, Ink);
                    y += 12.5f;
                }
            }

            y += 14;

            // Items table
            const float colQty = 380f, colPrice = 460f, colTotal = PageWidth - Margin;
            void TableHeader()
            {
                using var fill = new SKPaint { Color = Cream };
                canvas!.DrawRect(Margin, y - 12, PageWidth - 2 * Margin, 20, fill);
                pen.Draw(canvas, L("Item", "পণ্য"), Margin + 6, y + 2, 9, true, Ink);
                pen.Draw(canvas, L("Qty", "পরিমাণ"), colQty, y + 2, 9, true, Ink, SKTextAlign.Right);
                pen.Draw(canvas, L("Unit price", "একক মূল্য"), colPrice, y + 2, 9, true, Ink, SKTextAlign.Right);
                pen.Draw(canvas, L("Total", "মোট"), colTotal - 6, y + 2, 9, true, Ink, SKTextAlign.Right);
                y += 22;
            }

            TableHeader();
            foreach (var line in m.Lines)
            {
                var nameLines = pen.Wrap(line.Name, 9.5f, false, colQty - Margin - 60, 3);
                var detail = string.Join(" · ", new[] { line.Variant, line.Sku }.Where(s => !string.IsNullOrWhiteSpace(s)));
                var height = nameLines.Count * 12.5f + (detail.Length > 0 ? 11f : 0f) + 8f;
                if (y + height > BottomLimit)
                {
                    NewPage();
                    TableHeader();
                }

                var rowTop = y;
                foreach (var wrapped in nameLines)
                {
                    pen.Draw(canvas!, wrapped, Margin + 6, y, 9.5f, false, Ink);
                    y += 12.5f;
                }

                if (detail.Length > 0)
                {
                    pen.Draw(canvas!, detail, Margin + 6, y - 1, 8, false, Muted);
                    y += 11f;
                }

                pen.Draw(canvas!, line.Quantity.ToString(CultureInfo.InvariantCulture), colQty, rowTop, 9.5f, false, Ink, SKTextAlign.Right);
                pen.Draw(canvas!, Money(m.Currency, line.UnitPrice, bn), colPrice, rowTop, 9.5f, false, Ink, SKTextAlign.Right);
                pen.Draw(canvas!, Money(m.Currency, line.LineTotal, bn), colTotal - 6, rowTop, 9.5f, false, Ink, SKTextAlign.Right);
                y += 8f;
                using var rule = new SKPaint { Color = Cream, StrokeWidth = 0.7f };
                canvas!.DrawLine(Margin, y - 9, PageWidth - Margin, y - 9, rule);
            }

            // Totals
            if (y + 130 > BottomLimit)
            {
                NewPage();
            }

            y += 6;
            void Row(string label, string value, bool strong, SKColor color)
            {
                pen.Draw(canvas!, label, colPrice - 30, y, strong ? 11 : 9.5f, strong, color, SKTextAlign.Right);
                pen.Draw(canvas!, value, colTotal - 6, y, strong ? 11 : 9.5f, strong, color, SKTextAlign.Right);
                y += strong ? 18 : 14.5f;
            }

            if (!m.IsCreditNote)
            {
                Row(L("Subtotal", "উপমোট"), Money(m.Currency, m.Subtotal, bn), false, Ink);
                if (m.Discount > 0)
                {
                    Row(L("Discount", "ছাড়"), "- " + Money(m.Currency, m.Discount, bn), false, Ink);
                }

                Row(L("Delivery", "ডেলিভারি"), Money(m.Currency, m.DeliveryCharge, bn), false, Ink);
            }

            using (var strongLine = new SKPaint { Color = Ink, StrokeWidth = 1 })
            {
                canvas!.DrawLine(colPrice - 120, y - 8, colTotal, y - 8, strongLine);
            }

            y += 4;
            Row(m.IsCreditNote ? L("Amount credited", "ক্রেডিট পরিমাণ") : L("Total (tax included)", "সর্বমোট (কর সহ)"), Money(m.Currency, m.GrandTotal, bn), true, Red);
            if (m.TaxLabel is not null)
            {
                Row(L($"Net of {m.TaxLabel}", $"{m.TaxLabel} বাদে নিট"), Money(m.Currency, m.NetTotal, bn), false, Muted);
                Row(L($"Includes {m.TaxLabel}", $"{m.TaxLabel} সহ"), Money(m.Currency, m.TaxTotal, bn), false, Muted);
            }

            y += 8;
            if (!m.IsCreditNote)
            {
                pen.Draw(canvas!, $"{L("Payment", "পেমেন্ট")}: {m.PaymentMethod} · {m.PaymentStatus}", Margin, y, 9.5f, false, Ink);
                y += 14;
            }

            if (m.Note is not null)
            {
                foreach (var wrapped in pen.Wrap(m.Note, 9, false, PageWidth - 2 * Margin, 3))
                {
                    pen.Draw(canvas!, wrapped, Margin, y, 9, false, Muted);
                    y += 12;
                }
            }

            Footer();
            document.EndPage();
            document.Close();
        }

        return output.ToArray();
    }
}
