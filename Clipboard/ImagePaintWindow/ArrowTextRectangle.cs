using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Polygon = System.Windows.Shapes.Polygon;
using Polyline = System.Windows.Shapes.Polyline;
using Rectangle = System.Windows.Shapes.Rectangle;
using static Clipboard.ImagePaintMetrics;

namespace Clipboard;

internal sealed class ArrowTextRectangle : Canvas
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
