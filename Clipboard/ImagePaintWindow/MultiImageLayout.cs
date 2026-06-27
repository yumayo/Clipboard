using System.Collections.Generic;

namespace Clipboard;

internal sealed partial class ImagePaintWindow
{
	private sealed record MultiImageLayout(
		double CanvasWidth,
		double CanvasHeight,
		double ReferenceHeight,
		IReadOnlyList<PlacedImageLayout> PlacedImages);
}
