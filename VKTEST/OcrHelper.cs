using System;
using System.Threading.Tasks;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage;
using Windows.Storage.Streams;
using System.Drawing;
using System.Drawing.Imaging;
using Windows.Globalization;


namespace BmwBenchmarkAutomator;

public sealed class OcrTextLine
{
    public string Text { get; set; } = "";
    public double X { get; set; }   // координаты строки на снимке (пиксели) — используются для кликов
    public double Y { get; set; }
    public double W { get; set; }
    public double H { get; set; }
}

public sealed class OcrHelper
{
    private OcrEngine? _engine;
    public bool Available => _engine != null;

    public void Init()
    {
        _engine = OcrEngine.TryCreateFromUserProfileLanguages()
               ?? OcrEngine.TryCreateFromLanguage(new Language("ru-RU"))
               ?? OcrEngine.TryCreateFromLanguage(new Language("en-US"));
        if (_engine == null)
            Log.Warn("OCR недоступен. Установите языковой пакет с компонентом «Оптическое распознавание " +
                     "символов» (ru-RU): Параметры → Время и язык → Язык и регион.");
        else
            Log.Info($"OCR-движок готов ({_engine.RecognizerLanguage.LanguageTag}).");
    }

    public async Task<(string Text, OcrTextLine[] Lines)?> RecognizeAsync(Bitmap bmp)
    {
        if (_engine == null) return null;

        try
        {
            using var ms = new MemoryStream();
            bmp.Save(ms, ImageFormat.Png);
            ms.Position = 0;

            using var ras = ms.AsRandomAccessStream();
            var decoder = await BitmapDecoder.CreateAsync(ras);
            using var soft = await decoder.GetSoftwareBitmapAsync();

            var res = await _engine.RecognizeAsync(soft);

            var lines = res.Lines
                .Where(l => l.Words != null && l.Words.Count > 0)
                .Select(l =>
                {
                    // Считаем общий прямоугольник из слов строки
                    var first = l.Words[0].BoundingRect;
                    double minX = first.X, minY = first.Y;
                    double maxX = first.X + first.Width;
                    double maxY = first.Y + first.Height;

                    foreach (var w in l.Words)
                    {
                        var r = w.BoundingRect;
                        if (r.X < minX) minX = r.X;
                        if (r.Y < minY) minY = r.Y;
                        if (r.X + r.Width > maxX) maxX = r.X + r.Width;
                        if (r.Y + r.Height > maxY) maxY = r.Y + r.Height;
                    }

                    return new OcrTextLine
                    {
                        Text = l.Text,
                        X = minX,
                        Y = minY,
                        W = maxX - minX,
                        H = maxY - minY
                    };
                })
                .ToArray();

            return (res.Text, lines);
        }
        catch (Exception ex)
        {
            Log.Warn($"OCR: {ex.Message}");
            return null;
        }
    }
}