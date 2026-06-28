using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Rectangle = System.Windows.Shapes.Rectangle;
using static Clipboard.ImagePaintBounds;
using static Clipboard.ImagePaintImageFactory;
using static Clipboard.ImagePaintMetrics;
using static Clipboard.ImagePaintSizeValue;
using static Clipboard.ImagePaintToolbarFactory;
using static Clipboard.MultiImageLayoutCalculator;

namespace Clipboard;

internal sealed class ImagePaintWindow : Window
{
	private readonly BitmapSource _sourceImage;
	private readonly ImagePaintEditorState _state = new();
	private readonly ScaleTransform _zoomTransform = new(1, 1);
	private Grid _zoomContainer = null!;
	private Rectangle _workspaceGridBackground = null!;
	private Grid _paintSurface = null!;
	private Canvas _imageLayerCanvas = null!;
	private Canvas _overlayCanvas = null!;
	private ImagePaintOutlineNumberLabelManager _outlineNumberLabelManager = null!;
	private ImagePaintRenderer _renderer = null!;
	private ScrollViewer _scrollViewer = null!;
	private Button _moveImageButton = null!;
	private Button _blackFillButton = null!;
	private Button _redOutlineButton = null!;
	private Button _arrowTextButton = null!;
	private TextBox _strokeThicknessTextBox = null!;
	private TextBox _fontSizeTextBox = null!;
	private CheckBox _outlineNumberCheckBox = null!;

	private double CanvasWidth
	{
		get => _state.Workspace.CanvasWidth;
		set => _state.Workspace.CanvasWidth = value;
	}

	private double CanvasHeight
	{
		get => _state.Workspace.CanvasHeight;
		set => _state.Workspace.CanvasHeight = value;
	}

	public ImagePaintWindow(byte[] imageBytes)
	{
		_sourceImage = LoadImage(imageBytes);
		var placedImage = new PlacedImageLayout(
			_sourceImage,
			new Rect(0, 0, _sourceImage.PixelWidth, _sourceImage.PixelHeight));
		InitializeWindow(_sourceImage.PixelHeight, new[] { placedImage }, PaintMode.RedOutlineRectangle);
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
		InitializeWindow(layout.ReferenceHeight, layout.PlacedImages, PaintMode.MoveImage);
	}

	private void InitializeWindow(
		double referenceHeight,
		IReadOnlyList<PlacedImageLayout> placedImages,
		PaintMode initialPaintMode)
	{
		_state.Tools.PaintMode = initialPaintMode;
		_state.Tools.PaintStrokeThickness = CalculatePaintStrokeThickness(referenceHeight);
		_state.Tools.CurrentArrowTextFontSize = CalculateDefaultArrowTextFontSize(referenceHeight);
		double initialContentWidth = _sourceImage.PixelWidth;
		double initialContentHeight = _sourceImage.PixelHeight;
		CanvasWidth = initialContentWidth + WorkspaceInitialMargin * 2;
		CanvasHeight = initialContentHeight + WorkspaceInitialMargin * 2;
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

		_moveImageButton = CreateModeButton("画像の移動", CreateMoveImageIcon());
		_moveImageButton.Click += (_, _) => SetPaintMode(PaintMode.MoveImage);
		toolbar.Children.Add(_moveImageButton);

		_blackFillButton = CreateModeButton("塗りつぶし", CreateFillIcon());
		_blackFillButton.Margin = new Thickness(8, 0, 0, 0);
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

		_strokeThicknessTextBox = CreateStrokeThicknessTextBox(_state.Tools.PaintStrokeThickness);
		toolbar.Children.Add(CreateToolbarTextEditor("線幅", _strokeThicknessTextBox));

		_fontSizeTextBox = CreateFontSizeTextBox(_state.Tools.CurrentArrowTextFontSize);
		toolbar.Children.Add(CreateToolbarTextEditor("文字サイズ", _fontSizeTextBox));

		_outlineNumberCheckBox = CreateOutlineNumberCheckBox(
			() => SetShowOutlineNumbers(true),
			() => SetShowOutlineNumbers(false));
		toolbar.Children.Add(_outlineNumberCheckBox);

		_imageLayerCanvas = new Canvas
		{
			Width = CanvasWidth,
			Height = CanvasHeight,
			Background = Brushes.Transparent,
			IsHitTestVisible = false
		};
		AddPlacedImages(placedImages);

		_overlayCanvas = new Canvas
		{
			Width = CanvasWidth,
			Height = CanvasHeight,
			Background = Brushes.Transparent,
			Cursor = Cursors.Cross,
			Focusable = true
		};
		_overlayCanvas.MouseLeftButtonDown += OverlayCanvas_MouseLeftButtonDown;
		_overlayCanvas.MouseMove += OverlayCanvas_MouseMove;
		_overlayCanvas.MouseLeftButtonUp += OverlayCanvas_MouseLeftButtonUp;
		_outlineNumberLabelManager = new ImagePaintOutlineNumberLabelManager(
			_overlayCanvas,
			_state.History,
			_state.OutlineNumberLabels,
			() => _state.Tools.CurrentArrowTextFontSize);

		_paintSurface = new Grid
		{
			Width = CanvasWidth,
			Height = CanvasHeight,
			Background = Brushes.Transparent,
			ClipToBounds = false,
			// 作業領域拡張時にズーム済みコンテンツが中央寄せで再配置されないよう左上に固定する。
			HorizontalAlignment = HorizontalAlignment.Left,
			VerticalAlignment = VerticalAlignment.Top
		};
		_paintSurface.Children.Add(_imageLayerCanvas);
		_paintSurface.Children.Add(_overlayCanvas);
		_renderer = new ImagePaintRenderer(
			_sourceImage,
			_paintSurface,
			_imageLayerCanvas,
			_overlayCanvas,
			_state,
			_outlineNumberLabelManager,
			() => CanvasWidth,
			() => CanvasHeight);

		_zoomContainer = new Grid
		{
			Width = CanvasWidth,
			Height = CanvasHeight,
			LayoutTransform = _zoomTransform,
			HorizontalAlignment = HorizontalAlignment.Left,
			VerticalAlignment = VerticalAlignment.Top
		};
		_workspaceGridBackground = new Rectangle
		{
			Width = CanvasWidth,
			Height = CanvasHeight,
			Fill = CreateWorkspaceGridBrush(),
			IsHitTestVisible = false,
			SnapsToDevicePixels = true,
			HorizontalAlignment = HorizontalAlignment.Left,
			VerticalAlignment = VerticalAlignment.Top
		};
		_zoomContainer.Children.Add(_workspaceGridBackground);
		_zoomContainer.Children.Add(_paintSurface);
		ShiftCanvasContent(WorkspaceInitialMargin, WorkspaceInitialMargin);
		SetZoom(CalculateInitialZoom(windowWidth, windowHeight));

		_scrollViewer = new ScrollViewer
		{
			Content = _zoomContainer,
			HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden,
			VerticalScrollBarVisibility = ScrollBarVisibility.Hidden,
			HorizontalContentAlignment = HorizontalAlignment.Left,
			VerticalContentAlignment = VerticalAlignment.Top
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
		AppTheme.ThemeChanged += AppTheme_ThemeChanged;
		Closed += ImagePaintWindow_Closed;
		AddHandler(Keyboard.PreviewKeyDownEvent, new KeyEventHandler(ImagePaintWindow_PreviewKeyDown), true);
		SetPaintMode(_state.Tools.PaintMode);
	}

	private void AppTheme_ThemeChanged(object? sender, EventArgs e)
	{
		_workspaceGridBackground.Fill = CreateWorkspaceGridBrush();
	}

	private void ImagePaintWindow_Closed(object? sender, EventArgs e)
	{
		AppTheme.ThemeChanged -= AppTheme_ThemeChanged;
	}

	private void AddPlacedImages(IReadOnlyList<PlacedImageLayout> placedImages)
	{
		foreach (PlacedImageLayout placedImageLayout in placedImages)
		{
			var placedImage = new PlacedImage(
				placedImageLayout.Image,
				CanvasWidth,
				CanvasHeight,
				placedImageLayout.Bounds);
			placedImage.Changed += PlacedImage_Changed;
			_imageLayerCanvas.Children.Add(placedImage);
		}
	}

	private void ImagePaintWindow_PreviewKeyDown(object sender, KeyEventArgs e)
	{
		bool isControlPressed = (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control;
		bool isShiftPressed = (Keyboard.Modifiers & ModifierKeys.Shift) == ModifierKeys.Shift;
		Key key = GetInputKey(e);
		TextBox? textInputSource = FindFocusedTextBox(e.OriginalSource);
		bool isTextInputSource = textInputSource != null;
		ArrowTextRectangle? arrowTextInputSource = textInputSource == null
			? null
			: FindVisualParent<ArrowTextRectangle>(textInputSource);

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
			if (arrowTextInputSource != null &&
				ShouldUsePaintHistoryForTextInputUndoRedo(arrowTextInputSource, key, isShiftPressed))
			{
				HandlePaintHistoryShortcut(key, isShiftPressed);
				e.Handled = true;
			}

			return;
		}

		if (isControlPressed && key == Key.Z)
		{
			if (isShiftPressed)
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

	private static TextBox? FindFocusedTextBox(object source)
	{
		if (source is not DependencyObject dependencyObject)
		{
			return null;
		}

		TextBox? textBox = FindVisualParent<TextBox>(dependencyObject);
		return textBox is { IsKeyboardFocusWithin: true } ? textBox : null;
	}

	private static bool ShouldUsePaintHistoryForTextInputUndoRedo(
		ArrowTextRectangle arrowTextRectangle,
		Key key,
		bool isShiftPressed)
	{
		if (!arrowTextRectangle.IsTextInputEmpty)
		{
			return false;
		}

		// 空文字の矢印矩形だけ親の履歴へ逃がす。Backspace/Delete はここに入れない。
		return IsRedoShortcut(key, isShiftPressed)
			? !arrowTextRectangle.CanRedoTextInput
			: !arrowTextRectangle.CanUndoTextInput;
	}

	private static bool IsRedoShortcut(Key key, bool isShiftPressed)
	{
		return key == Key.Y || (key == Key.Z && isShiftPressed);
	}

	private void HandlePaintHistoryShortcut(Key key, bool isShiftPressed)
	{
		if (IsRedoShortcut(key, isShiftPressed))
		{
			Redo();
			return;
		}

		Undo();
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
		if (!_state.MiddleButtonPan.IsPanning)
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
		if (!_state.MiddleButtonPan.IsPanning || e.ChangedButton != MouseButton.Middle)
		{
			return;
		}

		UpdateMiddleButtonPan(e.GetPosition(_scrollViewer));
		EndMiddleButtonPan(releaseCapture: true);
		e.Handled = true;
	}

	private void ScrollViewer_LostMouseCapture(object sender, MouseEventArgs e)
	{
		if (_state.MiddleButtonPan.IsPanning)
		{
			EndMiddleButtonPan(releaseCapture: false);
		}
	}

	private void BeginMiddleButtonPan(Point point)
	{
		if (_state.MiddleButtonPan.IsPanning)
		{
			return;
		}

		_state.MiddleButtonPan.IsPanning = true;
		_state.MiddleButtonPan.StartPoint = point;
		_state.MiddleButtonPan.StartHorizontalOffset = _scrollViewer.HorizontalOffset;
		_state.MiddleButtonPan.StartVerticalOffset = _scrollViewer.VerticalOffset;
		_state.MiddleButtonPan.PreviousOverrideCursor = Mouse.OverrideCursor;
		Mouse.OverrideCursor = Cursors.SizeAll;
		_scrollViewer.CaptureMouse();
	}

	private void UpdateMiddleButtonPan(Point point)
	{
		Vector offset = point - _state.MiddleButtonPan.StartPoint;
		double horizontalOffset = _state.MiddleButtonPan.StartHorizontalOffset - offset.X;
		double verticalOffset = _state.MiddleButtonPan.StartVerticalOffset - offset.Y;
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
			SetCanvasSize(CanvasWidth + expandLeft, CanvasHeight + expandTop);
			ShiftCanvasContent(expandLeft, expandTop);

			double horizontalShift = expandLeft * zoom;
			double verticalShift = expandTop * zoom;
			_state.MiddleButtonPan.StartHorizontalOffset += horizontalShift;
			_state.MiddleButtonPan.StartVerticalOffset += verticalShift;
			horizontalOffset += horizontalShift;
			verticalOffset += verticalShift;
			resized = true;
		}

		double viewportWidth = GetScrollViewerViewportWidth();
		double viewportHeight = GetScrollViewerViewportHeight();
		double expandRight = CalculateWorkspaceExpansion(Math.Max(0, horizontalOffset + viewportWidth - CanvasWidth * zoom) / zoom);
		double expandBottom = CalculateWorkspaceExpansion(Math.Max(0, verticalOffset + viewportHeight - CanvasHeight * zoom) / zoom);
		if (expandRight > 0 || expandBottom > 0)
		{
			SetCanvasSize(CanvasWidth + expandRight, CanvasHeight + expandBottom);
			resized = true;
		}

		if (resized)
		{
			_scrollViewer.UpdateLayout();
		}
	}

	private void EnsureWorkspaceForZoom(
		ref double horizontalOffset,
		ref double verticalOffset,
		double zoom,
		double viewportWidth,
		double viewportHeight)
	{
		if (!double.IsFinite(zoom) || zoom <= 0 ||
			!double.IsFinite(horizontalOffset) ||
			!double.IsFinite(verticalOffset) ||
			viewportWidth <= 0 ||
			viewportHeight <= 0)
		{
			return;
		}

		double expandLeft = CalculateWorkspaceExpansion(Math.Max(0, -horizontalOffset) / zoom);
		double expandTop = CalculateWorkspaceExpansion(Math.Max(0, -verticalOffset) / zoom);
		if (expandLeft > 0 || expandTop > 0)
		{
			SetCanvasSize(CanvasWidth + expandLeft, CanvasHeight + expandTop);
			ShiftCanvasContent(expandLeft, expandTop);
			horizontalOffset += expandLeft * zoom;
			verticalOffset += expandTop * zoom;
		}

		EnsureWorkspaceAllowsScrollOffset(horizontalOffset, verticalOffset, zoom, viewportWidth, viewportHeight);
	}

	private void EnsureWorkspaceAllowsScrollOffset(
		double horizontalOffset,
		double verticalOffset,
		double zoom,
		double viewportWidth,
		double viewportHeight)
	{
		if (!double.IsFinite(zoom) || zoom <= 0 ||
			!double.IsFinite(horizontalOffset) ||
			!double.IsFinite(verticalOffset) ||
			viewportWidth <= 0 ||
			viewportHeight <= 0)
		{
			return;
		}

		double expandRight = CalculateWorkspaceExpansion(Math.Max(0, horizontalOffset + viewportWidth - CanvasWidth * zoom) / zoom);
		double expandBottom = CalculateWorkspaceExpansion(Math.Max(0, verticalOffset + viewportHeight - CanvasHeight * zoom) / zoom);
		if (expandRight > 0 || expandBottom > 0)
		{
			SetCanvasSize(CanvasWidth + expandRight, CanvasHeight + expandBottom);
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
		_state.MiddleButtonPan.IsPanning = false;
		Mouse.OverrideCursor = _state.MiddleButtonPan.PreviousOverrideCursor;
		_state.MiddleButtonPan.PreviousOverrideCursor = null;
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
		double horizontalOffset = imagePoint.X * nextZoom - viewerPoint.X;
		double verticalOffset = imagePoint.Y * nextZoom - viewerPoint.Y;
		EnsureWorkspaceForZoom(
			ref horizontalOffset,
			ref verticalOffset,
			nextZoom,
			GetScrollViewerViewportWidth(),
			GetScrollViewerViewportHeight());
		SetZoom(nextZoom);
		scrollViewer.UpdateLayout();

		scrollViewer.ScrollToHorizontalOffset(ClampScrollOffset(horizontalOffset, scrollViewer.ScrollableWidth));
		scrollViewer.ScrollToVerticalOffset(ClampScrollOffset(verticalOffset, scrollViewer.ScrollableHeight));
	}

	private void SetZoom(double zoom)
	{
		_zoomTransform.ScaleX = zoom;
		_zoomTransform.ScaleY = zoom;
	}

	private void ScrollToInitialWorkspacePosition()
	{
		_scrollViewer.UpdateLayout();
		double zoom = _zoomTransform.ScaleX;
		double viewportWidth = GetScrollViewerViewportWidth();
		double viewportHeight = GetScrollViewerViewportHeight();
		Rect contentBounds = _renderer.CalculateRenderBounds();
		double horizontalOffset = CalculateCenteredScrollOffset(contentBounds.Left, contentBounds.Width, zoom, viewportWidth);
		double verticalOffset = CalculateCenteredScrollOffset(contentBounds.Top, contentBounds.Height, zoom, viewportHeight);
		EnsureWorkspaceForZoom(
			ref horizontalOffset,
			ref verticalOffset,
			zoom,
			viewportWidth,
			viewportHeight);
		_scrollViewer.UpdateLayout();

		_scrollViewer.ScrollToHorizontalOffset(ClampScrollOffset(horizontalOffset, _scrollViewer.ScrollableWidth));
		_scrollViewer.ScrollToVerticalOffset(ClampScrollOffset(verticalOffset, _scrollViewer.ScrollableHeight));
	}

	private static double CalculateCenteredScrollOffset(
		double contentStart,
		double contentLength,
		double zoom,
		double viewportLength)
	{
		if (!double.IsFinite(contentStart) ||
			!double.IsFinite(contentLength) ||
			!double.IsFinite(zoom) ||
			!double.IsFinite(viewportLength) ||
			contentLength <= 0 ||
			zoom <= 0 ||
			viewportLength <= 0)
		{
			return 0;
		}

		return (contentStart + contentLength / 2) * zoom - viewportLength / 2;
	}

	private static double ClampScrollOffset(double offset, double scrollableLength)
	{
		if (!double.IsFinite(offset) || offset <= 0 ||
			!double.IsFinite(scrollableLength) || scrollableLength <= 0)
		{
			return 0;
		}

		return Math.Min(offset, scrollableLength);
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
		_state.Drag.StartPoint = e.GetPosition(_overlayCanvas);
		var element = CreatePaintElement(_state.Tools.PaintMode);
		_state.Drag.Element = element;
		UpdateDragElement(_state.Drag.StartPoint);
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
		if (_state.Drag.Element is not { })
		{
			return;
		}

		UpdateDragElement(e.GetPosition(_overlayCanvas));
		e.Handled = true;
	}

	private void OverlayCanvas_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
	{
		if (_state.Drag.Element is not { })
		{
			return;
		}

		UpdateDragElement(e.GetPosition(_overlayCanvas));
		CompleteActiveDrag(focusTextInput: true);
		e.Handled = true;
	}

	private void UpdateDragElement(Point currentPoint)
	{
		if (_state.Drag.Element is PaintRectangle paintRectangle)
		{
			paintRectangle.Update(_state.Drag.StartPoint, currentPoint);
		}
		else if (_state.Drag.Element is ArrowTextRectangle arrowTextRectangle)
		{
			arrowTextRectangle.Update(_state.Drag.StartPoint, currentPoint);
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
		if (requireChanges && !_state.HasPaintChanges)
		{
			return false;
		}

		ClipboardManager.CopyImageToClipboard(_renderer.RenderPaintedImage());
		return true;
	}

	private void Undo()
	{
		if (_state.Drag.Element != null)
		{
			CancelActiveDrag();
			return;
		}

		if (_state.History.CompletedElements.Count == 0)
		{
			return;
		}

		UIElement element = _state.History.CompletedElements[^1];
		_state.History.CompletedElements.RemoveAt(_state.History.CompletedElements.Count - 1);
		_overlayCanvas.Children.Remove(element);
		_state.History.RedoElements.Add(element);
		if (element is PaintRectangle paintRectangle && ReferenceEquals(_state.Tools.ActivePaintRectangle, paintRectangle))
		{
			SetActivePaintRectangle(null);
		}

		if (element is ArrowTextRectangle arrowTextRectangle && ReferenceEquals(_state.Tools.ActiveArrowTextRectangle, arrowTextRectangle))
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
		if (_state.Drag.Element != null)
		{
			CancelActiveDrag();
			return;
		}

		if (_state.History.RedoElements.Count == 0)
		{
			return;
		}

		UIElement element = _state.History.RedoElements[^1];
		_state.History.RedoElements.RemoveAt(_state.History.RedoElements.Count - 1);
		_overlayCanvas.Children.Add(element);
		_state.History.CompletedElements.Add(element);
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
		if (_state.History.CompletedElements.Contains(paintRectangle))
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
		if (_state.History.CompletedElements.Contains(arrowTextRectangle))
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
		if (_state.History.CompletedElements.Count > 0 && _state.History.CompletedElements[^1] is PaintRectangle paintRectangle)
		{
			SetActivePaintRectangle(paintRectangle);
			FocusPaintSurface();
			return;
		}

		if (_state.History.CompletedElements.Count > 0 && _state.History.CompletedElements[^1] is ArrowTextRectangle arrowTextRectangle)
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
		if (!_state.HasPaintChanges && _state.History.CompletedElements.Count == 0 && _state.Drag.Element == null)
		{
			return;
		}

		_state.HasPaintChanges = true;
	}

	// 画像の移動やリサイズも編集状態として扱う。
	private void MarkImagePlacementChanged()
	{
		_state.HasPaintChanges = true;
	}

	private void PlacedImage_Changed(object? sender, EventArgs e)
	{
		if (sender is PlacedImage placedImage)
		{
			PreserveScrollOffsetIfChanged(() => EnsureCanvasContains(placedImage.Bounds));
		}

		MarkImagePlacementChanged();
		if (sender is not PlacedImage { IsEditing: true })
		{
			PreserveScrollOffset(TrimWorkspaceToContentIfIdle);
		}
	}

	private void EnsureCanvasContainsElement(UIElement element)
	{
		if (TryGetRenderableElementBounds(element, out Rect bounds))
		{
			EnsureCanvasContains(bounds);
		}
	}

	private bool EnsureCanvasContains(Rect bounds)
	{
		if (!IsUsableBounds(bounds))
		{
			return false;
		}

		double expandRight = CalculateWorkspaceExpansion(Math.Max(0, bounds.Right - CanvasWidth));
		double expandBottom = CalculateWorkspaceExpansion(Math.Max(0, bounds.Bottom - CanvasHeight));
		if (expandRight <= 0 && expandBottom <= 0)
		{
			return false;
		}

		SetCanvasSize(CanvasWidth + expandRight, CanvasHeight + expandBottom);
		return true;
	}

	private static double CalculateWorkspaceExpansion(double overflow)
	{
		if (overflow <= 0)
		{
			return 0;
		}

		return Math.Ceiling(overflow / WorkspaceExpansionChunk) * WorkspaceExpansionChunk;
	}

	private void PreserveScrollOffset(Action action)
	{
		double horizontalOffset = _scrollViewer.HorizontalOffset;
		double verticalOffset = _scrollViewer.VerticalOffset;
		action();
		_scrollViewer.UpdateLayout();
		_scrollViewer.ScrollToHorizontalOffset(ClampScrollOffset(horizontalOffset, _scrollViewer.ScrollableWidth));
		_scrollViewer.ScrollToVerticalOffset(ClampScrollOffset(verticalOffset, _scrollViewer.ScrollableHeight));
	}

	private bool PreserveScrollOffsetIfChanged(Func<bool> action)
	{
		double horizontalOffset = _scrollViewer.HorizontalOffset;
		double verticalOffset = _scrollViewer.VerticalOffset;
		bool changedLayout = action();
		if (changedLayout)
		{
			_scrollViewer.UpdateLayout();
			_scrollViewer.ScrollToHorizontalOffset(ClampScrollOffset(horizontalOffset, _scrollViewer.ScrollableWidth));
			_scrollViewer.ScrollToVerticalOffset(ClampScrollOffset(verticalOffset, _scrollViewer.ScrollableHeight));
		}

		return changedLayout;
	}

	private void ShiftCanvasContent(double offsetX, double offsetY)
	{
		foreach (PlacedImage placedImage in _imageLayerCanvas.Children.OfType<PlacedImage>())
		{
			placedImage.ShiftContent(offsetX, offsetY);
		}

		foreach (UIElement element in _state.History.CompletedElements)
		{
			ShiftPaintElementContent(element, offsetX, offsetY);
		}

		foreach (UIElement element in _state.History.RedoElements)
		{
			ShiftPaintElementContent(element, offsetX, offsetY);
		}

		if (_state.Drag.Element != null)
		{
			ShiftPaintElementContent(_state.Drag.Element, offsetX, offsetY);
			_state.Drag.StartPoint = new Point(_state.Drag.StartPoint.X + offsetX, _state.Drag.StartPoint.Y + offsetY);
		}

		_outlineNumberLabelManager.ShiftLabels(offsetX, offsetY);
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

	private void TrimWorkspaceToContentIfIdle()
	{
		if (Mouse.LeftButton == MouseButtonState.Released)
		{
			TrimWorkspaceToContent();
		}
	}

	private void TrimWorkspaceToContent()
	{
		Rect bounds = _renderer.CalculateRenderBounds();
		double minWidth = _sourceImage.PixelWidth + WorkspaceInitialMargin * 2;
		double minHeight = _sourceImage.PixelHeight + WorkspaceInitialMargin * 2;
		double targetWidth = Math.Max(minWidth, Math.Ceiling(bounds.Right + WorkspaceInitialMargin));
		double targetHeight = Math.Max(minHeight, Math.Ceiling(bounds.Bottom + WorkspaceInitialMargin));
		if (targetWidth < CanvasWidth || targetHeight < CanvasHeight)
		{
			SetCanvasSize(Math.Min(CanvasWidth, targetWidth), Math.Min(CanvasHeight, targetHeight));
		}
	}

	private void SetCanvasSize(double width, double height)
	{
		double roundedWidth = Math.Max(1, Math.Ceiling(width));
		double roundedHeight = Math.Max(1, Math.Ceiling(height));
		if (Math.Abs(CanvasWidth - roundedWidth) < 0.001 && Math.Abs(CanvasHeight - roundedHeight) < 0.001)
		{
			return;
		}

		CanvasWidth = roundedWidth;
		CanvasHeight = roundedHeight;
		_paintSurface.Width = roundedWidth;
		_paintSurface.Height = roundedHeight;
		_zoomContainer.Width = roundedWidth;
		_zoomContainer.Height = roundedHeight;
		_workspaceGridBackground.Width = roundedWidth;
		_workspaceGridBackground.Height = roundedHeight;
		_imageLayerCanvas.Width = roundedWidth;
		_imageLayerCanvas.Height = roundedHeight;
		_overlayCanvas.Width = roundedWidth;
		_overlayCanvas.Height = roundedHeight;

		foreach (PlacedImage placedImage in _imageLayerCanvas.Children.OfType<PlacedImage>())
		{
			placedImage.SetCanvasSize(roundedWidth, roundedHeight);
		}

		foreach (UIElement element in _state.History.CompletedElements)
		{
			SetPaintElementCanvasSize(element, roundedWidth, roundedHeight);
		}

		foreach (UIElement element in _state.History.RedoElements)
		{
			SetPaintElementCanvasSize(element, roundedWidth, roundedHeight);
		}

		if (_state.Drag.Element != null)
		{
			SetPaintElementCanvasSize(_state.Drag.Element, roundedWidth, roundedHeight);
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
		if (_state.Drag.Element is not { } element)
		{
			return;
		}

		_overlayCanvas.ReleaseMouseCapture();
		if (IsDrawableElement(element))
		{
			_state.History.CompletedElements.Add(element);
			_state.History.RedoElements.Clear();
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

		_state.Drag.Element = null;
		TrimWorkspaceToContent();
	}

	private void CancelActiveDrag()
	{
		if (_state.Drag.Element is not { } element)
		{
			return;
		}

		_overlayCanvas.ReleaseMouseCapture();
		_overlayCanvas.Children.Remove(element);
		_state.Drag.Element = null;
	}

	private void UpdateOutlineNumberLabels()
	{
		_outlineNumberLabelManager.Update(_state.Tools.ShowOutlineNumbers);
	}

	protected override void OnClosing(CancelEventArgs e)
	{
		if (_state.MiddleButtonPan.IsPanning)
		{
			EndMiddleButtonPan(releaseCapture: true);
		}

		base.OnClosing(e);
	}

	private void SetPaintMode(PaintMode mode)
	{
		_state.Tools.PaintMode = mode;
		UpdateModeButton(_moveImageButton, mode == PaintMode.MoveImage);
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

	private void SetShowOutlineNumbers(bool showOutlineNumbers)
	{
		if (_state.Tools.ShowOutlineNumbers == showOutlineNumbers)
		{
			return;
		}

		_state.Tools.ShowOutlineNumbers = showOutlineNumbers;
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
		if (_state.Tools.IsUpdatingStrokeThicknessTextBox)
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

		_state.Tools.PaintStrokeThickness = strokeThickness;
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
		if (_state.Tools.IsUpdatingFontSizeTextBox)
		{
			return true;
		}

		if (!TryParseFontSize(_fontSizeTextBox.Text, out double fontSize))
		{
			if (restoreInvalidValue)
			{
				UpdateFontSizeTextBox(_state.Tools.ActiveArrowTextRectangle?.TextFontSize ?? _state.Tools.CurrentArrowTextFontSize);
			}

			return false;
		}

		_state.Tools.CurrentArrowTextFontSize = fontSize;
		_state.Tools.ActiveArrowTextRectangle?.SetTextFontSize(fontSize);
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
		if (_state.Drag.Element is PaintRectangle { IsOutline: true } draggingPaintRectangle)
		{
			draggingPaintRectangle.SetStrokeThickness(strokeThickness);
		}
		else if (_state.Drag.Element is ArrowTextRectangle draggingArrowTextRectangle)
		{
			draggingArrowTextRectangle.SetStrokeThickness(strokeThickness);
		}

		if (_state.Tools.ActivePaintRectangle is { IsOutline: true } activePaintRectangle)
		{
			activePaintRectangle.SetStrokeThickness(strokeThickness);
		}

		_state.Tools.ActiveArrowTextRectangle?.SetStrokeThickness(strokeThickness);
	}

	private double GetActiveStrokeThickness()
	{
		if (_state.Tools.ActiveArrowTextRectangle != null)
		{
			return _state.Tools.ActiveArrowTextRectangle.StrokeThickness;
		}

		if (_state.Tools.ActivePaintRectangle is { IsOutline: true } activePaintRectangle)
		{
			return activePaintRectangle.StrokeThickness;
		}

		return _state.Tools.PaintStrokeThickness;
	}

	private void SetActivePaintRectangle(PaintRectangle? paintRectangle)
	{
		_state.Tools.ActivePaintRectangle = paintRectangle;
		if (paintRectangle != null)
		{
			_state.Tools.ActiveArrowTextRectangle = null;
			if (paintRectangle.IsOutline)
			{
				_state.Tools.PaintStrokeThickness = paintRectangle.StrokeThickness;
			}
		}

		UpdateStrokeThicknessTextBox(GetActiveStrokeThickness());
		UpdateFontSizeTextBox(_state.Tools.CurrentArrowTextFontSize);
	}

	private void SetActiveArrowTextRectangle(ArrowTextRectangle? arrowTextRectangle)
	{
		_state.Tools.ActiveArrowTextRectangle = arrowTextRectangle;
		if (arrowTextRectangle != null)
		{
			_state.Tools.ActivePaintRectangle = null;
			_state.Tools.CurrentArrowTextFontSize = arrowTextRectangle.TextFontSize;
			_state.Tools.PaintStrokeThickness = arrowTextRectangle.StrokeThickness;
			UpdateOutlineNumberLabels();
		}

		UpdateStrokeThicknessTextBox(_state.Tools.PaintStrokeThickness);
		UpdateFontSizeTextBox(_state.Tools.CurrentArrowTextFontSize);
	}

	private void UpdateStrokeThicknessTextBox(double strokeThickness)
	{
		_state.Tools.IsUpdatingStrokeThicknessTextBox = true;
		_strokeThicknessTextBox.Text = FormatSizeValue(strokeThickness);
		_state.Tools.IsUpdatingStrokeThicknessTextBox = false;
	}

	private void UpdateFontSizeTextBox(double fontSize)
	{
		_state.Tools.IsUpdatingFontSizeTextBox = true;
		_fontSizeTextBox.Text = FormatSizeValue(fontSize);
		_state.Tools.IsUpdatingFontSizeTextBox = false;
	}

	private UIElement CreatePaintElement(PaintMode mode)
	{
		if (mode == PaintMode.ArrowTextRectangle)
		{
			var arrowTextRectangle = new ArrowTextRectangle(
				_overlayCanvas.Width,
				_overlayCanvas.Height,
				_state.Tools.PaintStrokeThickness,
				_state.Tools.CurrentArrowTextFontSize);
			arrowTextRectangle.TextInputFocused += ArrowTextRectangle_TextInputFocused;
			arrowTextRectangle.Changed += ArrowTextRectangle_Changed;
			return arrowTextRectangle;
		}

		var paintRectangle = CreateRectangle(mode, _overlayCanvas.Width, _overlayCanvas.Height, _state.Tools.PaintStrokeThickness);
		paintRectangle.Focused += PaintRectangle_Focused;
		paintRectangle.BoundsChanged += PaintRectangle_BoundsChanged;
		return paintRectangle;
	}

	private static PaintRectangle CreateRectangle(PaintMode mode, double canvasWidth, double canvasHeight, double strokeThickness)
	{
		return new PaintRectangle(canvasWidth, canvasHeight, mode == PaintMode.RedOutlineRectangle, strokeThickness);
	}

	private static DrawingBrush CreateWorkspaceGridBrush()
	{
		Color backgroundColor = AppTheme.IsDark
			? Color.FromRgb(37, 39, 43)
			: Colors.White;
		Color lineColor = AppTheme.IsDark
			? Color.FromRgb(67, 72, 80)
			: Color.FromRgb(222, 226, 232);

		var backgroundBrush = new SolidColorBrush(backgroundColor);
		backgroundBrush.Freeze();
		var lineBrush = new SolidColorBrush(lineColor);
		lineBrush.Freeze();
		var linePen = new Pen(lineBrush, 1);
		linePen.Freeze();

		var lineGeometry = new GeometryGroup();
		lineGeometry.Children.Add(new LineGeometry(new Point(0.5, 0), new Point(0.5, WorkspaceGridCellSize)));
		lineGeometry.Children.Add(new LineGeometry(new Point(0, 0.5), new Point(WorkspaceGridCellSize, 0.5)));

		var tile = new DrawingGroup();
		tile.Children.Add(new GeometryDrawing(
			backgroundBrush,
			null,
			new RectangleGeometry(new Rect(0, 0, WorkspaceGridCellSize, WorkspaceGridCellSize))));
		tile.Children.Add(new GeometryDrawing(null, linePen, lineGeometry));
		tile.Freeze();

		var brush = new DrawingBrush(tile)
		{
			TileMode = TileMode.Tile,
			Viewport = new Rect(0, 0, WorkspaceGridCellSize, WorkspaceGridCellSize),
			ViewportUnits = BrushMappingMode.Absolute,
			Viewbox = new Rect(0, 0, WorkspaceGridCellSize, WorkspaceGridCellSize),
			ViewboxUnits = BrushMappingMode.Absolute,
			Stretch = Stretch.None
		};
		brush.Freeze();
		return brush;
	}
}
