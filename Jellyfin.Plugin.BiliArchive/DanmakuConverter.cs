using System.Globalization;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace Jellyfin.Plugin.BiliArchive;

internal sealed record DanmakuComment(double Seconds, int Mode, int Size, int Color, string Text);

internal static class DanmakuConverter
{
    private const int Width = 1920;
    private const int Height = 1080;
    private const double ScrollSpeed = 260;

    internal static async Task<IReadOnlyList<DanmakuComment>> ParseAsync(Stream stream, CancellationToken ct)
    {
        using var reader = XmlReader.Create(stream, new XmlReaderSettings
        {
            Async = true, DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null,
            MaxCharactersInDocument = 32 * 1024 * 1024
        });
        var xml = await XDocument.LoadAsync(reader, LoadOptions.None, ct).ConfigureAwait(false);
        if (xml.Root?.Name != "i") throw new InvalidDataException("B 站未返回有效的弹幕 XML。");
        var comments = new List<DanmakuComment>();
        foreach (var element in xml.Root.Elements("d"))
        {
            var fields = ((string?)element.Attribute("p"))?.Split(',');
            if (fields is null || fields.Length < 4 ||
                !double.TryParse(fields[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds) ||
                !double.IsFinite(seconds) || seconds < 0 || seconds > 86400 ||
                !int.TryParse(fields[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var mode) ||
                mode is not (1 or 2 or 3 or 4 or 5 or 6) ||
                !int.TryParse(fields[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var size) ||
                !int.TryParse(fields[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out var color)) continue;
            var content = element.Value.Trim();
            if (content.Length == 0) continue;
            comments.Add(new DanmakuComment(seconds, mode, Math.Clamp(size, 18, 48),
                Math.Clamp(color, 0, 0xffffff), content[..Math.Min(content.Length, 300)]));
            if (comments.Count == 20000) break;
        }
        return comments;
    }

    internal static string ToAss(IEnumerable<DanmakuComment> comments)
    {
        var output = new StringBuilder("[Script Info]\nScriptType: v4.00+\nPlayResX: 1920\nPlayResY: 1080\nWrapStyle: 2\nScaledBorderAndShadow: yes\n\n[V4+ Styles]\nFormat: Name, Fontname, Fontsize, PrimaryColour, SecondaryColour, OutlineColour, BackColour, Bold, Italic, Underline, StrikeOut, ScaleX, ScaleY, Spacing, Angle, BorderStyle, Outline, Shadow, Alignment, MarginL, MarginR, MarginV, Encoding\nStyle: Danmaku,Arial,38,&H00FFFFFF,&H00FFFFFF,&H00000000,&H80000000,0,0,0,0,100,100,0,0,1,2,0,7,0,0,0,1\n\n[Events]\nFormat: Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text\n");
        var scrollLanes = new double[14];
        var reverseLanes = new double[14];
        var topLanes = new double[5];
        var bottomLanes = new double[5];
        foreach (var comment in comments.OrderBy(x => x.Seconds))
        {
            var text = Escape(comment.Text);
            if (text.Length == 0) continue;
            var width = Math.Min(Width * 2, EstimateWidth(comment.Text, comment.Size));
            var moving = comment.Mode is 1 or 2 or 3 or 6;
            var lanes = comment.Mode switch
            {
                4 => bottomLanes,
                5 => topLanes,
                6 => reverseLanes,
                _ => scrollLanes
            };
            var lane = Array.FindIndex(lanes, freeAt => freeAt <= comment.Seconds);
            if (lane < 0) continue;
            var duration = moving ? (Width + width) / ScrollSpeed : 5;
            lanes[lane] = comment.Seconds + (moving ? width / ScrollSpeed + 0.5 : duration);
            var y = comment.Mode switch
            {
                4 => Height - 80 - lane * 60,
                5 => 60 + lane * 60,
                _ => 60 + lane * 62
            };
            var position = comment.Mode switch
            {
                4 or 5 => $"\\an8\\pos({Width / 2},{y})",
                6 => $"\\an7\\move({-width},{y},{Width},{y})",
                _ => $"\\an7\\move({Width},{y},{-width},{y})"
            };
            var rgb = comment.Color;
            var bgr = ((rgb & 0xff) << 16) | (rgb & 0xff00) | ((rgb >> 16) & 0xff);
            output.Append("Dialogue: 0,").Append(Time(comment.Seconds)).Append(',')
                .Append(Time(comment.Seconds + duration)).Append(",Danmaku,,0,0,0,,{")
                .Append(position).Append("\\fs").Append(comment.Size)
                .Append("\\c&H").Append(bgr.ToString("X6", CultureInfo.InvariantCulture)).Append("&}")
                .Append(text).Append('\n');
        }
        return output.ToString();
    }

    private static int EstimateWidth(string text, int size) =>
        (int)Math.Ceiling(text.Sum(ch => ch < 128 ? size * 0.55 : size));

    private static string Escape(string text)
    {
        var result = new StringBuilder(text.Length);
        foreach (var ch in text)
        {
            if (ch is '\r' or '\n') result.Append(' ');
            else if (char.IsControl(ch)) continue;
            else if (ch == '\\') result.Append('\\').Append('\\');
            else if (ch == '{') result.Append('（');
            else if (ch == '}') result.Append('）');
            else result.Append(ch);
        }
        return result.ToString();
    }

    private static string Time(double seconds)
    {
        var centiseconds = (long)Math.Round(seconds * 100, MidpointRounding.AwayFromZero);
        return string.Create(CultureInfo.InvariantCulture,
            $"{centiseconds / 360000}:{centiseconds / 6000 % 60:D2}:{centiseconds / 100 % 60:D2}.{centiseconds % 100:D2}");
    }
}
