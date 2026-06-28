using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows;
using static Clipboard.ImagePaintImageFactory;

namespace Clipboard;

internal sealed class ImagePaintSerializedState
{
	internal const string FillRectangleElementKind = "fillRectangle";
	internal const string OutlineRectangleElementKind = "outlineRectangle";
	internal const string ArrowTextRectangleElementKind = "arrowTextRectangle";
	private const int CurrentVersion = 1;
	private static readonly JsonSerializerOptions JsonOptions = new()
	{
		DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
		PropertyNameCaseInsensitive = true
	};

	public int Version { get; set; } = CurrentVersion;
	public double ContentWidth { get; set; }
	public double ContentHeight { get; set; }
	public double PaintStrokeThickness { get; set; }
	public double CurrentArrowTextFontSize { get; set; }
	public bool ShowOutlineNumbers { get; set; }
	public PaintMode PaintMode { get; set; }
	public List<ImagePaintSerializedImage> Images { get; set; } = new();
	public List<ImagePaintSerializedElement> Elements { get; set; } = new();

	internal static string CreateJson(
		IEnumerable<PlacedImage> placedImages,
		IEnumerable<UIElement> completedElements,
		PaintToolState tools,
		Rect renderBounds)
	{
		var state = new ImagePaintSerializedState
		{
			ContentWidth = Math.Max(1, (int)Math.Round(renderBounds.Width)),
			ContentHeight = Math.Max(1, (int)Math.Round(renderBounds.Height)),
			PaintStrokeThickness = tools.PaintStrokeThickness,
			CurrentArrowTextFontSize = tools.CurrentArrowTextFontSize,
			ShowOutlineNumbers = tools.ShowOutlineNumbers,
			PaintMode = tools.PaintMode
		};

		foreach (PlacedImage placedImage in placedImages)
		{
			state.Images.Add(new ImagePaintSerializedImage
			{
				ImageBytes = EncodePng(placedImage.Source),
				Bounds = ImagePaintSerializedRect.FromRect(ToOutputRect(placedImage.Bounds, renderBounds))
			});
		}

		foreach (UIElement element in completedElements)
		{
			ImagePaintSerializedElement? serializedElement = element switch
			{
				PaintRectangle paintRectangle => CreateSerializedPaintRectangle(paintRectangle, renderBounds),
				ArrowTextRectangle arrowTextRectangle => CreateSerializedArrowTextRectangle(arrowTextRectangle, renderBounds),
				_ => null
			};
			if (serializedElement != null)
			{
				state.Elements.Add(serializedElement);
			}
		}

		return JsonSerializer.Serialize(state, JsonOptions);
	}

	internal static bool TryDeserialize(string? json, out ImagePaintSerializedState? state)
	{
		state = null;
		if (string.IsNullOrWhiteSpace(json))
		{
			return false;
		}

		try
		{
			ImagePaintSerializedState? deserialized = JsonSerializer.Deserialize<ImagePaintSerializedState>(json, JsonOptions);
			if (deserialized != null)
			{
				deserialized.Images ??= new List<ImagePaintSerializedImage>();
				deserialized.Elements ??= new List<ImagePaintSerializedElement>();
			}

			if (deserialized is not { Version: CurrentVersion } ||
				deserialized.ContentWidth <= 0 ||
				deserialized.ContentHeight <= 0 ||
				deserialized.Images is not { Count: > 0 })
			{
				return false;
			}

			state = deserialized;
			return true;
		}
		catch (Exception ex)
		{
			Logger.Warning($"ImagePaintSerializedState: ペイント状態を読み込めませんでした。Error={ex.Message}");
			return false;
		}
	}

	private static ImagePaintSerializedElement CreateSerializedPaintRectangle(
		PaintRectangle paintRectangle,
		Rect renderBounds)
	{
		return new ImagePaintSerializedElement
		{
			Kind = paintRectangle.IsOutline ? OutlineRectangleElementKind : FillRectangleElementKind,
			Bounds = ImagePaintSerializedRect.FromRect(ToOutputRect(paintRectangle.Bounds, renderBounds)),
			StrokeThickness = paintRectangle.StrokeThickness
		};
	}

	private static ImagePaintSerializedElement CreateSerializedArrowTextRectangle(
		ArrowTextRectangle arrowTextRectangle,
		Rect renderBounds)
	{
		return new ImagePaintSerializedElement
		{
			Kind = ArrowTextRectangleElementKind,
			Bounds = ImagePaintSerializedRect.FromRect(ToOutputRect(arrowTextRectangle.TextRectangleBounds, renderBounds)),
			ArrowTip = ImagePaintSerializedPoint.FromPoint(ToOutputPoint(arrowTextRectangle.ArrowTip, renderBounds)),
			StrokeThickness = arrowTextRectangle.StrokeThickness,
			TextFontSize = arrowTextRectangle.TextFontSize,
			Text = arrowTextRectangle.Text
		};
	}

	private static Rect ToOutputRect(Rect bounds, Rect renderBounds)
	{
		return new Rect(
			bounds.Left - renderBounds.Left,
			bounds.Top - renderBounds.Top,
			bounds.Width,
			bounds.Height);
	}

	private static Point ToOutputPoint(Point point, Rect renderBounds)
	{
		return new Point(point.X - renderBounds.Left, point.Y - renderBounds.Top);
	}
}

internal sealed class ImagePaintSerializedImage
{
	public byte[] ImageBytes { get; set; } = Array.Empty<byte>();
	public ImagePaintSerializedRect Bounds { get; set; } = new();
}

internal sealed class ImagePaintSerializedElement
{
	public string Kind { get; set; } = string.Empty;
	public ImagePaintSerializedRect Bounds { get; set; } = new();
	public double StrokeThickness { get; set; }
	public double TextFontSize { get; set; }
	public string? Text { get; set; }
	public ImagePaintSerializedPoint? ArrowTip { get; set; }
}

internal sealed class ImagePaintSerializedRect
{
	public double Left { get; set; }
	public double Top { get; set; }
	public double Width { get; set; }
	public double Height { get; set; }

	internal static ImagePaintSerializedRect FromRect(Rect rect)
	{
		return new ImagePaintSerializedRect
		{
			Left = rect.Left,
			Top = rect.Top,
			Width = rect.Width,
			Height = rect.Height
		};
	}

	internal Rect ToRect()
	{
		return new Rect(Left, Top, Math.Max(0, Width), Math.Max(0, Height));
	}
}

internal sealed class ImagePaintSerializedPoint
{
	public double X { get; set; }
	public double Y { get; set; }

	internal static ImagePaintSerializedPoint FromPoint(Point point)
	{
		return new ImagePaintSerializedPoint
		{
			X = point.X,
			Y = point.Y
		};
	}

	internal Point ToPoint()
	{
		return new Point(X, Y);
	}
}
