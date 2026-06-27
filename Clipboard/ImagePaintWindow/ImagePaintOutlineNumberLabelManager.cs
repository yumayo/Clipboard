using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using static Clipboard.ImagePaintMetrics;

namespace Clipboard;

internal sealed class ImagePaintOutlineNumberLabelManager
{
	private readonly Canvas _canvas;
	private readonly PaintElementHistory _history;
	private readonly Dictionary<PaintRectangle, TextBlock> _labels;
	private readonly Func<double> _fontSizeProvider;

	internal ImagePaintOutlineNumberLabelManager(
		Canvas canvas,
		PaintElementHistory history,
		Dictionary<PaintRectangle, TextBlock> labels,
		Func<double> fontSizeProvider)
	{
		_canvas = canvas;
		_history = history;
		_labels = labels;
		_fontSizeProvider = fontSizeProvider;
	}

	internal IEnumerable<TextBlock> Labels => _labels.Values;

	internal void Update(bool showOutlineNumbers)
	{
		foreach (TextBlock label in _labels.Values)
		{
			_canvas.Children.Remove(label);
		}

		_labels.Clear();
		if (!showOutlineNumbers)
		{
			return;
		}

		int outlineCount = CountOutlineRectangles();
		if (outlineCount < 2)
		{
			return;
		}

		var placedLabelBounds = new List<Rect>();
		int outlineNumber = 1;
		foreach (UIElement element in _history.CompletedElements)
		{
			if (element is not PaintRectangle paintRectangle || !paintRectangle.IsOutline)
			{
				continue;
			}

			var label = CreateOutlineNumberLabel(outlineNumber);
			PositionOutlineNumberLabel(paintRectangle, label, placedLabelBounds);
			_labels[paintRectangle] = label;
			_canvas.Children.Add(label);
			outlineNumber++;
		}
	}

	internal void ShiftLabels(double offsetX, double offsetY)
	{
		foreach (TextBlock label in _labels.Values)
		{
			OffsetCanvasChild(label, offsetX, offsetY);
		}
	}

	private int CountOutlineRectangles()
	{
		int count = 0;
		foreach (UIElement element in _history.CompletedElements)
		{
			if (element is PaintRectangle { IsOutline: true })
			{
				count++;
			}
		}

		return count;
	}

	private TextBlock CreateOutlineNumberLabel(int number)
	{
		return new TextBlock
		{
			Text = GetOutlineNumberText(number),
			Foreground = Brushes.Red,
			FontSize = _fontSizeProvider(),
			FontWeight = FontWeights.Bold,
			IsHitTestVisible = false
		};
	}

	private static string GetOutlineNumberText(int number)
	{
		if (number >= 1 && number <= CircledNumberTexts.Length)
		{
			return CircledNumberTexts[number - 1];
		}

		return number.ToString(System.Globalization.CultureInfo.InvariantCulture);
	}

	private void PositionOutlineNumberLabel(
		PaintRectangle paintRectangle,
		TextBlock label,
		List<Rect> placedLabelBounds)
	{
		label.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
		Size labelSize = label.DesiredSize;
		if (labelSize.Width <= 0 || labelSize.Height <= 0)
		{
			labelSize = new Size(20, 22);
		}

		Rect rectangleBounds = paintRectangle.Bounds;
		Rect[] candidates =
		{
			new(rectangleBounds.Left - labelSize.Width - OutlineNumberGap, rectangleBounds.Top, labelSize.Width, labelSize.Height),
			new(rectangleBounds.Right + OutlineNumberGap, rectangleBounds.Top, labelSize.Width, labelSize.Height),
			new(rectangleBounds.Left, rectangleBounds.Top - labelSize.Height - OutlineNumberGap, labelSize.Width, labelSize.Height),
			new(rectangleBounds.Left, rectangleBounds.Bottom + OutlineNumberGap, labelSize.Width, labelSize.Height),
			new(rectangleBounds.Left + OutlineNumberGap, rectangleBounds.Top + OutlineNumberGap, labelSize.Width, labelSize.Height)
		};

		Rect labelBounds = ChooseOutlineNumberBounds(candidates, paintRectangle, placedLabelBounds);
		Canvas.SetLeft(label, labelBounds.Left);
		Canvas.SetTop(label, labelBounds.Top);
		placedLabelBounds.Add(labelBounds);
	}

	private Rect ChooseOutlineNumberBounds(
		Rect[] candidates,
		PaintRectangle paintRectangle,
		List<Rect> placedLabelBounds)
	{
		for (int i = 0; i < candidates.Length - 1; i++)
		{
			if (CanPlaceOutlineNumber(candidates[i], paintRectangle, placedLabelBounds, allowInsideOwnRectangle: false))
			{
				return candidates[i];
			}
		}

		Rect insideCandidate = candidates[^1];
		if (CanPlaceOutlineNumber(insideCandidate, paintRectangle, placedLabelBounds, allowInsideOwnRectangle: true))
		{
			return insideCandidate;
		}

		foreach (Rect candidate in candidates)
		{
			if (IsInsideCanvas(candidate) && !IntersectsAny(candidate, placedLabelBounds))
			{
				return candidate;
			}
		}

		return ClampToCanvas(insideCandidate);
	}

	private bool CanPlaceOutlineNumber(
		Rect candidate,
		PaintRectangle ownRectangle,
		List<Rect> placedLabelBounds,
		bool allowInsideOwnRectangle)
	{
		if (!IsInsideCanvas(candidate) || IntersectsAny(candidate, placedLabelBounds))
		{
			return false;
		}

		foreach (UIElement element in _history.CompletedElements)
		{
			if (allowInsideOwnRectangle && ReferenceEquals(element, ownRectangle))
			{
				continue;
			}

			if (TryGetPaintElementBounds(element, out Rect bounds) && candidate.IntersectsWith(bounds))
			{
				return false;
			}
		}

		return true;
	}

	private static bool TryGetPaintElementBounds(UIElement element, out Rect bounds)
	{
		switch (element)
		{
			case PaintRectangle paintRectangle:
				bounds = paintRectangle.Bounds;
				return true;
			case ArrowTextRectangle arrowTextRectangle:
				bounds = arrowTextRectangle.TextRectangleBounds;
				return true;
			default:
				bounds = Rect.Empty;
				return false;
		}
	}

	private bool IsInsideCanvas(Rect bounds)
	{
		return bounds.Left >= 0 &&
			bounds.Top >= 0 &&
			bounds.Right <= _canvas.Width &&
			bounds.Bottom <= _canvas.Height;
	}

	private static bool IntersectsAny(Rect bounds, List<Rect> others)
	{
		foreach (Rect other in others)
		{
			if (bounds.IntersectsWith(other))
			{
				return true;
			}
		}

		return false;
	}

	private Rect ClampToCanvas(Rect bounds)
	{
		double left = Math.Max(0, Math.Min(bounds.Left, _canvas.Width - bounds.Width));
		double top = Math.Max(0, Math.Min(bounds.Top, _canvas.Height - bounds.Height));
		return new Rect(left, top, bounds.Width, bounds.Height);
	}

	private static void OffsetCanvasChild(UIElement element, double offsetX, double offsetY)
	{
		double left = Canvas.GetLeft(element);
		double top = Canvas.GetTop(element);
		Canvas.SetLeft(element, (double.IsNaN(left) ? 0 : left) + offsetX);
		Canvas.SetTop(element, (double.IsNaN(top) ? 0 : top) + offsetY);
	}
}
