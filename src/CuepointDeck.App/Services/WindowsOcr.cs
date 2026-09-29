using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage;
using Windows.Storage.Streams;
using CuepointDeck.Core.Logging;

namespace CuepointDeck.App.Services;

/// <summary>Reads on-screen lyrics from a video frame with the OCR engine built into Windows 10/11. Offline, no service.
/// Output is a draft note: stylised lyric fonts misread now and then.</summary>
public static class WindowsOcr
{
    private static OcrEngine? _engine;
    private static bool _tried;

    public static bool IsAvailable => Engine is not null;
    public static string? LanguageTag => Engine?.RecognizerLanguage.LanguageTag;

    private static OcrEngine? Engine
    {
        get
        {
            if (_tried) return _engine;
            _tried = true;
            try
            {
                _engine = OcrEngine.TryCreateFromUserProfileLanguages() ?? OcrEngine.TryCreateFromLanguage(new Windows.Globalization.Language("en-US"));
                Log.Info(_engine is null ? "Windows OCR: no language pack available" : $"Windows OCR ready ({_engine.RecognizerLanguage.DisplayName})");
            }
            catch (Exception ex) { Log.Warn($"Windows OCR not available: {ex.Message}"); }
            return _engine;
        }
    }

    /// <summary>Text lines found in the image, top to bottom, or an empty list.</summary>
    public static async Task<List<string>> ReadLinesAsync(string imagePath)
    {
        var engine = Engine;
        if (engine is null || !File.Exists(imagePath)) return new List<string>();
        try
        {
            var file = await StorageFile.GetFileFromPathAsync(Path.GetFullPath(imagePath));
            using IRandomAccessStream stream = await file.OpenAsync(FileAccessMode.Read);
            var decoder = await BitmapDecoder.CreateAsync(stream);
            using var bitmap = await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied);
            var result = await engine.RecognizeAsync(bitmap);
            return result.Lines.Select(l => Clean(l.Text)).Where(t => t.Length > 0).ToList();
        }
        catch (Exception ex)
        {
            Log.Warn($"OCR failed on {Path.GetFileName(imagePath)}: {ex.Message}");
            return new List<string>();
        }
    }

    /// <summary>A one-or-two-line lyric note from the recognised lines: the first two lines that look like words.</summary>
    public static string ToLyricNote(IEnumerable<string> lines)
    {
        var good = lines.Where(LooksLikeWords).Take(2).ToList();
        var note = string.Join(" / ", good);
        return note.Length > 90 ? note[..87].TrimEnd() + "…" : note;
    }

    private static bool LooksLikeWords(string line)
    {
        var letters = line.Count(char.IsLetter);
        return letters >= 3 && letters >= line.Length * 0.5;
    }

    private static string Clean(string text)
    {
        var sb = new StringBuilder(text.Length);
        foreach (var ch in text) if (!char.IsControl(ch)) sb.Append(ch);
        return Regex.Replace(sb.ToString(), @"\s+", " ").Trim();
    }
}
