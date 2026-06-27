using System.Globalization;
using System.IO;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Clipboard;

internal sealed class ClipboardMigrationProgress
{
	public ClipboardMigrationProgress(string message, int processedItems, int totalItems, string? detail = null, bool isIndeterminate = false)
	{
		Message = message;
		ProcessedItems = processedItems;
		TotalItems = totalItems;
		Detail = detail;
		IsIndeterminate = isIndeterminate;
	}

	public string Message { get; }
	public int ProcessedItems { get; }
	public int TotalItems { get; }
	public string? Detail { get; }
	public bool IsIndeterminate { get; }
}

internal static class ClipboardLegacyMigrator
{
	private const string LegacyMigrationFlag = "legacy_appdata_clipboard_migrated";

	public static bool NeedsMigration()
	{
		if (ClipboardDatabase.GetMigrationFlag(LegacyMigrationFlag))
		{
			return false;
		}

		return File.Exists(ClipboardSettings.SettingsFilePath) || EnumerateLegacyHistoryFiles(CancellationToken.None).Any();
	}

	public static void Migrate(IProgress<ClipboardMigrationProgress> progress, CancellationToken cancellationToken)
	{
		ClipboardDatabase.Initialize();
		progress.Report(new ClipboardMigrationProgress("旧データを確認しています...", 0, 0, isIndeterminate: true));

		bool hasLegacySettings = File.Exists(ClipboardSettings.SettingsFilePath);
		List<FileInfo> legacyFiles = EnumerateLegacyHistoryFiles(cancellationToken)
			.OrderBy(file => file.LastWriteTimeUtc)
			.ThenBy(file => file.FullName, StringComparer.OrdinalIgnoreCase)
			.ToList();

		int totalItems = legacyFiles.Count + (hasLegacySettings ? 1 : 0);
		int processedItems = 0;
		if (totalItems == 0)
		{
			MarkMigrationComplete();
			progress.Report(new ClipboardMigrationProgress("移行する旧データはありません。", 0, 0));
			return;
		}

		using SqliteConnection connection = ClipboardDatabase.OpenMigrationConnection();
		using SqliteTransaction transaction = connection.BeginTransaction();
		bool committed = false;

		try
		{
			if (hasLegacySettings)
			{
				cancellationToken.ThrowIfCancellationRequested();
				progress.Report(new ClipboardMigrationProgress("設定を移行しています...", processedItems, totalItems, "settings.json"));
				MigrateSettings(connection, transaction);
				processedItems++;
				progress.Report(new ClipboardMigrationProgress("設定を移行しました。", processedItems, totalItems, "settings.json"));
			}

			foreach (FileInfo file in legacyFiles)
			{
				cancellationToken.ThrowIfCancellationRequested();
				progress.Report(new ClipboardMigrationProgress("履歴を移行しています...", processedItems, totalItems, file.FullName));
				try
				{
					MigrateHistoryFile(connection, transaction, file);
				}
				catch (IOException ex)
				{
					Logger.Error(ex, $"履歴ファイルの移行をスキップしました: {file.FullName}");
				}
				catch (UnauthorizedAccessException ex)
				{
					Logger.Error(ex, $"履歴ファイルの移行をスキップしました: {file.FullName}");
				}
				processedItems++;
				progress.Report(new ClipboardMigrationProgress("履歴を移行しています...", processedItems, totalItems, file.FullName));
			}

			ClipboardDatabase.SetMigrationFlag(connection, transaction, LegacyMigrationFlag);
			transaction.Commit();
			committed = true;
			progress.Report(new ClipboardMigrationProgress("旧データの移行が完了しました。", processedItems, totalItems));
		}
		catch
		{
			if (!committed)
			{
				transaction.Rollback();
			}
			throw;
		}
	}

	private static void MarkMigrationComplete()
	{
		using SqliteConnection connection = ClipboardDatabase.OpenMigrationConnection();
		using SqliteTransaction transaction = connection.BeginTransaction();
		ClipboardDatabase.SetMigrationFlag(connection, transaction, LegacyMigrationFlag);
		transaction.Commit();
	}

	private static IEnumerable<FileInfo> EnumerateLegacyHistoryFiles(CancellationToken cancellationToken)
	{
		if (!Directory.Exists(ClipboardSettings.LegacyBaseDirectoryPath))
		{
			yield break;
		}

		var baseDirectory = new DirectoryInfo(ClipboardSettings.LegacyBaseDirectoryPath);
		foreach (FileInfo file in EnumerateSupportedFiles(baseDirectory, SearchOption.TopDirectoryOnly, cancellationToken))
		{
			yield return file;
		}

		var datedDirectories = baseDirectory
			.EnumerateDirectories()
			.Select(directory => new
			{
				Directory = directory,
				Date = TryGetHistoryDirectoryDate(directory.Name, out var date) ? date : (DateTime?)null
			})
			.Where(item => item.Date.HasValue)
			.OrderBy(item => item.Date.GetValueOrDefault());

		var datedDirectoryPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		foreach (var item in datedDirectories)
		{
			cancellationToken.ThrowIfCancellationRequested();
			datedDirectoryPaths.Add(item.Directory.FullName);
			foreach (FileInfo file in EnumerateSupportedFiles(item.Directory, SearchOption.TopDirectoryOnly, cancellationToken))
			{
				yield return file;
			}
		}

		var fallbackDirectories = baseDirectory
			.EnumerateDirectories()
			.Where(directory => !datedDirectoryPaths.Contains(directory.FullName))
			.OrderBy(directory => directory.LastWriteTimeUtc);

		foreach (DirectoryInfo directory in fallbackDirectories)
		{
			cancellationToken.ThrowIfCancellationRequested();
			foreach (FileInfo file in EnumerateSupportedFiles(directory, SearchOption.AllDirectories, cancellationToken))
			{
				yield return file;
			}
		}
	}

	private static IEnumerable<FileInfo> EnumerateSupportedFiles(DirectoryInfo directory, SearchOption searchOption, CancellationToken cancellationToken)
	{
		foreach (FileInfo file in directory.EnumerateFiles("*.*", searchOption))
		{
			cancellationToken.ThrowIfCancellationRequested();
			if (ClipboardHistoryMetadata.GetKindFromExtension(file.Extension) != ClipboardHistoryKind.Unknown)
			{
				yield return file;
			}
		}
	}

	private static bool TryGetHistoryDirectoryDate(string directoryName, out DateTime date)
	{
		return DateTime.TryParseExact(
			directoryName,
			"yyyyMMdd",
			CultureInfo.InvariantCulture,
			DateTimeStyles.None,
			out date);
	}

	private static void MigrateSettings(SqliteConnection connection, SqliteTransaction transaction)
	{
		try
		{
			string json = File.ReadAllText(ClipboardSettings.SettingsFilePath);
			var settings = JsonSerializer.Deserialize<ClipboardSettingsData>(json);
			string separator = ClipboardSettings.NormalizeSeparator(settings?.ConcatenationSeparator);
			ClipboardDatabase.SaveConcatenationSeparator(connection, transaction, separator);
		}
		catch (Exception ex)
		{
			Logger.Error(ex, "旧設定の移行に失敗しました。デフォルト設定を使用します。");
			ClipboardDatabase.SaveConcatenationSeparator(connection, transaction, ClipboardSettings.DefaultConcatenationSeparator);
		}
	}

	private static void MigrateHistoryFile(SqliteConnection connection, SqliteTransaction transaction, FileInfo file)
	{
		ClipboardHistoryKind kind = ClipboardHistoryMetadata.GetKindFromExtension(file.Extension);
		if (kind == ClipboardHistoryKind.Unknown)
		{
			return;
		}

		byte[] content = ReadAllBytesShared(file.FullName);
		string contentHash = ClipboardContentHash.CalculateSha256(content);
		ClipboardDatabase.InsertLegacyHistoryIfMissing(
			connection,
			transaction,
			kind,
			content,
			contentHash,
			file.LastWriteTime,
			file.FullName,
			file.LastWriteTime,
			file.Name);
	}

	private static byte[] ReadAllBytesShared(string filePath)
	{
		using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
		using var memoryStream = new MemoryStream();
		stream.CopyTo(memoryStream);
		return memoryStream.ToArray();
	}
}
