using System;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Clipboard;

internal static class ImagePaintImageFactory
{
	internal static BitmapSource CreateWhiteCanvasImage(double width, double height)
	{
		int pixelWidth = Math.Max(1, (int)Math.Round(width));
		int pixelHeight = Math.Max(1, (int)Math.Round(height));
		var drawingVisual = new DrawingVisual();
		using (DrawingContext drawingContext = drawingVisual.RenderOpen())
		{
			drawingContext.DrawRectangle(Brushes.White, null, new Rect(0, 0, pixelWidth, pixelHeight));
		}

		var bitmap = new RenderTargetBitmap(pixelWidth, pixelHeight, 96, 96, PixelFormats.Pbgra32);
		bitmap.Render(drawingVisual);
		bitmap.Freeze();
		return bitmap;
	}

	internal static BitmapSource LoadImage(byte[] imageBytes)
	{
		if (imageBytes.Length == 0)
		{
			throw new InvalidOperationException("画像データが空です。");
		}

		using var stream = new MemoryStream(imageBytes);
		var image = new BitmapImage();
		image.BeginInit();
		image.CacheOption = BitmapCacheOption.OnLoad;
		image.StreamSource = stream;
		image.EndInit();
		image.Freeze();
		return image;
	}

	internal static byte[] EncodePng(BitmapSource image)
	{
		using var output = new MemoryStream();
		var encoder = new PngBitmapEncoder();
		encoder.Frames.Add(BitmapFrame.Create(image));
		encoder.Save(output);
		return output.ToArray();
	}

	internal static BitmapFrame? LoadIcon()
	{
		string path = Path.Combine(AppContext.BaseDirectory, "Clipboard.ico");
		if (!File.Exists(path))
		{
			path = Path.Combine(Directory.GetCurrentDirectory(), "Clipboard.ico");
		}

		return File.Exists(path) ? BitmapFrame.Create(new Uri(path)) : null;
	}
}
