using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Text.RegularExpressions;
using Windows.Globalization;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage.Streams;

namespace FreightCopilot;

public sealed class LocalOcrService
{
    // Mission-details panel measured from a 2560x1440 reference screenshot.
    private const double MissionPanelLeft = 685.0 / 2560.0;
    private const double MissionPanelTop = 88.0 / 1440.0;
    private const double MissionPanelRight = 2475.0 / 2560.0;
    private const double MissionPanelBottom = 1282.0 / 1440.0;

    private readonly OcrEngine _engine;

    public LocalOcrService()
    {
        // Star Citizen's contract UI is English; prefer that recognizer even when the
        // Windows display language is Danish or another locale.
        _engine = TryEnglishEngine() ?? OcrEngine.TryCreateFromUserProfileLanguages() ??
                  throw new InvalidOperationException(
                      "Windows OCR is unavailable. Install the Windows English language OCR feature in Settings > Time & language > Language & region, then restart Freight Planner.");
    }

    public string EngineDescription => $"Windows OCR ({_engine.RecognizerLanguage.DisplayName})";

    public async Task<string> ReadTextAsync(string imagePath, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var source = Image.FromFile(imagePath);
        using var color = PrepareMissionPanel(source, enhanced: false);
        using var enhanced = PrepareMissionPanel(source, enhanced: true);

        // The game UI mixes cyan and white type on a translucent background. Run the
        // built-in engine on both the preserved-color crop and a high-contrast local
        // preprocessing pass, then retain whichever contains more mission evidence.
        var colorText = await RecognizeAsync(color, cancellationToken);
        var enhancedText = await RecognizeAsync(enhanced, cancellationToken);
        var selected = EvidenceScore(enhancedText) > EvidenceScore(colorText) ? enhancedText : colorText;

        // The objective font occupies only the right half of a 2560-wide screenshot.
        // Give that area a dedicated full-resolution OCR pass so small values such as
        // "0/5" are not discarded while the whole panel is being analyzed.
        using var objectives = CropAndScale(color, new RectangleF(0.50f, 0.18f, 0.50f, 0.70f));
        var objectiveText = await RecognizePlainAsync(objectives, cancellationToken);
        return ReplaceSection(selected, "OBJECTIVES", objectiveText);
    }

    private async Task<string> RecognizeAsync(Bitmap bitmap, CancellationToken cancellationToken)
    {
        var result = await RecognizeResultAsync(bitmap, cancellationToken);
        return RebuildLayout(result, bitmap.Width, bitmap.Height);
    }

    private async Task<string> RecognizePlainAsync(Bitmap bitmap, CancellationToken cancellationToken)
    {
        var result = await RecognizeResultAsync(bitmap, cancellationToken);
        return string.Join(Environment.NewLine, result.Lines.Select(line => line.Text));
    }

    private async Task<OcrResult> RecognizeResultAsync(Bitmap bitmap, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var encoded = new MemoryStream();
        bitmap.Save(encoded, ImageFormat.Png);

        using var stream = new InMemoryRandomAccessStream();
        using (var writer = new DataWriter(stream.GetOutputStreamAt(0)))
        {
            writer.WriteBytes(encoded.ToArray());
            await writer.StoreAsync().AsTask(cancellationToken);
            await writer.FlushAsync().AsTask(cancellationToken);
        }
        stream.Seek(0);
        var decoder = await BitmapDecoder.CreateAsync(stream).AsTask(cancellationToken);
        using var softwareBitmap = await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied)
            .AsTask(cancellationToken);
        return await _engine.RecognizeAsync(softwareBitmap).AsTask(cancellationToken);
    }

    private static Bitmap PrepareMissionPanel(Image source, bool enhanced)
    {
        var left = Math.Clamp((int)Math.Round(source.Width * MissionPanelLeft), 0, source.Width - 1);
        var top = Math.Clamp((int)Math.Round(source.Height * MissionPanelTop), 0, source.Height - 1);
        var right = Math.Clamp((int)Math.Round(source.Width * MissionPanelRight), left + 1, source.Width);
        var bottom = Math.Clamp((int)Math.Round(source.Height * MissionPanelBottom), top + 1, source.Height);
        var width = right - left;
        var height = bottom - top;
        var maxDimension = Math.Max(1, (int)OcrEngine.MaxImageDimension - 8);
        var scale = Math.Min(1.45, Math.Min((double)maxDimension / width, (double)maxDimension / height));
        var outputWidth = Math.Max(1, (int)Math.Round(width * scale));
        var outputHeight = Math.Max(1, (int)Math.Round(height * scale));

        var output = new Bitmap(outputWidth, outputHeight, PixelFormat.Format24bppRgb);
        output.SetResolution(96, 96);
        using var graphics = Graphics.FromImage(output);
        graphics.CompositingQuality = CompositingQuality.HighQuality;
        graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
        graphics.SmoothingMode = SmoothingMode.HighQuality;
        graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
        if (!enhanced)
        {
            graphics.DrawImage(source, new Rectangle(0, 0, outputWidth, outputHeight),
                new Rectangle(left, top, width, height), GraphicsUnit.Pixel);
            return output;
        }

        using var attributes = new ImageAttributes();
        attributes.SetColorMatrix(new ColorMatrix([
            [ -0.40f, -0.40f, -0.40f, 0, 0 ],
            [ -0.40f, -0.40f, -0.40f, 0, 0 ],
            [ -0.40f, -0.40f, -0.40f, 0, 0 ],
            [ 0, 0, 0, 1, 0 ],
            [ 1.08f, 1.08f, 1.08f, 0, 1 ]
        ]));
        graphics.Clear(Color.White);
        graphics.DrawImage(source, new Rectangle(0, 0, outputWidth, outputHeight), left, top, width, height,
            GraphicsUnit.Pixel, attributes);
        return output;
    }

    private static Bitmap CropAndScale(Bitmap source, RectangleF normalized)
    {
        var crop = new Rectangle(
            Math.Clamp((int)Math.Round(source.Width * normalized.X), 0, source.Width - 1),
            Math.Clamp((int)Math.Round(source.Height * normalized.Y), 0, source.Height - 1),
            Math.Max(1, (int)Math.Round(source.Width * normalized.Width)),
            Math.Max(1, (int)Math.Round(source.Height * normalized.Height)));
        crop.Width = Math.Min(crop.Width, source.Width - crop.X);
        crop.Height = Math.Min(crop.Height, source.Height - crop.Y);
        var maxDimension = Math.Max(1, (int)OcrEngine.MaxImageDimension - 8);
        var scale = Math.Min(2.4, Math.Min((double)maxDimension / crop.Width, (double)maxDimension / crop.Height));
        var output = new Bitmap(Math.Max(1, (int)Math.Round(crop.Width * scale)),
            Math.Max(1, (int)Math.Round(crop.Height * scale)), PixelFormat.Format24bppRgb);
        using var graphics = Graphics.FromImage(output);
        graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
        graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
        graphics.DrawImage(source, new Rectangle(0, 0, output.Width, output.Height), crop, GraphicsUnit.Pixel);
        return output;
    }

    private static string ReplaceSection(string text, string section, string content)
    {
        var pattern = $@"\[{Regex.Escape(section)}\]\s*.*?(?=\r?\n\[[A-Z_]+\]|$)";
        return Regex.Replace(text, pattern, $"[{section}]{Environment.NewLine}{content.Trim()}", RegexOptions.Singleline);
    }

    private static int EvidenceScore(string text)
    {
        var score = text.Length / 40;
        foreach (var token in new[] { "reward", "cargo", "haul", "deliver", "collect", "scu", "contracted" })
            if (text.Contains(token, StringComparison.OrdinalIgnoreCase)) score += 15;
        return score;
    }

    private static string RebuildLayout(OcrResult result, int width, int height)
    {
        var words = result.Lines.SelectMany(line => line.Words).Select(word => new PositionedWord(
            word.Text, word.BoundingRect.X, word.BoundingRect.Y, word.BoundingRect.Width, word.BoundingRect.Height)).ToList();
        var sections = new[] { "HEADER", "DETAILS", "OBJECTIVES", "LIST_EDGE" };
        var output = new List<string>();
        foreach (var section in sections)
        {
            var sectionWords = words.Where(word => Classify(word, width, height) == section).ToList();
            output.Add($"[{section}]");
            output.AddRange(BuildRows(sectionWords));
        }
        return string.Join(Environment.NewLine, output);
    }

    private static string Classify(PositionedWord word, int width, int height)
    {
        var centerX = word.X + word.Width / 2.0;
        var centerY = word.Y + word.Height / 2.0;
        if (centerX < width * 0.11) return "LIST_EDGE";
        if (centerY < height * 0.20) return "HEADER";
        return centerX > width * 0.51 ? "OBJECTIVES" : "DETAILS";
    }

    private static IEnumerable<string> BuildRows(List<PositionedWord> words)
    {
        var rows = new List<WordRow>();
        foreach (var word in words.OrderBy(x => x.Y + x.Height / 2.0).ThenBy(x => x.X))
        {
            var center = word.Y + word.Height / 2.0;
            var row = rows.OrderBy(x => Math.Abs(x.CenterY - center))
                .FirstOrDefault(x => Math.Abs(x.CenterY - center) <= Math.Max(8, Math.Max(x.Height, word.Height) * 0.55));
            if (row is null)
            {
                row = new WordRow(center, word.Height);
                rows.Add(row);
            }
            row.Words.Add(word);
            row.CenterY = row.Words.Average(x => x.Y + x.Height / 2.0);
            row.Height = row.Words.Max(x => x.Height);
        }
        return rows.OrderBy(x => x.CenterY).Select(row => string.Join(" ", row.Words.OrderBy(x => x.X).Select(x => x.Text)));
    }

    private sealed record PositionedWord(string Text, double X, double Y, double Width, double Height);
    private sealed class WordRow(double centerY, double height)
    {
        public double CenterY { get; set; } = centerY;
        public double Height { get; set; } = height;
        public List<PositionedWord> Words { get; } = [];
    }

    private static OcrEngine? TryEnglishEngine()
    {
        try
        {
            var language = new Language("en-US");
            return OcrEngine.IsLanguageSupported(language) ? OcrEngine.TryCreateFromLanguage(language) : null;
        }
        catch { return null; }
    }
}
