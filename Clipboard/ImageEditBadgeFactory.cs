using System.Windows;
using System.Windows.Controls;

namespace Clipboard;

internal static class ImageEditBadgeFactory
{
	internal static Border Create()
	{
		var label = new TextBlock
		{
			Text = "編集済み",
			FontSize = 10,
			FontWeight = FontWeights.SemiBold
		};
		label.SetResourceReference(TextBlock.ForegroundProperty, AppTheme.TextBrushKey);

		var badge = new Border
		{
			Child = label,
			Padding = new Thickness(5, 2, 5, 2),
			Margin = new Thickness(5),
			HorizontalAlignment = HorizontalAlignment.Right,
			VerticalAlignment = VerticalAlignment.Bottom,
			BorderThickness = new Thickness(1),
			CornerRadius = new CornerRadius(3),
			IsHitTestVisible = false
		};
		badge.SetResourceReference(Border.BackgroundProperty, AppTheme.SurfaceSelectedBrushKey);
		badge.SetResourceReference(Border.BorderBrushProperty, AppTheme.AccentBorderBrushKey);
		return badge;
	}
}
