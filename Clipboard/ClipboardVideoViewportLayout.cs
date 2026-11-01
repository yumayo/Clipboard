namespace Clipboard;

// 再生映像と編集画像で、余白・拡大率・物理ピクセルへの丸めを共有する。
internal readonly record struct ClipboardVideoViewportLayout(double Left, double Top, double Width, double Height, double Zoom)
{
	internal static bool TryFit(double width, double height, double viewportWidth, double viewportHeight,
		double dpiScaleX, double dpiScaleY, out ClipboardVideoViewportLayout layout)
	{
		layout = default;
		if (!double.IsFinite(width) || width <= 0 || !double.IsFinite(height) || height <= 0 ||
			!double.IsFinite(viewportWidth) || viewportWidth <= 0 ||
			!double.IsFinite(viewportHeight) || viewportHeight <= 0 ||
			!double.IsFinite(dpiScaleX) || dpiScaleX <= 0 ||
			!double.IsFinite(dpiScaleY) || dpiScaleY <= 0)
			return false;

		double zoom = Math.Min(1, Math.Min(Math.Max(1, viewportWidth - 32) / width,
			Math.Max(1, viewportHeight - 32) / height));
		double displayWidth = width * zoom;
		double displayHeight = height * zoom;
		layout = new ClipboardVideoViewportLayout(
			Math.Round((viewportWidth - displayWidth) / 2 * dpiScaleX) / dpiScaleX,
			Math.Round((viewportHeight - displayHeight) / 2 * dpiScaleY) / dpiScaleY,
			displayWidth, displayHeight, zoom);
		return true;
	}
}
