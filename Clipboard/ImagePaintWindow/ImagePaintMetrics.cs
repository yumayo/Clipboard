using System.Windows.Media;

namespace Clipboard;

internal static class ImagePaintMetrics
{
	internal const double ToolbarHeight = 48;
	internal const double MinZoom = 0.05;
	internal const double MaxZoom = 8;
	internal const double ZoomStep = 1.1;
	internal const double OutlineNumberGap = 4;
	internal const double PaintStrokeThicknessHeightRatio = 1.0 / 256.0;
	internal const double MinPaintStrokeThickness = 1;
	internal const double MinPaintRectangleWidth = 2;
	internal const double MinPaintRectangleHeight = 2;
	internal const double PaintRectangleResizeHandleSize = 18;
	internal const double ArrowTailLength = 36;
	internal const double ArrowHeadLengthStrokeMultiplier = 5.5;
	internal const double ArrowHeadWidthStrokeMultiplier = 4.65;
	internal const double MinArrowHeadLength = 23;
	internal const double MinArrowHeadWidth = 20;
	internal const double ArrowLineEndHeadInsetRatio = 0.55;
	internal const double ArrowResizeHandleSize = 18;
	internal const double ArrowBendDistanceRatio = 0.55;
	internal const double ArrowTextPadding = 6;
	internal const double ArrowTextFontSizeHeightRatio = 1.0 / 64.0;
	internal const double MinArrowTextFontSize = 24;
	internal const double MinArrowTextRectangleWidth = 36;
	internal const double MinArrowTextRectangleHeight = 24;
	internal const double MultiImageGap = 24;
	internal const double MultiImageOuterMargin = 24;
	internal const double MultiImageMinPlacedSize = 16;
	internal const double PlacedImageResizeHandleSize = 18;
	internal const double WorkspaceGridCellSize = 64;
	internal const double WorkspaceInitialMargin = 1024;
	internal const double WorkspaceExpansionChunk = 1024;
	internal const double RenderBoundsPadding = 2;
	internal const string ArrowTextFontFamilyName = "Meiryo UI";

	internal static readonly SolidColorBrush ArrowBrush = CreateFrozenBrush(Color.FromRgb(226, 104, 0));
	internal static readonly string[] CircledNumberTexts =
	{
		"①", "②", "③", "④", "⑤", "⑥", "⑦", "⑧", "⑨", "⑩",
		"⑪", "⑫", "⑬", "⑭", "⑮", "⑯", "⑰", "⑱", "⑲", "⑳"
	};

	private static SolidColorBrush CreateFrozenBrush(Color color)
	{
		var brush = new SolidColorBrush(color);
		brush.Freeze();
		return brush;
	}
}
