#region License Information (GPL v3)

/*
    ShareX - A program that allows you to take screenshots and share any file type
    Copyright (c) 2007-2026 ShareX Team

    This program is free software; you can redistribute it and/or
    modify it under the terms of the GNU General Public License
    as published by the Free Software Foundation; either version 2
    of the License, or (at your option) any later version.

    This program is distributed in the hope that it will be useful,
    but WITHOUT ANY WARRANTY; without even the implied warranty of
    MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
    GNU General Public License for more details.

    You should have received a copy of the GNU General Public License
    along with this program; if not, write to the Free Software
    Foundation, Inc., 51 Franklin Street, Fifth Floor, Boston, MA  02110-1301, USA.

    Optionally you can also view the license at <http://www.gnu.org/licenses/>.
*/

#endregion License Information (GPL v3)

using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using Bitmap = SkiaSharp.SKBitmap;
using Image = SkiaSharp.SKBitmap;
using ImageFormat = SkiaSharp.SKEncodedImageFormat;

namespace ShareX.HelpersLib
{
    public static class ClipboardHelpers
    {
        public const string FORMAT_PNG = "PNG";
        public const string FORMAT_17 = "Format17";

        public static bool Clear()
        {
            try { return AvaloniaClipboard.Clear(); }
            catch (Exception e) { DebugHelper.WriteException(e, "Clipboard clear failed."); return false; }
        }

        public static ClipboardData CaptureData() => AvaloniaClipboard.Capture();

        public static Bitmap ConvertClipboardDibToBitmap(byte[] data)
        {
            return ClipboardHelpersEx.ImageFromClipboardDib(data);
        }

        public static Bitmap ConvertClipboardDibV5ToBitmap(byte[] data)
        {
            return ClipboardHelpersEx.DIBV5ToBitmap(data);
        }

        public static bool CopyText(string text)
        {
            if (string.IsNullOrEmpty(text)) return false;
            try { return AvaloniaClipboard.SetText(text); }
            catch (Exception e) { DebugHelper.WriteException(e, "Clipboard copy text failed."); return false; }
        }

        public static uint SequenceNumber => OperatingSystem.IsWindows() ? WindowsClipboard.SequenceNumber : 0;

        // The clipboard PNG is never written to a file by ShareX, so it is packed at the quickest level: how soon
        // the copy is ready matters more than its size (about 4 % larger than the slower packing for a screenshot).
        private static readonly SkiaSharp.SKPngEncoderOptions ClipboardPngOptions = new(SkiaSharp.SKPngEncoderFilterFlags.Sub, 1);

        // stillWanted, if given, is asked just before the write; false leaves the clipboard as it is. A copy that is
        // prepared off the UI thread uses it so that it does not overwrite a newer copy.
        public static bool CopyImage(Image img, string fileName = null, Func<bool> stillWanted = null)
        {
            return CopyImage(img, preEncodedPng: default, fileName: fileName, knownOpaque: false, stillWanted: stillWanted);
        }

        /// <summary>
        /// Copies <paramref name="img"/> to the clipboard, reusing <paramref name="preEncodedPng"/> when provided
        /// so captures that also save PNG to disk avoid encoding the image twice.
        /// </summary>
        public static bool CopyImage(Image img, ReadOnlyMemory<byte> preEncodedPng, string fileName = null,
            bool knownOpaque = false, Func<bool> stillWanted = null)
        {
            if (img == null) return false;
            string size = $"{img.Width}x{img.Height}";
            try
            {
                if (OperatingSystem.IsWindows())
                {
                    using PreparedClipboardImage prepared = PrepareImageHandles(img, preEncodedPng, fileName, knownOpaque);
                    return CommitPreparedImage(prepared, stillWanted);
                }

                byte[] pngBytes = !preEncodedPng.IsEmpty ? preEncodedPng.ToArray() : EncodeClipboardPng(img);
                string html = CreateImageHtml(fileName);
                bool copied = AvaloniaClipboard.SetImage(pngBytes, null, html, stillWanted);
                LogImageCopy(copied, size, 0, pngBytes.Length, html != null);
                return copied;
            }
            catch (Exception e) { DebugHelper.WriteException(e, $"Clipboard copy image failed ({size})."); return false; }
        }

        /// <summary>
        /// Pre-allocated Win32 clipboard handles for an image, prepared off the UI thread so disk writes and
        /// clipboard preparation can run concurrently. Dispose frees any handles not yet committed.
        /// </summary>
        public sealed class PreparedClipboardImage : IDisposable
        {
            internal IntPtr PngHandle;
            internal IntPtr DibHandle;
            internal IntPtr HtmlHandle;
            internal int PngBytes;
            internal int DibBytes;
            internal bool HasHtml;
            internal string Size;

            public void Dispose()
            {
                if (HtmlHandle != IntPtr.Zero) { WindowsClipboard.FreeHGlobal(HtmlHandle); HtmlHandle = IntPtr.Zero; }
                if (PngHandle != IntPtr.Zero) { WindowsClipboard.FreeHGlobal(PngHandle); PngHandle = IntPtr.Zero; }
                if (DibHandle != IntPtr.Zero) { WindowsClipboard.FreeHGlobal(DibHandle); DibHandle = IntPtr.Zero; }
            }
        }

        /// <summary>
        /// Prepares Win32 CF_DIB, PNG and optional HTML clipboard handles off the UI thread.
        /// When <paramref name="preEncodedPng"/> is non-empty, copies it directly into an HGLOBAL with zero re-encoding.
        /// </summary>
        public static PreparedClipboardImage PrepareImageHandles(Image img, ReadOnlyMemory<byte> preEncodedPng = default,
            string fileName = null, bool knownOpaque = false)
        {
            if (img == null) return null;
            if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();

            PreparedClipboardImage prepared = new() { Size = $"{img.Width}x{img.Height}" };
            try
            {
                if (!preEncodedPng.IsEmpty)
                {
                    prepared.PngHandle = WindowsClipboard.AllocHGlobal(preEncodedPng.Span);
                    prepared.PngBytes = preEncodedPng.Length;
                }
                else
                {
                    using MemoryStream png = new();
                    SkiaImageHelpers.SavePNG(img, png, PNGBitDepth.Automatic, long.MaxValue, ClipboardPngOptions);
                    if (png.TryGetBuffer(out ArraySegment<byte> seg))
                    {
                        prepared.PngHandle = WindowsClipboard.AllocHGlobal(seg.AsSpan());
                        prepared.PngBytes = seg.Count;
                    }
                    else
                    {
                        byte[] arr = png.ToArray();
                        prepared.PngHandle = WindowsClipboard.AllocHGlobal(arr);
                        prepared.PngBytes = arr.Length;
                    }
                }

                prepared.DibHandle = CreateDibHGlobal(img, knownOpaque, out prepared.DibBytes);

                string html = CreateImageHtml(fileName);
                if (!string.IsNullOrEmpty(html))
                {
                    prepared.HtmlHandle = WindowsClipboard.AllocHGlobal(Encoding.UTF8.GetBytes(html + "\0"));
                    prepared.HasHtml = true;
                }

                return prepared;
            }
            catch
            {
                prepared.Dispose();
                throw;
            }
        }

        /// <summary>Commits handles from <see cref="PrepareImageHandles"/> to the Win32 clipboard.</summary>
        public static bool CommitPreparedImage(PreparedClipboardImage prepared, Func<bool> stillWanted = null)
        {
            if (prepared == null) return false;
            IntPtr png = prepared.PngHandle; prepared.PngHandle = IntPtr.Zero;
            IntPtr dib = prepared.DibHandle; prepared.DibHandle = IntPtr.Zero;
            IntPtr html = prepared.HtmlHandle; prepared.HtmlHandle = IntPtr.Zero;
            try
            {
                bool copied = AvaloniaClipboard.SetImageHandles(png, dib, html, stillWanted);
                LogImageCopy(copied, prepared.Size, prepared.DibBytes, prepared.PngBytes, prepared.HasHtml);
                return copied;
            }
            catch (Exception e)
            {
                DebugHelper.WriteException(e, $"Clipboard copy image failed ({prepared.Size}).");
                return false;
            }
        }

        private static byte[] EncodeClipboardPng(Image img)
        {
            using MemoryStream png = new();
            SkiaImageHelpers.SavePNG(img, png, PNGBitDepth.Automatic, long.MaxValue, ClipboardPngOptions);
            return png.ToArray();
        }

        private static void LogImageCopy(bool copied, string size, int dibBytes, int pngBytes, bool hasHtml)
        {
            // One line per image copy: the size and formats tell what another app could paste.
            // KB rounded up, so a small image never shows as 0 KB.
            string formats = string.Join(" + ", new[] { dibBytes > 0 ? $"DIB {(dibBytes + 1023) / 1024} KB" : null,
                $"PNG {(pngBytes + 1023) / 1024} KB", hasHtml ? "HTML" : null }.Where(format => format != null));
            DebugHelper.WriteLine(copied
                ? $"Clipboard: image {size} written as {formats}."
                : $"Clipboard: image {size} was not written ({formats}): the clipboard changed while it was being prepared.");
        }

        private static IntPtr CreateDibHGlobal(Image img, bool knownOpaque, out int byteCount)
        {
            if (HelpersOptions.UseAlternativeClipboardCopyImage && !HelpersOptions.DefaultCopyImageFillBackground)
            {
                byte[] alt = ClipboardHelpersEx.ConvertToDib(img);
                byteCount = alt.Length;
                return WindowsClipboard.AllocHGlobal(alt);
            }

            // Fast path: write 24-bpp bottom-up DIB directly into GMEM_MOVEABLE without an intermediate LOH byte[].
            IntPtr hDib = SkiaImageHelpers.TryCreateOpaqueDibHGlobal(img, knownOpaque, out byteCount);
            if (hDib != IntPtr.Zero) return hDib;

            using Bitmap opaque = SkiaImageHelpers.FillBackground(img, Color.White);
            hDib = SkiaImageHelpers.TryCreateOpaqueDibHGlobal(opaque, knownOpaque: true, out byteCount);
            if (hDib != IntPtr.Zero) return hDib;

            using MemoryStream bmp = new();
            opaque.Save(bmp, ImageFormat.Bmp);
            byte[] fallback = bmp.ToArray()[14..];
            byteCount = fallback.Length;
            return WindowsClipboard.AllocHGlobal(fallback);
        }

        private static string CreateImageHtml(string fileName)
        {
            if (!HelpersOptions.UseAlternativeClipboardCopyImage || string.IsNullOrEmpty(fileName)) return null;
            string html = $"<img src=\"{fileName}\"/>";
            return OperatingSystem.IsWindows() ? GenerateHTMLFragment(html) : html;
        }

        public static bool CopyFile(string path)
        {
            if (!string.IsNullOrEmpty(path))
            {
                return CopyFile(new string[] { path });
            }

            return false;
        }

        public static bool CopyFile(string[] paths)
        {
            if (paths == null || paths.Length == 0) return false;
            try { return AvaloniaClipboard.SetFiles(paths); }
            catch (Exception e) { DebugHelper.WriteException(e, "Clipboard copy file failed."); return false; }
        }

        public static bool CopyImageFromFile(string path)
        {
            if (!string.IsNullOrEmpty(path) && File.Exists(path))
            {
                try
                {
                    using (Bitmap bmp = SkiaImageHelpers.LoadImage(path))
                    {
                        string fileName = Path.GetFileName(path);
                        return CopyImage(bmp, fileName);
                    }
                }
                catch (Exception e)
                {
                    DebugHelper.WriteException(e, "Clipboard copy image from file failed.");
                }
            }

            return false;
        }

        public static bool CopyTextFromFile(string path)
        {
            if (!string.IsNullOrEmpty(path) && File.Exists(path))
            {
                try
                {
                    string text = File.ReadAllText(path, Encoding.UTF8);
                    return CopyText(text);
                }
                catch (Exception e)
                {
                    DebugHelper.WriteException(e, "Clipboard copy text from file failed.");
                }
            }

            return false;
        }

        public static Bitmap GetImage(bool checkContainsImage = false)
        {
            try
            {
                byte[] png = AvaloniaClipboard.GetImage();
                if (png != null) return SkiaImageHelpers.ByteArrayToBitmap(png);
                ClipboardData data = CaptureData();
                foreach (string format in new[] { FORMAT_PNG, "image/png", FORMAT_17, ClipboardDataFormats.Dib })
                {
                    if (data.GetData(format) is not byte[] bytes) continue;
                    Bitmap image = format is FORMAT_PNG or "image/png" ? SkiaImageHelpers.ByteArrayToBitmap(bytes) :
                        ClipboardHelpersEx.ImageFromClipboardDib(bytes);
                    if (image != null) return image;
                }
            }
            catch (Exception e) { DebugHelper.WriteException(e, "Clipboard get image failed."); }
            return null;
        }

        public static Bitmap GetImageAlternative2() => GetImage();

        public static string GetText(bool checkContainsText = false)
        {
            try { return AvaloniaClipboard.GetText(); }
            catch (Exception e) { DebugHelper.WriteException(e, "Clipboard get text failed."); return null; }
        }

        public static string[] GetFileDropList(bool checkContainsFileDropList = false)
        {
            try { return AvaloniaClipboard.GetFiles(); }
            catch (Exception e) { DebugHelper.WriteException(e, "Clipboard get files failed."); return null; }
        }

        public static Bitmap TryGetImage()
        {
            if (ContainsImage())
            {
                return GetImage();
            }
            else if (ContainsFileDropList())
            {
                string[] files = GetFileDropList();

                if (files != null)
                {
                    string imageFilePath = files.FirstOrDefault(x => FileHelpers.IsImageFile(x));

                    if (!string.IsNullOrEmpty(imageFilePath))
                    {
                        return SkiaImageHelpers.LoadImage(imageFilePath);
                    }
                }
            }

            return null;
        }

        private static string GenerateHTMLFragment(string html)
        {
            StringBuilder sb = new StringBuilder();

            string header = "Version:0.9\r\nStartHTML:<<<<<<<<<1\r\nEndHTML:<<<<<<<<<2\r\nStartFragment:<<<<<<<<<3\r\nEndFragment:<<<<<<<<<4\r\n";
            string startHTML = "<html>\r\n<body>\r\n";
            string startFragment = "<!--StartFragment-->";
            string endFragment = "<!--EndFragment-->";
            string endHTML = "\r\n</body>\r\n</html>";

            sb.Append(header);

            int startHTMLLength = header.Length;
            int startFragmentLength = startHTMLLength + startHTML.Length + startFragment.Length;
            int endFragmentLength = startFragmentLength + Encoding.UTF8.GetByteCount(html);
            int endHTMLLength = endFragmentLength + endFragment.Length + endHTML.Length;

            sb.Replace("<<<<<<<<<1", startHTMLLength.ToString("D10"));
            sb.Replace("<<<<<<<<<2", endHTMLLength.ToString("D10"));
            sb.Replace("<<<<<<<<<3", startFragmentLength.ToString("D10"));
            sb.Replace("<<<<<<<<<4", endFragmentLength.ToString("D10"));

            sb.Append(startHTML);
            sb.Append(startFragment);
            sb.Append(html);
            sb.Append(endFragment);
            sb.Append(endHTML);

            return sb.ToString();
        }

        public static bool ContainsImage() => Contains(ClipboardDataFormats.Bitmap);
        public static bool ContainsText() => Contains(ClipboardDataFormats.Text);
        public static bool ContainsFileDropList() => Contains(ClipboardDataFormats.FileDrop);

        private static bool Contains(string format)
        {
            try { return AvaloniaClipboard.Contains(format); }
            catch (Exception e) { DebugHelper.WriteException(e); return false; }
        }
    }
}
