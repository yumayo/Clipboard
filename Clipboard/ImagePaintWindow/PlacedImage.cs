using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Rectangle = System.Windows.Shapes.Rectangle;
using static Clipboard.ImagePaintMetrics;

namespace Clipboard;

internal sealed class PlacedImage : Canvas
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

	public bool IsEditing => _isMoving || _isResizing;

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

		SetBounds(CalculateAspectPreservingResizeBounds(_resizeDragStartBounds, _activeResizeHandle, offset));
	}

	private static Rect CalculateAspectPreservingResizeBounds(Rect startBounds, ResizeHandleKind resizeHandleKind, Vector offset)
	{
		double startWidth = startBounds.Width;
		double startHeight = startBounds.Height;
		if (startWidth <= 0 || startHeight <= 0)
		{
			return startBounds;
		}

		Point anchorPoint = GetResizeAnchorPoint(startBounds, resizeHandleKind);
		Point startMovingPoint = GetResizeMovingPoint(startBounds, resizeHandleKind);
		Vector startVector = startMovingPoint - anchorPoint;
		Point desiredMovingPoint = startMovingPoint + offset;
		Vector desiredVector = desiredMovingPoint - anchorPoint;
		double startVectorLengthSquared = startVector.X * startVector.X + startVector.Y * startVector.Y;
		if (startVectorLengthSquared <= 0)
		{
			return startBounds;
		}

		double scale = ((desiredVector.X * startVector.X) + (desiredVector.Y * startVector.Y)) / startVectorLengthSquared;
		double minScale = Math.Max(MultiImageMinPlacedSize / startWidth, MultiImageMinPlacedSize / startHeight);
		if (!double.IsFinite(scale))
		{
			scale = minScale;
		}

		scale = Math.Max(scale, minScale);
		var scaledVector = new Vector(startVector.X * scale, startVector.Y * scale);
		Point movingPoint = anchorPoint + scaledVector;
		double left = Math.Min(anchorPoint.X, movingPoint.X);
		double top = Math.Min(anchorPoint.Y, movingPoint.Y);
		double width = Math.Abs(movingPoint.X - anchorPoint.X);
		double height = Math.Abs(movingPoint.Y - anchorPoint.Y);
		return new Rect(left, top, width, height);
	}

	private static Point GetResizeAnchorPoint(Rect bounds, ResizeHandleKind resizeHandleKind)
	{
		return resizeHandleKind switch
		{
			ResizeHandleKind.TopLeft => new Point(bounds.Right, bounds.Bottom),
			ResizeHandleKind.TopRight => new Point(bounds.Left, bounds.Bottom),
			ResizeHandleKind.BottomLeft => new Point(bounds.Right, bounds.Top),
			ResizeHandleKind.BottomRight => new Point(bounds.Left, bounds.Top),
			_ => new Point(bounds.Left, bounds.Top)
		};
	}

	private static Point GetResizeMovingPoint(Rect bounds, ResizeHandleKind resizeHandleKind)
	{
		return resizeHandleKind switch
		{
			ResizeHandleKind.TopLeft => new Point(bounds.Left, bounds.Top),
			ResizeHandleKind.TopRight => new Point(bounds.Right, bounds.Top),
			ResizeHandleKind.BottomLeft => new Point(bounds.Left, bounds.Bottom),
			ResizeHandleKind.BottomRight => new Point(bounds.Right, bounds.Bottom),
			_ => new Point(bounds.Right, bounds.Bottom)
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

	private enum ResizeHandleKind
	{
		TopLeft,
		TopRight,
		BottomLeft,
		BottomRight
	}
}
