using Microsoft.Data.Sqlite;

namespace Clipboard;

internal sealed record ClipboardVideoFrameSummary(TimeSpan Position, byte[] ThumbnailBytes);

internal sealed record ClipboardVideoFrameEdit(byte[] ImageBytes, string PaintStateJson);

// 各コマは独立したキーフレーム。画像と編集状態は元動画への参照にせずDB内に保持する。
internal static class ClipboardVideoFrameStore
{
	public static void Initialize(SqliteConnection connection, SqliteTransaction transaction)
	{
		using var command = connection.CreateCommand();
		command.Transaction = transaction;
		command.CommandText =
			"""
			CREATE TABLE IF NOT EXISTS clipboard_video_frame_edits (
				history_id INTEGER NOT NULL REFERENCES clipboard_history(id) ON DELETE CASCADE,
				position_ticks INTEGER NOT NULL CHECK(position_ticks >= 0),
				image BLOB NOT NULL,
				thumbnail BLOB NOT NULL,
				paint_state TEXT NOT NULL,
				PRIMARY KEY (history_id, position_ticks)
			);
			""";
		command.ExecuteNonQuery();
	}

	public static void Save(
		SqliteConnection connection, SqliteTransaction transaction, long historyId,
		TimeSpan position, byte[] imageBytes, byte[] thumbnailBytes, string paintStateJson)
	{
		ArgumentOutOfRangeException.ThrowIfNegative(position.Ticks);
		if (imageBytes.Length == 0 || thumbnailBytes.Length == 0 || string.IsNullOrWhiteSpace(paintStateJson))
			throw new ArgumentException("コマの画像と編集状態が必要です。");

		using var command = connection.CreateCommand();
		command.Transaction = transaction;
		command.CommandText =
			"""
			INSERT INTO clipboard_video_frame_edits (history_id, position_ticks, image, thumbnail, paint_state)
			SELECT id, $position, $image, $thumbnail, $paint_state
			FROM clipboard_history WHERE id = $id AND kind = $video_kind
			ON CONFLICT(history_id, position_ticks) DO UPDATE SET
				image = excluded.image, thumbnail = excluded.thumbnail, paint_state = excluded.paint_state;
			""";
		command.Parameters.AddWithValue("$id", historyId);
		command.Parameters.AddWithValue("$video_kind", (int)ClipboardHistoryKind.Video);
		command.Parameters.AddWithValue("$position", position.Ticks);
		command.Parameters.AddWithValue("$image", imageBytes);
		command.Parameters.AddWithValue("$thumbnail", thumbnailBytes);
		command.Parameters.AddWithValue("$paint_state", paintStateJson);
		if (command.ExecuteNonQuery() != 1)
			throw new InvalidOperationException("編集元の動画履歴が見つかりません。");

		command.CommandText = "UPDATE clipboard_history SET thumbnail = $thumbnail WHERE id = $id;";
		command.ExecuteNonQuery();
	}

	public static List<ClipboardVideoFrameSummary> LoadSummaries(SqliteConnection connection, long historyId)
	{
		using var command = connection.CreateCommand();
		command.CommandText =
			"""
			SELECT position_ticks, thumbnail FROM clipboard_video_frame_edits
			WHERE history_id = $id ORDER BY position_ticks;
			""";
		command.Parameters.AddWithValue("$id", historyId);
		var frames = new List<ClipboardVideoFrameSummary>();
		using var reader = command.ExecuteReader();
		while (reader.Read())
			frames.Add(new ClipboardVideoFrameSummary(TimeSpan.FromTicks(reader.GetInt64(0)), (byte[])reader.GetValue(1)));
		return frames;
	}

	public static ClipboardVideoFrameEdit? Load(SqliteConnection connection, long historyId, TimeSpan position)
	{
		using var command = connection.CreateCommand();
		command.CommandText =
			"""
			SELECT image, paint_state FROM clipboard_video_frame_edits
			WHERE history_id = $id AND position_ticks = $position;
			""";
		command.Parameters.AddWithValue("$id", historyId);
		command.Parameters.AddWithValue("$position", position.Ticks);
		using var reader = command.ExecuteReader();
		return reader.Read() ? new ClipboardVideoFrameEdit((byte[])reader.GetValue(0), reader.GetString(1)) : null;
	}
}
