using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace Clipboard;

internal sealed class ImagePaintEditorState
{
	internal PaintElementHistory History { get; } = new();

	internal PaintToolState Tools { get; } = new();

	internal PaintDragState Drag { get; } = new();

	internal MiddleButtonPanState MiddleButtonPan { get; } = new();

	internal ImagePaintWorkspaceState Workspace { get; } = new();

	internal Dictionary<PaintRectangle, TextBlock> OutlineNumberLabels { get; } = new();

	internal bool IsMultiImageMode { get; set; }

	internal bool HasPaintChanges { get; set; }
}

internal sealed class ImagePaintWorkspaceState
{
	internal double CanvasWidth { get; set; }

	internal double CanvasHeight { get; set; }
}

internal sealed class PaintElementHistory
{
	internal List<UIElement> CompletedElements { get; } = new();

	internal List<UIElement> RedoElements { get; } = new();
}

internal sealed class PaintToolState
{
	internal double PaintStrokeThickness { get; set; }

	internal double CurrentArrowTextFontSize { get; set; }

	internal bool IsUpdatingStrokeThicknessTextBox { get; set; }

	internal bool IsUpdatingFontSizeTextBox { get; set; }

	internal bool ShowOutlineNumbers { get; set; } = true;

	internal PaintRectangle? ActivePaintRectangle { get; set; }

	internal ArrowTextRectangle? ActiveArrowTextRectangle { get; set; }

	internal PaintMode PaintMode { get; set; } = PaintMode.RedOutlineRectangle;
}

internal sealed class PaintDragState
{
	internal Point StartPoint { get; set; }

	internal UIElement? Element { get; set; }
}

internal sealed class MiddleButtonPanState
{
	internal Point StartPoint { get; set; }

	internal double StartHorizontalOffset { get; set; }

	internal double StartVerticalOffset { get; set; }

	internal Cursor? PreviousOverrideCursor { get; set; }

	internal bool IsPanning { get; set; }
}
