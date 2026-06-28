using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using static Clipboard.ImagePaintBounds;

namespace Clipboard;

internal sealed class ImagePaintRenderer
{
	private readonly BitmapSource _sourceImage;
	private readonly Grid _paintSurface;
	private readonly Canvas _imageLayerCanvas;
	private readonly Canvas _overlayCanvas;
	private readonly ImagePaintEditorState _state;
	private readonly ImagePaintOutlineNumberLabelManager _outlineNumberLabelManager;
	private readonly Func<double> _canvasWidthProvider;
	private readonly Func<double> _canvasHeightProvider;

	internal ImagePaintRenderer(
		BitmapSource sourceImage,
		Grid paintSurface,
		Canvas imageLayerCanvas,
		Canvas overlayCanvas,
		ImagePaintEditorState state,
		ImagePaintOutlineNumberLabelManager outlineNumberLabelManager,
		Func<double> canvasWidthProvider,
		Func<double> canvasHeightProvider)
	{
		_sourceImage = sourceImage;
		_paintSurface = paintSurface;
		_imageLayerCanvas = imageLayerCanvas;
		_overlayCanvas = overlayCanvas;
		_state = state;
		_outlineNumberLabelManager = outlineNumberLabelManager;
		_canvasWidthProvider = canvasWidthProvider;
		_canvasHeightProvider = canvasHeightProvider;
	}

	internal BitmapSource RenderPaintedImage()
	{
		return RenderPaintedImage(out _);
	}

	internal BitmapSource RenderPaintedImage(out Rect renderBounds)
	{
		PrepareTextInputsForRender();
		renderBounds = CalculateRenderBounds();
		SetPlacedImagesEditingChromeVisible(false);
		try
		{
			int renderWidth = Math.Max(1, (int)Math.Round(renderBounds.Width));
			int renderHeight = Math.Max(1, (int)Math.Round(renderBounds.Height));
			double canvasWidth = _canvasWidthProvider();
			double canvasHeight = _canvasHeightProvider();
			_paintSurface.Measure(new Size(canvasWidth, canvasHeight));
			_paintSurface.Arrange(new Rect(0, 0, canvasWidth, canvasHeight));
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

	internal Rect CalculateRenderBounds()
	{
		Rect bounds = Rect.Empty;
		foreach (PlacedImage placedImage in _imageLayerCanvas.Children.OfType<PlacedImage>())
		{
			UnionBounds(ref bounds, placedImage.Bounds);
		}

		foreach (UIElement element in _state.History.CompletedElements)
		{
			if (TryGetRenderableElementBounds(element, out Rect elementBounds))
			{
				UnionBounds(ref bounds, elementBounds);
			}
		}

		foreach (TextBlock label in _outlineNumberLabelManager.Labels)
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

	private void SetPlacedImagesEditingChromeVisible(bool visible)
	{
		foreach (PlacedImage placedImage in _imageLayerCanvas.Children.OfType<PlacedImage>())
		{
			placedImage.SetEditingChromeVisible(visible);
		}
	}

	private void PrepareTextInputsForRender()
	{
		foreach (UIElement element in _state.History.CompletedElements)
		{
			if (element is ArrowTextRectangle arrowTextRectangle)
			{
				arrowTextRectangle.PrepareForRender();
			}
		}

		if (_state.Drag.Element is ArrowTextRectangle activeArrowTextRectangle)
		{
			activeArrowTextRectangle.PrepareForRender();
		}

		_overlayCanvas.Focusable = true;
		_overlayCanvas.Focus();
		Keyboard.ClearFocus();
	}
}
