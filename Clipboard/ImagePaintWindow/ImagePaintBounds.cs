using System.Windows;
using System.Windows.Controls;
using static Clipboard.ImagePaintMetrics;

namespace Clipboard;

internal static class ImagePaintBounds
{
	internal static bool IsUsableBounds(Rect bounds)
	{
		return !bounds.IsEmpty &&
			double.IsFinite(bounds.Left) &&
			double.IsFinite(bounds.Top) &&
			double.IsFinite(bounds.Right) &&
			double.IsFinite(bounds.Bottom);
	}

	internal static bool TryGetRenderableElementBounds(UIElement element, out Rect bounds)
	{
		switch (element)
		{
			case PaintRectangle paintRectangle:
				bounds = paintRectangle.RenderBounds;
				return IsUsableBounds(bounds);
			case ArrowTextRectangle arrowTextRectangle:
				bounds = arrowTextRectangle.RenderBounds;
				return IsUsableBounds(bounds);
			default:
				bounds = Rect.Empty;
				return false;
		}
	}

	internal static Rect GetTextBlockBounds(TextBlock textBlock)
	{
		textBlock.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
		Size size = textBlock.DesiredSize;
		return GetCanvasChildBounds(textBlock, size.Width, size.Height);
	}

	internal static Rect GetCanvasChildBounds(UIElement element, double width, double height)
	{
		double left = Canvas.GetLeft(element);
		double top = Canvas.GetTop(element);
		return new Rect(
			double.IsNaN(left) ? 0 : left,
			double.IsNaN(top) ? 0 : top,
			Math.Max(0, width),
			Math.Max(0, height));
	}

	internal static void UnionBounds(ref Rect bounds, Rect addition)
	{
		if (!IsUsableBounds(addition))
		{
			return;
		}

		if (bounds.IsEmpty)
		{
			bounds = addition;
			return;
		}

		bounds.Union(addition);
	}

	internal static Rect RoundRenderBounds(Rect bounds)
	{
		bounds.Inflate(RenderBoundsPadding, RenderBoundsPadding);
		double left = Math.Floor(bounds.Left);
		double top = Math.Floor(bounds.Top);
		double right = Math.Ceiling(bounds.Right);
		double bottom = Math.Ceiling(bounds.Bottom);
		return new Rect(left, top, Math.Max(1, right - left), Math.Max(1, bottom - top));
	}
}
