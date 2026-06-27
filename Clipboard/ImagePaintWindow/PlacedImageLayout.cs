using System.Windows;
using System.Windows.Media.Imaging;

namespace Clipboard;

internal sealed partial class ImagePaintWindow
{
	private sealed record PlacedImageLayout(BitmapSource Image, Rect Bounds);
}
