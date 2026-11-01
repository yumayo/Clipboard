namespace Clipboard;

// 画像の画素座標を、余白を含む作業領域から表示領域へ写す。
internal readonly record struct ImagePaintViewportLayout(double Zoom, double HorizontalOffset, double VerticalOffset)
{
	internal static bool TryFit(
		double left, double top, double width, double height,
		double viewportWidth, double viewportHeight, double minZoom,
		out ImagePaintViewportLayout layout)
	{
		layout = default;
		if (!double.IsFinite(left) || !double.IsFinite(top) ||
			!double.IsFinite(width) || width <= 0 || !double.IsFinite(height) || height <= 0 ||
			!double.IsFinite(viewportWidth) || viewportWidth <= 0 ||
			!double.IsFinite(viewportHeight) || viewportHeight <= 0 ||
			!double.IsFinite(minZoom) || minZoom <= 0 || minZoom > 1)
			return false;

		double zoom = Math.Clamp(Math.Min(
			Math.Max(1, viewportWidth - 32) / width,
			Math.Max(1, viewportHeight - 32) / height), minZoom, 1);
		layout = new ImagePaintViewportLayout(zoom,
			(left + width / 2) * zoom - viewportWidth / 2,
			(top + height / 2) * zoom - viewportHeight / 2);
		return true;
	}
}
