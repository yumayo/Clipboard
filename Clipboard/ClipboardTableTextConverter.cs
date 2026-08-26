using System.Text;

namespace Clipboard;

internal enum ClipboardTableTextFormat
{
	Tsv,
	Csv,
	Markdown
}

internal static class ClipboardTableTextConverter
{
	private enum SourceFormat
	{
		PlainText,
		Tsv,
		Csv,
		Markdown
	}

	internal static string Convert(string text, ClipboardTableTextFormat outputFormat)
	{
		if (text.Length == 0)
		{
			return string.Empty;
		}

		string normalizedText = NormalizeNewLines(ClipboardTextNormalizer.NormalizeForPaste(text));
		List<List<string>> rows = ParseRows(normalizedText, DetectSourceFormat(normalizedText));
		return outputFormat switch
		{
			ClipboardTableTextFormat.Tsv => SerializeDelimited(rows, '\t'),
			ClipboardTableTextFormat.Csv => SerializeDelimited(rows, ','),
			ClipboardTableTextFormat.Markdown => SerializeMarkdown(rows),
			_ => normalizedText
		};
	}

	internal static bool IsTableText(string text)
	{
		if (text.Length == 0)
		{
			return false;
		}

		string normalizedText = NormalizeNewLines(ClipboardTextNormalizer.NormalizeForPaste(text));
		return DetectSourceFormat(normalizedText) != SourceFormat.PlainText;
	}

	private static SourceFormat DetectSourceFormat(string text)
	{
		string[] lines = text.Split('\n');
		if (CountUnquotedCharacters(text, '\t') > 0)
		{
			return SourceFormat.Tsv;
		}

		if (LooksLikeMarkdown(lines))
		{
			return SourceFormat.Markdown;
		}

		if (CountUnquotedCharacters(text, ',') > 0)
		{
			return SourceFormat.Csv;
		}

		if (lines.Any(line => CountUnescapedPipes(line) > 0))
		{
			return SourceFormat.Markdown;
		}

		return SourceFormat.PlainText;
	}

	private static List<List<string>> ParseRows(string text, SourceFormat sourceFormat)
	{
		return sourceFormat switch
		{
			SourceFormat.Tsv => ParseDelimited(text, '\t'),
			SourceFormat.Csv => ParseDelimited(text, ','),
			SourceFormat.Markdown => ParseMarkdown(text.Split('\n')),
			_ => ParsePlainText(text)
		};
	}

	private static List<List<string>> ParsePlainText(string text)
	{
		var rows = text
			.Split('\n')
			.Select(line => new List<string> { line })
			.ToList();
		RemoveTrailingEmptyRows(rows);
		return rows;
	}

	private static bool LooksLikeMarkdown(string[] lines)
	{
		List<string> meaningfulLines = lines
			.Where(line => !string.IsNullOrWhiteSpace(line))
			.Take(12)
			.ToList();
		if (meaningfulLines.Count == 0)
		{
			return false;
		}

		if (meaningfulLines.Count >= 2 &&
			(CountUnescapedPipes(meaningfulLines[0]) > 0 || CountUnescapedPipes(meaningfulLines[1]) > 0) &&
			IsMarkdownSeparatorRow(ParseMarkdownRow(meaningfulLines[1])))
		{
			return true;
		}

		foreach (string line in meaningfulLines)
		{
			string trimmed = line.Trim();
			if (CountUnescapedPipes(trimmed) > 0 &&
				(trimmed.StartsWith('|') || EndsWithUnescapedPipe(trimmed)))
			{
				return true;
			}
		}

		return meaningfulLines.Count >= 2 && meaningfulLines.All(line => CountUnescapedPipes(line) > 0);
	}

	private static List<List<string>> ParseDelimited(string text, char delimiter)
	{
		var rows = new List<List<string>>();
		var row = new List<string>();
		var cell = new StringBuilder();
		bool inQuotes = false;
		for (int index = 0; index < text.Length; index++)
		{
			char character = text[index];
			if (character == '"')
			{
				if (inQuotes && index + 1 < text.Length && text[index + 1] == '"')
				{
					cell.Append('"');
					index++;
				}
				else if (inQuotes)
				{
					inQuotes = false;
				}
				else if (cell.Length == 0)
				{
					inQuotes = true;
				}
				else
				{
					cell.Append(character);
				}
			}
			else if (!inQuotes && character == delimiter)
			{
				row.Add(cell.ToString());
				cell.Clear();
			}
			else if (!inQuotes && character == '\n')
			{
				row.Add(cell.ToString());
				cell.Clear();
				rows.Add(row);
				row = new List<string>();
			}
			else
			{
				cell.Append(character);
			}
		}

		row.Add(cell.ToString());
		rows.Add(row);
		RemoveTrailingEmptyRows(rows);
		return rows;
	}

	private static List<List<string>> ParseMarkdown(string[] lines)
	{
		var rows = lines
			.Select(ParseMarkdownRow)
			.ToList();
		RemoveLeadingAndTrailingEmptyRows(rows);
		if (rows.Count >= 2 && IsMarkdownSeparatorRow(rows[1]))
		{
			rows.RemoveAt(1);
		}

		return rows;
	}

	private static List<string> ParseMarkdownRow(string line)
	{
		string value = line.Trim();
		if (value.StartsWith('|'))
		{
			value = value[1..];
		}
		if (EndsWithUnescapedPipe(value))
		{
			value = value[..^1];
		}

		var cells = new List<string>();
		var cell = new StringBuilder();
		for (int index = 0; index < value.Length; index++)
		{
			char character = value[index];
			if (character == '\\' && index + 1 < value.Length &&
				(value[index + 1] == '|' || value[index + 1] == '\\'))
			{
				cell.Append(value[index + 1]);
				index++;
			}
			else if (character == '|')
			{
				cells.Add(cell.ToString().Trim());
				cell.Clear();
			}
			else
			{
				cell.Append(character);
			}
		}

		cells.Add(cell.ToString().Trim());
		return cells;
	}

	private static bool IsMarkdownSeparatorRow(List<string> row)
	{
		return row.Count > 0 && row.All(IsMarkdownSeparatorCell);
	}

	private static bool IsMarkdownSeparatorCell(string cell)
	{
		string value = cell.Trim();
		if (value.StartsWith(':'))
		{
			value = value[1..];
		}
		if (value.EndsWith(':'))
		{
			value = value[..^1];
		}

		return value.Length >= 3 && value.All(character => character == '-');
	}

	private static string SerializeDelimited(List<List<string>> rows, char delimiter)
	{
		return string.Join(
			"\r\n",
			rows.Select(row => string.Join(delimiter, row.Select(cell => EscapeDelimitedCell(cell, delimiter)))));
	}

	private static string EscapeDelimitedCell(string cell, char delimiter)
	{
		cell = cell.Replace("\r\n", "\n").Replace('\r', '\n').Replace("\n", "\r\n");
		if (!cell.Contains(delimiter) && !cell.Contains('"') && !cell.Contains('\r') && !cell.Contains('\n'))
		{
			return cell;
		}

		return '"' + cell.Replace("\"", "\"\"") + '"';
	}

	private static string SerializeMarkdown(List<List<string>> rows)
	{
		if (rows.Count == 0)
		{
			return string.Empty;
		}

		int columnCount = Math.Max(1, rows.Max(row => row.Count));
		var outputLines = new List<string>(rows.Count + 1)
		{
			SerializeMarkdownRow(rows[0], columnCount),
			"| " + string.Join(" | ", Enumerable.Repeat("---", columnCount)) + " |"
		};
		for (int rowIndex = 1; rowIndex < rows.Count; rowIndex++)
		{
			outputLines.Add(SerializeMarkdownRow(rows[rowIndex], columnCount));
		}

		return string.Join("\r\n", outputLines);
	}

	private static string SerializeMarkdownRow(List<string> row, int columnCount)
	{
		return "| " + string.Join(
			" | ",
			Enumerable.Range(0, columnCount)
				.Select(index => index < row.Count ? EscapeMarkdownCell(row[index]) : string.Empty)) + " |";
	}

	private static string EscapeMarkdownCell(string cell)
	{
		return cell
			.Replace("\\", "\\\\")
			.Replace("|", "\\|")
			.Replace("\r\n", "<br>")
			.Replace("\r", "<br>")
			.Replace("\n", "<br>");
	}

	private static int CountUnquotedCharacters(string text, char target)
	{
		int count = 0;
		bool inQuotes = false;
		for (int index = 0; index < text.Length; index++)
		{
			if (text[index] == '"')
			{
				if (inQuotes && index + 1 < text.Length && text[index + 1] == '"')
				{
					index++;
				}
				else
				{
					inQuotes = !inQuotes;
				}
			}
			else if (!inQuotes && text[index] == target)
			{
				count++;
			}
		}

		return count;
	}

	private static int CountUnescapedPipes(string text)
	{
		int count = 0;
		for (int index = 0; index < text.Length; index++)
		{
			if (text[index] == '\\')
			{
				index++;
			}
			else if (text[index] == '|')
			{
				count++;
			}
		}

		return count;
	}

	private static bool EndsWithUnescapedPipe(string text)
	{
		if (!text.EndsWith('|'))
		{
			return false;
		}

		int slashCount = 0;
		for (int index = text.Length - 2; index >= 0 && text[index] == '\\'; index--)
		{
			slashCount++;
		}
		return slashCount % 2 == 0;
	}

	private static void RemoveTrailingEmptyRows(List<List<string>> rows)
	{
		while (rows.Count > 1 && rows[^1].All(string.IsNullOrEmpty))
		{
			rows.RemoveAt(rows.Count - 1);
		}
	}

	private static void RemoveLeadingAndTrailingEmptyRows(List<List<string>> rows)
	{
		RemoveTrailingEmptyRows(rows);
		while (rows.Count > 1 && rows[0].All(string.IsNullOrWhiteSpace))
		{
			rows.RemoveAt(0);
		}
	}

	private static string NormalizeNewLines(string text)
	{
		return text.Replace("\r\n", "\n").Replace('\r', '\n');
	}
}
