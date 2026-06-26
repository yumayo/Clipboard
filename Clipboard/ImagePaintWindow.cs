using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Ellipse = System.Windows.Shapes.Ellipse;
using Polygon = System.Windows.Shapes.Polygon;
using Polyline = System.Windows.Shapes.Polyline;
using Rectangle = System.Windows.Shapes.Rectangle;

namespace Clipboard;

internal sealed class ImagePaintWindow : Window
{
	private const double ToolbarHeight = 48;
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
	private const double MultiImageGap = 24;
	private const double MultiImageOuterMargin = 24;
	private const double MultiImageMinPlacedSize = 16;
	private const double PlacedImageResizeHandleSize = 18;
	private const double WorkspaceInitialMargin = 1024;
	private const double WorkspaceExpansionChunk = 1024;
	private const double RenderBoundsPadding = 2;
	private const string ArrowTextFontFamilyName = "Meiryo UI";
	private static readonly SolidColorBrush ArrowBrush = CreateFrozenBrush(Color.FromRgb(226, 104, 0));
	private static readonly string[] CircledNumberTexts =
	{
		"①", "②", "③", "④", "⑤", "⑥", "⑦", "⑧", "⑨", "⑩",
		"⑪", "⑫", "⑬", "⑭", "⑮", "⑯", "⑰", "⑱", "⑲", "⑳"
	};
	private readonly BitmapSource _sourceImage;
	private readonly ScaleTransform _zoomTransform = new(1, 1);
	private double _canvasWidth;
	private double _canvasHeight;
	private Grid _zoomContainer = null!;
	private Grid _paintSurface = null!;
	private Canvas _baseImageCanvas = null!;
	private Image _sourceImageControl = null!;
	private Canvas _imageLayerCanvas = null!;
	private Canvas _overlayCanvas = null!;
	private ScrollViewer _scrollViewer = null!;
	private Button? _moveImageButton;
	private Button _blackFillButton = null!;
	private Button _redOutlineButton = null!;
	private Button _arrowTextButton = null!;
	private bool _isMultiImageMode;
	private TextBox _strokeThicknessTextBox = null!;
	private TextBox _fontSizeTextBox = null!;
	private CheckBox _outlineNumberCheckBox = null!;
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
	private Cursor? _previousOverrideCursor;
	private PaintMode _paintMode = PaintMode.RedOutlineRectangle;
	private Point _dragStartPoint;
	private UIElement? _dragElement;
	private Point _middleButtonPanStartPoint;
	private double _middleButtonPanStartHorizontalOffset;
	private double _middleButtonPanStartVerticalOffset;
	private bool _isMiddleButtonPanning;

	public ImagePaintWindow(byte[] imageBytes)
	{
		_sourceImage = LoadImage(imageBytes);
		InitializeWindow(_sourceImage.PixelHeight, placedImages: null);
	}

	public ImagePaintWindow(IReadOnlyList<byte[]> imageBytesList)
	{
		if (imageBytesList.Count == 0)
		{
			throw new InvalidOperationException("画像データが空です。");
		}

		List<BitmapSource> images = imageBytesList.Select(LoadImage).ToList();
		MultiImageLayout layout = CalculateMultiImageLayout(images);
		_sourceImage = CreateWhiteCanvasImage(layout.CanvasWidth, layout.CanvasHeight);
		InitializeWindow(layout.ReferenceHeight, layout.PlacedImages);
	}

	private void InitializeWindow(double referenceHeight, IReadOnlyList<PlacedImageLayout>? placedImages)
	{
		_isMultiImageMode = placedImages is { Count: > 0 };
		if (_isMultiImageMode)
		{
			_paintMode = PaintMode.MoveImage;
		}

		_paintStrokeThickness = CalculatePaintStrokeThickness(referenceHeight);
		_currentArrowTextFontSize = CalculateDefaultArrowTextFontSize(referenceHeight);
		double initialContentWidth = _sourceImage.PixelWidth;
		double initialContentHeight = _sourceImage.PixelHeight;
		_canvasWidth = initialContentWidth + WorkspaceInitialMargin * 2;
		_canvasHeight = initialContentHeight + WorkspaceInitialMargin * 2;
		double windowWidth = Math.Min(SystemParameters.WorkArea.Width - 80, Math.Max(520, initialContentWidth + 36));
		double windowHeight = Math.Min(SystemParameters.WorkArea.Height - 80, Math.Max(420, initialContentHeight + ToolbarHeight + 36));

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

		if (_isMultiImageMode)
		{
			_moveImageButton = CreateModeButton("画像の移動", CreateMoveImageIcon());
			_moveImageButton.Click += (_, _) => SetPaintMode(PaintMode.MoveImage);
			toolbar.Children.Add(_moveImageButton);
		}

		_blackFillButton = CreateModeButton("塗りつぶし", CreateFillIcon());
		_blackFillButton.Margin = _isMultiImageMode ? new Thickness(8, 0, 0, 0) : new Thickness(0);
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

		_sourceImageControl = new Image
		{
			Source = _sourceImage,
			Stretch = Stretch.Fill,
			Width = initialContentWidth,
			Height = initialContentHeight,
			SnapsToDevicePixels = true,
			Visibility = _isMultiImageMode ? Visibility.Collapsed : Visibility.Visible
		};
		RenderOptions.SetBitmapScalingMode(_sourceImageControl, BitmapScalingMode.HighQuality);
		Canvas.SetLeft(_sourceImageControl, 0);
		Canvas.SetTop(_sourceImageControl, 0);
		_baseImageCanvas = new Canvas
		{
			Width = _canvasWidth,
			Height = _canvasHeight,
			Background = Brushes.Transparent
		};
		_baseImageCanvas.Children.Add(_sourceImageControl);

		_imageLayerCanvas = new Canvas
		{
			Width = _canvasWidth,
			Height = _canvasHeight,
			Background = Brushes.Transparent,
			IsHitTestVisible = placedImages is { Count: > 0 }
		};
		AddPlacedImages(placedImages);

		_overlayCanvas = new Canvas
		{
			Width = _canvasWidth,
			Height = _canvasHeight,
			Background = Brushes.Transparent,
			Cursor = Cursors.Cross,
			Focusable = true
		};
		_overlayCanvas.MouseLeftButtonDown += OverlayCanvas_MouseLeftButtonDown;
		_overlayCanvas.MouseMove += OverlayCanvas_MouseMove;
		_overlayCanvas.MouseLeftButtonUp += OverlayCanvas_MouseLeftButtonUp;

		_paintSurface = new Grid
		{
			Width = _canvasWidth,
			Height = _canvasHeight,
			Background = Brushes.Transparent,
			ClipToBounds = false
		};
		_paintSurface.Children.Add(_baseImageCanvas);
		_paintSurface.Children.Add(_imageLayerCanvas);
		_paintSurface.Children.Add(_overlayCanvas);

		_zoomContainer = new Grid
		{
			Width = _canvasWidth,
			Height = _canvasHeight,
			LayoutTransform = _zoomTransform
		};
		_zoomContainer.Children.Add(_paintSurface);
		ShiftCanvasContent(WorkspaceInitialMargin, WorkspaceInitialMargin);
		SetZoom(CalculateInitialZoom(windowWidth, windowHeight));

		_scrollViewer = new ScrollViewer
		{
			Content = _zoomContainer,
			HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden,
			VerticalScrollBarVisibility = ScrollBarVisibility.Hidden
		};
		_scrollViewer.PreviewMouseWheel += ScrollViewer_PreviewMouseWheel;
		_scrollViewer.PreviewMouseDown += ScrollViewer_PreviewMouseDown;
		_scrollViewer.PreviewMouseMove += ScrollViewer_PreviewMouseMove;
		_scrollViewer.PreviewMouseUp += ScrollViewer_PreviewMouseUp;
		_scrollViewer.LostMouseCapture += ScrollViewer_LostMouseCapture;
		_scrollViewer.Loaded += (_, _) => ScrollToInitialWorkspacePosition();
		_scrollViewer.SetResourceReference(Control.BackgroundProperty, AppTheme.ThumbnailBackgroundBrushKey);
		root.Children.Add(_scrollViewer);

		Content = root;
		AddHandler(Keyboard.PreviewKeyDownEvent, new KeyEventHandler(ImagePaintWindow_PreviewKeyDown), true);
		SetPaintMode(_paintMode);
	}

	private void AddPlacedImages(IReadOnlyList<PlacedImageLayout>? placedImages)
	{
		if (placedImages == null)
		{
			return;
		}

		foreach (PlacedImageLayout placedImageLayout in placedImages)
		{
			var placedImage = new PlacedImage(
				placedImageLayout.Image,
				_canvasWidth,
				_canvasHeight,
				placedImageLayout.Bounds);
			placedImage.Changed += PlacedImage_Changed;
			_imageLayerCanvas.Children.Add(placedImage);
		}
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

	private void ScrollViewer_PreviewMouseDown(object sender, MouseButtonEventArgs e)
	{
		if (e.ChangedButton != MouseButton.Middle)
		{
			return;
		}

		BeginMiddleButtonPan(e.GetPosition(_scrollViewer));
		e.Handled = true;
	}

	private void ScrollViewer_PreviewMouseMove(object sender, MouseEventArgs e)
	{
		if (!_isMiddleButtonPanning)
		{
			return;
		}

		if (e.MiddleButton != MouseButtonState.Pressed)
		{
			EndMiddleButtonPan(releaseCapture: true);
			e.Handled = true;
			return;
		}

		UpdateMiddleButtonPan(e.GetPosition(_scrollViewer));
		e.Handled = true;
	}

	private void ScrollViewer_PreviewMouseUp(object sender, MouseButtonEventArgs e)
	{
		if (!_isMiddleButtonPanning || e.ChangedButton != MouseButton.Middle)
		{
			return;
		}

		UpdateMiddleButtonPan(e.GetPosition(_scrollViewer));
		EndMiddleButtonPan(releaseCapture: true);
		e.Handled = true;
	}

	private void ScrollViewer_LostMouseCapture(object sender, MouseEventArgs e)
	{
		if (_isMiddleButtonPanning)
		{
			EndMiddleButtonPan(releaseCapture: false);
		}
	}

	private void BeginMiddleButtonPan(Point point)
	{
		if (_isMiddleButtonPanning)
		{
			return;
		}

		_isMiddleButtonPanning = true;
		_middleButtonPanStartPoint = point;
		_middleButtonPanStartHorizontalOffset = _scrollViewer.HorizontalOffset;
		_middleButtonPanStartVerticalOffset = _scrollViewer.VerticalOffset;
		_previousOverrideCursor = Mouse.OverrideCursor;
		Mouse.OverrideCursor = Cursors.SizeAll;
		_scrollViewer.CaptureMouse();
	}

	private void UpdateMiddleButtonPan(Point point)
	{
		Vector offset = point - _middleButtonPanStartPoint;
		double horizontalOffset = _middleButtonPanStartHorizontalOffset - offset.X;
		double verticalOffset = _middleButtonPanStartVerticalOffset - offset.Y;
		EnsureWorkspaceForMiddleButtonPan(ref horizontalOffset, ref verticalOffset);
		_scrollViewer.ScrollToHorizontalOffset(horizontalOffset);
		_scrollViewer.ScrollToVerticalOffset(verticalOffset);
	}

	private void EnsureWorkspaceForMiddleButtonPan(ref double horizontalOffset, ref double verticalOffset)
	{
		double zoom = _zoomTransform.ScaleX;
		if (!double.IsFinite(zoom) || zoom <= 0)
		{
			return;
		}

		bool resized = false;
		double expandLeft = CalculateWorkspaceExpansion(Math.Max(0, -horizontalOffset) / zoom);
		double expandTop = CalculateWorkspaceExpansion(Math.Max(0, -verticalOffset) / zoom);
		if (expandLeft > 0 || expandTop > 0)
		{
			SetCanvasSize(_canvasWidth + expandLeft, _canvasHeight + expandTop);
			ShiftCanvasContent(expandLeft, expandTop);

			double horizontalShift = expandLeft * zoom;
			double verticalShift = expandTop * zoom;
			_middleButtonPanStartHorizontalOffset += horizontalShift;
			_middleButtonPanStartVerticalOffset += verticalShift;
			horizontalOffset += horizontalShift;
			verticalOffset += verticalShift;
			resized = true;
		}

		double viewportWidth = GetScrollViewerViewportWidth();
		double viewportHeight = GetScrollViewerViewportHeight();
		double expandRight = CalculateWorkspaceExpansion(Math.Max(0, horizontalOffset + viewportWidth - _canvasWidth * zoom) / zoom);
		double expandBottom = CalculateWorkspaceExpansion(Math.Max(0, verticalOffset + viewportHeight - _canvasHeight * zoom) / zoom);
		if (expandRight > 0 || expandBottom > 0)
		{
			SetCanvasSize(_canvasWidth + expandRight, _canvasHeight + expandBottom);
			resized = true;
		}

		if (resized)
		{
			_scrollViewer.UpdateLayout();
		}
	}

	private double GetScrollViewerViewportWidth()
	{
		return GetPositiveFiniteValue(_scrollViewer.ViewportWidth, _scrollViewer.ActualWidth);
	}

	private double GetScrollViewerViewportHeight()
	{
		return GetPositiveFiniteValue(_scrollViewer.ViewportHeight, _scrollViewer.ActualHeight);
	}

	private static double GetPositiveFiniteValue(double primary, double fallback)
	{
		if (double.IsFinite(primary) && primary > 0)
		{
			return primary;
		}

		return double.IsFinite(fallback) && fallback > 0 ? fallback : 0;
	}

	private void EndMiddleButtonPan(bool releaseCapture)
	{
		_isMiddleButtonPanning = false;
		Mouse.OverrideCursor = _previousOverrideCursor;
		_previousOverrideCursor = null;
		if (releaseCapture && _scrollViewer.IsMouseCaptured)
		{
			_scrollViewer.ReleaseMouseCapture();
		}
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

	private void ScrollToInitialWorkspacePosition()
	{
		_scrollViewer.ScrollToHorizontalOffset(WorkspaceInitialMargin * _zoomTransform.ScaleX);
		_scrollViewer.ScrollToVerticalOffset(WorkspaceInitialMargin * _zoomTransform.ScaleY);
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
		_dragStartPoint = e.GetPosition(_overlayCanvas);
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

		UpdateDragElement(e.GetPosition(_overlayCanvas));
		e.Handled = true;
	}

	private void OverlayCanvas_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
	{
		if (_dragElement is not { })
		{
			return;
		}

		UpdateDragElement(e.GetPosition(_overlayCanvas));
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
		CompleteActiveDrag(focusTextInput: false);
		if (requireChanges && !_hasPaintChanges)
		{
			return false;
		}

		ClipboardManager.CopyImageToClipboard(RenderPaintedImage());
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
		MarkPaintChanged();
		FocusCurrentEditableElement();
		TrimWorkspaceToContent();
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
		EnsureCanvasContainsElement(element);
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
		TrimWorkspaceToContent();
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
		if (sender is not PaintRectangle paintRectangle)
		{
			return;
		}

		EnsureCanvasContains(paintRectangle.RenderBounds);
		if (_completedElements.Contains(paintRectangle))
		{
			UpdateOutlineNumberLabels();
			MarkPaintChanged();
		}

		TrimWorkspaceToContentIfIdle();
	}

	private void ArrowTextRectangle_Changed(object? sender, EventArgs e)
	{
		if (sender is not ArrowTextRectangle arrowTextRectangle)
		{
			return;
		}

		EnsureCanvasContains(arrowTextRectangle.RenderBounds);
		if (_completedElements.Contains(arrowTextRectangle))
		{
			MarkPaintChanged();
		}

		TrimWorkspaceToContentIfIdle();
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
	}

	// 画像の移動やリサイズも編集とみなし、閉じる際にクリップボードへ保存されるようにする。
	private void MarkImagePlacementChanged()
	{
		_hasPaintChanges = true;
	}

	private void PlacedImage_Changed(object? sender, EventArgs e)
	{
		if (sender is PlacedImage placedImage)
		{
			EnsureCanvasContains(placedImage.Bounds);
		}

		MarkImagePlacementChanged();
		TrimWorkspaceToContentIfIdle();
	}

	private void EnsureCanvasContainsElement(UIElement element)
	{
		if (TryGetRenderableElementBounds(element, out Rect bounds))
		{
			EnsureCanvasContains(bounds);
		}
	}

	private void EnsureCanvasContains(Rect bounds)
	{
		if (!IsUsableBounds(bounds))
		{
			return;
		}

		double expandRight = CalculateWorkspaceExpansion(Math.Max(0, bounds.Right - _canvasWidth));
		double expandBottom = CalculateWorkspaceExpansion(Math.Max(0, bounds.Bottom - _canvasHeight));
		if (expandRight <= 0 && expandBottom <= 0)
		{
			return;
		}

		SetCanvasSize(_canvasWidth + expandRight, _canvasHeight + expandBottom);
	}

	private static double CalculateWorkspaceExpansion(double overflow)
	{
		if (overflow <= 0)
		{
			return 0;
		}

		return Math.Ceiling(overflow / WorkspaceExpansionChunk) * WorkspaceExpansionChunk;
	}

	private static bool IsUsableBounds(Rect bounds)
	{
		return !bounds.IsEmpty &&
			double.IsFinite(bounds.Left) &&
			double.IsFinite(bounds.Top) &&
			double.IsFinite(bounds.Right) &&
			double.IsFinite(bounds.Bottom);
	}

	private void ShiftCanvasContent(double offsetX, double offsetY)
	{
		OffsetCanvasChild(_sourceImageControl, offsetX, offsetY);

		foreach (PlacedImage placedImage in _imageLayerCanvas.Children.OfType<PlacedImage>())
		{
			placedImage.ShiftContent(offsetX, offsetY);
		}

		foreach (UIElement element in _completedElements)
		{
			ShiftPaintElementContent(element, offsetX, offsetY);
		}

		foreach (UIElement element in _redoElements)
		{
			ShiftPaintElementContent(element, offsetX, offsetY);
		}

		if (_dragElement != null)
		{
			ShiftPaintElementContent(_dragElement, offsetX, offsetY);
			_dragStartPoint = new Point(_dragStartPoint.X + offsetX, _dragStartPoint.Y + offsetY);
		}

		foreach (TextBlock label in _outlineNumberLabels.Values)
		{
			OffsetCanvasChild(label, offsetX, offsetY);
		}
	}

	private static void ShiftPaintElementContent(UIElement element, double offsetX, double offsetY)
	{
		switch (element)
		{
			case PaintRectangle paintRectangle:
				paintRectangle.ShiftContent(offsetX, offsetY);
				break;
			case ArrowTextRectangle arrowTextRectangle:
				arrowTextRectangle.ShiftContent(offsetX, offsetY);
				break;
		}
	}

	private static void OffsetCanvasChild(UIElement element, double offsetX, double offsetY)
	{
		double left = Canvas.GetLeft(element);
		double top = Canvas.GetTop(element);
		Canvas.SetLeft(element, (double.IsNaN(left) ? 0 : left) + offsetX);
		Canvas.SetTop(element, (double.IsNaN(top) ? 0 : top) + offsetY);
	}

	private void TrimWorkspaceToContentIfIdle()
	{
		if (Mouse.LeftButton == MouseButtonState.Released)
		{
			TrimWorkspaceToContent();
		}
	}

	private void TrimWorkspaceToContent()
	{
		Rect bounds = CalculateRenderBounds();
		double minWidth = _sourceImage.PixelWidth + WorkspaceInitialMargin * 2;
		double minHeight = _sourceImage.PixelHeight + WorkspaceInitialMargin * 2;
		double targetWidth = Math.Max(minWidth, Math.Ceiling(bounds.Right + WorkspaceInitialMargin));
		double targetHeight = Math.Max(minHeight, Math.Ceiling(bounds.Bottom + WorkspaceInitialMargin));
		if (targetWidth < _canvasWidth || targetHeight < _canvasHeight)
		{
			SetCanvasSize(Math.Min(_canvasWidth, targetWidth), Math.Min(_canvasHeight, targetHeight));
		}
	}

	private void SetCanvasSize(double width, double height)
	{
		double roundedWidth = Math.Max(1, Math.Ceiling(width));
		double roundedHeight = Math.Max(1, Math.Ceiling(height));
		if (Math.Abs(_canvasWidth - roundedWidth) < 0.001 && Math.Abs(_canvasHeight - roundedHeight) < 0.001)
		{
			return;
		}

		_canvasWidth = roundedWidth;
		_canvasHeight = roundedHeight;
		_paintSurface.Width = roundedWidth;
		_paintSurface.Height = roundedHeight;
		_zoomContainer.Width = roundedWidth;
		_zoomContainer.Height = roundedHeight;
		_baseImageCanvas.Width = roundedWidth;
		_baseImageCanvas.Height = roundedHeight;
		_imageLayerCanvas.Width = roundedWidth;
		_imageLayerCanvas.Height = roundedHeight;
		_overlayCanvas.Width = roundedWidth;
		_overlayCanvas.Height = roundedHeight;

		foreach (PlacedImage placedImage in _imageLayerCanvas.Children.OfType<PlacedImage>())
		{
			placedImage.SetCanvasSize(roundedWidth, roundedHeight);
		}

		foreach (UIElement element in _completedElements)
		{
			SetPaintElementCanvasSize(element, roundedWidth, roundedHeight);
		}

		foreach (UIElement element in _redoElements)
		{
			SetPaintElementCanvasSize(element, roundedWidth, roundedHeight);
		}

		if (_dragElement != null)
		{
			SetPaintElementCanvasSize(_dragElement, roundedWidth, roundedHeight);
		}
	}

	private static void SetPaintElementCanvasSize(UIElement element, double width, double height)
	{
		switch (element)
		{
			case PaintRectangle paintRectangle:
				paintRectangle.SetCanvasSize(width, height);
				break;
			case ArrowTextRectangle arrowTextRectangle:
				arrowTextRectangle.SetCanvasSize(width, height);
				break;
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
			MarkPaintChanged();
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
		}
		else
		{
			_overlayCanvas.Children.Remove(element);
		}

		_dragElement = null;
		TrimWorkspaceToContent();
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

	private BitmapSource RenderPaintedImage()
	{
		PrepareTextInputsForRender();
		Rect renderBounds = CalculateRenderBounds();
		SetPlacedImagesEditingChromeVisible(false);
		try
		{
			int renderWidth = Math.Max(1, (int)Math.Round(renderBounds.Width));
			int renderHeight = Math.Max(1, (int)Math.Round(renderBounds.Height));
			_paintSurface.Measure(new Size(_canvasWidth, _canvasHeight));
			_paintSurface.Arrange(new Rect(0, 0, _canvasWidth, _canvasHeight));
			_paintSurface.UpdateLayout();

			var bitmap = new RenderTargetBitmap(
				renderWidth,
				renderHeight,
				96,
				96,
				PixelFormats.Pbgra32);
			RenderCroppedPaintSurface(bitmap, renderBounds, renderWidth, renderHeight);
			bitmap.Freeze();
			return bitmap;
		}
		finally
		{
			SetPlacedImagesEditingChromeVisible(true);
		}
	}

	private Rect CalculateRenderBounds()
	{
		Rect bounds = Rect.Empty;
		if (!_isMultiImageMode)
		{
			UnionBounds(ref bounds, GetSourceImageBounds());
		}

		foreach (PlacedImage placedImage in _imageLayerCanvas.Children.OfType<PlacedImage>())
		{
			UnionBounds(ref bounds, placedImage.Bounds);
		}

		foreach (UIElement element in _completedElements)
		{
			if (TryGetRenderableElementBounds(element, out Rect elementBounds))
			{
				UnionBounds(ref bounds, elementBounds);
			}
		}

		foreach (TextBlock label in _outlineNumberLabels.Values)
		{
			UnionBounds(ref bounds, GetTextBlockBounds(label));
		}

		if (bounds.IsEmpty)
		{
			bounds = new Rect(0, 0, Math.Max(1, _sourceImage.PixelWidth), Math.Max(1, _sourceImage.PixelHeight));
		}

		return RoundRenderBounds(bounds);
	}

	private void RenderCroppedPaintSurface(RenderTargetBitmap bitmap, Rect renderBounds, int width, int height)
	{
		var paintSurfaceBrush = new VisualBrush(_paintSurface)
		{
			Viewbox = renderBounds,
			ViewboxUnits = BrushMappingMode.Absolute,
			Viewport = new Rect(0, 0, width, height),
			ViewportUnits = BrushMappingMode.Absolute,
			Stretch = Stretch.Fill,
			TileMode = TileMode.None
		};

		var drawingVisual = new DrawingVisual();
		using (DrawingContext drawingContext = drawingVisual.RenderOpen())
		{
			drawingContext.DrawRectangle(Brushes.White, null, new Rect(0, 0, width, height));
			drawingContext.DrawRectangle(paintSurfaceBrush, null, new Rect(0, 0, width, height));
		}

		bitmap.Render(drawingVisual);
	}

	private Rect GetSourceImageBounds()
	{
		return GetCanvasChildBounds(_sourceImageControl, _sourceImageControl.Width, _sourceImageControl.Height);
	}

	private static bool TryGetRenderableElementBounds(UIElement element, out Rect bounds)
	{
		switch (element)
		{
			case PaintRectangle paintRectangle:
				bounds = paintRectangle.RenderBounds;
				return IsUsableBounds(bounds);
			case ArrowTextRectangle arrowTextRectangle:
				bounds = arrowTextRectangle.RenderBounds;
				return IsUsableBounds(bounds);
			default:
				bounds = Rect.Empty;
				return false;
		}
	}

	private static Rect GetTextBlockBounds(TextBlock textBlock)
	{
		textBlock.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
		Size size = textBlock.DesiredSize;
		return GetCanvasChildBounds(textBlock, size.Width, size.Height);
	}

	private static Rect GetCanvasChildBounds(UIElement element, double width, double height)
	{
		double left = Canvas.GetLeft(element);
		double top = Canvas.GetTop(element);
		return new Rect(
			double.IsNaN(left) ? 0 : left,
			double.IsNaN(top) ? 0 : top,
			Math.Max(0, width),
			Math.Max(0, height));
	}

	private static void UnionBounds(ref Rect bounds, Rect addition)
	{
		if (!IsUsableBounds(addition))
		{
			return;
		}

		if (bounds.IsEmpty)
		{
			bounds = addition;
			return;
		}

		bounds.Union(addition);
	}

	private static Rect RoundRenderBounds(Rect bounds)
	{
		bounds.Inflate(RenderBoundsPadding, RenderBoundsPadding);
		double left = Math.Floor(bounds.Left);
		double top = Math.Floor(bounds.Top);
		double right = Math.Ceiling(bounds.Right);
		double bottom = Math.Ceiling(bounds.Bottom);
		return new Rect(left, top, Math.Max(1, right - left), Math.Max(1, bottom - top));
	}

	private void SetPlacedImagesEditingChromeVisible(bool visible)
	{
		foreach (PlacedImage placedImage in _imageLayerCanvas.Children.OfType<PlacedImage>())
		{
			placedImage.SetEditingChromeVisible(visible);
		}
	}

	protected override void OnClosing(CancelEventArgs e)
	{
		if (_isMiddleButtonPanning)
		{
			EndMiddleButtonPan(releaseCapture: true);
		}

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

		base.OnClosing(e);
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
		if (_moveImageButton != null)
		{
			UpdateModeButton(_moveImageButton, mode == PaintMode.MoveImage);
		}

		UpdateModeButton(_blackFillButton, mode == PaintMode.BlackFillRectangle);
		UpdateModeButton(_redOutlineButton, mode == PaintMode.RedOutlineRectangle);
		UpdateModeButton(_arrowTextButton, mode == PaintMode.ArrowTextRectangle);

		// 画像移動モードでは画像レイヤーを操作可能にし、描画用オーバーレイのヒットテストを止める。
		// 描画モードではその逆にして、画像の上から矩形や矢印を描けるようにする。
		bool isMoveImageMode = mode == PaintMode.MoveImage;
		_imageLayerCanvas.IsHitTestVisible = isMoveImageMode;
		_overlayCanvas.IsHitTestVisible = !isMoveImageMode;
		_overlayCanvas.Cursor = isMoveImageMode ? Cursors.Arrow : Cursors.Cross;
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
		if (restoreInvalidValue)
		{
			UpdateStrokeThicknessTextBox(strokeThickness);
		}

		MarkPaintChanged();
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
		if (restoreInvalidValue)
		{
			UpdateFontSizeTextBox(fontSize);
		}

		MarkPaintChanged();
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

	private static Canvas CreateMoveImageIcon()
	{
		var icon = new Canvas
		{
			Width = 16,
			Height = 16
		};

		var moveArrows = new Polyline
		{
			Stroke = ArrowBrush,
			StrokeThickness = 1.4,
			StrokeLineJoin = PenLineJoin.Round,
			Fill = Brushes.Transparent,
			Points = new PointCollection
			{
				new(8, 0), new(5.5, 2.5), new(10.5, 2.5), new(8, 0),
				new(8, 16), new(5.5, 13.5), new(10.5, 13.5), new(8, 16),
				new(8, 8),
				new(0, 8), new(2.5, 5.5), new(2.5, 10.5), new(0, 8),
				new(16, 8), new(13.5, 5.5), new(13.5, 10.5), new(16, 8)
			}
		};
		icon.Children.Add(moveArrows);
		return icon;
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

	private sealed class PaintRectangle : Canvas
	{
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

		public Rect RenderBounds
		{
			get
			{
				if (_bounds.IsEmpty)
				{
					return Rect.Empty;
				}

				Rect bounds = _bounds;
				if (_isOutline)
				{
					bounds.Inflate(_rectangle.StrokeThickness / 2, _rectangle.StrokeThickness / 2);
				}

				return bounds;
			}
		}

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
			BoundsChanged?.Invoke(this, EventArgs.Empty);
		}

		public void SetCanvasSize(double canvasWidth, double canvasHeight)
		{
			Width = canvasWidth;
			Height = canvasHeight;
		}

		public void ShiftContent(double offsetX, double offsetY)
		{
			var offset = new Vector(offsetX, offsetY);
			SetBounds(ShiftRect(_bounds, offsetX, offsetY), notifyChanged: false);
			_moveDragStartPoint += offset;
			_moveDragStartBounds = ShiftRect(_moveDragStartBounds, offsetX, offsetY);
			_resizeDragStartPoint += offset;
			_resizeDragStartBounds = ShiftRect(_resizeDragStartBounds, offsetX, offsetY);
		}

		private void SetBounds(Rect bounds, bool notifyChanged = true)
		{
			_bounds = bounds;
			Canvas.SetLeft(_rectangle, bounds.Left);
			Canvas.SetTop(_rectangle, bounds.Top);
			_rectangle.Width = bounds.Width;
			_rectangle.Height = bounds.Height;
			PositionResizeHandles(bounds);
			if (notifyChanged)
			{
				BoundsChanged?.Invoke(this, EventArgs.Empty);
			}
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
			SetBounds(movedBounds);
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
			double minWidth = MinPaintRectangleWidth;
			double minHeight = MinPaintRectangleHeight;

			if (_activeResizeHandle is ResizeHandleKind.TopLeft or ResizeHandleKind.BottomLeft)
			{
				left = Math.Min(left + offset.X, right - minWidth);
			}
			else
			{
				right = Math.Max(right + offset.X, left + minWidth);
			}

			if (_activeResizeHandle is ResizeHandleKind.TopLeft or ResizeHandleKind.TopRight)
			{
				top = Math.Min(top + offset.Y, bottom - minHeight);
			}
			else
			{
				bottom = Math.Max(bottom + offset.Y, top + minHeight);
			}

			SetBounds(new Rect(left, top, right - left, bottom - top));
		}

		private static Rect ShiftRect(Rect bounds, double offsetX, double offsetY)
		{
			if (bounds.IsEmpty)
			{
				return bounds;
			}

			bounds.Offset(offsetX, offsetY);
			return bounds;
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
		private double _canvasWidth;
		private double _canvasHeight;
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

		public Rect RenderBounds
		{
			get
			{
				if (_textRectangleBounds.IsEmpty)
				{
					return Rect.Empty;
				}

				Rect bounds = _textRectangleBounds;
				bounds.Union(_arrowTip);
				double arrowInset = Math.Max(_arrowHeadLength, _strokeThickness);
				bounds.Inflate(arrowInset, arrowInset);
				return bounds;
			}
		}

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
			SetTextRectangleBounds(new Rect(left, top, width, height), notifyChanged: false);

			if (!_isArrowTipCustomized)
			{
				_arrowTip = CalculateDefaultArrowTip(_textRectangleBounds);
			}

			UpdateArrow(_textRectangleBounds);
			Changed?.Invoke(this, EventArgs.Empty);
		}

		private void SetTextRectangleBounds(Rect bounds, bool notifyChanged = true)
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
			if (notifyChanged)
			{
				Changed?.Invoke(this, EventArgs.Empty);
			}
		}

		public void FocusTextInput()
		{
			_textBox.Focus();
			_textBox.CaretIndex = _textBox.Text.Length;
		}

		public void SetTextFontSize(double fontSize)
		{
			_textBox.FontSize = fontSize;
		}

		public void SetCanvasSize(double canvasWidth, double canvasHeight)
		{
			_canvasWidth = canvasWidth;
			_canvasHeight = canvasHeight;
			Width = canvasWidth;
			Height = canvasHeight;
		}

		public void ShiftContent(double offsetX, double offsetY)
		{
			var offset = new Vector(offsetX, offsetY);
			SetTextRectangleBounds(ShiftRect(_textRectangleBounds, offsetX, offsetY), notifyChanged: false);
			_arrowDragStartPoint += offset;
			_arrowTip += offset;
			_arrowDragStartTip += offset;
			_rectangleDragStartPoint += offset;
			_rectangleDragStartBounds = ShiftRect(_rectangleDragStartBounds, offsetX, offsetY);
			_resizeDragStartPoint += offset;
			_resizeDragStartBounds = ShiftRect(_resizeDragStartBounds, offsetX, offsetY);
			UpdateArrow(_textRectangleBounds);
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
				SetTextRectangleBounds(_textRectangleBounds, notifyChanged: false);
				UpdateArrow(_textRectangleBounds);
				Changed?.Invoke(this, EventArgs.Empty);
			}
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
			SetTextRectangleBounds(movedBounds, notifyChanged: false);
			UpdateArrow(_textRectangleBounds);
			Changed?.Invoke(this, EventArgs.Empty);
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
			double minWidth = MinArrowTextRectangleWidth;
			double minHeight = MinArrowTextRectangleHeight;

			if (_activeResizeHandle is ResizeHandleKind.TopLeft or ResizeHandleKind.BottomLeft)
			{
				left = Math.Min(left + offset.X, right - minWidth);
			}
			else
			{
				right = Math.Max(right + offset.X, left + minWidth);
			}

			if (_activeResizeHandle is ResizeHandleKind.TopLeft or ResizeHandleKind.TopRight)
			{
				top = Math.Min(top + offset.Y, bottom - minHeight);
			}
			else
			{
				bottom = Math.Max(bottom + offset.Y, top + minHeight);
			}

			SetTextRectangleBounds(new Rect(left, top, right - left, bottom - top), notifyChanged: false);
			UpdateArrow(_textRectangleBounds);
			Changed?.Invoke(this, EventArgs.Empty);
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
			}

			MoveArrowTip(tipPoint);
		}

		private void MoveArrowTip(Point point)
		{
			_arrowTip = point;
			UpdateArrow(_textRectangleBounds);
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
			Point tipPoint = _arrowTip;
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

		private static Rect ShiftRect(Rect bounds, double offsetX, double offsetY)
		{
			if (bounds.IsEmpty)
			{
				return bounds;
			}

			bounds.Offset(offsetX, offsetY);
			return bounds;
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

	private sealed class PlacedImage : Canvas
	{
		private readonly Image _image;
		private readonly Border _selectionBorder;
		private readonly Dictionary<ResizeHandleKind, Rectangle> _resizeHandles = new();
		private Rect _bounds = Rect.Empty;
		private Point _moveDragStartPoint;
		private Rect _moveDragStartBounds = Rect.Empty;
		private Point _resizeDragStartPoint;
		private Rect _resizeDragStartBounds = Rect.Empty;
		private ResizeHandleKind _activeResizeHandle;
		private bool _isMoving;
		private bool _isResizing;

		public PlacedImage(BitmapSource source, double canvasWidth, double canvasHeight, Rect bounds)
		{
			Width = canvasWidth;
			Height = canvasHeight;
			ClipToBounds = false;

			_image = new Image
			{
				Source = source,
				Stretch = Stretch.Fill,
				SnapsToDevicePixels = true,
				Cursor = Cursors.SizeAll
			};
			RenderOptions.SetBitmapScalingMode(_image, BitmapScalingMode.HighQuality);
			_image.MouseLeftButtonDown += Image_MouseLeftButtonDown;
			_image.MouseMove += Image_MouseMove;
			_image.MouseLeftButtonUp += Image_MouseLeftButtonUp;

			_selectionBorder = new Border
			{
				BorderBrush = ArrowBrush,
				BorderThickness = new Thickness(1),
				Background = Brushes.Transparent,
				IsHitTestVisible = false
			};

			Children.Add(_image);
			Children.Add(_selectionBorder);
			foreach (ResizeHandleKind resizeHandleKind in Enum.GetValues<ResizeHandleKind>())
			{
				Rectangle resizeHandle = CreateResizeHandle(resizeHandleKind);
				_resizeHandles.Add(resizeHandleKind, resizeHandle);
				Children.Add(resizeHandle);
			}

			SetBounds(bounds);
		}

		public event EventHandler? Changed;

		public Rect Bounds => _bounds;

		// レンダリング時に選択枠やリサイズハンドルを画像へ焼き込まないよう、編集用の装飾を一時的に隠す。
		public void SetEditingChromeVisible(bool visible)
		{
			Visibility chromeVisibility = visible ? Visibility.Visible : Visibility.Collapsed;
			_selectionBorder.Visibility = chromeVisibility;
			foreach (Rectangle resizeHandle in _resizeHandles.Values)
			{
				resizeHandle.Visibility = chromeVisibility;
			}
		}

		public void SetCanvasSize(double canvasWidth, double canvasHeight)
		{
			Width = canvasWidth;
			Height = canvasHeight;
		}

		public void ShiftContent(double offsetX, double offsetY)
		{
			var offset = new Vector(offsetX, offsetY);
			SetBounds(ShiftRect(_bounds, offsetX, offsetY), notifyChanged: false);
			_moveDragStartPoint += offset;
			_moveDragStartBounds = ShiftRect(_moveDragStartBounds, offsetX, offsetY);
			_resizeDragStartPoint += offset;
			_resizeDragStartBounds = ShiftRect(_resizeDragStartBounds, offsetX, offsetY);
		}

		private void SetBounds(Rect bounds, bool notifyChanged = true)
		{
			_bounds = bounds;
			Canvas.SetLeft(_image, bounds.Left);
			Canvas.SetTop(_image, bounds.Top);
			_image.Width = bounds.Width;
			_image.Height = bounds.Height;

			Canvas.SetLeft(_selectionBorder, bounds.Left);
			Canvas.SetTop(_selectionBorder, bounds.Top);
			_selectionBorder.Width = bounds.Width;
			_selectionBorder.Height = bounds.Height;

			PositionResizeHandles(bounds);
			if (notifyChanged)
			{
				Changed?.Invoke(this, EventArgs.Empty);
			}
		}

		private void Image_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
		{
			_isMoving = true;
			_moveDragStartPoint = e.GetPosition(this);
			_moveDragStartBounds = _bounds;
			_image.CaptureMouse();
			e.Handled = true;
		}

		private void Image_MouseMove(object sender, MouseEventArgs e)
		{
			if (!_isMoving)
			{
				return;
			}

			MoveImage(e.GetPosition(this) - _moveDragStartPoint);
			e.Handled = true;
		}

		private void Image_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
		{
			if (!_isMoving)
			{
				return;
			}

			MoveImage(e.GetPosition(this) - _moveDragStartPoint);
			_isMoving = false;
			_image.ReleaseMouseCapture();
			e.Handled = true;
		}

		private void MoveImage(Vector offset)
		{
			Rect movedBounds = _moveDragStartBounds;
			movedBounds.Offset(offset.X, offset.Y);
			SetBounds(movedBounds);
		}

		private Rectangle CreateResizeHandle(ResizeHandleKind resizeHandleKind)
		{
			var resizeHandle = new Rectangle
			{
				Width = PlacedImageResizeHandleSize,
				Height = PlacedImageResizeHandleSize,
				Fill = Brushes.White,
				Stroke = ArrowBrush,
				StrokeThickness = 1.5,
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

			Canvas.SetLeft(resizeHandle, x - PlacedImageResizeHandleSize / 2);
			Canvas.SetTop(resizeHandle, y - PlacedImageResizeHandleSize / 2);
		}

		private void ResizeHandle_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
		{
			if (sender is not Rectangle resizeHandle || resizeHandle.Tag is not ResizeHandleKind resizeHandleKind)
			{
				return;
			}

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

			ResizeImage(e.GetPosition(this) - _resizeDragStartPoint);
			e.Handled = true;
		}

		private void ResizeHandle_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
		{
			if (!_isResizing)
			{
				return;
			}

			ResizeImage(e.GetPosition(this) - _resizeDragStartPoint);
			_isResizing = false;
			if (sender is Rectangle resizeHandle)
			{
				resizeHandle.ReleaseMouseCapture();
			}

			e.Handled = true;
		}

		private void ResizeImage(Vector offset)
		{
			if (_resizeDragStartBounds.IsEmpty)
			{
				return;
			}

			double left = _resizeDragStartBounds.Left;
			double top = _resizeDragStartBounds.Top;
			double right = _resizeDragStartBounds.Right;
			double bottom = _resizeDragStartBounds.Bottom;
			double minSize = MultiImageMinPlacedSize;

			if (_activeResizeHandle is ResizeHandleKind.TopLeft or ResizeHandleKind.BottomLeft)
			{
				left = Math.Min(left + offset.X, right - minSize);
			}
			else
			{
				right = Math.Max(right + offset.X, left + minSize);
			}

			if (_activeResizeHandle is ResizeHandleKind.TopLeft or ResizeHandleKind.TopRight)
			{
				top = Math.Min(top + offset.Y, bottom - minSize);
			}
			else
			{
				bottom = Math.Max(bottom + offset.Y, top + minSize);
			}

			SetBounds(new Rect(left, top, right - left, bottom - top));
		}

		private static Rect ShiftRect(Rect bounds, double offsetX, double offsetY)
		{
			if (bounds.IsEmpty)
			{
				return bounds;
			}

			bounds.Offset(offsetX, offsetY);
			return bounds;
		}

		private enum ResizeHandleKind
		{
			TopLeft,
			TopRight,
			BottomLeft,
			BottomRight
		}
	}

	private static MultiImageLayout CalculateMultiImageLayout(IReadOnlyList<BitmapSource> images)
	{
		// 作業領域に収まる幅へ全画像を合わせる。大きい画像は縮小、小さい画像も同じ幅へ拡大して縦並びを揃える。
		double maxContentWidth = Math.Max(MultiImageMinPlacedSize, SystemParameters.WorkArea.Width - 160);
		double maxSourceWidth = images.Max(image => (double)image.PixelWidth);
		double contentWidth = Math.Min(maxContentWidth, maxSourceWidth);

		var placedImages = new List<PlacedImageLayout>(images.Count);
		double currentTop = MultiImageOuterMargin;
		double referenceHeight = 0;
		foreach (BitmapSource image in images)
		{
			double width = contentWidth;
			double aspectRatio = image.PixelHeight / (double)image.PixelWidth;
			double height = Math.Max(MultiImageMinPlacedSize, width * aspectRatio);
			var bounds = new Rect(MultiImageOuterMargin, currentTop, width, height);
			placedImages.Add(new PlacedImageLayout(image, bounds));
			currentTop += height + MultiImageGap;
			referenceHeight = Math.Max(referenceHeight, height);
		}

		double canvasWidth = contentWidth + MultiImageOuterMargin * 2;
		double canvasHeight = currentTop - MultiImageGap + MultiImageOuterMargin;
		return new MultiImageLayout(
			Math.Max(1, Math.Round(canvasWidth)),
			Math.Max(1, Math.Round(canvasHeight)),
			referenceHeight,
			placedImages);
	}

	private static BitmapSource CreateWhiteCanvasImage(double width, double height)
	{
		int pixelWidth = Math.Max(1, (int)Math.Round(width));
		int pixelHeight = Math.Max(1, (int)Math.Round(height));
		var drawingVisual = new DrawingVisual();
		using (DrawingContext drawingContext = drawingVisual.RenderOpen())
		{
			drawingContext.DrawRectangle(Brushes.White, null, new Rect(0, 0, pixelWidth, pixelHeight));
		}

		var bitmap = new RenderTargetBitmap(pixelWidth, pixelHeight, 96, 96, PixelFormats.Pbgra32);
		bitmap.Render(drawingVisual);
		bitmap.Freeze();
		return bitmap;
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
		MoveImage,
		BlackFillRectangle,
		RedOutlineRectangle,
		ArrowTextRectangle
	}

	private sealed record MultiImageLayout(
		double CanvasWidth,
		double CanvasHeight,
		double ReferenceHeight,
		IReadOnlyList<PlacedImageLayout> PlacedImages);

	private sealed record PlacedImageLayout(BitmapSource Image, Rect Bounds);
}
