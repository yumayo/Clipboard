using System.IO;
using System.Text.Json;

namespace Clipboard;

internal static class ClipboardVideoFiles
{
	private static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase)
	{
		".mp4", ".m4v", ".mov", ".avi", ".wmv", ".mkv", ".webm", ".mpg", ".mpeg", ".mts", ".m2ts", ".3gp"
	};

	public static string[] Filter(IEnumerable<string> paths)
	{
		return paths.Where(path => !string.IsNullOrWhiteSpace(path) &&
			Path.IsPathFullyQualified(path) && Extensions.Contains(Path.GetExtension(path)))
			.Distinct(StringComparer.OrdinalIgnoreCase)
			.ToArray();
	}

	public static byte[] Encode(IEnumerable<string> paths) => JsonSerializer.SerializeToUtf8Bytes(Filter(paths));

	public static string[] Decode(byte[] bytes)
	{
		try
		{
			return Filter(JsonSerializer.Deserialize<string[]>(bytes) ?? Array.Empty<string>());
		}
		catch (Exception ex) when (ex is JsonException or ArgumentException or NotSupportedException)
		{
			return Array.Empty<string>();
		}
	}
}
