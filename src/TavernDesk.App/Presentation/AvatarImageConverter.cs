using System.Globalization;
using System.IO;
using System.Windows.Data;
using System.Windows.Media.Imaging;

namespace TavernDesk.App.Presentation;

/// <summary>
/// Decodes avatar paths into frozen, downsampled <see cref="BitmapSource"/> instances.
/// Raw path bindings decode the entire image, increasing memory usage in shelves and long conversations.
/// ConverterParameter is the display width in DIP (default 96); decoding at twice that width supports 200% DPI.
/// Missing files and decoding failures return null, matching an invalid Image source.
/// </summary>
[ValueConversion(typeof(string), typeof(BitmapSource))]
public sealed class AvatarImageConverter : IValueConverter
{
    private const int DefaultDisplayWidth = 96;
    private const int MaxCacheEntries = 256;

    // Move cache hits to the tail and evict from the head; one lock protects the bounded LRU.
    private static readonly object CacheLock = new();
    private static readonly Dictionary<string, LinkedListNode<(string Key, BitmapSource Image)>> CacheIndex = new();
    private static readonly LinkedList<(string Key, BitmapSource Image)> CacheOrder = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not string path || string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        var displayWidth = parameter switch
        {
            int i when i > 0 => i,
            string s when int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) && n > 0 => n,
            _ => DefaultDisplayWidth
        };

        return Load(path, displayWidth * 2);
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();

    internal static BitmapSource? Load(string path, int decodeWidth)
    {
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            // Including the modification time invalidates replaced avatars without restarting.
            var key = string.Create(
                CultureInfo.InvariantCulture,
                $"{path}|{File.GetLastWriteTimeUtc(path).Ticks}|{decodeWidth}");

            lock (CacheLock)
            {
                if (CacheIndex.TryGetValue(key, out var hit))
                {
                    CacheOrder.Remove(hit);
                    CacheOrder.AddLast(hit);
                    return hit.Value.Image;
                }
            }

            var image = Decode(path, decodeWidth);

            lock (CacheLock)
            {
                if (!CacheIndex.ContainsKey(key))
                {
                    CacheIndex[key] = CacheOrder.AddLast((key, image));
                    while (CacheOrder.Count > MaxCacheEntries)
                    {
                        CacheIndex.Remove(CacheOrder.First!.Value.Key);
                        CacheOrder.RemoveFirst();
                    }
                }
            }

            return image;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException
                                       or InvalidOperationException or ArgumentException)
        {
            return null;
        }
    }

    private static BitmapSource Decode(string path, int decodeWidth)
    {
        // OnLoad releases the file handle once decoding completes, allowing avatar replacement.
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
        bitmap.DecodePixelWidth = decodeWidth;
        bitmap.StreamSource = stream;
        bitmap.EndInit();
        bitmap.Freeze();
        return bitmap;
    }
}
