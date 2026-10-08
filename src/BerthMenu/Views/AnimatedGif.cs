using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;

namespace BerthMenu.Views
{
    /// <summary>
    /// Plays animated GIFs. WPF's own image loading only ever shows a GIF's first
    /// frame, which is why GIF backgrounds and icons used to sit still.
    ///
    /// How: every frame is decoded and composed into a full picture once (GIF frames
    /// are often just the part that changed, drawn over the previous one), on a
    /// background thread, then played back as a looping animation of the picture
    /// being shown — ImageBrush.ImageSource for the dock background (AnimateBrush),
    /// Image.Source for an icon tile (the SourcePath attached property, set in
    /// MainWindow.xaml's IconTileTemplate). Anything that isn't a GIF with more than
    /// one frame is left alone, still showing as the ordinary static image.
    ///
    /// Memory: composed frames are full pictures, so big or long GIFs are scaled
    /// down to stay within a budget (see Load) instead of using hundreds of MB.
    /// Decoded GIFs are kept per file and size, so the same icon on several tiles,
    /// or a reopened dock, doesn't decode it again.
    /// </summary>
    public static class AnimatedGif
    {
        public sealed class Frames
        {
            public required IReadOnlyList<BitmapSource> Images { get; init; }
            public required ObjectAnimationUsingKeyFrames Animation { get; init; }
        }

        private static readonly Dictionary<string, Task<Frames?>> Cache = new(StringComparer.OrdinalIgnoreCase);

        public static bool IsGif(string? path) =>
            !string.IsNullOrEmpty(path) && path.EndsWith(".gif", StringComparison.OrdinalIgnoreCase);

        /// <summary>Decodes (or reuses) a GIF's frames, at most <paramref name="maxSide"/>
        /// pixels on the longest side and about <paramref name="budgetBytes"/> in total.
        /// Null if it isn't an animated GIF or can't be read. Call from the UI thread;
        /// the decoding itself runs in the background.</summary>
        public static Task<Frames?> LoadAsync(string path, int maxSide, long budgetBytes)
        {
            string key;
            try { key = $"{path}|{File.GetLastWriteTimeUtc(path).Ticks}|{maxSide}|{budgetBytes}"; }
            catch { return Task.FromResult<Frames?>(null); }

            if (!Cache.TryGetValue(key, out var task))
            {
                if (Cache.Count >= 64)
                    Cache.Clear(); // plenty for the icons in use; keeps memory bounded
                task = Task.Run(() => Load(path, maxSide, budgetBytes));
                Cache[key] = task;
            }
            return task;
        }

        // The dock background keeps only its latest GIF decoded (it can be large),
        // separate from the icon cache above.
        private static string? _backgroundKey;
        private static Task<Frames?>? _backgroundTask;

        /// <summary>Starts the GIF playing on a background brush once it's decoded.
        /// <paramref name="stillCurrent"/> is checked first, so a background that was
        /// changed again meanwhile isn't overwritten. The caller stops the animation
        /// (BeginAnimation(ImageSourceProperty, null)) when it replaces the brush, so
        /// an old brush doesn't keep animating unseen.</summary>
        public static async void AnimateBrush(ImageBrush brush, string path, Func<bool> stillCurrent)
        {
            const int maxSide = 1600;
            const long budget = 160L * 1024 * 1024;

            string key;
            try { key = $"{path}|{File.GetLastWriteTimeUtc(path).Ticks}|{new FileInfo(path).Length}"; }
            catch { return; }

            if (_backgroundKey != key || _backgroundTask == null)
            {
                _backgroundKey = key;
                _backgroundTask = Task.Run(() => Load(path, maxSide, budget));
            }

            var frames = await _backgroundTask;
            if (frames == null || !stillCurrent())
                return;
            brush.BeginAnimation(ImageBrush.ImageSourceProperty, frames.Animation);
        }

        // ---- Icon tiles: local:AnimatedGif.SourcePath="{Binding AnimatedIconPath}"

        public static readonly DependencyProperty SourcePathProperty =
            DependencyProperty.RegisterAttached("SourcePath", typeof(string), typeof(AnimatedGif),
                new PropertyMetadata(null, OnSourcePathChanged));

        public static string? GetSourcePath(DependencyObject d) => (string?)d.GetValue(SourcePathProperty);
        public static void SetSourcePath(DependencyObject d, string? value) => d.SetValue(SourcePathProperty, value);

        // Marks an Image whose Loaded/Unloaded are already hooked.
        private static readonly DependencyProperty HookedProperty =
            DependencyProperty.RegisterAttached("Hooked", typeof(bool), typeof(AnimatedGif), new PropertyMetadata(false));

        private static void OnSourcePathChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is not Image image)
                return;

            if (!(bool)image.GetValue(HookedProperty))
            {
                image.SetValue(HookedProperty, true);
                // Only animate while the tile is actually on screen: an animation
                // keeps running (and keeps its tile in memory) until it's stopped, so
                // a tile that's thrown away — a search result, say — stops here.
                image.Loaded += (_, _) => Start(image);
                image.Unloaded += (_, _) => image.BeginAnimation(Image.SourceProperty, null);
            }

            Start(image);
        }

        private static async void Start(Image image)
        {
            // Back to the bound, static Source (a non-GIF icon, or none).
            image.BeginAnimation(Image.SourceProperty, null);

            string? path = GetSourcePath(image);
            if (!image.IsLoaded || !IsGif(path) || !File.Exists(path))
                return;

            var frames = await LoadAsync(path!, maxSide: 192, budgetBytes: 24L * 1024 * 1024);
            if (frames == null || !image.IsLoaded
                || !string.Equals(GetSourcePath(image), path, StringComparison.OrdinalIgnoreCase))
                return; // not animated, gone, or moved on to another icon meanwhile

            // An animation outranks the Source binding while it runs, and hands
            // Source back to the binding once removed.
            image.BeginAnimation(Image.SourceProperty, frames.Animation);
        }

        // ---- Decoding

        private static Frames? Load(string path, int maxSide, long budgetBytes)
        {
            try
            {
                using var stream = File.OpenRead(path);
                var decoder = new GifBitmapDecoder(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
                int count = Math.Min(decoder.Frames.Count, 500);
                if (count < 2)
                    return null; // a still GIF — the normal image loading already shows it

                int width = 0, height = 0;
                try
                {
                    if (decoder.Metadata is BitmapMetadata meta)
                    {
                        width = Convert.ToInt32(meta.GetQuery("/logscrdesc/Width"));
                        height = Convert.ToInt32(meta.GetQuery("/logscrdesc/Height"));
                    }
                }
                catch { /* fall back to the first frame's size */ }
                if (width <= 0 || height <= 0)
                {
                    width = decoder.Frames[0].PixelWidth;
                    height = decoder.Frames[0].PixelHeight;
                }

                // Scale so the longest side is at most maxSide and all frames together
                // stay within the budget (4 bytes per pixel per frame).
                double scale = Math.Min(1.0, (double)maxSide / Math.Max(width, height));
                double perFrameBudget = (double)budgetBytes / count;
                double fullFrameBytes = (double)width * height * 4;
                if (fullFrameBytes * scale * scale > perFrameBudget)
                    scale = Math.Sqrt(perFrameBudget / fullFrameBytes);

                int stride = width * 4;
                var canvas = new byte[stride * height];
                var images = new List<BitmapSource>(count);
                var delays = new List<TimeSpan>(count);

                for (int i = 0; i < count; i++)
                {
                    var frame = decoder.Frames[i];
                    var (left, top, delay, disposal) = ReadFrameInfo(frame);

                    // Disposal 3 ("restore previous"): this frame is temporary, so
                    // remember the picture underneath it.
                    byte[]? saved = disposal == 3 ? (byte[])canvas.Clone() : null;

                    DrawFrame(frame, canvas, width, height, left, top);

                    BitmapSource composed = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, canvas, stride);
                    if (scale < 0.999)
                    {
                        var scaled = new TransformedBitmap(composed, new ScaleTransform(scale, scale));
                        composed = new CachedBitmap(scaled, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
                    }
                    composed.Freeze();
                    images.Add(composed);
                    delays.Add(delay);

                    if (disposal == 2)
                        ClearRect(canvas, width, height, left, top, frame.PixelWidth, frame.PixelHeight);
                    else if (saved != null)
                        canvas = saved;
                }

                var animation = new ObjectAnimationUsingKeyFrames { RepeatBehavior = RepeatBehavior.Forever };
                var at = TimeSpan.Zero;
                for (int i = 0; i < images.Count; i++)
                {
                    animation.KeyFrames.Add(new DiscreteObjectKeyFrame(images[i], KeyTime.FromTimeSpan(at)));
                    at += delays[i];
                }
                animation.Duration = new Duration(at);
                animation.Freeze();

                return new Frames { Images = images, Animation = animation };
            }
            catch (Exception ex)
            {
                // Unreadable — the static image still shows. Noted in
                // %AppData%\BerthMenu\gif.log so a GIF that won't play can be looked into.
                Log($"{path}: {ex.GetType().Name}: {ex.Message}");
                return null;
            }
        }

        private static void Log(string message)
        {
            try
            {
                string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "BerthMenu");
                Directory.CreateDirectory(dir);
                File.AppendAllText(Path.Combine(dir, "gif.log"), $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {message}{Environment.NewLine}");
            }
            catch
            {
                // Logging is best-effort.
            }
        }

        private static (int Left, int Top, TimeSpan Delay, int Disposal) ReadFrameInfo(BitmapFrame frame)
        {
            int left = 0, top = 0, delayCs = 0, disposal = 0;
            try
            {
                if (frame.Metadata is BitmapMetadata meta)
                {
                    left = Convert.ToInt32(meta.GetQuery("/imgdesc/Left") ?? 0);
                    top = Convert.ToInt32(meta.GetQuery("/imgdesc/Top") ?? 0);
                    delayCs = Convert.ToInt32(meta.GetQuery("/grctlext/Delay") ?? 0);
                    disposal = Convert.ToInt32(meta.GetQuery("/grctlext/Disposal") ?? 0);
                }
            }
            catch { /* a frame without that info just uses the defaults */ }

            // Delays are in hundredths of a second. Like web browsers, treat 0 or 1
            // (meaning "as fast as possible") as 0.1s, so such GIFs don't race.
            if (delayCs < 2)
                delayCs = 10;
            return (left, top, TimeSpan.FromMilliseconds(delayCs * 10), disposal);
        }

        /// <summary>Draws one frame onto the canvas at its offset. GIF transparency
        /// is all-or-nothing, so fully transparent pixels leave what's underneath.</summary>
        private static void DrawFrame(BitmapSource frame, byte[] canvas, int width, int height, int left, int top)
        {
            var bgra = new FormatConvertedBitmap(frame, PixelFormats.Bgra32, null, 0);
            int fw = bgra.PixelWidth, fh = bgra.PixelHeight;
            int fstride = fw * 4;
            var pixels = new byte[fstride * fh];
            bgra.CopyPixels(pixels, fstride, 0);

            for (int y = 0; y < fh; y++)
            {
                int cy = top + y;
                if (cy < 0 || cy >= height)
                    continue;
                for (int x = 0; x < fw; x++)
                {
                    int cx = left + x;
                    if (cx < 0 || cx >= width)
                        continue;
                    int src = y * fstride + x * 4;
                    if (pixels[src + 3] == 0)
                        continue;
                    int dst = cy * width * 4 + cx * 4;
                    canvas[dst] = pixels[src];
                    canvas[dst + 1] = pixels[src + 1];
                    canvas[dst + 2] = pixels[src + 2];
                    canvas[dst + 3] = pixels[src + 3];
                }
            }
        }

        private static void ClearRect(byte[] canvas, int width, int height, int left, int top, int w, int h)
        {
            for (int y = Math.Max(0, top); y < Math.Min(height, top + h); y++)
            {
                int start = (y * width + Math.Max(0, left)) * 4;
                int end = (y * width + Math.Min(width, left + w)) * 4;
                if (end > start)
                    Array.Clear(canvas, start, end - start);
            }
        }
    }
}
