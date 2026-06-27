using System;
using static Clipboard.ImagePaintMetrics;

namespace Clipboard;

internal static class ImagePaintSizeValue
{
	internal static double CalculatePaintStrokeThickness(double canvasHeight)
	{
		return Math.Max(MinPaintStrokeThickness, RoundSizeValue(canvasHeight * PaintStrokeThicknessHeightRatio));
	}

	internal static double CalculateDefaultArrowTextFontSize(double canvasHeight)
	{
		return Math.Max(MinArrowTextFontSize, RoundSizeValue(canvasHeight * ArrowTextFontSizeHeightRatio));
	}

	internal static bool TryParseStrokeThickness(string text, out double strokeThickness)
	{
		if (!TryParseNonNegativeIntegerSizeValue(text, out strokeThickness))
		{
			return false;
		}

		strokeThickness = Math.Max(MinPaintStrokeThickness, strokeThickness);
		return true;
	}

	internal static bool TryParseFontSize(string text, out double fontSize)
	{
		if (!TryParseNonNegativeIntegerSizeValue(text, out fontSize))
		{
			return false;
		}

		fontSize = Math.Max(MinArrowTextFontSize, fontSize);
		return true;
	}

	internal static bool IsIntegerText(string text)
	{
		if (text.Length == 0)
		{
			return false;
		}

		foreach (char character in text)
		{
			if (character < '0' || character > '9')
			{
				return false;
			}
		}

		return true;
	}

	internal static string FormatSizeValue(double sizeValue)
	{
		return RoundSizeValue(sizeValue).ToString("0", System.Globalization.CultureInfo.InvariantCulture);
	}

	private static bool TryParseNonNegativeIntegerSizeValue(string text, out double sizeValue)
	{
		string trimmedText = text.Trim();
		if (!IsIntegerText(trimmedText) ||
			!long.TryParse(
				trimmedText,
				System.Globalization.NumberStyles.None,
				System.Globalization.CultureInfo.InvariantCulture,
				out long integerValue))
		{
			sizeValue = 0;
			return false;
		}

		sizeValue = integerValue;
		return true;
	}

	private static double RoundSizeValue(double sizeValue)
	{
		return Math.Round(sizeValue, MidpointRounding.AwayFromZero);
	}
}
