using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.IO;
using System.Text;

namespace Clipboard;

internal sealed class ClipboardImagePointer
{
	private const int MaxPointerByteLength = 512;
	private const string VersionLine = "version https://git-lfs.github.com/spec/v1";
	private const string OidLinePrefix = "oid sha256:";
	private const string SizeLinePrefix = "size ";

	public required string Oid { get; init; }
	public required long Size { get; init; }

	public byte[] ToBytes()
	{
		return Encoding.UTF8.GetBytes(ToPointerText());
	}

	public string ToPointerText()
	{
		return $"{VersionLine}\n{OidLinePrefix}{Oid}\n{SizeLinePrefix}{Size}\n";
	}

	public static bool TryParse(byte[] bytes, [NotNullWhen(true)] out ClipboardImagePointer? pointer)
	{
		pointer = null;
		if (bytes.Length == 0 || bytes.Length > MaxPointerByteLength)
		{
			return false;
		}

		string text = Encoding.UTF8.GetString(bytes).Replace("\r\n", "\n").Replace('\r', '\n');
		string[] lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
		if (lines.Length < 3 ||
			!string.Equals(lines[0], VersionLine, StringComparison.Ordinal) ||
			!lines[1].StartsWith(OidLinePrefix, StringComparison.Ordinal) ||
			!lines[2].StartsWith(SizeLinePrefix, StringComparison.Ordinal))
		{
			return false;
		}

		if (!ClipboardContentHash.TryNormalizeSha256(lines[1][OidLinePrefix.Length..], out string oid))
		{
			return false;
		}

		if (!long.TryParse(lines[2][SizeLinePrefix.Length..], NumberStyles.None, CultureInfo.InvariantCulture, out long size) ||
			size < 0)
		{
			return false;
		}

		pointer = new ClipboardImagePointer
		{
			Oid = oid,
			Size = size
		};
		return true;
	}
}

internal static class ClipboardImageStore
{
	private const string ObjectExtension = ".png";

	public static ClipboardImagePointer StoreImage(byte[] imageBytes)
	{
		string oid = ClipboardContentHash.CalculateSha256(imageBytes);
		string objectPath = GetObjectPath(oid);
		Directory.CreateDirectory(Path.GetDirectoryName(objectPath)!);

		if (!File.Exists(objectPath) || new FileInfo(objectPath).Length != imageBytes.LongLength)
		{
			WriteObjectFile(objectPath, imageBytes);
		}

		return new ClipboardImagePointer
		{
			Oid = oid,
			Size = imageBytes.LongLength
		};
	}

	public static bool TryResolveImageBytes(
		byte[] storedBytes,
		out byte[] imageBytes,
		out ClipboardImagePointer? pointer,
		out string? errorMessage)
	{
		imageBytes = storedBytes;
		pointer = null;
		errorMessage = null;

		if (!ClipboardImagePointer.TryParse(storedBytes, out pointer))
		{
			return true;
		}

		string objectPath = GetObjectPath(pointer.Oid);
		if (!File.Exists(objectPath))
		{
			imageBytes = Array.Empty<byte>();
			errorMessage = $"画像オブジェクトが見つかりません。Oid={pointer.Oid} Path={objectPath}";
			return false;
		}

		imageBytes = File.ReadAllBytes(objectPath);
		if (imageBytes.LongLength != pointer.Size)
		{
			errorMessage = $"画像オブジェクトのサイズがポインタと一致しません。Oid={pointer.Oid} Expected={pointer.Size} Actual={imageBytes.LongLength}";
			imageBytes = Array.Empty<byte>();
			return false;
		}

		return true;
	}

	public static string GetObjectRelativePath(string oid)
	{
		string normalizedOid = NormalizeOidOrThrow(oid);
		return Path.Combine("sha256", normalizedOid[..2], normalizedOid + ObjectExtension);
	}

	public static string GetObjectPath(string oid)
	{
		return Path.Combine(ClipboardSettings.ImageObjectDirectoryPath, GetObjectRelativePath(oid));
	}

	public static void DeleteObjectFile(string oid)
	{
		try
		{
			string objectPath = GetObjectPath(oid);
			if (File.Exists(objectPath))
			{
				File.Delete(objectPath);
			}
		}
		catch (Exception ex)
		{
			Logger.Error(ex, $"画像オブジェクトを削除できませんでした。Oid={oid}");
		}
	}

	private static void WriteObjectFile(string objectPath, byte[] imageBytes)
	{
		string directoryPath = Path.GetDirectoryName(objectPath)!;
		string temporaryPath = Path.Combine(directoryPath, $"{Path.GetFileName(objectPath)}.{Guid.NewGuid():N}.tmp");
		File.WriteAllBytes(temporaryPath, imageBytes);
		try
		{
			File.Move(temporaryPath, objectPath, overwrite: true);
		}
		finally
		{
			if (File.Exists(temporaryPath))
			{
				File.Delete(temporaryPath);
			}
		}
	}

	private static string NormalizeOidOrThrow(string oid)
	{
		if (!ClipboardContentHash.TryNormalizeSha256(oid, out string normalizedOid))
		{
			throw new ArgumentException("SHA-256 hash expected.", nameof(oid));
		}

		return normalizedOid;
	}
}
