using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Ellipse = System.Windows.Shapes.Ellipse;
using Polygon = System.Windows.Shapes.Polygon;
using Polyline = System.Windows.Shapes.Polyline;
using Rectangle = System.Windows.Shapes.Rectangle;

namespace Clipboard;

internal sealed class ImagePaintWindow : Window
{
	private const double ToolbarHeight = 48;
	private const double AutoClipboardWriteDebounceMilliseconds = 350;
	private const double MinZoom = 0.05;
	private const double MaxZoom = 8;
	private const double ZoomStep = 1.1;
	private const double OutlineNumberGap = 4;
	private const double PaintStrokeThicknessHeightRatio = 1.0 / 256.0;
	private const double MinPaintStrokeThickness = 1;
	private const double MinPaintRectangleWidth = 2;
	private const double MinPaintRectangleHeight = 2;
	private const double PaintRectangleResizeHandleSize = 18;
	private const double ArrowTailLength = 36;
	private const double ArrowHeadLengthStrokeMultiplier = 5.5;
	private const double ArrowHeadWidthStrokeMultiplier = 4.65;
	private const double MinArrowHeadLength = 23;
	private const double MinArrowHeadWidth = 20;
	private const double ArrowLineEndHeadInsetRatio = 0.55;
	private const double ArrowResizeHandleSize = 18;
	private const double ArrowBendDistanceRatio = 0.55;
	private const double ArrowTextPadding = 6;
	private const double ArrowTextFontSizeHeightRatio = 1.0 / 64.0;
	private const double MinArrowTextFontSize = 24;
	private const double MinArrowTextRectangleWidth = 36;
	private const double MinArrowTextRectangleHeight = 24;
	private const string ArrowTextFontFamilyName = "Meiryo UI";
	private static readonly SolidColorBrush ArrowBrush = CreateFrozenBrush(Color.FromRgb(226, 104, 0));
	private static readonly string[] CircledNumberTexts =
	{
		"①", "②", "③", "④", "⑤", "⑥", "⑦", "⑧", "⑨", "⑩",
		"⑪", "⑫", "⑬", "⑭", "⑮", "⑯", "⑰", "⑱", "⑲", "⑳"
	};
	private readonly BitmapSource _sourceImage;
	private readonly DispatcherTimer _autoClipboardWriteTimer;
	private readonly ScaleTransform _zoomTransform = new(1, 1);
	private readonly Grid _zoomContainer;
	private readonly Grid _paintSurface;
	private readonly Canvas _overlayCanvas;
	private readonly Button _blackFillButton;
	private readonly Button _redOutlineButton;
	private readonly Button _arrowTextButton;
	private readonly TextBox _strokeThicknessTextBox;
	private readonly TextBox _fontSizeTextBox;
	private readonly CheckBox _outlineNumberCheckBox;
	private readonly List<UIElement> _completedElements = new();
	private readonly List<UIElement> _redoElements = new();
	private readonly Dictionary<PaintRectangle, TextBlock> _outlineNumberLabels = new();
	private double _paintStrokeThickness;
	private double _currentArrowTextFontSize;
	private bool _isUpdatingStrokeThicknessTextBox;
	private bool _isUpdatingFontSizeTextBox;
	private bool _showOutlineNumbers = true;
	private bool _hasPaintChanges;
	private bool _hasCopiedToClipboardBeforeClose;
	private PaintRectangle? _activePaintRectangle;
	private ArrowTextRectangle? _activeArrowTextRectangle;
	private PaintMode _paintMode = PaintMode.RedOutlineRectangle;
	private Point _dragStartPoint;
	private UIElement? _dragElement;

	public ImagePaintWindow(byte[] imageBytes)
	{
		_sourceImage = LoadImage(imageBytes);
		_paintStrokeThickness = CalculatePaintStrokeThickness(_sourceImage.PixelHeight);
		_currentArrowTextFontSize = CalculateDefaultArrowTextFontSize(_sourceImage.PixelHeight);
		_autoClipboardWriteTimer = new DispatcherTimer
		{
			Interval = TimeSpan.FromMilliseconds(AutoClipboardWriteDebounceMilliseconds)
		};
		_autoClipboardWriteTimer.Tick += AutoClipboardWriteTimer_Tick;
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

		var toolbar = new WrapPanel
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

		_arrowTextButton = CreateModeButton("矢印矩形", CreateArrowTextRectangleIcon());
		_arrowTextButton.Margin = new Thickness(8, 0, 0, 0);
		_arrowTextButton.Click += (_, _) => SetPaintMode(PaintMode.ArrowTextRectangle);
		toolbar.Children.Add(_arrowTextButton);

		_strokeThicknessTextBox = CreateStrokeThicknessTextBox(_paintStrokeThickness);
		toolbar.Children.Add(CreateToolbarTextEditor("線幅", _strokeThicknessTextBox));

		_fontSizeTextBox = CreateFontSizeTextBox(_currentArrowTextFontSize);
		toolbar.Children.Add(CreateToolbarTextEditor("文字サイズ", _fontSizeTextBox));

		_outlineNumberCheckBox = CreateOutlineNumberCheckBox();
		toolbar.Children.Add(_outlineNumberCheckBox);

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
			Cursor = Cursors.Cross,
			Focusable = true
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
		AddHandler(Keyboard.PreviewKeyDownEvent, new KeyEventHandler(ImagePaintWindow_PreviewKeyDown), true);
		SetPaintMode(_paintMode);
	}

	private void ImagePaintWindow_PreviewKeyDown(object sender, KeyEventArgs e)
	{
		bool isControlPressed = (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control;
		Key key = GetInputKey(e);
		bool isTextInputSource = IsTextInputSource(e.OriginalSource);

		if (key == Key.Escape && isTextInputSource)
		{
			ExitTextInputMode();
			e.Handled = true;
			return;
		}

		if (isControlPressed && (key == Key.S || key == Key.C))
		{
			SaveToClipboardAndClose();
			e.Handled = true;
			return;
		}

		if (isTextInputSource && isControlPressed && IsUndoRedoKey(key))
		{
			return;
		}

		if (isControlPressed && key == Key.Z)
		{
			if ((Keyboard.Modifiers & ModifierKeys.Shift) == ModifierKeys.Shift)
			{
				Redo();
				e.Handled = true;
				return;
			}

			Undo();
			e.Handled = true;
			return;
		}

		if (isControlPressed && key == Key.Y)
		{
			Redo();
			e.Handled = true;
			return;
		}
	}

	private static Key GetInputKey(KeyEventArgs e)
	{
		return e.Key switch
		{
			Key.System => e.SystemKey,
			Key.ImeProcessed => e.ImeProcessedKey,
			Key.DeadCharProcessed => e.DeadCharProcessedKey,
			_ => e.Key
		};
	}

	private static bool IsUndoRedoKey(Key key)
	{
		return key is Key.Z or Key.Y;
	}

	private static bool IsTextInputSource(object source)
	{
		return source is DependencyObject dependencyObject &&
			FindVisualParent<TextBox>(dependencyObject) is { IsKeyboardFocusWithin: true };
	}

	private void ExitTextInputMode()
	{
		SetActiveArrowTextRectangle(null);
		FocusPaintSurface();
	}

	private static T? FindVisualParent<T>(DependencyObject dependencyObject)
		where T : DependencyObject
	{
		DependencyObject? current = dependencyObject;
		while (current != null)
		{
			if (current is T match)
			{
				return match;
			}

			try
			{
				current = VisualTreeHelper.GetParent(current);
			}
			catch (InvalidOperationException)
			{
				return null;
			}
		}

		return null;
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
		if (IsExistingPaintElementSource(e.OriginalSource))
		{
			return;
		}

		CancelActiveDrag();
		SetActivePaintRectangle(null);
		SetActiveArrowTextRectangle(null);
		_dragStartPoint = ClampToCanvas(e.GetPosition(_overlayCanvas));
		var element = CreatePaintElement(_paintMode);
		_dragElement = element;
		UpdateDragElement(_dragStartPoint);
		_overlayCanvas.Children.Add(element);
		_overlayCanvas.CaptureMouse();
		e.Handled = true;
	}

	private static bool IsExistingPaintElementSource(object source)
	{
		return source is DependencyObject dependencyObject &&
			(FindVisualParent<PaintRectangle>(dependencyObject) != null ||
				FindVisualParent<ArrowTextRectangle>(dependencyObject) != null);
	}

	private void OverlayCanvas_MouseMove(object sender, MouseEventArgs e)
	{
		if (_dragElement is not { })
		{
			return;
		}

		UpdateDragElement(ClampToCanvas(e.GetPosition(_overlayCanvas)));
		e.Handled = true;
	}

	private void OverlayCanvas_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
	{
		if (_dragElement is not { })
		{
			return;
		}

		UpdateDragElement(ClampToCanvas(e.GetPosition(_overlayCanvas)));
		CompleteActiveDrag(focusTextInput: true);
		e.Handled = true;
	}

	private void UpdateDragElement(Point currentPoint)
	{
		if (_dragElement is PaintRectangle paintRectangle)
		{
			paintRectangle.Update(_dragStartPoint, currentPoint);
		}
		else if (_dragElement is ArrowTextRectangle arrowTextRectangle)
		{
			arrowTextRectangle.Update(_dragStartPoint, currentPoint);
		}
	}

	private static bool IsDrawableElement(UIElement element)
	{
		return element switch
		{
			PaintRectangle paintRectangle => paintRectangle.IsDrawable,
			ArrowTextRectangle arrowTextRectangle => arrowTextRectangle.IsDrawable,
			_ => false
		};
	}

	private void SaveToClipboardAndClose()
	{
		try
		{
			CopyPaintedImageToClipboard(requireChanges: false);
			_hasCopiedToClipboardBeforeClose = true;
			Close();
		}
		catch (Exception ex)
		{
			Logger.Error(ex, "ImagePaintWindow: 編集画像をクリップボードにコピーできませんでした。");
			MessageBox.Show(this, "編集画像をクリップボードにコピーできませんでした。", "Clipboard", MessageBoxButton.OK, MessageBoxImage.Error);
		}
	}

	private bool CopyPaintedImageToClipboard(bool requireChanges)
	{
		_autoClipboardWriteTimer.Stop();
		CompleteActiveDrag(focusTextInput: false);
		_autoClipboardWriteTimer.Stop();
		if (requireChanges && !_hasPaintChanges)
		{
			return false;
		}

		ClipboardManager.CopyImageToClipboard(RenderPaintedImage(), overwriteLatestHistory: _hasPaintChanges);
		return true;
	}

	private void Undo()
	{
		if (_dragElement != null)
		{
			CancelActiveDrag();
			return;
		}

		if (_completedElements.Count == 0)
		{
			return;
		}

		UIElement element = _completedElements[^1];
		_completedElements.RemoveAt(_completedElements.Count - 1);
		_overlayCanvas.Children.Remove(element);
		_redoElements.Add(element);
		if (element is PaintRectangle paintRectangle && ReferenceEquals(_activePaintRectangle, paintRectangle))
		{
			SetActivePaintRectangle(null);
		}

		if (element is ArrowTextRectangle arrowTextRectangle && ReferenceEquals(_activeArrowTextRectangle, arrowTextRectangle))
		{
			SetActiveArrowTextRectangle(null);
		}

		UpdateOutlineNumberLabels();
		FocusCurrentEditableElement();
		MarkPaintChanged();
	}

	private void Redo()
	{
		if (_dragElement != null)
		{
			CancelActiveDrag();
			return;
		}

		if (_redoElements.Count == 0)
		{
			return;
		}

		UIElement element = _redoElements[^1];
		_redoElements.RemoveAt(_redoElements.Count - 1);
		_overlayCanvas.Children.Add(element);
		_completedElements.Add(element);
		UpdateOutlineNumberLabels();
		if (element is PaintRectangle paintRectangle)
		{
			SetActivePaintRectangle(paintRectangle);
			FocusPaintSurface();
		}
		else if (element is ArrowTextRectangle arrowTextRectangle)
		{
			SetActiveArrowTextRectangle(arrowTextRectangle);
			arrowTextRectangle.FocusTextInput();
		}
		else
		{
			FocusPaintSurface();
		}

		MarkPaintChanged();
	}

	private void PaintRectangle_Focused(object? sender, EventArgs e)
	{
		if (sender is PaintRectangle paintRectangle)
		{
			SetActivePaintRectangle(paintRectangle);
		}
	}

	private void PaintRectangle_BoundsChanged(object? sender, EventArgs e)
	{
		if (sender is PaintRectangle paintRectangle && _completedElements.Contains(paintRectangle))
		{
			UpdateOutlineNumberLabels();
			MarkPaintChanged();
		}
	}

	private void ArrowTextRectangle_Changed(object? sender, EventArgs e)
	{
		if (sender is ArrowTextRectangle arrowTextRectangle && _completedElements.Contains(arrowTextRectangle))
		{
			MarkPaintChanged();
		}
	}

	private void ArrowTextRectangle_TextInputFocused(object? sender, EventArgs e)
	{
		if (sender is ArrowTextRectangle arrowTextRectangle)
		{
			SetActiveArrowTextRectangle(arrowTextRectangle);
		}
	}

	private void FocusCurrentEditableElement()
	{
		if (_completedElements.Count > 0 && _completedElements[^1] is PaintRectangle paintRectangle)
		{
			SetActivePaintRectangle(paintRectangle);
			FocusPaintSurface();
			return;
		}

		if (_completedElements.Count > 0 && _completedElements[^1] is ArrowTextRectangle arrowTextRectangle)
		{
			SetActiveArrowTextRectangle(arrowTextRectangle);
			arrowTextRectangle.FocusTextInput();
			return;
		}

		SetActivePaintRectangle(null);
		SetActiveArrowTextRectangle(null);
		FocusPaintSurface();
	}

	private void FocusPaintSurface()
	{
		_overlayCanvas.Focus();
	}

	private void MarkPaintChanged()
	{
		if (!_hasPaintChanges && _completedElements.Count == 0 && _dragElement == null)
		{
			return;
		}

		_hasPaintChanges = true;
		QueueAutoClipboardWrite();
	}

	private void QueueAutoClipboardWrite()
	{
		if (!_hasPaintChanges)
		{
			return;
		}

		_autoClipboardWriteTimer.Stop();
		_autoClipboardWriteTimer.Start();
	}

	private void AutoClipboardWriteTimer_Tick(object? sender, EventArgs e)
	{
		_autoClipboardWriteTimer.Stop();
		WritePaintedImageToClipboardAutomatically();
	}

	private void WritePaintedImageToClipboardAutomatically()
	{
		try
		{
			ClipboardManager.CopyImageToClipboard(
				RenderPaintedImage(prepareTextInputsForRender: false),
				overwriteLatestHistory: true);
		}
		catch (Exception ex)
		{
			Logger.Error(ex, "ImagePaintWindow: 編集画像の自動クリップボード書き込みに失敗しました。");
		}
	}

	private void CompleteActiveDrag(bool focusTextInput)
	{
		if (_dragElement is not { } element)
		{
			return;
		}

		_overlayCanvas.ReleaseMouseCapture();
		if (IsDrawableElement(element))
		{
			_completedElements.Add(element);
			_redoElements.Clear();
			UpdateOutlineNumberLabels();
			if (focusTextInput && element is PaintRectangle paintRectangle)
			{
				SetActivePaintRectangle(paintRectangle);
				FocusPaintSurface();
			}
			else if (focusTextInput && element is ArrowTextRectangle arrowTextRectangle)
			{
				SetActiveArrowTextRectangle(arrowTextRectangle);
				arrowTextRectangle.FocusTextInput();
			}

			MarkPaintChanged();
		}
		else
		{
			_overlayCanvas.Children.Remove(element);
		}

		_dragElement = null;
	}

	private void CancelActiveDrag()
	{
		if (_dragElement is not { } element)
		{
			return;
		}

		_overlayCanvas.ReleaseMouseCapture();
		_overlayCanvas.Children.Remove(element);
		_dragElement = null;
	}

	private void UpdateOutlineNumberLabels()
	{
		foreach (TextBlock label in _outlineNumberLabels.Values)
		{
			_overlayCanvas.Children.Remove(label);
		}

		_outlineNumberLabels.Clear();
		if (!_showOutlineNumbers)
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
		foreach (UIElement element in _completedElements)
		{
			if (element is not PaintRectangle paintRectangle || !paintRectangle.IsOutline)
			{
				continue;
			}

			var label = CreateOutlineNumberLabel(outlineNumber);
			PositionOutlineNumberLabel(paintRectangle, label, placedLabelBounds);
			_outlineNumberLabels[paintRectangle] = label;
			_overlayCanvas.Children.Add(label);
			outlineNumber++;
		}
	}

	private int CountOutlineRectangles()
	{
		int count = 0;
		foreach (UIElement element in _completedElements)
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
			FontSize = _currentArrowTextFontSize,
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

		foreach (UIElement element in _completedElements)
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

	private BitmapSource RenderPaintedImage(bool prepareTextInputsForRender = true)
	{
		if (prepareTextInputsForRender)
		{
			PrepareTextInputsForRender();
		}

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

	protected override void OnClosing(CancelEventArgs e)
	{
		if (!_hasCopiedToClipboardBeforeClose)
		{
			try
			{
				_hasCopiedToClipboardBeforeClose = CopyPaintedImageToClipboard(requireChanges: true);
			}
			catch (Exception ex)
			{
				Logger.Error(ex, "ImagePaintWindow: ウィンドウを閉じる前に編集画像をクリップボードにコピーできませんでした。");
				MessageBox.Show(this, "編集画像をクリップボードにコピーできませんでした。", "Clipboard", MessageBoxButton.OK, MessageBoxImage.Error);
				e.Cancel = true;
				return;
			}
		}

		_autoClipboardWriteTimer.Stop();
		base.OnClosing(e);
	}

	protected override void OnClosed(EventArgs e)
	{
		_autoClipboardWriteTimer.Stop();
		base.OnClosed(e);
	}

	private void PrepareTextInputsForRender()
	{
		foreach (UIElement element in _completedElements)
		{
			if (element is ArrowTextRectangle arrowTextRectangle)
			{
				arrowTextRectangle.PrepareForRender();
			}
		}

		if (_dragElement is ArrowTextRectangle activeArrowTextRectangle)
		{
			activeArrowTextRectangle.PrepareForRender();
		}

		_overlayCanvas.Focusable = true;
		_overlayCanvas.Focus();
		Keyboard.ClearFocus();
	}

	private void SetPaintMode(PaintMode mode)
	{
		_paintMode = mode;
		UpdateModeButton(_blackFillButton, mode == PaintMode.BlackFillRectangle);
		UpdateModeButton(_redOutlineButton, mode == PaintMode.RedOutlineRectangle);
		UpdateModeButton(_arrowTextButton, mode == PaintMode.ArrowTextRectangle);
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

	private StackPanel CreateToolbarTextEditor(string labelText, TextBox textBox)
	{
		var label = new TextBlock
		{
			Text = labelText,
			Margin = new Thickness(12, 0, 6, 0),
			VerticalAlignment = VerticalAlignment.Center
		};
		label.SetResourceReference(TextBlock.ForegroundProperty, AppTheme.TextBrushKey);

		var editor = new StackPanel
		{
			Orientation = Orientation.Horizontal,
			VerticalAlignment = VerticalAlignment.Center
		};
		editor.Children.Add(label);
		editor.Children.Add(textBox);
		return editor;
	}

	private CheckBox CreateOutlineNumberCheckBox()
	{
		var checkBox = new CheckBox
		{
			Content = "数字の番号を付ける",
			IsChecked = true,
			Margin = new Thickness(12, 0, 0, 0),
			VerticalAlignment = VerticalAlignment.Center
		};
		checkBox.SetResourceReference(Control.ForegroundProperty, AppTheme.TextBrushKey);
		checkBox.Checked += (_, _) => SetShowOutlineNumbers(true);
		checkBox.Unchecked += (_, _) => SetShowOutlineNumbers(false);
		return checkBox;
	}

	private void SetShowOutlineNumbers(bool showOutlineNumbers)
	{
		if (_showOutlineNumbers == showOutlineNumbers)
		{
			return;
		}

		_showOutlineNumbers = showOutlineNumbers;
		UpdateOutlineNumberLabels();
		MarkPaintChanged();
	}

	private TextBox CreateStrokeThicknessTextBox(double strokeThickness)
	{
		var textBox = CreateToolbarTextBox(FormatSizeValue(strokeThickness));
		textBox.TextChanged += (_, _) => TryApplyStrokeThicknessText(restoreInvalidValue: false);
		textBox.LostKeyboardFocus += (_, _) => TryApplyStrokeThicknessText(restoreInvalidValue: true);
		textBox.PreviewKeyDown += StrokeThicknessTextBox_PreviewKeyDown;
		return textBox;
	}

	private TextBox CreateFontSizeTextBox(double fontSize)
	{
		var textBox = CreateToolbarTextBox(FormatSizeValue(fontSize));
		textBox.TextChanged += (_, _) => TryApplyFontSizeText(restoreInvalidValue: false);
		textBox.LostKeyboardFocus += (_, _) => TryApplyFontSizeText(restoreInvalidValue: true);
		textBox.PreviewKeyDown += FontSizeTextBox_PreviewKeyDown;
		return textBox;
	}

	private static TextBox CreateToolbarTextBox(string text)
	{
		var textBox = new TextBox
		{
			Text = text,
			Width = 64,
			Height = 30,
			VerticalContentAlignment = VerticalAlignment.Center
		};
		AppTheme.ApplyTextBox(textBox);
		textBox.PreviewTextInput += IntegerTextBox_PreviewTextInput;
		DataObject.AddPastingHandler(textBox, IntegerTextBox_Pasting);
		return textBox;
	}

	private static void IntegerTextBox_PreviewTextInput(object sender, TextCompositionEventArgs e)
	{
		e.Handled = !IsIntegerText(e.Text);
	}

	private static void IntegerTextBox_Pasting(object sender, DataObjectPastingEventArgs e)
	{
		if (!e.DataObject.GetDataPresent(DataFormats.UnicodeText) ||
			e.DataObject.GetData(DataFormats.UnicodeText) is not string text ||
			!IsIntegerText(text))
		{
			e.CancelCommand();
		}
	}

	private void StrokeThicknessTextBox_PreviewKeyDown(object sender, KeyEventArgs e)
	{
		if (e.Key != Key.Enter)
		{
			return;
		}

		TryApplyStrokeThicknessText(restoreInvalidValue: true);
		_overlayCanvas.Focusable = true;
		_overlayCanvas.Focus();
		e.Handled = true;
	}

	private void FontSizeTextBox_PreviewKeyDown(object sender, KeyEventArgs e)
	{
		if (e.Key != Key.Enter)
		{
			return;
		}

		TryApplyFontSizeText(restoreInvalidValue: true);
		_overlayCanvas.Focusable = true;
		_overlayCanvas.Focus();
		e.Handled = true;
	}

	private bool TryApplyStrokeThicknessText(bool restoreInvalidValue)
	{
		if (_isUpdatingStrokeThicknessTextBox)
		{
			return true;
		}

		if (!TryParseStrokeThickness(_strokeThicknessTextBox.Text, out double strokeThickness))
		{
			if (restoreInvalidValue)
			{
				UpdateStrokeThicknessTextBox(GetActiveStrokeThickness());
			}

			return false;
		}

		_paintStrokeThickness = strokeThickness;
		ApplyStrokeThicknessToActiveElement(strokeThickness);
		MarkPaintChanged();
		if (restoreInvalidValue)
		{
			UpdateStrokeThicknessTextBox(strokeThickness);
		}

		return true;
	}

	private bool TryApplyFontSizeText(bool restoreInvalidValue)
	{
		if (_isUpdatingFontSizeTextBox)
		{
			return true;
		}

		if (!TryParseFontSize(_fontSizeTextBox.Text, out double fontSize))
		{
			if (restoreInvalidValue)
			{
				UpdateFontSizeTextBox(_activeArrowTextRectangle?.TextFontSize ?? _currentArrowTextFontSize);
			}

			return false;
		}

		_currentArrowTextFontSize = fontSize;
		_activeArrowTextRectangle?.SetTextFontSize(fontSize);
		UpdateOutlineNumberLabels();
		MarkPaintChanged();
		if (restoreInvalidValue)
		{
			UpdateFontSizeTextBox(fontSize);
		}

		return true;
	}

	private void ApplyStrokeThicknessToActiveElement(double strokeThickness)
	{
		if (_dragElement is PaintRectangle { IsOutline: true } draggingPaintRectangle)
		{
			draggingPaintRectangle.SetStrokeThickness(strokeThickness);
		}
		else if (_dragElement is ArrowTextRectangle draggingArrowTextRectangle)
		{
			draggingArrowTextRectangle.SetStrokeThickness(strokeThickness);
		}

		if (_activePaintRectangle is { IsOutline: true } activePaintRectangle)
		{
			activePaintRectangle.SetStrokeThickness(strokeThickness);
		}

		_activeArrowTextRectangle?.SetStrokeThickness(strokeThickness);
	}

	private double GetActiveStrokeThickness()
	{
		if (_activeArrowTextRectangle != null)
		{
			return _activeArrowTextRectangle.StrokeThickness;
		}

		if (_activePaintRectangle is { IsOutline: true } activePaintRectangle)
		{
			return activePaintRectangle.StrokeThickness;
		}

		return _paintStrokeThickness;
	}

	private void SetActivePaintRectangle(PaintRectangle? paintRectangle)
	{
		_activePaintRectangle = paintRectangle;
		if (paintRectangle != null)
		{
			_activeArrowTextRectangle = null;
			if (paintRectangle.IsOutline)
			{
				_paintStrokeThickness = paintRectangle.StrokeThickness;
			}
		}

		UpdateStrokeThicknessTextBox(GetActiveStrokeThickness());
		UpdateFontSizeTextBox(_currentArrowTextFontSize);
	}

	private void SetActiveArrowTextRectangle(ArrowTextRectangle? arrowTextRectangle)
	{
		_activeArrowTextRectangle = arrowTextRectangle;
		if (arrowTextRectangle != null)
		{
			_activePaintRectangle = null;
			_currentArrowTextFontSize = arrowTextRectangle.TextFontSize;
			_paintStrokeThickness = arrowTextRectangle.StrokeThickness;
			UpdateOutlineNumberLabels();
		}

		UpdateStrokeThicknessTextBox(_paintStrokeThickness);
		UpdateFontSizeTextBox(_currentArrowTextFontSize);
	}

	private void UpdateStrokeThicknessTextBox(double strokeThickness)
	{
		_isUpdatingStrokeThicknessTextBox = true;
		_strokeThicknessTextBox.Text = FormatSizeValue(strokeThickness);
		_isUpdatingStrokeThicknessTextBox = false;
	}

	private void UpdateFontSizeTextBox(double fontSize)
	{
		_isUpdatingFontSizeTextBox = true;
		_fontSizeTextBox.Text = FormatSizeValue(fontSize);
		_isUpdatingFontSizeTextBox = false;
	}

	private static bool TryParseStrokeThickness(string text, out double strokeThickness)
	{
		if (!TryParseNonNegativeIntegerSizeValue(text, out strokeThickness))
		{
			return false;
		}

		strokeThickness = Math.Max(MinPaintStrokeThickness, strokeThickness);
		return true;
	}

	private static bool TryParseFontSize(string text, out double fontSize)
	{
		if (!TryParseNonNegativeIntegerSizeValue(text, out fontSize))
		{
			return false;
		}

		fontSize = Math.Max(MinArrowTextFontSize, fontSize);
		return true;
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

	private static bool IsIntegerText(string text)
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

	private static string FormatSizeValue(double sizeValue)
	{
		return RoundSizeValue(sizeValue).ToString("0", System.Globalization.CultureInfo.InvariantCulture);
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

	private static Canvas CreateArrowTextRectangleIcon()
	{
		var icon = new Canvas
		{
			Width = 18,
			Height = 16
		};

		var arrowLine = new Polyline
		{
			Stroke = ArrowBrush,
			StrokeThickness = 1.6,
			StrokeLineJoin = PenLineJoin.Round,
			Points = new PointCollection
			{
				new(2, 2),
				new(2, 10),
				new(7, 10)
			}
		};
		var arrowHead = new Polygon
		{
			Fill = ArrowBrush,
			Points = new PointCollection
			{
				new(2, 2),
				new(0, 6),
				new(4, 6)
			}
		};
		var rectangle = new Rectangle
		{
			Width = 10,
			Height = 7,
			Fill = Brushes.White,
			Stroke = ArrowBrush,
			StrokeThickness = 1.4
		};
		Canvas.SetLeft(rectangle, 7);
		Canvas.SetTop(rectangle, 7);

		icon.Children.Add(arrowLine);
		icon.Children.Add(arrowHead);
		icon.Children.Add(rectangle);
		return icon;
	}

	private static void UpdateModeButton(Button button, bool selected)
	{
		button.FontWeight = selected ? FontWeights.SemiBold : FontWeights.Normal;
		button.BorderThickness = selected ? new Thickness(2) : new Thickness(1);
		button.SetResourceReference(
			Control.BorderBrushProperty,
			selected ? AppTheme.AccentBorderBrushKey : AppTheme.InputBorderBrushKey);
	}

	private UIElement CreatePaintElement(PaintMode mode)
	{
		if (mode == PaintMode.ArrowTextRectangle)
		{
			var arrowTextRectangle = new ArrowTextRectangle(
				_overlayCanvas.Width,
				_overlayCanvas.Height,
				_paintStrokeThickness,
				_currentArrowTextFontSize);
			arrowTextRectangle.TextInputFocused += ArrowTextRectangle_TextInputFocused;
			arrowTextRectangle.Changed += ArrowTextRectangle_Changed;
			return arrowTextRectangle;
		}

		var paintRectangle = CreateRectangle(mode, _overlayCanvas.Width, _overlayCanvas.Height, _paintStrokeThickness);
		paintRectangle.Focused += PaintRectangle_Focused;
		paintRectangle.BoundsChanged += PaintRectangle_BoundsChanged;
		return paintRectangle;
	}

	private static PaintRectangle CreateRectangle(PaintMode mode, double canvasWidth, double canvasHeight, double strokeThickness)
	{
		return new PaintRectangle(canvasWidth, canvasHeight, mode == PaintMode.RedOutlineRectangle, strokeThickness);
	}

	private static double CalculatePaintStrokeThickness(double canvasHeight)
	{
		return Math.Max(MinPaintStrokeThickness, RoundSizeValue(canvasHeight * PaintStrokeThicknessHeightRatio));
	}

	private static double CalculateDefaultArrowTextFontSize(double canvasHeight)
	{
		return Math.Max(MinArrowTextFontSize, RoundSizeValue(canvasHeight * ArrowTextFontSizeHeightRatio));
	}

	private static double RoundSizeValue(double sizeValue)
	{
		return Math.Round(sizeValue, MidpointRounding.AwayFromZero);
	}

	private static SolidColorBrush CreateFrozenBrush(Color color)
	{
		var brush = new SolidColorBrush(color);
		brush.Freeze();
		return brush;
	}

	private Point ClampToCanvas(Point point)
	{
		return new Point(
			Math.Max(0, Math.Min(point.X, _overlayCanvas.Width)),
			Math.Max(0, Math.Min(point.Y, _overlayCanvas.Height)));
	}

	private sealed class PaintRectangle : Canvas
	{
		private readonly double _canvasWidth;
		private readonly double _canvasHeight;
		private readonly bool _isOutline;
		private readonly Rectangle _rectangle;
		private readonly Dictionary<ResizeHandleKind, Rectangle> _resizeHandles = new();
		private Rect _bounds = Rect.Empty;
		private Point _moveDragStartPoint;
		private Rect _moveDragStartBounds = Rect.Empty;
		private Point _resizeDragStartPoint;
		private Rect _resizeDragStartBounds = Rect.Empty;
		private ResizeHandleKind _activeResizeHandle;
		private bool _isMoving;
		private bool _isResizing;

		public PaintRectangle(double canvasWidth, double canvasHeight, bool isOutline, double strokeThickness)
		{
			_canvasWidth = canvasWidth;
			_canvasHeight = canvasHeight;
			_isOutline = isOutline;
			Width = canvasWidth;
			Height = canvasHeight;
			ClipToBounds = false;

			_rectangle = new Rectangle
			{
				Fill = isOutline ? null : Brushes.Black,
				Stroke = isOutline ? Brushes.Red : Brushes.Transparent,
				StrokeThickness = isOutline ? strokeThickness : 0,
				SnapsToDevicePixels = true,
				Cursor = Cursors.SizeAll
			};
			_rectangle.MouseLeftButtonDown += Rectangle_MouseLeftButtonDown;
			_rectangle.MouseMove += Rectangle_MouseMove;
			_rectangle.MouseLeftButtonUp += Rectangle_MouseLeftButtonUp;

			foreach (ResizeHandleKind resizeHandleKind in Enum.GetValues<ResizeHandleKind>())
			{
				_resizeHandles.Add(resizeHandleKind, CreateResizeHandle(resizeHandleKind));
			}

			Children.Add(_rectangle);
			foreach (Rectangle resizeHandle in _resizeHandles.Values)
			{
				Children.Add(resizeHandle);
			}
		}

		public event EventHandler? Focused;

		public event EventHandler? BoundsChanged;

		public Rect Bounds => _bounds;

		public bool IsOutline => _isOutline;

		public double StrokeThickness => _rectangle.StrokeThickness;

		public bool IsDrawable =>
			_bounds.Width >= MinPaintRectangleWidth &&
			_bounds.Height >= MinPaintRectangleHeight;

		public void Update(Point startPoint, Point currentPoint)
		{
			double left = Math.Min(startPoint.X, currentPoint.X);
			double top = Math.Min(startPoint.Y, currentPoint.Y);
			double width = Math.Abs(currentPoint.X - startPoint.X);
			double height = Math.Abs(currentPoint.Y - startPoint.Y);
			SetBounds(new Rect(left, top, width, height));
		}

		public void SetStrokeThickness(double strokeThickness)
		{
			if (!_isOutline)
			{
				return;
			}

			_rectangle.StrokeThickness = strokeThickness;
		}

		private void SetBounds(Rect bounds)
		{
			_bounds = bounds;
			Canvas.SetLeft(_rectangle, bounds.Left);
			Canvas.SetTop(_rectangle, bounds.Top);
			_rectangle.Width = bounds.Width;
			_rectangle.Height = bounds.Height;
			PositionResizeHandles(bounds);
			BoundsChanged?.Invoke(this, EventArgs.Empty);
		}

		private void Rectangle_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
		{
			Focused?.Invoke(this, EventArgs.Empty);
			_isMoving = true;
			_moveDragStartPoint = e.GetPosition(this);
			_moveDragStartBounds = _bounds;
			_rectangle.CaptureMouse();
			e.Handled = true;
		}

		private void Rectangle_MouseMove(object sender, MouseEventArgs e)
		{
			if (!_isMoving)
			{
				return;
			}

			MoveRectangle(e.GetPosition(this) - _moveDragStartPoint);
			e.Handled = true;
		}

		private void Rectangle_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
		{
			if (!_isMoving)
			{
				return;
			}

			MoveRectangle(e.GetPosition(this) - _moveDragStartPoint);
			_isMoving = false;
			_rectangle.ReleaseMouseCapture();
			e.Handled = true;
		}

		private void MoveRectangle(Vector offset)
		{
			Rect movedBounds = _moveDragStartBounds;
			movedBounds.Offset(offset.X, offset.Y);
			SetBounds(ClampRectangleToBounds(movedBounds));
		}

		private Rectangle CreateResizeHandle(ResizeHandleKind resizeHandleKind)
		{
			var resizeHandle = new Rectangle
			{
				Width = PaintRectangleResizeHandleSize,
				Height = PaintRectangleResizeHandleSize,
				Fill = Brushes.Transparent,
				Stroke = Brushes.Transparent,
				Cursor = GetResizeHandleCursor(resizeHandleKind),
				Tag = resizeHandleKind
			};
			resizeHandle.MouseLeftButtonDown += ResizeHandle_MouseLeftButtonDown;
			resizeHandle.MouseMove += ResizeHandle_MouseMove;
			resizeHandle.MouseLeftButtonUp += ResizeHandle_MouseLeftButtonUp;
			return resizeHandle;
		}

		private static Cursor GetResizeHandleCursor(ResizeHandleKind resizeHandleKind)
		{
			return resizeHandleKind is ResizeHandleKind.TopLeft or ResizeHandleKind.BottomRight
				? Cursors.SizeNWSE
				: Cursors.SizeNESW;
		}

		private void PositionResizeHandles(Rect bounds)
		{
			PositionResizeHandle(ResizeHandleKind.TopLeft, bounds.Left, bounds.Top);
			PositionResizeHandle(ResizeHandleKind.TopRight, bounds.Right, bounds.Top);
			PositionResizeHandle(ResizeHandleKind.BottomLeft, bounds.Left, bounds.Bottom);
			PositionResizeHandle(ResizeHandleKind.BottomRight, bounds.Right, bounds.Bottom);
		}

		private void PositionResizeHandle(ResizeHandleKind resizeHandleKind, double x, double y)
		{
			if (!_resizeHandles.TryGetValue(resizeHandleKind, out Rectangle? resizeHandle))
			{
				return;
			}

			Canvas.SetLeft(resizeHandle, x - PaintRectangleResizeHandleSize / 2);
			Canvas.SetTop(resizeHandle, y - PaintRectangleResizeHandleSize / 2);
		}

		private void ResizeHandle_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
		{
			if (sender is not Rectangle resizeHandle || resizeHandle.Tag is not ResizeHandleKind resizeHandleKind)
			{
				return;
			}

			Focused?.Invoke(this, EventArgs.Empty);
			_isResizing = true;
			_activeResizeHandle = resizeHandleKind;
			_resizeDragStartPoint = e.GetPosition(this);
			_resizeDragStartBounds = _bounds;
			resizeHandle.CaptureMouse();
			e.Handled = true;
		}

		private void ResizeHandle_MouseMove(object sender, MouseEventArgs e)
		{
			if (!_isResizing)
			{
				return;
			}

			ResizeRectangle(e.GetPosition(this) - _resizeDragStartPoint);
			e.Handled = true;
		}

		private void ResizeHandle_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
		{
			if (!_isResizing)
			{
				return;
			}

			ResizeRectangle(e.GetPosition(this) - _resizeDragStartPoint);
			_isResizing = false;
			if (sender is Rectangle resizeHandle)
			{
				resizeHandle.ReleaseMouseCapture();
			}

			e.Handled = true;
		}

		private void ResizeRectangle(Vector offset)
		{
			if (_resizeDragStartBounds.IsEmpty)
			{
				return;
			}

			double left = _resizeDragStartBounds.Left;
			double top = _resizeDragStartBounds.Top;
			double right = _resizeDragStartBounds.Right;
			double bottom = _resizeDragStartBounds.Bottom;
			double minWidth = Math.Min(MinPaintRectangleWidth, _canvasWidth);
			double minHeight = Math.Min(MinPaintRectangleHeight, _canvasHeight);

			if (_activeResizeHandle is ResizeHandleKind.TopLeft or ResizeHandleKind.BottomLeft)
			{
				left = Clamp(left + offset.X, 0, Math.Max(0, right - minWidth));
			}
			else
			{
				right = Clamp(right + offset.X, Math.Min(_canvasWidth, left + minWidth), _canvasWidth);
			}

			if (_activeResizeHandle is ResizeHandleKind.TopLeft or ResizeHandleKind.TopRight)
			{
				top = Clamp(top + offset.Y, 0, Math.Max(0, bottom - minHeight));
			}
			else
			{
				bottom = Clamp(bottom + offset.Y, Math.Min(_canvasHeight, top + minHeight), _canvasHeight);
			}

			SetBounds(new Rect(left, top, right - left, bottom - top));
		}

		private Rect ClampRectangleToBounds(Rect bounds)
		{
			double left = Clamp(bounds.Left, 0, Math.Max(0, _canvasWidth - bounds.Width));
			double top = Clamp(bounds.Top, 0, Math.Max(0, _canvasHeight - bounds.Height));
			return new Rect(left, top, bounds.Width, bounds.Height);
		}

		private static double Clamp(double value, double min, double max)
		{
			return Math.Max(min, Math.Min(value, max));
		}

		private enum ResizeHandleKind
		{
			TopLeft,
			TopRight,
			BottomLeft,
			BottomRight
		}
	}

	private sealed class ArrowTextRectangle : Canvas
	{
		private readonly double _canvasWidth;
		private readonly double _canvasHeight;
		private readonly Rectangle _rectangle;
		private readonly Polyline _arrowLine;
		private readonly Polygon _arrowHead;
		private readonly TextBox _textBox;
		private readonly Dictionary<ResizeHandleKind, Rectangle> _resizeHandles = new();
		private double _strokeThickness;
		private double _arrowHeadLength;
		private double _arrowHeadWidth;
		private Rect _textRectangleBounds = Rect.Empty;
		private Point _arrowTip;
		private Point _arrowDragStartPoint;
		private Point _arrowDragStartTip;
		private double _arrowDragGrabAlongDirection;
		private double _arrowDragGrabAlongNormal;
		private Point _rectangleDragStartPoint;
		private Rect _rectangleDragStartBounds = Rect.Empty;
		private Point _resizeDragStartPoint;
		private Rect _resizeDragStartBounds = Rect.Empty;
		private ResizeHandleKind _activeResizeHandle;
		private bool _isArrowTipCustomized;
		private bool _isDraggingArrowTip;
		private bool _isDraggingRectangle;
		private bool _isResizingRectangle;

		public ArrowTextRectangle(double canvasWidth, double canvasHeight, double strokeThickness, double textFontSize)
		{
			_canvasWidth = canvasWidth;
			_canvasHeight = canvasHeight;
			_strokeThickness = strokeThickness;
			_arrowHeadLength = CalculateArrowHeadLength(strokeThickness);
			_arrowHeadWidth = CalculateArrowHeadWidth(strokeThickness);
			Width = canvasWidth;
			Height = canvasHeight;
			ClipToBounds = false;

			_arrowLine = new Polyline
			{
				Stroke = ArrowBrush,
				StrokeThickness = strokeThickness,
				StrokeLineJoin = PenLineJoin.Round,
				StrokeStartLineCap = PenLineCap.Round,
				StrokeEndLineCap = PenLineCap.Flat,
				IsHitTestVisible = false
			};
			_arrowHead = new Polygon
			{
				Fill = ArrowBrush,
				Cursor = Cursors.SizeAll,
				IsHitTestVisible = true
			};
			_arrowHead.MouseLeftButtonDown += ArrowHead_MouseLeftButtonDown;
			_arrowHead.MouseMove += ArrowHead_MouseMove;
			_arrowHead.MouseLeftButtonUp += ArrowHead_MouseLeftButtonUp;
			_rectangle = new Rectangle
			{
				Fill = Brushes.White,
				Stroke = ArrowBrush,
				StrokeThickness = strokeThickness,
				SnapsToDevicePixels = true,
				Cursor = Cursors.SizeAll
			};
			_rectangle.MouseLeftButtonDown += Rectangle_MouseLeftButtonDown;
			_rectangle.MouseMove += Rectangle_MouseMove;
			_rectangle.MouseLeftButtonUp += Rectangle_MouseLeftButtonUp;
			_textBox = new TextBox
			{
				AcceptsReturn = true,
				Background = Brushes.Transparent,
				BorderThickness = new Thickness(0),
				CaretBrush = Brushes.Black,
				FontFamily = new FontFamily(ArrowTextFontFamilyName),
				FontSize = textFontSize,
				Foreground = Brushes.Black,
				HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
				MinHeight = 0,
				MinWidth = 0,
				Padding = new Thickness(0),
				TextWrapping = TextWrapping.Wrap,
				VerticalScrollBarVisibility = ScrollBarVisibility.Disabled
			};
			_textBox.GotKeyboardFocus += (_, _) => TextInputFocused?.Invoke(this, EventArgs.Empty);
			_textBox.TextChanged += (_, _) => OnChanged();

			foreach (ResizeHandleKind resizeHandleKind in Enum.GetValues<ResizeHandleKind>())
			{
				_resizeHandles.Add(resizeHandleKind, CreateResizeHandle(resizeHandleKind));
			}

			Children.Add(_arrowLine);
			Children.Add(_arrowHead);
			Children.Add(_rectangle);
			Children.Add(_textBox);
			foreach (Rectangle resizeHandle in _resizeHandles.Values)
			{
				Children.Add(resizeHandle);
			}
		}

		public event EventHandler? TextInputFocused;

		public event EventHandler? Changed;

		public Rect TextRectangleBounds => _textRectangleBounds;

		public double TextFontSize => _textBox.FontSize;

		public double StrokeThickness => _strokeThickness;

		public bool IsDrawable =>
			_textRectangleBounds.Width >= MinArrowTextRectangleWidth &&
			_textRectangleBounds.Height >= MinArrowTextRectangleHeight;

		public void Update(Point startPoint, Point currentPoint)
		{
			double left = Math.Min(startPoint.X, currentPoint.X);
			double top = Math.Min(startPoint.Y, currentPoint.Y);
			double width = Math.Abs(currentPoint.X - startPoint.X);
			double height = Math.Abs(currentPoint.Y - startPoint.Y);
			SetTextRectangleBounds(new Rect(left, top, width, height));

			if (!_isArrowTipCustomized)
			{
				_arrowTip = CalculateDefaultArrowTip(_textRectangleBounds);
			}

			UpdateArrow(_textRectangleBounds);
			OnChanged();
		}

		private void SetTextRectangleBounds(Rect bounds)
		{
			_textRectangleBounds = bounds;
			Canvas.SetLeft(_rectangle, bounds.Left);
			Canvas.SetTop(_rectangle, bounds.Top);
			_rectangle.Width = bounds.Width;
			_rectangle.Height = bounds.Height;

			double textInset = _strokeThickness + ArrowTextPadding;
			Canvas.SetLeft(_textBox, bounds.Left + textInset);
			Canvas.SetTop(_textBox, bounds.Top + textInset);
			_textBox.Width = Math.Max(0, bounds.Width - textInset * 2);
			_textBox.Height = Math.Max(0, bounds.Height - textInset * 2);
			PositionResizeHandles(bounds);
		}

		public void FocusTextInput()
		{
			_textBox.Focus();
			_textBox.CaretIndex = _textBox.Text.Length;
		}

		public void SetTextFontSize(double fontSize)
		{
			_textBox.FontSize = fontSize;
			OnChanged();
		}

		public void SetStrokeThickness(double strokeThickness)
		{
			if (Math.Abs(_strokeThickness - strokeThickness) < 0.001)
			{
				return;
			}

			_strokeThickness = strokeThickness;
			_arrowHeadLength = CalculateArrowHeadLength(strokeThickness);
			_arrowHeadWidth = CalculateArrowHeadWidth(strokeThickness);
			_arrowLine.StrokeThickness = strokeThickness;
			_rectangle.StrokeThickness = strokeThickness;
			if (!_textRectangleBounds.IsEmpty)
			{
				SetTextRectangleBounds(_textRectangleBounds);
				UpdateArrow(_textRectangleBounds);
			}

			OnChanged();
		}

		public void PrepareForRender()
		{
			if (_textBox.SelectionLength > 0)
			{
				_textBox.Select(_textBox.CaretIndex, 0);
			}
		}

		private void Rectangle_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
		{
			_isDraggingRectangle = true;
			_isArrowTipCustomized = true;
			_rectangleDragStartPoint = e.GetPosition(this);
			_rectangleDragStartBounds = _textRectangleBounds;
			_rectangle.CaptureMouse();
			e.Handled = true;
		}

		private void Rectangle_MouseMove(object sender, MouseEventArgs e)
		{
			if (!_isDraggingRectangle)
			{
				return;
			}

			MoveTextRectangle(e.GetPosition(this) - _rectangleDragStartPoint);
			e.Handled = true;
		}

		private void Rectangle_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
		{
			if (!_isDraggingRectangle)
			{
				return;
			}

			MoveTextRectangle(e.GetPosition(this) - _rectangleDragStartPoint);
			_isDraggingRectangle = false;
			_rectangle.ReleaseMouseCapture();
			e.Handled = true;
		}

		private void MoveTextRectangle(Vector offset)
		{
			Rect movedBounds = _rectangleDragStartBounds;
			movedBounds.Offset(offset.X, offset.Y);
			SetTextRectangleBounds(ClampRectangleToBounds(movedBounds));
			UpdateArrow(_textRectangleBounds);
			OnChanged();
		}

		private Rectangle CreateResizeHandle(ResizeHandleKind resizeHandleKind)
		{
			var resizeHandle = new Rectangle
			{
				Width = ArrowResizeHandleSize,
				Height = ArrowResizeHandleSize,
				Fill = Brushes.Transparent,
				Stroke = Brushes.Transparent,
				Cursor = GetResizeHandleCursor(resizeHandleKind),
				Tag = resizeHandleKind
			};
			resizeHandle.MouseLeftButtonDown += ResizeHandle_MouseLeftButtonDown;
			resizeHandle.MouseMove += ResizeHandle_MouseMove;
			resizeHandle.MouseLeftButtonUp += ResizeHandle_MouseLeftButtonUp;
			return resizeHandle;
		}

		private static Cursor GetResizeHandleCursor(ResizeHandleKind resizeHandleKind)
		{
			return resizeHandleKind is ResizeHandleKind.TopLeft or ResizeHandleKind.BottomRight
				? Cursors.SizeNWSE
				: Cursors.SizeNESW;
		}

		private void PositionResizeHandles(Rect bounds)
		{
			PositionResizeHandle(ResizeHandleKind.TopLeft, bounds.Left, bounds.Top);
			PositionResizeHandle(ResizeHandleKind.TopRight, bounds.Right, bounds.Top);
			PositionResizeHandle(ResizeHandleKind.BottomLeft, bounds.Left, bounds.Bottom);
			PositionResizeHandle(ResizeHandleKind.BottomRight, bounds.Right, bounds.Bottom);
		}

		private void PositionResizeHandle(ResizeHandleKind resizeHandleKind, double x, double y)
		{
			if (!_resizeHandles.TryGetValue(resizeHandleKind, out Rectangle? resizeHandle))
			{
				return;
			}

			Canvas.SetLeft(resizeHandle, x - ArrowResizeHandleSize / 2);
			Canvas.SetTop(resizeHandle, y - ArrowResizeHandleSize / 2);
		}

		private void ResizeHandle_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
		{
			if (sender is not Rectangle resizeHandle || resizeHandle.Tag is not ResizeHandleKind resizeHandleKind)
			{
				return;
			}

			_isResizingRectangle = true;
			_isArrowTipCustomized = true;
			_activeResizeHandle = resizeHandleKind;
			_resizeDragStartPoint = e.GetPosition(this);
			_resizeDragStartBounds = _textRectangleBounds;
			resizeHandle.CaptureMouse();
			e.Handled = true;
		}

		private void ResizeHandle_MouseMove(object sender, MouseEventArgs e)
		{
			if (!_isResizingRectangle)
			{
				return;
			}

			ResizeTextRectangle(e.GetPosition(this) - _resizeDragStartPoint);
			e.Handled = true;
		}

		private void ResizeHandle_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
		{
			if (!_isResizingRectangle)
			{
				return;
			}

			ResizeTextRectangle(e.GetPosition(this) - _resizeDragStartPoint);
			_isResizingRectangle = false;
			if (sender is Rectangle resizeHandle)
			{
				resizeHandle.ReleaseMouseCapture();
			}

			e.Handled = true;
		}

		private void ResizeTextRectangle(Vector offset)
		{
			if (_resizeDragStartBounds.IsEmpty)
			{
				return;
			}

			double left = _resizeDragStartBounds.Left;
			double top = _resizeDragStartBounds.Top;
			double right = _resizeDragStartBounds.Right;
			double bottom = _resizeDragStartBounds.Bottom;
			double minWidth = Math.Min(MinArrowTextRectangleWidth, _canvasWidth);
			double minHeight = Math.Min(MinArrowTextRectangleHeight, _canvasHeight);

			if (_activeResizeHandle is ResizeHandleKind.TopLeft or ResizeHandleKind.BottomLeft)
			{
				left = Clamp(left + offset.X, 0, Math.Max(0, right - minWidth));
			}
			else
			{
				right = Clamp(right + offset.X, Math.Min(_canvasWidth, left + minWidth), _canvasWidth);
			}

			if (_activeResizeHandle is ResizeHandleKind.TopLeft or ResizeHandleKind.TopRight)
			{
				top = Clamp(top + offset.Y, 0, Math.Max(0, bottom - minHeight));
			}
			else
			{
				bottom = Clamp(bottom + offset.Y, Math.Min(_canvasHeight, top + minHeight), _canvasHeight);
			}

			SetTextRectangleBounds(new Rect(left, top, right - left, bottom - top));
			UpdateArrow(_textRectangleBounds);
			OnChanged();
		}

		private void ArrowHead_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
		{
			_isDraggingArrowTip = true;
			_isArrowTipCustomized = true;
			_arrowDragStartPoint = e.GetPosition(this);
			_arrowDragStartTip = _arrowTip;
			Vector arrowDirection = GetArrowDirection(_textRectangleBounds, _arrowTip, out _, out _);
			arrowDirection.Normalize();
			Vector arrowNormal = new(-arrowDirection.Y, arrowDirection.X);
			Vector grabOffset = _arrowDragStartPoint - _arrowTip;
			_arrowDragGrabAlongDirection = Vector.Multiply(grabOffset, arrowDirection);
			_arrowDragGrabAlongNormal = Vector.Multiply(grabOffset, arrowNormal);
			_arrowHead.CaptureMouse();
			e.Handled = true;
		}

		private void ArrowHead_MouseMove(object sender, MouseEventArgs e)
		{
			if (!_isDraggingArrowTip)
			{
				return;
			}

			MoveArrowTipFromDragPoint(e.GetPosition(this));
			e.Handled = true;
		}

		private void ArrowHead_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
		{
			if (!_isDraggingArrowTip)
			{
				return;
			}

			MoveArrowTipFromDragPoint(e.GetPosition(this));
			_isDraggingArrowTip = false;
			_arrowHead.ReleaseMouseCapture();
			e.Handled = true;
		}

		private void MoveArrowTipFromDragPoint(Point dragPoint)
		{
			Point tipPoint = _arrowDragStartTip + (dragPoint - _arrowDragStartPoint);
			for (int i = 0; i < 3; i++)
			{
				Vector arrowDirection = GetArrowDirection(_textRectangleBounds, tipPoint, out _, out _);
				arrowDirection.Normalize();
				Vector arrowNormal = new(-arrowDirection.Y, arrowDirection.X);
				tipPoint = dragPoint -
					arrowDirection * _arrowDragGrabAlongDirection -
					arrowNormal * _arrowDragGrabAlongNormal;
				tipPoint = ClampToBounds(tipPoint);
			}

			MoveArrowTip(tipPoint);
		}

		private void MoveArrowTip(Point point)
		{
			_arrowTip = ClampToBounds(point);
			UpdateArrow(_textRectangleBounds);
			OnChanged();
		}

		private void OnChanged()
		{
			Changed?.Invoke(this, EventArgs.Empty);
		}

		private Point CalculateDefaultArrowTip(Rect rectangleBounds)
		{
			double leftSpace = rectangleBounds.Left;
			double rightSpace = Math.Max(0, _canvasWidth - rectangleBounds.Right);
			bool useLeft = leftSpace >= rightSpace;
			double attachX = useLeft ? rectangleBounds.Left : rectangleBounds.Right;
			double horizontalLength = Math.Min(ArrowTailLength, useLeft ? leftSpace : rightSpace);
			double bendX = useLeft
				? Math.Max(0, attachX - horizontalLength)
				: Math.Min(_canvasWidth, attachX + horizontalLength);

			double topSpace = rectangleBounds.Top;
			double bottomSpace = Math.Max(0, _canvasHeight - rectangleBounds.Bottom);
			bool useUp = topSpace >= bottomSpace;
			double tipY = useUp
				? Math.Max(0, rectangleBounds.Top - Math.Min(ArrowTailLength, topSpace))
				: Math.Min(_canvasHeight, rectangleBounds.Bottom + Math.Min(ArrowTailLength, bottomSpace));

			return new Point(bendX, tipY);
		}

		private void UpdateArrow(Rect rectangleBounds)
		{
			Point tipPoint = ClampToBounds(_arrowTip);
			_arrowTip = tipPoint;
			Vector arrowDirection = GetArrowDirection(rectangleBounds, tipPoint, out Point attachPoint, out Point bendPoint);

			Point lineEndPoint = GetArrowLineEndPoint(tipPoint, bendPoint, attachPoint, arrowDirection);
			_arrowLine.Points = new PointCollection
			{
				attachPoint,
				bendPoint,
				lineEndPoint
			};

			UpdateArrowHead(tipPoint, arrowDirection);
		}

		private Vector GetArrowDirection(Rect rectangleBounds, Point tipPoint, out Point attachPoint, out Point bendPoint)
		{
			attachPoint = GetArrowAttachPoint(rectangleBounds, tipPoint, out ArrowAttachSide attachSide);
			bendPoint = GetArrowBendPoint(attachPoint, tipPoint, attachSide);
			Vector arrowDirection = tipPoint - bendPoint;
			if (arrowDirection.LengthSquared < 0.001)
			{
				arrowDirection = tipPoint - attachPoint;
			}

			if (arrowDirection.LengthSquared < 0.001)
			{
				arrowDirection = attachSide switch
				{
					ArrowAttachSide.Left => new Vector(-1, 0),
					ArrowAttachSide.Right => new Vector(1, 0),
					ArrowAttachSide.Top => new Vector(0, -1),
					_ => new Vector(0, 1)
				};
			}

			return arrowDirection;
		}

		private static Point GetArrowBendPoint(Point attachPoint, Point tipPoint, ArrowAttachSide attachSide)
		{
			return attachSide is ArrowAttachSide.Left or ArrowAttachSide.Right
				? new Point(attachPoint.X + (tipPoint.X - attachPoint.X) * ArrowBendDistanceRatio, attachPoint.Y)
				: new Point(attachPoint.X, attachPoint.Y + (tipPoint.Y - attachPoint.Y) * ArrowBendDistanceRatio);
		}

		private Point GetArrowLineEndPoint(Point tipPoint, Point bendPoint, Point attachPoint, Vector arrowDirection)
		{
			double tipSegmentLength = (tipPoint - bendPoint).Length;
			if (tipSegmentLength < 0.001)
			{
				tipSegmentLength = (tipPoint - attachPoint).Length;
			}

			double inset = Math.Min(_arrowHeadLength * ArrowLineEndHeadInsetRatio, Math.Max(_strokeThickness, tipSegmentLength * 0.8));
			arrowDirection.Normalize();
			return tipPoint - arrowDirection * inset;
		}

		private Point GetArrowAttachPoint(Rect rectangleBounds, Point tipPoint, out ArrowAttachSide attachSide)
		{
			double leftOverflow = Math.Max(0, rectangleBounds.Left - tipPoint.X);
			double rightOverflow = Math.Max(0, tipPoint.X - rectangleBounds.Right);
			double topOverflow = Math.Max(0, rectangleBounds.Top - tipPoint.Y);
			double bottomOverflow = Math.Max(0, tipPoint.Y - rectangleBounds.Bottom);
			double horizontalOverflow = Math.Max(leftOverflow, rightOverflow);
			double verticalOverflow = Math.Max(topOverflow, bottomOverflow);

			if (horizontalOverflow >= verticalOverflow && horizontalOverflow > 0)
			{
				attachSide = leftOverflow >= rightOverflow ? ArrowAttachSide.Left : ArrowAttachSide.Right;
			}
			else if (verticalOverflow > 0)
			{
				attachSide = topOverflow >= bottomOverflow ? ArrowAttachSide.Top : ArrowAttachSide.Bottom;
			}
			else
			{
				double leftDistance = Math.Abs(tipPoint.X - rectangleBounds.Left);
				double rightDistance = Math.Abs(rectangleBounds.Right - tipPoint.X);
				double topDistance = Math.Abs(tipPoint.Y - rectangleBounds.Top);
				double bottomDistance = Math.Abs(rectangleBounds.Bottom - tipPoint.Y);
				double minDistance = Math.Min(Math.Min(leftDistance, rightDistance), Math.Min(topDistance, bottomDistance));
				attachSide = minDistance == leftDistance
					? ArrowAttachSide.Left
					: minDistance == rightDistance
						? ArrowAttachSide.Right
						: minDistance == topDistance
							? ArrowAttachSide.Top
							: ArrowAttachSide.Bottom;
			}

			return attachSide switch
			{
				ArrowAttachSide.Left => new Point(rectangleBounds.Left, rectangleBounds.Top + rectangleBounds.Height / 2),
				ArrowAttachSide.Right => new Point(rectangleBounds.Right, rectangleBounds.Top + rectangleBounds.Height / 2),
				ArrowAttachSide.Top => new Point(rectangleBounds.Left + rectangleBounds.Width / 2, rectangleBounds.Top),
				_ => new Point(rectangleBounds.Left + rectangleBounds.Width / 2, rectangleBounds.Bottom)
			};
		}

		private Point ClampToBounds(Point point)
		{
			return new Point(
				Clamp(point.X, 0, _canvasWidth),
				Clamp(point.Y, 0, _canvasHeight));
		}

		private Rect ClampRectangleToBounds(Rect bounds)
		{
			double left = Clamp(bounds.Left, 0, Math.Max(0, _canvasWidth - bounds.Width));
			double top = Clamp(bounds.Top, 0, Math.Max(0, _canvasHeight - bounds.Height));
			return new Rect(left, top, bounds.Width, bounds.Height);
		}

		private static double Clamp(double value, double min, double max)
		{
			return Math.Max(min, Math.Min(value, max));
		}

		private static double CalculateArrowHeadLength(double strokeThickness)
		{
			return Math.Max(MinArrowHeadLength, strokeThickness * ArrowHeadLengthStrokeMultiplier);
		}

		private static double CalculateArrowHeadWidth(double strokeThickness)
		{
			return Math.Max(MinArrowHeadWidth, strokeThickness * ArrowHeadWidthStrokeMultiplier);
		}

		private void UpdateArrowHead(Point tipPoint, Vector direction)
		{
			direction.Normalize();
			Vector normal = new(-direction.Y, direction.X);
			Point baseCenter = tipPoint - direction * _arrowHeadLength;
			_arrowHead.Points = new PointCollection
			{
				tipPoint,
				baseCenter + normal * (_arrowHeadWidth / 2),
				baseCenter - normal * (_arrowHeadWidth / 2)
			};
		}

		private enum ArrowAttachSide
		{
			Left,
			Right,
			Top,
			Bottom
		}

		private enum ResizeHandleKind
		{
			TopLeft,
			TopRight,
			BottomLeft,
			BottomRight
		}
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
		RedOutlineRectangle,
		ArrowTextRectangle
	}
}
