using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Clipboard;

internal static class ClipboardImageAlphaNormalizer
{
	private const int Bgra32BytesPerPixel = 4;
	private const int MaxScanBufferBytes = 1024 * 1024;

	internal static BitmapSource NormalizeInvalidAlpha(BitmapSource source)
	{
		// ExcelなどのDIB画像は、見えるRGB値があるにもかかわらず全アルファ値が0で
		// WPFへ渡されることがある。履歴表示に限り、未使用のアルファを不透明として扱う。
		if (source.Format != PixelFormats.Bgra32 || !HasOnlyTransparentAlpha(source))
		{
			return source;
		}

		var opaqueSource = new FormatConvertedBitmap(source, PixelFormats.Bgr32, null, 0);
		opaqueSource.Freeze();
		return opaqueSource;
	}

	private static bool HasOnlyTransparentAlpha(BitmapSource source)
	{
		if (source.PixelWidth <= 0 || source.PixelHeight <= 0)
		{
			return false;
		}

		int stride = checked(source.PixelWidth * Bgra32BytesPerPixel);
		int rowsPerChunk = Math.Max(1, MaxScanBufferBytes / stride);
		var pixels = new byte[checked(stride * rowsPerChunk)];
		for (int top = 0; top < source.PixelHeight; top += rowsPerChunk)
		{
			int rowCount = Math.Min(rowsPerChunk, source.PixelHeight - top);
			int byteCount = checked(stride * rowCount);
			source.CopyPixels(
				new Int32Rect(0, top, source.PixelWidth, rowCount),
				pixels,
				stride,
				0);
			for (int index = 3; index < byteCount; index += Bgra32BytesPerPixel)
			{
				if (pixels[index] != 0)
				{
					return false;
				}
			}
		}

		return true;
	}
}
