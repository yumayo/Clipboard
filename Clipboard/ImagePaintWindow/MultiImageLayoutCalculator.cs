using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Media.Imaging;
using static Clipboard.ImagePaintMetrics;

namespace Clipboard;

internal static class MultiImageLayoutCalculator
{
	internal static MultiImageLayout CalculateMultiImageLayout(IReadOnlyList<BitmapSource> images)
	{
		// 作業領域に収まる幅へ全画像を合わせる。大きい画像は縮小、小さい画像も同じ幅へ拡大して縦並びを揃える。
		double maxContentWidth = Math.Max(MultiImageMinPlacedSize, SystemParameters.WorkArea.Width - 160);
		double maxSourceWidth = images.Max(image => (double)image.PixelWidth);
		double contentWidth = Math.Min(maxContentWidth, maxSourceWidth);

		var placedImages = new List<PlacedImageLayout>(images.Count);
		double currentTop = MultiImageOuterMargin;
		double referenceHeight = 0;
		foreach (BitmapSource image in images)
		{
			double width = contentWidth;
			double aspectRatio = image.PixelHeight / (double)image.PixelWidth;
			double height = Math.Max(MultiImageMinPlacedSize, width * aspectRatio);
			var bounds = new Rect(MultiImageOuterMargin, currentTop, width, height);
			placedImages.Add(new PlacedImageLayout(image, bounds));
			currentTop += height + MultiImageGap;
			referenceHeight = Math.Max(referenceHeight, height);
		}

		double canvasWidth = contentWidth + MultiImageOuterMargin * 2;
		double canvasHeight = currentTop - MultiImageGap + MultiImageOuterMargin;
		return new MultiImageLayout(
			Math.Max(1, Math.Round(canvasWidth)),
			Math.Max(1, Math.Round(canvasHeight)),
			referenceHeight,
			placedImages);
	}
}
