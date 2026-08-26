using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Effects;
using static Clipboard.ImagePaintMetrics;

namespace Clipboard;

internal sealed class ImagePaintOutlineNumberLabel : FrameworkElement
{
	private const double CircleFontSizeRatio = 1.15;
	private const double NumberFontSizeRatio = 0.55;
	private const double NumberMaxWidthRatio = 0.62;
	private const double NumberMaxHeightRatio = 0.58;
	private static readonly Typeface LabelTypeface = new(
		new FontFamily(ArrowTextFontFamilyName),
		FontStyles.Normal,
		FontWeights.Bold,
		FontStretches.Normal);
	private static readonly Pen CirclePen = CreatePen(OutlineNumberStrokeThickness);
	private static readonly Pen NumberPen = CreatePen(OutlineNumberTextStrokeThickness);
	private readonly string _numberText;
	private readonly double _fontSize;

	internal ImagePaintOutlineNumberLabel(string numberText, double fontSize)
	{
		_numberText = numberText;
		_fontSize = fontSize;
		IsHitTestVisible = false;
		SnapsToDevicePixels = true;
		Effect = CreateShadowEffect();
	}

	protected override Size MeasureOverride(Size availableSize)
	{
		FormattedText circleText = CreateFormattedText("○", _fontSize * CircleFontSizeRatio);
		double padding = GetRenderPadding();
		return new Size(
			Math.Ceiling(circleText.WidthIncludingTrailingWhitespace + padding * 2),
			Math.Ceiling(circleText.Height + padding * 2));
	}

	protected override void OnRender(DrawingContext drawingContext)
	{
		base.OnRender(drawingContext);
		FormattedText circleText = CreateFormattedText("○", _fontSize * CircleFontSizeRatio);
		double padding = GetRenderPadding();
		Geometry circleGeometry = circleText.BuildGeometry(new Point(padding, padding));
		drawingContext.DrawGeometry(Brushes.Red, CirclePen, circleGeometry);

		FormattedText numberText = CreateFittedNumberText(circleText);
		double numberLeft = padding +
			(circleText.WidthIncludingTrailingWhitespace - numberText.WidthIncludingTrailingWhitespace) / 2;
		double numberTop = padding + (circleText.Height - numberText.Height) / 2;
		Geometry numberGeometry = numberText.BuildGeometry(new Point(numberLeft, numberTop));
		drawingContext.DrawGeometry(Brushes.Red, NumberPen, numberGeometry);
	}

	private FormattedText CreateFittedNumberText(FormattedText circleText)
	{
		double numberFontSize = _fontSize * NumberFontSizeRatio;
		FormattedText numberText = CreateFormattedText(_numberText, numberFontSize);
		double widthScale = circleText.WidthIncludingTrailingWhitespace * NumberMaxWidthRatio /
			numberText.WidthIncludingTrailingWhitespace;
		double heightScale = circleText.Height * NumberMaxHeightRatio / numberText.Height;
		double scale = Math.Min(1, Math.Min(widthScale, heightScale));
		return scale < 1
			? CreateFormattedText(_numberText, numberFontSize * scale)
			: numberText;
	}

	private FormattedText CreateFormattedText(string text, double fontSize)
	{
		return new FormattedText(
			text,
			CultureInfo.CurrentUICulture,
			FlowDirection.LeftToRight,
			LabelTypeface,
			fontSize,
			Brushes.Red,
			VisualTreeHelper.GetDpi(this).PixelsPerDip);
	}

	private static double GetRenderPadding()
	{
		return OutlineNumberStrokeThickness / 2 +
			OutlineNumberShadowBlurRadius +
			OutlineNumberShadowDepth;
	}

	private static Pen CreatePen(double strokeThickness)
	{
		var pen = new Pen(Brushes.Red, strokeThickness)
		{
			LineJoin = PenLineJoin.Round
		};
		pen.Freeze();
		return pen;
	}

	private static DropShadowEffect CreateShadowEffect()
	{
		var effect = new DropShadowEffect
		{
			Color = Colors.Black,
			BlurRadius = OutlineNumberShadowBlurRadius,
			ShadowDepth = OutlineNumberShadowDepth,
			Direction = 315,
			Opacity = 0.75,
			RenderingBias = RenderingBias.Quality
		};
		effect.Freeze();
		return effect;
	}
}
