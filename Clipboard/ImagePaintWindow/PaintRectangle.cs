using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Rectangle = System.Windows.Shapes.Rectangle;

namespace Clipboard;

internal sealed partial class ImagePaintWindow
{
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
}
