namespace Clipboard;

internal static class ClipboardTextNormalizer
{
	private const char ByteOrderMark = '\uFEFF';
	private const char NoBreakSpace = '\u00A0';
	private const char FigureSpace = '\u2007';
	private const char NarrowNoBreakSpace = '\u202F';

	public static string NormalizeForPaste(string text)
	{
		if (text.Length == 0)
		{
			return text;
		}

		int startIndex = 0;
		while (startIndex < text.Length && text[startIndex] == ByteOrderMark)
		{
			startIndex++;
		}

		if (startIndex > 0)
		{
			text = text[startIndex..];
		}

		return NormalizeNoBreakSpaces(text);
	}

	public static string NormalizeNoBreakSpaces(string text)
	{
		return text
			.Replace(NoBreakSpace, ' ')
			.Replace(FigureSpace, ' ')
			.Replace(NarrowNoBreakSpace, ' ');
	}
}
