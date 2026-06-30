using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Clipboard;

internal sealed class ClipboardHistoryMetadata
{
	private const int PreviewTextLength = 180;
	internal const int ThumbnailLogicalWidth = 144;
	internal const int ThumbnailLogicalHeight = 108;
	internal const int ThumbnailPixelScale = 3;
	internal const int ThumbnailPixelWidth = ThumbnailLogicalWidth * ThumbnailPixelScale;
	internal const int ThumbnailPixelHeight = ThumbnailLogicalHeight * ThumbnailPixelScale;

	public required string PreviewText { get; init; }
	public required string SearchText { get; init; }
	public byte[]? ThumbnailBytes { get; init; }

	public static ClipboardHistoryKind GetKindFromExtension(string extension)
	{
		return extension.ToLowerInvariant() switch
		{
			".png" => ClipboardHistoryKind.Image,
			".html" => ClipboardHistoryKind.Html,
			".rtf" => ClipboardHistoryKind.Rtf,
			".txt" => ClipboardHistoryKind.Text,
			_ => ClipboardHistoryKind.Unknown
		};
	}

	public static string GetExtension(ClipboardHistoryKind kind)
	{
		return kind switch
		{
			ClipboardHistoryKind.Image => ".png",
			ClipboardHistoryKind.Html => ".html",
			ClipboardHistoryKind.Rtf => ".rtf",
			ClipboardHistoryKind.Text => ".txt",
			_ => string.Empty
		};
	}

	public static ClipboardHistoryMetadata Create(
		byte[] content,
		ClipboardHistoryKind kind,
		DateTime createdAt,
		string? displayName,
		string? sourcePath,
		string? plainText = null)
	{
		string previewText;
		string searchableContent = kind == ClipboardHistoryKind.Image && plainText == null
			? string.Empty
			: CreateSearchableText(content, kind, plainText);
		byte[]? thumbnailBytes = null;

		if (kind == ClipboardHistoryKind.Image)
		{
			(previewText, thumbnailBytes) = CreateImageMetadata(content, displayName);
		}
		else
		{
			previewText = CreatePreviewText(searchableContent, displayName);
		}

		return new ClipboardHistoryMetadata
		{
			PreviewText = previewText,
			SearchText = CreateEntrySearchText(kind, createdAt, previewText, searchableContent, displayName, sourcePath),
			ThumbnailBytes = thumbnailBytes
		};
	}

	public static string CreateSearchableText(byte[] content, ClipboardHistoryKind kind, string? plainText = null)
	{
		string text = plainText ?? Encoding.UTF8.GetString(content);
		if (plainText != null)
		{
			return NormalizePreviewText(text);
		}

		if (kind == ClipboardHistoryKind.Html)
		{
			text = ClipboardHtmlTextConverter.FallbackConvertToPlainText(text);
		}
		else if (kind == ClipboardHistoryKind.Rtf)
		{
			text = FallbackConvertRtfToPlainText(text);
		}

		return NormalizePreviewText(text);
	}

	public static string CreateDisplayText(byte[] content, ClipboardHistoryKind kind, string? plainText = null)
	{
		string text = plainText ?? Encoding.UTF8.GetString(content);
		if (plainText != null)
		{
			return NormalizeDisplayText(text);
		}

		if (kind == ClipboardHistoryKind.Html)
		{
			text = ClipboardHtmlTextConverter.FallbackConvertToPlainText(text);
		}
		else if (kind == ClipboardHistoryKind.Rtf)
		{
			text = FallbackConvertRtfToMultilinePlainText(text);
		}

		return NormalizeDisplayText(text);
	}

	public static string CreatePreviewText(string text, string? displayName = null)
	{
		if (text.Length > PreviewTextLength)
		{
			text = text[..PreviewTextLength] + "...";
		}

		return string.IsNullOrWhiteSpace(text) ? displayName ?? "履歴" : text;
	}

	private static string CreateEntrySearchText(
		ClipboardHistoryKind kind,
		DateTime createdAt,
		string previewText,
		string searchableContent,
		string? displayName,
		string? sourcePath)
	{
		return string.Join(
			"\n",
			new[]
			{
				displayName,
				sourcePath,
				createdAt.ToString("yyyy/MM/dd HH:mm:ss", CultureInfo.InvariantCulture),
				kind.ToString(),
				previewText,
				searchableContent
			}.Where(text => !string.IsNullOrWhiteSpace(text)));
	}

	private static (string PreviewText, byte[]? ThumbnailBytes) CreateImageMetadata(byte[] content, string? displayName)
	{
		try
		{
			using var stream = new MemoryStream(content);
			BitmapDecoder decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
			BitmapFrame frame = decoder.Frames[0];
			string name = string.IsNullOrWhiteSpace(displayName) ? "画像" : displayName;
			string previewText = $"{name} / {frame.PixelWidth} x {frame.PixelHeight}";
			return (previewText, CreateThumbnailBytes(frame));
		}
		catch
		{
			return (string.IsNullOrWhiteSpace(displayName) ? "画像" : displayName, null);
		}
	}

	private static byte[]? CreateThumbnailBytes(BitmapFrame frame)
	{
		if (frame.PixelWidth <= 0 || frame.PixelHeight <= 0)
		{
			return null;
		}

		double scale = Math.Min((double)ThumbnailPixelWidth / frame.PixelWidth, (double)ThumbnailPixelHeight / frame.PixelHeight);
		if (scale <= 0 || scale >= 1)
		{
			scale = 1;
		}

		BitmapSource thumbnailSource = frame;
		if (scale < 1)
		{
			var transformed = new TransformedBitmap(frame, new ScaleTransform(scale, scale));
			transformed.Freeze();
			thumbnailSource = transformed;
		}

		var encoder = new PngBitmapEncoder();
		encoder.Frames.Add(BitmapFrame.Create(thumbnailSource));
		using var output = new MemoryStream();
		encoder.Save(output);
		return output.ToArray();
	}

	public static string NormalizePreviewText(string text)
	{
		return Regex.Replace(text, @"\s+", " ").Trim();
	}

	private static string NormalizeDisplayText(string text)
	{
		text = text.Replace("\r\n", "\n").Replace('\r', '\n');
		text = Regex.Replace(text, @"[ \t\f\v]+\n", "\n");
		text = Regex.Replace(text, @"\n[ \t\f\v]+", "\n");
		text = Regex.Replace(text, @"\n{4,}", "\n\n\n");
		return text.Trim();
	}

	private static string FallbackConvertRtfToPlainText(string rtf)
	{
		return NormalizePreviewText(FallbackConvertRtfToMultilinePlainText(rtf));
	}

	private static string FallbackConvertRtfToMultilinePlainText(string rtf)
	{
		string text = Regex.Replace(rtf, @"\\'[0-9a-fA-F]{2}", " ");
		text = Regex.Replace(text, @"\\(par|line)\b\s*", "\n");
		text = text.Replace(@"\tab", "\t");
		text = Regex.Replace(text, @"\\[a-zA-Z]+\d* ?", " ");
		text = Regex.Replace(text, @"[{}]", " ");
		return text;
	}
}
