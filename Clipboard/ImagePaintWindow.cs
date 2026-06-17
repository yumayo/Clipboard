using System.Collections.Generic;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Ellipse = System.Windows.Shapes.Ellipse;
using Rectangle = System.Windows.Shapes.Rectangle;

namespace Clipboard;

internal sealed class ImagePaintWindow : Window
{
	private const double RedStrokeThickness = 4;
	private const double ToolbarHeight = 48;
	private const double MinZoom = 0.05;
	private const double MaxZoom = 8;
	private const double ZoomStep = 1.1;
	private const double OutlineNumberGap = 4;
	private const double OutlineNumberFontSize = 18;
	private static readonly string[] CircledNumberTexts =
	{
		"①", "②", "③", "④", "⑤", "⑥", "⑦", "⑧", "⑨", "⑩",
		"⑪", "⑫", "⑬", "⑭", "⑮", "⑯", "⑰", "⑱", "⑲", "⑳"
	};
	private readonly BitmapSource _sourceImage;
	private readonly ScaleTransform _zoomTransform = new(1, 1);
	private readonly Grid _zoomContainer;
	private readonly Grid _paintSurface;
	private readonly Canvas _overlayCanvas;
	private readonly Button _blackFillButton;
	private readonly Button _redOutlineButton;
	private readonly List<Rectangle> _completedRectangles = new();
	private readonly List<Rectangle> _redoRectangles = new();
	private readonly Dictionary<Rectangle, TextBlock> _outlineNumberLabels = new();
	private PaintMode _paintMode = PaintMode.RedOutlineRectangle;
	private Point _dragStartPoint;
	private Rectangle? _dragRectangle;

	public ImagePaintWindow(byte[] imageBytes)
	{
		_sourceImage = LoadImage(imageBytes);
		double windowWidth = Math.Min(SystemParameters.WorkArea.Width - 80, Math.Max(520, _sourceImage.PixelWidth + 36));
		double windowHeight = Math.Min(SystemParameters.WorkArea.Height - 80, Math.Max(420, _sourceImage.PixelHeight + ToolbarHeight + 36));

		Title = "ペイント";
		WindowStartupLocation = WindowStartupLocation.CenterScreen;
		Width = windowWidth;
		Height = windowHeight;
		MinWidth = 420;
		MinHeight = 320;
		Icon = LoadIcon();
		AppTheme.ApplyWindow(this);

		var root = new DockPanel();
		root.SetResourceReference(Panel.BackgroundProperty, AppTheme.WindowBackgroundBrushKey);

		var toolbar = new StackPanel
		{
			Orientation = Orientation.Horizontal,
			HorizontalAlignment = HorizontalAlignment.Center,
			Margin = new Thickness(10, 10, 10, 8)
		};
		DockPanel.SetDock(toolbar, Dock.Top);
		root.Children.Add(toolbar);

		_blackFillButton = CreateModeButton("塗りつぶし", CreateFillIcon());
		_blackFillButton.Click += (_, _) => SetPaintMode(PaintMode.BlackFillRectangle);
		toolbar.Children.Add(_blackFillButton);

		_redOutlineButton = CreateModeButton("アウトライン", CreateOutlineIcon());
		_redOutlineButton.Margin = new Thickness(8, 0, 0, 0);
		_redOutlineButton.Click += (_, _) => SetPaintMode(PaintMode.RedOutlineRectangle);
		toolbar.Children.Add(_redOutlineButton);

		var image = new Image
		{
			Source = _sourceImage,
			Stretch = Stretch.Fill,
			Width = _sourceImage.PixelWidth,
			Height = _sourceImage.PixelHeight,
			SnapsToDevicePixels = true
		};
		RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);

		_overlayCanvas = new Canvas
		{
			Width = _sourceImage.PixelWidth,
			Height = _sourceImage.PixelHeight,
			Background = Brushes.Transparent,
			Cursor = Cursors.Cross
		};
		_overlayCanvas.MouseLeftButtonDown += OverlayCanvas_MouseLeftButtonDown;
		_overlayCanvas.MouseMove += OverlayCanvas_MouseMove;
		_overlayCanvas.MouseLeftButtonUp += OverlayCanvas_MouseLeftButtonUp;

		_paintSurface = new Grid
		{
			Width = _sourceImage.PixelWidth,
			Height = _sourceImage.PixelHeight,
			ClipToBounds = true
		};
		_paintSurface.Children.Add(image);
		_paintSurface.Children.Add(_overlayCanvas);

		_zoomContainer = new Grid
		{
			Width = _sourceImage.PixelWidth,
			Height = _sourceImage.PixelHeight,
			LayoutTransform = _zoomTransform
		};
		_zoomContainer.Children.Add(_paintSurface);
		SetZoom(CalculateInitialZoom(windowWidth, windowHeight));

		var scrollViewer = new ScrollViewer
		{
			Content = _zoomContainer,
			HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
			VerticalScrollBarVisibility = ScrollBarVisibility.Auto
		};
		scrollViewer.PreviewMouseWheel += ScrollViewer_PreviewMouseWheel;
		scrollViewer.SetResourceReference(Control.BackgroundProperty, AppTheme.ThumbnailBackgroundBrushKey);
		root.Children.Add(scrollViewer);

		Content = root;
		SetPaintMode(_paintMode);
	}

	protected override void OnPreviewKeyDown(KeyEventArgs e)
	{
		if ((Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control && e.Key == Key.S)
		{
			SaveToClipboardAndClose();
			e.Handled = true;
			return;
		}

		if ((Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control && e.Key == Key.Z)
		{
			Undo();
			e.Handled = true;
			return;
		}

		if ((Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control && e.Key == Key.Y)
		{
			Redo();
			e.Handled = true;
			return;
		}

		base.OnPreviewKeyDown(e);
	}

	private void ScrollViewer_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
	{
		if ((Keyboard.Modifiers & ModifierKeys.Control) != ModifierKeys.Control || sender is not ScrollViewer scrollViewer)
		{
			return;
		}

		ZoomFromWheel(e, scrollViewer);
		e.Handled = true;
	}

	private void ZoomFromWheel(MouseWheelEventArgs e, ScrollViewer scrollViewer)
	{
		double nextZoom = e.Delta > 0 ? _zoomTransform.ScaleX * ZoomStep : _zoomTransform.ScaleX / ZoomStep;
		nextZoom = Math.Max(MinZoom, Math.Min(MaxZoom, nextZoom));
		if (Math.Abs(nextZoom - _zoomTransform.ScaleX) < 0.001)
		{
			return;
		}

		Point imagePoint = e.GetPosition(_paintSurface);
		Point viewerPoint = e.GetPosition(scrollViewer);
		SetZoom(nextZoom);
		scrollViewer.UpdateLayout();

		scrollViewer.ScrollToHorizontalOffset(Math.Max(0, imagePoint.X * nextZoom - viewerPoint.X));
		scrollViewer.ScrollToVerticalOffset(Math.Max(0, imagePoint.Y * nextZoom - viewerPoint.Y));
	}

	private void SetZoom(double zoom)
	{
		_zoomTransform.ScaleX = zoom;
		_zoomTransform.ScaleY = zoom;
	}

	private double CalculateInitialZoom(double windowWidth, double windowHeight)
	{
		double availableWidth = Math.Max(1, windowWidth - 48);
		double availableHeight = Math.Max(1, windowHeight - ToolbarHeight - 64);
		double fitZoom = Math.Min(availableWidth / _sourceImage.PixelWidth, availableHeight / _sourceImage.PixelHeight);
		if (!double.IsFinite(fitZoom) || fitZoom >= 1)
		{
			return 1;
		}

		return Math.Max(MinZoom, fitZoom);
	}

	private void OverlayCanvas_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
	{
		CancelActiveDrag();
		_dragStartPoint = ClampToCanvas(e.GetPosition(_overlayCanvas));
		var rectangle = CreateRectangle(_paintMode);
		_dragRectangle = rectangle;
		Canvas.SetLeft(rectangle, _dragStartPoint.X);
		Canvas.SetTop(rectangle, _dragStartPoint.Y);
		_overlayCanvas.Children.Add(rectangle);
		_overlayCanvas.CaptureMouse();
		e.Handled = true;
	}

	private void OverlayCanvas_MouseMove(object sender, MouseEventArgs e)
	{
		if (_dragRectangle is not { })
		{
			return;
		}

		UpdateDragRectangle(ClampToCanvas(e.GetPosition(_overlayCanvas)));
		e.Handled = true;
	}

	private void OverlayCanvas_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
	{
		if (_dragRectangle is not { } rectangle)
		{
			return;
		}

		UpdateDragRectangle(ClampToCanvas(e.GetPosition(_overlayCanvas)));
		if (IsDrawableRectangle(rectangle))
		{
			_completedRectangles.Add(rectangle);
			_redoRectangles.Clear();
			UpdateOutlineNumberLabels();
		}
		else
		{
			_overlayCanvas.Children.Remove(rectangle);
		}

		_dragRectangle = null;
		_overlayCanvas.ReleaseMouseCapture();
		e.Handled = true;
	}

	private void UpdateDragRectangle(Point currentPoint)
	{
		if (_dragRectangle is not { } rectangle)
		{
			return;
		}

		double left = Math.Min(_dragStartPoint.X, currentPoint.X);
		double top = Math.Min(_dragStartPoint.Y, currentPoint.Y);
		double width = Math.Abs(currentPoint.X - _dragStartPoint.X);
		double height = Math.Abs(currentPoint.Y - _dragStartPoint.Y);
		Canvas.SetLeft(rectangle, left);
		Canvas.SetTop(rectangle, top);
		rectangle.Width = width;
		rectangle.Height = height;
	}

	private static bool IsDrawableRectangle(Rectangle rectangle)
	{
		return rectangle.Width >= 2 && rectangle.Height >= 2;
	}

	private void SaveToClipboardAndClose()
	{
		try
		{
			CompleteActiveDrag();

			ClipboardManager.CopyImageToClipboard(RenderPaintedImage());
			Close();
		}
		catch (Exception ex)
		{
			Logger.Error(ex, "ImagePaintWindow: 編集画像をクリップボードにコピーできませんでした。");
			MessageBox.Show(this, "編集画像をクリップボードにコピーできませんでした。", "Clipboard", MessageBoxButton.OK, MessageBoxImage.Error);
		}
	}

	private void Undo()
	{
		if (_dragRectangle != null)
		{
			CancelActiveDrag();
			return;
		}

		if (_completedRectangles.Count == 0)
		{
			return;
		}

		Rectangle rectangle = _completedRectangles[^1];
		_completedRectangles.RemoveAt(_completedRectangles.Count - 1);
		_overlayCanvas.Children.Remove(rectangle);
		_redoRectangles.Add(rectangle);
		UpdateOutlineNumberLabels();
	}

	private void Redo()
	{
		if (_dragRectangle != null)
		{
			CancelActiveDrag();
			return;
		}

		if (_redoRectangles.Count == 0)
		{
			return;
		}

		Rectangle rectangle = _redoRectangles[^1];
		_redoRectangles.RemoveAt(_redoRectangles.Count - 1);
		_overlayCanvas.Children.Add(rectangle);
		_completedRectangles.Add(rectangle);
		UpdateOutlineNumberLabels();
	}

	private void CompleteActiveDrag()
	{
		if (_dragRectangle is not { } rectangle)
		{
			return;
		}

		_overlayCanvas.ReleaseMouseCapture();
		if (IsDrawableRectangle(rectangle))
		{
			_completedRectangles.Add(rectangle);
			_redoRectangles.Clear();
			UpdateOutlineNumberLabels();
		}
		else
		{
			_overlayCanvas.Children.Remove(rectangle);
		}

		_dragRectangle = null;
	}

	private void CancelActiveDrag()
	{
		if (_dragRectangle is not { } rectangle)
		{
			return;
		}

		_overlayCanvas.ReleaseMouseCapture();
		_overlayCanvas.Children.Remove(rectangle);
		_dragRectangle = null;
	}

	private void UpdateOutlineNumberLabels()
	{
		foreach (TextBlock label in _outlineNumberLabels.Values)
		{
			_overlayCanvas.Children.Remove(label);
		}

		_outlineNumberLabels.Clear();
		int outlineCount = CountOutlineRectangles();
		if (outlineCount < 2)
		{
			return;
		}

		var placedLabelBounds = new List<Rect>();
		int outlineNumber = 1;
		foreach (Rectangle rectangle in _completedRectangles)
		{
			if (!IsOutlineRectangle(rectangle))
			{
				continue;
			}

			var label = CreateOutlineNumberLabel(outlineNumber);
			PositionOutlineNumberLabel(rectangle, label, placedLabelBounds);
			_outlineNumberLabels[rectangle] = label;
			_overlayCanvas.Children.Add(label);
			outlineNumber++;
		}
	}

	private int CountOutlineRectangles()
	{
		int count = 0;
		foreach (Rectangle rectangle in _completedRectangles)
		{
			if (IsOutlineRectangle(rectangle))
			{
				count++;
			}
		}

		return count;
	}

	private static bool IsOutlineRectangle(Rectangle rectangle)
	{
		return rectangle.Stroke is SolidColorBrush brush && brush.Color == Colors.Red;
	}

	private static TextBlock CreateOutlineNumberLabel(int number)
	{
		return new TextBlock
		{
			Text = GetOutlineNumberText(number),
			Foreground = Brushes.Red,
			FontSize = OutlineNumberFontSize,
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
		Rectangle rectangle,
		TextBlock label,
		List<Rect> placedLabelBounds)
	{
		label.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
		Size labelSize = label.DesiredSize;
		if (labelSize.Width <= 0 || labelSize.Height <= 0)
		{
			labelSize = new Size(20, 22);
		}

		Rect rectangleBounds = GetRectangleBounds(rectangle);
		Rect[] candidates =
		{
			new(rectangleBounds.Left - labelSize.Width - OutlineNumberGap, rectangleBounds.Top, labelSize.Width, labelSize.Height),
			new(rectangleBounds.Right + OutlineNumberGap, rectangleBounds.Top, labelSize.Width, labelSize.Height),
			new(rectangleBounds.Left, rectangleBounds.Top - labelSize.Height - OutlineNumberGap, labelSize.Width, labelSize.Height),
			new(rectangleBounds.Left, rectangleBounds.Bottom + OutlineNumberGap, labelSize.Width, labelSize.Height),
			new(rectangleBounds.Left + OutlineNumberGap, rectangleBounds.Top + OutlineNumberGap, labelSize.Width, labelSize.Height)
		};

		Rect labelBounds = ChooseOutlineNumberBounds(candidates, rectangle, placedLabelBounds);
		Canvas.SetLeft(label, labelBounds.Left);
		Canvas.SetTop(label, labelBounds.Top);
		placedLabelBounds.Add(labelBounds);
	}

	private Rect ChooseOutlineNumberBounds(
		Rect[] candidates,
		Rectangle rectangle,
		List<Rect> placedLabelBounds)
	{
		for (int i = 0; i < candidates.Length - 1; i++)
		{
			if (CanPlaceOutlineNumber(candidates[i], rectangle, placedLabelBounds, allowInsideOwnRectangle: false))
			{
				return candidates[i];
			}
		}

		Rect insideCandidate = candidates[^1];
		if (CanPlaceOutlineNumber(insideCandidate, rectangle, placedLabelBounds, allowInsideOwnRectangle: true))
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
		Rectangle ownRectangle,
		List<Rect> placedLabelBounds,
		bool allowInsideOwnRectangle)
	{
		if (!IsInsideCanvas(candidate) || IntersectsAny(candidate, placedLabelBounds))
		{
			return false;
		}

		foreach (Rectangle rectangle in _completedRectangles)
		{
			if (allowInsideOwnRectangle && ReferenceEquals(rectangle, ownRectangle))
			{
				continue;
			}

			if (candidate.IntersectsWith(GetRectangleBounds(rectangle)))
			{
				return false;
			}
		}

		return true;
	}

	private bool IsInsideCanvas(Rect bounds)
	{
		return bounds.Left >= 0 &&
			bounds.Top >= 0 &&
			bounds.Right <= _overlayCanvas.Width &&
			bounds.Bottom <= _overlayCanvas.Height;
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
		double left = Math.Max(0, Math.Min(bounds.Left, _overlayCanvas.Width - bounds.Width));
		double top = Math.Max(0, Math.Min(bounds.Top, _overlayCanvas.Height - bounds.Height));
		return new Rect(left, top, bounds.Width, bounds.Height);
	}

	private static Rect GetRectangleBounds(Rectangle rectangle)
	{
		double left = Canvas.GetLeft(rectangle);
		double top = Canvas.GetTop(rectangle);
		return new Rect(
			double.IsNaN(left) ? 0 : left,
			double.IsNaN(top) ? 0 : top,
			rectangle.Width,
			rectangle.Height);
	}

	private BitmapSource RenderPaintedImage()
	{
		_paintSurface.Measure(new Size(_sourceImage.PixelWidth, _sourceImage.PixelHeight));
		_paintSurface.Arrange(new Rect(0, 0, _sourceImage.PixelWidth, _sourceImage.PixelHeight));
		_paintSurface.UpdateLayout();

		var bitmap = new RenderTargetBitmap(
			_sourceImage.PixelWidth,
			_sourceImage.PixelHeight,
			96,
			96,
			PixelFormats.Pbgra32);
		bitmap.Render(_paintSurface);
		bitmap.Freeze();
		return bitmap;
	}

	private void SetPaintMode(PaintMode mode)
	{
		_paintMode = mode;
		UpdateModeButton(_blackFillButton, mode == PaintMode.BlackFillRectangle);
		UpdateModeButton(_redOutlineButton, mode == PaintMode.RedOutlineRectangle);
	}

	private static Button CreateModeButton(string text, UIElement icon)
	{
		var label = new TextBlock
		{
			Text = text,
			Margin = new Thickness(6, 0, 0, 0),
			VerticalAlignment = VerticalAlignment.Center
		};
		label.SetResourceReference(TextBlock.ForegroundProperty, AppTheme.TextBrushKey);

		var content = new StackPanel
		{
			Orientation = Orientation.Horizontal,
			HorizontalAlignment = HorizontalAlignment.Center,
			VerticalAlignment = VerticalAlignment.Center
		};
		content.Children.Add(icon);
		content.Children.Add(label);

		var button = new Button
		{
			Content = content,
			Width = 124,
			Height = 30,
			Focusable = false
		};
		AppTheme.ApplyButton(button);
		return button;
	}

	private static Ellipse CreateFillIcon()
	{
		return new Ellipse
		{
			Width = 13,
			Height = 13,
			Fill = Brushes.Black,
			Stroke = Brushes.Black,
			StrokeThickness = 1,
			VerticalAlignment = VerticalAlignment.Center
		};
	}

	private static Ellipse CreateOutlineIcon()
	{
		return new Ellipse
		{
			Width = 13,
			Height = 13,
			Fill = Brushes.Transparent,
			Stroke = Brushes.Red,
			StrokeThickness = 2,
			VerticalAlignment = VerticalAlignment.Center
		};
	}

	private static void UpdateModeButton(Button button, bool selected)
	{
		button.FontWeight = selected ? FontWeights.SemiBold : FontWeights.Normal;
		button.BorderThickness = selected ? new Thickness(2) : new Thickness(1);
		button.SetResourceReference(
			Control.BorderBrushProperty,
			selected ? AppTheme.AccentBorderBrushKey : AppTheme.InputBorderBrushKey);
	}

	private static Rectangle CreateRectangle(PaintMode mode)
	{
		return mode switch
		{
			PaintMode.RedOutlineRectangle => new Rectangle
			{
				Fill = Brushes.Transparent,
				Stroke = Brushes.Red,
				StrokeThickness = RedStrokeThickness,
				SnapsToDevicePixels = true
			},
			_ => new Rectangle
			{
				Fill = Brushes.Black,
				StrokeThickness = 0,
				SnapsToDevicePixels = true
			}
		};
	}

	private Point ClampToCanvas(Point point)
	{
		return new Point(
			Math.Max(0, Math.Min(point.X, _overlayCanvas.Width)),
			Math.Max(0, Math.Min(point.Y, _overlayCanvas.Height)));
	}

	private static BitmapSource LoadImage(byte[] imageBytes)
	{
		if (imageBytes.Length == 0)
		{
			throw new InvalidOperationException("画像データが空です。");
		}

		using var stream = new MemoryStream(imageBytes);
		var image = new BitmapImage();
		image.BeginInit();
		image.CacheOption = BitmapCacheOption.OnLoad;
		image.StreamSource = stream;
		image.EndInit();
		image.Freeze();
		return image;
	}

	private static BitmapFrame? LoadIcon()
	{
		string path = Path.Combine(AppContext.BaseDirectory, "Clipboard.ico");
		if (!File.Exists(path))
		{
			path = Path.Combine(Directory.GetCurrentDirectory(), "Clipboard.ico");
		}

		return File.Exists(path) ? BitmapFrame.Create(new Uri(path)) : null;
	}

	private enum PaintMode
	{
		BlackFillRectangle,
		RedOutlineRectangle
	}
}
