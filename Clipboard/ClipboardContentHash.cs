using System.Security.Cryptography;

namespace Clipboard;

internal static class ClipboardContentHash
{
	public static string CalculateSha256(byte[] bytes)
	{
		byte[] hash = SHA256.HashData(bytes);
		return Convert.ToHexString(hash).ToLowerInvariant();
	}

	public static bool TryNormalizeSha256(string? value, out string normalizedHash)
	{
		normalizedHash = string.Empty;
		if (string.IsNullOrWhiteSpace(value))
		{
			return false;
		}

		string candidate = value.Trim();
		const string sha256Prefix = "sha256:";
		if (candidate.StartsWith(sha256Prefix, StringComparison.OrdinalIgnoreCase))
		{
			candidate = candidate[sha256Prefix.Length..];
		}

		candidate = candidate.Replace("-", string.Empty).ToLowerInvariant();
		if (candidate.Length != 64)
		{
			return false;
		}

		foreach (char character in candidate)
		{
			bool isHexDigit =
				character is >= '0' and <= '9' ||
				character is >= 'a' and <= 'f';
			if (!isHexDigit)
			{
				return false;
			}
		}

		normalizedHash = candidate;
		return true;
	}
}
