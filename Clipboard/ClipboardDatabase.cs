using System.IO;
using System.Text;
using Microsoft.Data.Sqlite;

namespace Clipboard;

internal sealed class ClipboardHistorySummary
{
	public required long Id { get; init; }
	public required ClipboardHistoryKind Kind { get; init; }
	public required DateTime CreatedAt { get; init; }
	public required string PreviewText { get; init; }
	public byte[]? ThumbnailBytes { get; init; }
}

internal sealed class ClipboardStoredContent
{
	public required ClipboardHistoryKind Kind { get; init; }
	public required byte[] Bytes { get; init; }
	public string? PlainText { get; init; }
	public string? PaintStateJson { get; init; }
}

internal static class ClipboardDatabase
{
	private const int SchemaVersion = 4;
	private const string ConcatenationSeparatorKey = "ConcatenationSeparator";
	private static readonly object Sync = new();
	private static bool _initialized;

	public static void Initialize()
	{
		lock (Sync)
		{
			if (_initialized)
			{
				return;
			}

			Directory.CreateDirectory(ClipboardSettings.ApplicationDirectoryPath);
			Directory.CreateDirectory(ClipboardSettings.ImageObjectDirectoryPath);

			using var connection = OpenConnection();
			using var transaction = connection.BeginTransaction();
			ExecuteNonQuery(
				connection,
				transaction,
				"""
				CREATE TABLE IF NOT EXISTS app_settings (
					key TEXT PRIMARY KEY,
					value TEXT NOT NULL
				);
				""");
			ExecuteNonQuery(
				connection,
				transaction,
				"""
				CREATE TABLE IF NOT EXISTS migration_state (
					key TEXT PRIMARY KEY,
					value TEXT NOT NULL
				);
				""");
			ExecuteNonQuery(
				connection,
				transaction,
				"""
				CREATE TABLE IF NOT EXISTS clipboard_history (
					id INTEGER PRIMARY KEY AUTOINCREMENT,
					kind INTEGER NOT NULL,
					extension TEXT NOT NULL,
					created_at_utc_ticks INTEGER NOT NULL,
					content_hash TEXT NOT NULL,
					content BLOB NOT NULL,
					plain_text TEXT NULL,
					preview_text TEXT NOT NULL,
					search_text TEXT NOT NULL,
					thumbnail BLOB NULL,
					paint_state TEXT NULL,
					source_file_path TEXT NULL,
					source_last_write_time_utc_ticks INTEGER NULL
				);
				""");
			ExecuteNonQuery(
				connection,
				transaction,
				"""
				CREATE TABLE IF NOT EXISTS clipboard_image_objects (
					oid TEXT PRIMARY KEY,
					byte_length INTEGER NOT NULL,
					relative_path TEXT NOT NULL,
					created_at_utc_ticks INTEGER NOT NULL
				);
				""");
			ExecuteNonQuery(connection, transaction, "DROP INDEX IF EXISTS ix_clipboard_history_created_at;");
			ExecuteNonQuery(connection, transaction, "CREATE INDEX IF NOT EXISTS ix_clipboard_history_kind ON clipboard_history (kind);");
			ExecuteNonQuery(connection, transaction, "CREATE INDEX IF NOT EXISTS ix_clipboard_history_content_hash ON clipboard_history (content_hash);");
			ExecuteNonQuery(
				connection,
				transaction,
				"CREATE UNIQUE INDEX IF NOT EXISTS ux_clipboard_history_source_file_path ON clipboard_history (source_file_path) WHERE source_file_path IS NOT NULL;");

			int currentSchemaVersion = GetSchemaVersion(connection, transaction);
			if (currentSchemaVersion < 2)
			{
				MigrateImageHistoryToFilePointers(connection, transaction);
			}
			EnsureHistoryPaintStateColumn(connection, transaction);
			EnsureHistoryPlainTextColumn(connection, transaction);

			SetStateValue(connection, transaction, "schema_version", SchemaVersion.ToString());
			transaction.Commit();
			_initialized = true;
		}
	}

	public static string LoadConcatenationSeparator()
	{
		return GetSettingValue(ConcatenationSeparatorKey) ?? ClipboardSettings.DefaultConcatenationSeparator;
	}

	public static void SaveConcatenationSeparator(string separator)
	{
		SetSettingValue(ConcatenationSeparatorKey, separator);
	}

	public static string? GetSettingValue(string key)
	{
		Initialize();
		lock (Sync)
		{
			using var connection = OpenConnection();
			using var command = connection.CreateCommand();
			command.CommandText = "SELECT value FROM app_settings WHERE key = $key;";
			AddParameter(command, "$key", key);
			return command.ExecuteScalar() as string;
		}
	}

	public static void SetSettingValue(string key, string value)
	{
		Initialize();
		lock (Sync)
		{
			using var connection = OpenConnection();
			using var transaction = connection.BeginTransaction();
			SetSettingValue(connection, transaction, key, value);
			transaction.Commit();
		}
	}

	public static bool GetMigrationFlag(string key)
	{
		Initialize();
		lock (Sync)
		{
			using var connection = OpenConnection();
			using var command = connection.CreateCommand();
			command.CommandText = "SELECT value FROM migration_state WHERE key = $key;";
			AddParameter(command, "$key", key);
			return string.Equals(command.ExecuteScalar() as string, "1", StringComparison.Ordinal);
		}
	}

	public static void SetMigrationFlag(string key)
	{
		Initialize();
		lock (Sync)
		{
			using var connection = OpenConnection();
			using var transaction = connection.BeginTransaction();
			SetStateValue(connection, transaction, key, "1");
			SetStateValue(connection, transaction, $"{key}_completed_at_utc", DateTime.UtcNow.ToString("O"));
			transaction.Commit();
		}
	}

	public static void InsertHistory(
		ClipboardHistoryKind kind,
		byte[] content,
		string contentHash,
		DateTime createdAt,
		string? sourceFilePath = null,
		DateTime? sourceLastWriteTime = null,
		string? displayName = null,
		string? paintStateJson = null,
		string? plainText = null)
	{
		Initialize();
		var metadata = ClipboardHistoryMetadata.Create(content, kind, createdAt, displayName, sourceFilePath, plainText);

		lock (Sync)
		{
			using var connection = OpenConnection();
			using var transaction = connection.BeginTransaction();
			InsertHistory(
				connection,
				transaction,
				kind,
				content,
				contentHash,
				createdAt,
				metadata,
				sourceFilePath,
				sourceLastWriteTime,
				paintStateJson,
				plainText);
			transaction.Commit();
		}
	}

	public static bool InsertPaintImageHistoryUnlessLatestMatches(
		byte[] content,
		string contentHash,
		DateTime createdAt,
		string paintStateJson)
	{
		Initialize();

		lock (Sync)
		{
			using var connection = OpenConnection();
			using var transaction = connection.BeginTransaction();
			if (TryLoadLatestHistoryIdentity(connection, transaction, out long latestId, out ClipboardHistoryKind latestKind, out string latestContentHash) &&
				latestKind == ClipboardHistoryKind.Image &&
				string.Equals(latestContentHash, contentHash, StringComparison.Ordinal))
			{
				UpdateHistoryPaintState(connection, transaction, latestId, paintStateJson);
				transaction.Commit();
				return false;
			}

			var metadata = ClipboardHistoryMetadata.Create(content, ClipboardHistoryKind.Image, createdAt, null, null);
			InsertHistory(
				connection,
				transaction,
				ClipboardHistoryKind.Image,
				content,
				contentHash,
				createdAt,
				metadata,
				null,
				null,
				paintStateJson,
				null);
			transaction.Commit();
			return true;
		}
	}

	public static bool InsertLegacyHistoryIfMissing(
		SqliteConnection connection,
		SqliteTransaction transaction,
		ClipboardHistoryKind kind,
		byte[] content,
		string contentHash,
		DateTime createdAt,
		string sourceFilePath,
		DateTime sourceLastWriteTime,
		string displayName)
	{
		if (HistorySourceExists(connection, transaction, sourceFilePath))
		{
			return false;
		}

		var metadata = ClipboardHistoryMetadata.Create(content, kind, createdAt, displayName, sourceFilePath);
		InsertHistory(connection, transaction, kind, content, contentHash, createdAt, metadata, sourceFilePath, sourceLastWriteTime, null, null);
		return true;
	}

	public static List<ClipboardHistorySummary> LoadHistorySummaries(
		string? searchText,
		int? maxEntryCount,
		CancellationToken cancellationToken)
	{
		return LoadHistorySummariesCore(searchText, null, maxEntryCount, cancellationToken);
	}

	public static List<ClipboardHistorySummary> LoadHistoryPageSummaries(
		long beforeId,
		int maxEntryCount,
		CancellationToken cancellationToken)
	{
		return LoadHistorySummariesCore(null, beforeId, maxEntryCount, cancellationToken);
	}

	public static List<ClipboardHistorySummary> LoadHistorySearchSummaries(
		string searchText,
		long? beforeId,
		int maxEntryCount,
		CancellationToken cancellationToken)
	{
		if (string.IsNullOrWhiteSpace(searchText))
		{
			return new List<ClipboardHistorySummary>();
		}

		return LoadHistorySummariesCore(searchText, beforeId, maxEntryCount, cancellationToken);
	}

	private static List<ClipboardHistorySummary> LoadHistorySummariesCore(
		string? searchText,
		long? beforeId,
		int? maxEntryCount,
		CancellationToken cancellationToken)
	{
		if (maxEntryCount.HasValue && maxEntryCount.Value <= 0)
		{
			return new List<ClipboardHistorySummary>();
		}

		Initialize();
		string[] searchTerms = SplitSearchTerms(searchText);
		lock (Sync)
		{
			using var connection = OpenConnection();
			using var command = connection.CreateCommand();
			command.CommandText = CreateHistorySummariesSql(searchTerms, beforeId.HasValue, maxEntryCount.HasValue);
			if (beforeId.HasValue)
			{
				AddParameter(command, "$before_id", beforeId.Value);
			}
			for (int i = 0; i < searchTerms.Length; i++)
			{
				AddParameter(command, $"$term{i}", $"%{EscapeLikePattern(searchTerms[i])}%");
			}
			if (maxEntryCount.HasValue)
			{
				AddParameter(command, "$limit", maxEntryCount.Value);
			}

			var entries = new List<ClipboardHistorySummary>();
			using var reader = command.ExecuteReader();
			while (reader.Read())
			{
				cancellationToken.ThrowIfCancellationRequested();
				entries.Add(ReadHistorySummary(reader));
			}

			return entries;
		}
	}

	public static void DeleteHistory(IReadOnlyCollection<long> ids)
	{
		if (ids.Count == 0)
		{
			return;
		}

		Initialize();
		lock (Sync)
		{
			using var connection = OpenConnection();
			using var transaction = connection.BeginTransaction();
			HashSet<string> imageOids = LoadImageOidsForHistoryIds(connection, transaction, ids);
			using var command = connection.CreateCommand();
			command.Transaction = transaction;
			var parameterNames = new List<string>(ids.Count);
			int index = 0;
			foreach (long id in ids)
			{
				string parameterName = $"$id{index}";
				parameterNames.Add(parameterName);
				AddParameter(command, parameterName, id);
				index++;
			}

			command.CommandText = $"DELETE FROM clipboard_history WHERE id IN ({string.Join(", ", parameterNames)});";
			command.ExecuteNonQuery();
			List<string> unreferencedImageOids = DeleteUnreferencedImageObjects(connection, transaction, imageOids);
			transaction.Commit();

			foreach (string oid in unreferencedImageOids)
			{
				ClipboardImageStore.DeleteObjectFile(oid);
			}
		}
	}

	public static ClipboardStoredContent? LoadContent(long id)
	{
		Initialize();
		lock (Sync)
		{
			using var connection = OpenConnection();
			using var command = connection.CreateCommand();
			command.CommandText = "SELECT kind, content, paint_state, plain_text FROM clipboard_history WHERE id = $id;";
			AddParameter(command, "$id", id);
			using var reader = command.ExecuteReader();
			if (!reader.Read())
			{
				return null;
			}

			var kind = (ClipboardHistoryKind)reader.GetInt32(0);
			byte[] content = (byte[])reader.GetValue(1);
			string? paintStateJson = reader.IsDBNull(2) ? null : reader.GetString(2);
			string? plainText = reader.IsDBNull(3) ? null : reader.GetString(3);
			byte[] resolvedContent = content;
			if (kind == ClipboardHistoryKind.Image &&
				!ClipboardImageStore.TryResolveImageBytes(content, out resolvedContent, out _, out string? errorMessage))
			{
				Logger.Warning($"ClipboardDatabase: {errorMessage} HistoryId={id}");
				return null;
			}
			if (kind == ClipboardHistoryKind.Image)
			{
				content = resolvedContent;
			}

			return new ClipboardStoredContent
			{
				Kind = kind,
				Bytes = content,
				PlainText = plainText,
				PaintStateJson = paintStateJson
			};
		}
	}

	public static SqliteConnection OpenMigrationConnection()
	{
		Initialize();
		return OpenConnection();
	}

	public static void SaveConcatenationSeparator(SqliteConnection connection, SqliteTransaction transaction, string separator)
	{
		SetSettingValue(connection, transaction, ConcatenationSeparatorKey, separator);
	}

	public static void SetMigrationFlag(SqliteConnection connection, SqliteTransaction transaction, string key)
	{
		SetStateValue(connection, transaction, key, "1");
		SetStateValue(connection, transaction, $"{key}_completed_at_utc", DateTime.UtcNow.ToString("O"));
	}

	private static int GetSchemaVersion(SqliteConnection connection, SqliteTransaction transaction)
	{
		using var command = connection.CreateCommand();
		command.Transaction = transaction;
		command.CommandText = "SELECT value FROM migration_state WHERE key = 'schema_version';";
		return int.TryParse(command.ExecuteScalar() as string, out int version) ? version : 0;
	}

	private static void EnsureHistoryPaintStateColumn(SqliteConnection connection, SqliteTransaction transaction)
	{
		if (ColumnExists(connection, transaction, "clipboard_history", "paint_state"))
		{
			return;
		}

		ExecuteNonQuery(connection, transaction, "ALTER TABLE clipboard_history ADD COLUMN paint_state TEXT NULL;");
	}

	private static void EnsureHistoryPlainTextColumn(SqliteConnection connection, SqliteTransaction transaction)
	{
		if (ColumnExists(connection, transaction, "clipboard_history", "plain_text"))
		{
			return;
		}

		ExecuteNonQuery(connection, transaction, "ALTER TABLE clipboard_history ADD COLUMN plain_text TEXT NULL;");
	}

	private static bool ColumnExists(
		SqliteConnection connection,
		SqliteTransaction transaction,
		string tableName,
		string columnName)
	{
		using var command = connection.CreateCommand();
		command.Transaction = transaction;
		command.CommandText = $"PRAGMA table_info({tableName});";
		using var reader = command.ExecuteReader();
		while (reader.Read())
		{
			if (string.Equals(reader.GetString(1), columnName, StringComparison.OrdinalIgnoreCase))
			{
				return true;
			}
		}

		return false;
	}

	private static void MigrateImageHistoryToFilePointers(SqliteConnection connection, SqliteTransaction transaction)
	{
		var updates = new List<(long Id, ClipboardImagePointer Pointer, byte[] PointerBytes)>();
		var imageObjects = new Dictionary<string, ClipboardImagePointer>(StringComparer.Ordinal);
		using (var command = connection.CreateCommand())
		{
			command.Transaction = transaction;
			command.CommandText =
				"""
				SELECT id, content_hash, content
				FROM clipboard_history
				WHERE kind = $kind
				ORDER BY id;
				""";
			AddParameter(command, "$kind", (int)ClipboardHistoryKind.Image);

			using var reader = command.ExecuteReader();
			while (reader.Read())
			{
				long id = reader.GetInt64(0);
				string contentHash = reader.GetString(1);
				byte[] content = (byte[])reader.GetValue(2);

				if (ClipboardImagePointer.TryParse(content, out ClipboardImagePointer? existingPointer) &&
					existingPointer != null)
				{
					imageObjects[existingPointer.Oid] = existingPointer;
					if (!string.Equals(contentHash, existingPointer.Oid, StringComparison.Ordinal))
					{
						updates.Add((id, existingPointer, existingPointer.ToBytes()));
					}
					continue;
				}

				ClipboardImagePointer pointer = ClipboardImageStore.StoreImage(content);
				imageObjects[pointer.Oid] = pointer;
				updates.Add((id, pointer, pointer.ToBytes()));
			}
		}

		foreach (ClipboardImagePointer pointer in imageObjects.Values)
		{
			UpsertImageObject(connection, transaction, pointer);
		}

		foreach ((long id, ClipboardImagePointer pointer, byte[] pointerBytes) in updates)
		{
			UpdateHistoryImagePointer(connection, transaction, id, pointer, pointerBytes);
		}

		if (updates.Count > 0)
		{
			Logger.Info($"ClipboardDatabase: 画像履歴をファイルポインタへ移行しました。Count={updates.Count}");
		}
	}

	private static void UpdateHistoryImagePointer(
		SqliteConnection connection,
		SqliteTransaction transaction,
		long id,
		ClipboardImagePointer pointer,
		byte[] pointerBytes)
	{
		using var command = connection.CreateCommand();
		command.Transaction = transaction;
		command.CommandText =
			"""
			UPDATE clipboard_history
			SET content_hash = $content_hash,
				content = $content
			WHERE id = $id;
			""";
		AddParameter(command, "$content_hash", pointer.Oid);
		AddParameter(command, "$content", pointerBytes);
		AddParameter(command, "$id", id);
		command.ExecuteNonQuery();
	}

	private static bool TryLoadLatestHistoryIdentity(
		SqliteConnection connection,
		SqliteTransaction transaction,
		out long id,
		out ClipboardHistoryKind kind,
		out string contentHash)
	{
		id = 0;
		kind = ClipboardHistoryKind.Unknown;
		contentHash = string.Empty;

		using var command = connection.CreateCommand();
		command.Transaction = transaction;
		command.CommandText =
			"""
			SELECT id, kind, content_hash
			FROM clipboard_history
			ORDER BY id DESC
			LIMIT 1;
			""";
		using var reader = command.ExecuteReader();
		if (!reader.Read())
		{
			return false;
		}

		id = reader.GetInt64(0);
		kind = (ClipboardHistoryKind)reader.GetInt32(1);
		contentHash = reader.GetString(2);
		return true;
	}

	private static void UpdateHistoryPaintState(
		SqliteConnection connection,
		SqliteTransaction transaction,
		long id,
		string paintStateJson)
	{
		using var command = connection.CreateCommand();
		command.Transaction = transaction;
		command.CommandText =
			"""
			UPDATE clipboard_history
			SET paint_state = $paint_state
			WHERE id = $id;
			""";
		AddParameter(command, "$paint_state", paintStateJson);
		AddParameter(command, "$id", id);
		command.ExecuteNonQuery();
	}

	private static void UpsertImageObject(
		SqliteConnection connection,
		SqliteTransaction transaction,
		ClipboardImagePointer pointer)
	{
		using var command = connection.CreateCommand();
		command.Transaction = transaction;
		command.CommandText =
			"""
			INSERT INTO clipboard_image_objects (
				oid,
				byte_length,
				relative_path,
				created_at_utc_ticks
			)
			VALUES (
				$oid,
				$byte_length,
				$relative_path,
				$created_at_utc_ticks
			)
			ON CONFLICT(oid) DO UPDATE SET
				byte_length = excluded.byte_length,
				relative_path = excluded.relative_path;
			""";
		AddParameter(command, "$oid", pointer.Oid);
		AddParameter(command, "$byte_length", pointer.Size);
		AddParameter(command, "$relative_path", ClipboardImageStore.GetObjectRelativePath(pointer.Oid));
		AddParameter(command, "$created_at_utc_ticks", DateTime.UtcNow.Ticks);
		command.ExecuteNonQuery();
	}

	private static HashSet<string> LoadImageOidsForHistoryIds(
		SqliteConnection connection,
		SqliteTransaction transaction,
		IReadOnlyCollection<long> ids)
	{
		var oids = new HashSet<string>(StringComparer.Ordinal);
		if (ids.Count == 0)
		{
			return oids;
		}

		using var command = connection.CreateCommand();
		command.Transaction = transaction;
		var parameterNames = new List<string>(ids.Count);
		int index = 0;
		foreach (long id in ids)
		{
			string parameterName = $"$id{index}";
			parameterNames.Add(parameterName);
			AddParameter(command, parameterName, id);
			index++;
		}

		command.CommandText =
			$"""
			SELECT content_hash, content
			FROM clipboard_history
			WHERE kind = $kind AND id IN ({string.Join(", ", parameterNames)});
			""";
		AddParameter(command, "$kind", (int)ClipboardHistoryKind.Image);

		using var reader = command.ExecuteReader();
		while (reader.Read())
		{
			string contentHash = reader.GetString(0);
			byte[] content = (byte[])reader.GetValue(1);
			if (ClipboardImagePointer.TryParse(content, out ClipboardImagePointer? pointer) &&
				pointer != null)
			{
				oids.Add(pointer.Oid);
			}
			else if (ClipboardContentHash.TryNormalizeSha256(contentHash, out string oid))
			{
				oids.Add(oid);
			}
			else
			{
				oids.Add(ClipboardContentHash.CalculateSha256(content));
			}
		}

		return oids;
	}

	private static List<string> DeleteUnreferencedImageObjects(
		SqliteConnection connection,
		SqliteTransaction transaction,
		HashSet<string> imageOids)
	{
		var unreferencedImageOids = new List<string>();
		foreach (string oid in imageOids)
		{
			if (ImageObjectHasReference(connection, transaction, oid))
			{
				continue;
			}

			DeleteImageObjectMetadata(connection, transaction, oid);
			unreferencedImageOids.Add(oid);
		}

		return unreferencedImageOids;
	}

	private static bool ImageObjectHasReference(
		SqliteConnection connection,
		SqliteTransaction transaction,
		string oid)
	{
		using var command = connection.CreateCommand();
		command.Transaction = transaction;
		command.CommandText =
			"""
			SELECT 1
			FROM clipboard_history
			WHERE kind = $kind AND content_hash = $oid
			LIMIT 1;
			""";
		AddParameter(command, "$kind", (int)ClipboardHistoryKind.Image);
		AddParameter(command, "$oid", oid);
		return command.ExecuteScalar() != null;
	}

	private static void DeleteImageObjectMetadata(
		SqliteConnection connection,
		SqliteTransaction transaction,
		string oid)
	{
		using var command = connection.CreateCommand();
		command.Transaction = transaction;
		command.CommandText = "DELETE FROM clipboard_image_objects WHERE oid = $oid;";
		AddParameter(command, "$oid", oid);
		command.ExecuteNonQuery();
	}

	private static SqliteConnection OpenConnection()
	{
		var connectionString = new SqliteConnectionStringBuilder
		{
			DataSource = ClipboardSettings.DatabaseFilePath,
			Mode = SqliteOpenMode.ReadWriteCreate,
			Cache = SqliteCacheMode.Shared
		}.ToString();
		var connection = new SqliteConnection(connectionString);
		connection.Open();
		return connection;
	}

	private static void InsertHistory(
		SqliteConnection connection,
		SqliteTransaction transaction,
		ClipboardHistoryKind kind,
		byte[] content,
		string contentHash,
		DateTime createdAt,
		ClipboardHistoryMetadata metadata,
		string? sourceFilePath,
		DateTime? sourceLastWriteTime,
		string? paintStateJson,
		string? plainText)
	{
		using var command = connection.CreateCommand();
		command.Transaction = transaction;
		long? sourceLastWriteTimeUtcTicks = sourceLastWriteTime.HasValue
			? sourceLastWriteTime.Value.ToUniversalTime().Ticks
			: null;
		byte[] storedContent = content;
		string storedContentHash = contentHash;
		string? storedPaintStateJson = kind == ClipboardHistoryKind.Image ? paintStateJson : null;
		string? storedPlainText = CreateStoredPlainText(kind, content, plainText);
		if (kind == ClipboardHistoryKind.Image)
		{
			ClipboardImagePointer pointer = ClipboardImageStore.StoreImage(content);
			UpsertImageObject(connection, transaction, pointer);
			storedContent = pointer.ToBytes();
			storedContentHash = pointer.Oid;
		}

		command.CommandText =
			"""
			INSERT OR IGNORE INTO clipboard_history (
				kind,
				extension,
				created_at_utc_ticks,
				content_hash,
				content,
				plain_text,
				preview_text,
				search_text,
				thumbnail,
				paint_state,
				source_file_path,
				source_last_write_time_utc_ticks
			)
			VALUES (
				$kind,
				$extension,
				$created_at_utc_ticks,
				$content_hash,
				$content,
				$plain_text,
				$preview_text,
				$search_text,
				$thumbnail,
				$paint_state,
				$source_file_path,
				$source_last_write_time_utc_ticks
			);
			""";
		AddParameter(command, "$kind", (int)kind);
		AddParameter(command, "$extension", ClipboardHistoryMetadata.GetExtension(kind));
		AddParameter(command, "$created_at_utc_ticks", createdAt.ToUniversalTime().Ticks);
		AddParameter(command, "$content_hash", storedContentHash);
		AddParameter(command, "$content", storedContent);
		AddParameter(command, "$plain_text", storedPlainText);
		AddParameter(command, "$preview_text", metadata.PreviewText);
		AddParameter(command, "$search_text", metadata.SearchText);
		AddParameter(command, "$thumbnail", metadata.ThumbnailBytes);
		AddParameter(command, "$paint_state", storedPaintStateJson);
		AddParameter(command, "$source_file_path", sourceFilePath);
		AddParameter(command, "$source_last_write_time_utc_ticks", sourceLastWriteTimeUtcTicks);
		command.ExecuteNonQuery();
	}

	private static string? CreateStoredPlainText(ClipboardHistoryKind kind, byte[] content, string? plainText)
	{
		if (plainText != null)
		{
			return plainText;
		}

		return kind == ClipboardHistoryKind.Text
			? Encoding.UTF8.GetString(content)
			: null;
	}

	private static bool HistorySourceExists(SqliteConnection connection, SqliteTransaction transaction, string sourceFilePath)
	{
		using var command = connection.CreateCommand();
		command.Transaction = transaction;
		command.CommandText = "SELECT 1 FROM clipboard_history WHERE source_file_path = $source_file_path LIMIT 1;";
		AddParameter(command, "$source_file_path", sourceFilePath);
		return command.ExecuteScalar() != null;
	}

	private static ClipboardHistorySummary ReadHistorySummary(SqliteDataReader reader)
	{
		long utcTicks = reader.GetInt64(2);
		byte[]? thumbnailBytes = reader.IsDBNull(4) ? null : (byte[])reader.GetValue(4);
		return new ClipboardHistorySummary
		{
			Id = reader.GetInt64(0),
			Kind = (ClipboardHistoryKind)reader.GetInt32(1),
			CreatedAt = new DateTime(utcTicks, DateTimeKind.Utc).ToLocalTime(),
			PreviewText = reader.GetString(3),
			ThumbnailBytes = thumbnailBytes
		};
	}

	private static string CreateHistorySummariesSql(
		string[] searchTerms,
		bool hasBeforeId,
		bool hasLimit)
	{
		string sql =
			"""
			SELECT id, kind, created_at_utc_ticks, preview_text, thumbnail
			FROM clipboard_history
			""";
		var whereClauses = new List<string>();
		if (hasBeforeId)
		{
			whereClauses.Add("id < $before_id");
		}
		if (searchTerms.Length > 0)
		{
			whereClauses.AddRange(searchTerms.Select((_, index) => $"search_text LIKE $term{index} ESCAPE '\\'"));
		}
		if (whereClauses.Count > 0)
		{
			sql += " WHERE " + string.Join(" AND ", whereClauses);
		}

		sql += " ORDER BY id DESC";
		if (hasLimit)
		{
			sql += " LIMIT $limit";
		}

		return sql + ";";
	}

	private static string[] SplitSearchTerms(string? searchText)
	{
		if (string.IsNullOrWhiteSpace(searchText))
		{
			return Array.Empty<string>();
		}

		return searchText.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
	}

	private static string EscapeLikePattern(string value)
	{
		return value
			.Replace(@"\", @"\\")
			.Replace("%", @"\%")
			.Replace("_", @"\_");
	}

	private static void SetSettingValue(SqliteConnection connection, SqliteTransaction transaction, string key, string value)
	{
		using var command = connection.CreateCommand();
		command.Transaction = transaction;
		command.CommandText =
			"""
			INSERT INTO app_settings (key, value)
			VALUES ($key, $value)
			ON CONFLICT(key) DO UPDATE SET value = excluded.value;
			""";
		AddParameter(command, "$key", key);
		AddParameter(command, "$value", value);
		command.ExecuteNonQuery();
	}

	private static void SetStateValue(SqliteConnection connection, SqliteTransaction transaction, string key, string value)
	{
		using var command = connection.CreateCommand();
		command.Transaction = transaction;
		command.CommandText =
			"""
			INSERT INTO migration_state (key, value)
			VALUES ($key, $value)
			ON CONFLICT(key) DO UPDATE SET value = excluded.value;
			""";
		AddParameter(command, "$key", key);
		AddParameter(command, "$value", value);
		command.ExecuteNonQuery();
	}

	private static void ExecuteNonQuery(SqliteConnection connection, SqliteTransaction transaction, string sql)
	{
		using var command = connection.CreateCommand();
		command.Transaction = transaction;
		command.CommandText = sql;
		command.ExecuteNonQuery();
	}

	private static void AddParameter(SqliteCommand command, string name, object? value)
	{
		var parameter = command.CreateParameter();
		parameter.ParameterName = name;
		parameter.Value = value ?? DBNull.Value;
		command.Parameters.Add(parameter);
	}
}
