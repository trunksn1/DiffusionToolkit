using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using FontAwesome.WPF;

namespace Diffusion.Toolkit.Services;

/// <summary>
/// Represents a resolved tag icon that can be either a bitmap or an emoji string.
/// </summary>
public class TagIcon
{
    public BitmapImage? Bitmap { get; set; }
    public string? Emoji { get; set; }
    public bool IsEmoji => Emoji != null;
    /// <summary>
    /// Color for rendering (hex string like "#FF5500"). Null means white (default).
    /// </summary>
    public string? Color { get; set; }
}

public static class TagIconCache
{
    private static readonly ConcurrentDictionary<string, BitmapImage> _builtinIcons = new();
    private static readonly ConcurrentDictionary<string, BitmapImage> _fileIcons = new();
    private static readonly ConcurrentDictionary<int, TagIcon?> _tagIcons = new();
    private static BitmapImage? _genericTagIcon;

    // FontAwesome font family (loaded from the package resource)
    private static readonly FontFamily _fontAwesomeFamily = new FontFamily(new Uri("pack://application:,,,/FontAwesome.WPF;component/"), "./#FontAwesome");
    private static readonly Typeface _fontAwesomeTypeface = new Typeface(_fontAwesomeFamily, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);

    public static readonly Dictionary<string, FontAwesomeIcon> BuiltinIconSet = new()
    {
        { "tag", FontAwesomeIcon.Tag },
        { "tags", FontAwesomeIcon.Tags },
        { "camera", FontAwesomeIcon.Camera },
        { "paintbrush", FontAwesomeIcon.PaintBrush },
        { "bolt", FontAwesomeIcon.Bolt },
        { "fire", FontAwesomeIcon.Fire },
        { "leaf", FontAwesomeIcon.Leaf },
        { "diamond", FontAwesomeIcon.Diamond },
        { "flag", FontAwesomeIcon.Flag },
        { "bookmark", FontAwesomeIcon.Bookmark },
        { "check", FontAwesomeIcon.Check },
        { "ban", FontAwesomeIcon.Ban },
        { "clock", FontAwesomeIcon.ClockOutline },
        { "user", FontAwesomeIcon.User },
        { "globe", FontAwesomeIcon.Globe },
        { "star", FontAwesomeIcon.Star },
        { "heart", FontAwesomeIcon.Heart },
        { "eye", FontAwesomeIcon.Eye },
        { "lock", FontAwesomeIcon.Lock },
        { "magic", FontAwesomeIcon.Magic },
    };

    public static void Initialize()
    {
        foreach (var kvp in BuiltinIconSet)
        {
            var bitmap = RenderFontAwesomeIcon(kvp.Value, Brushes.White, 24);
            if (bitmap != null)
                _builtinIcons[kvp.Key] = bitmap;
        }

        _genericTagIcon = _builtinIcons.GetValueOrDefault("tags");
    }

    public static void RefreshTagMappings()
    {
        _tagIcons.Clear();

        var tags = ServiceLocator.DataStore?.GetTags();
        if (tags == null) return;

        foreach (var tag in tags)
        {
            if (string.IsNullOrEmpty(tag.Icon))
            {
                _tagIcons[tag.Id] = null;
            }
            else
            {
                _tagIcons[tag.Id] = ResolveTagIcon(tag.Icon);
            }
        }
    }

    public static TagIcon? GetTagIcon(int tagId)
    {
        return _tagIcons.GetValueOrDefault(tagId);
    }

    public static BitmapImage? GetGenericTagIcon()
    {
        return _genericTagIcon;
    }

    public static BitmapImage? GetBuiltinIcon(string key)
    {
        return _builtinIcons.GetValueOrDefault(key);
    }

    public static IReadOnlyDictionary<string, BitmapImage> GetAllBuiltinIcons()
    {
        return _builtinIcons;
    }

    /// <summary>
    /// Extracts color from the end of an icon value. Color is appended as ":#RRGGBB".
    /// Returns the value without the color suffix and the color string.
    /// </summary>
    private static (string value, string? color) ExtractColor(string value)
    {
        // Look for color suffix like ":#FF5500" at the end
        var lastColon = value.LastIndexOf(":#");
        if (lastColon >= 0 && value.Length - lastColon >= 8) // :#RRGGBB = 8 chars
        {
            var colorPart = value.Substring(lastColon + 1); // "#FF5500"
            var valuePart = value.Substring(0, lastColon);
            return (valuePart, colorPart);
        }
        return (value, null);
    }

    /// <summary>
    /// Resolves an icon reference string to a TagIcon (bitmap or emoji).
    /// Formats: "builtin:key", "file:path", "emoji:text"
    /// Optional color suffix: "builtin:heart:#FF0000", "emoji:🎨:#FF5500"
    /// </summary>
    public static TagIcon? ResolveTagIcon(string? iconRef)
    {
        if (string.IsNullOrEmpty(iconRef)) return null;

        if (iconRef.StartsWith("builtin:"))
        {
            var rest = iconRef.Substring(8);
            var (key, color) = ExtractColor(rest);
            if (color != null)
            {
                // Render a colored version of the builtin icon
                if (BuiltinIconSet.TryGetValue(key, out var faIcon))
                {
                    var brush = BrushFromHex(color);
                    var bitmap = RenderFontAwesomeIcon(faIcon, brush, 24);
                    return bitmap != null ? new TagIcon { Bitmap = bitmap, Color = color } : null;
                }
                return null;
            }
            var defaultBitmap = _builtinIcons.GetValueOrDefault(key);
            return defaultBitmap != null ? new TagIcon { Bitmap = defaultBitmap } : null;
        }

        if (iconRef.StartsWith("file:"))
        {
            var path = iconRef.Substring(5);
            var bitmap = LoadFileIcon(path);
            return bitmap != null ? new TagIcon { Bitmap = bitmap } : null;
        }

        if (iconRef.StartsWith("emoji:"))
        {
            var rest = iconRef.Substring(6);
            var (emoji, color) = ExtractColor(rest);
            return !string.IsNullOrEmpty(emoji) ? new TagIcon { Emoji = emoji, Color = color } : null;
        }

        return null;
    }

    public static Brush BrushFromHex(string? hex)
    {
        if (string.IsNullOrEmpty(hex)) return Brushes.White;
        try
        {
            var color = (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(hex);
            var brush = new SolidColorBrush(color);
            brush.Freeze();
            return brush;
        }
        catch
        {
            return Brushes.White;
        }
    }

    /// <summary>
    /// Resolves an icon reference to a BitmapImage only (for UI Image controls).
    /// Emojis are rendered to bitmap for preview purposes.
    /// </summary>
    public static BitmapImage? ResolveIcon(string? iconRef)
    {
        if (string.IsNullOrEmpty(iconRef)) return null;

        if (iconRef.StartsWith("builtin:"))
        {
            var rest = iconRef.Substring(8);
            var (key, color) = ExtractColor(rest);
            if (color != null)
            {
                if (BuiltinIconSet.TryGetValue(key, out var faIcon))
                    return RenderFontAwesomeIcon(faIcon, BrushFromHex(color), 24);
                return null;
            }
            return _builtinIcons.GetValueOrDefault(key);
        }

        if (iconRef.StartsWith("file:"))
        {
            var path = iconRef.Substring(5);
            return LoadFileIcon(path);
        }

        if (iconRef.StartsWith("emoji:"))
        {
            var rest = iconRef.Substring(6);
            var (emoji, color) = ExtractColor(rest);
            return RenderEmojiToBitmap(emoji, 24, BrushFromHex(color));
        }

        return null;
    }

    private static BitmapImage? LoadFileIcon(string path)
    {
        if (_fileIcons.TryGetValue(path, out var cached))
            return cached;

        try
        {
            if (!File.Exists(path)) return null;

            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.UriSource = new Uri(path, UriKind.Absolute);
            bitmap.DecodePixelWidth = 32;
            bitmap.DecodePixelHeight = 32;
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.EndInit();
            bitmap.Freeze();

            _fileIcons[path] = bitmap;
            return bitmap;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Renders a FontAwesome icon using DrawingVisual (no visual tree required).
    /// </summary>
    private static BitmapImage? RenderFontAwesomeIcon(FontAwesomeIcon icon, Brush foreground, double size)
    {
        try
        {
            var unicodeChar = ((char)(int)icon).ToString();

            var formattedText = new FormattedText(
                unicodeChar,
                CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight,
                _fontAwesomeTypeface,
                size * 0.75, // font size slightly smaller than cell size
                foreground,
                null,
                TextFormattingMode.Display,
                96);
            formattedText.TextAlignment = TextAlignment.Center;

            var drawingVisual = new DrawingVisual();
            using (var dc = drawingVisual.RenderOpen())
            {
                // Center the glyph in the cell
                var xPos = (size - formattedText.WidthIncludingTrailingWhitespace) / 2;
                var yPos = (size - formattedText.Height) / 2;
                dc.DrawText(formattedText, new Point(xPos, yPos));
            }

            var renderTarget = new RenderTargetBitmap(
                (int)size, (int)size, 96, 96, PixelFormats.Pbgra32);
            renderTarget.Render(drawingVisual);

            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(renderTarget));

            using var ms = new MemoryStream();
            encoder.Save(ms);
            ms.Seek(0, SeekOrigin.Begin);

            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.StreamSource = ms;
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.EndInit();
            bitmap.Freeze();

            return bitmap;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Renders an emoji string to a BitmapImage for preview in UI controls.
    /// </summary>
    private static BitmapImage? RenderEmojiToBitmap(string emoji, double size, Brush? foreground = null)
    {
        try
        {
            var typeface = new Typeface(new FontFamily("Segoe UI Emoji"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
            var formattedText = new FormattedText(
                emoji,
                CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight,
                typeface,
                size * 0.85,
                foreground ?? Brushes.White,
                null,
                TextFormattingMode.Display,
                96);
            formattedText.TextAlignment = TextAlignment.Center;

            var drawingVisual = new DrawingVisual();
            using (var dc = drawingVisual.RenderOpen())
            {
                var xPos = (size - formattedText.WidthIncludingTrailingWhitespace) / 2;
                var yPos = (size - formattedText.Height) / 2;
                dc.DrawText(formattedText, new Point(xPos, yPos));
            }

            var renderTarget = new RenderTargetBitmap(
                (int)size, (int)size, 96, 96, PixelFormats.Pbgra32);
            renderTarget.Render(drawingVisual);

            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(renderTarget));

            using var ms = new MemoryStream();
            encoder.Save(ms);
            ms.Seek(0, SeekOrigin.Begin);

            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.StreamSource = ms;
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.EndInit();
            bitmap.Freeze();

            return bitmap;
        }
        catch
        {
            return null;
        }
    }
}
