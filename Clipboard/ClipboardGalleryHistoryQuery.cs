namespace Clipboard;

internal static class ClipboardGalleryHistoryQuery
{
	public static string Create(bool hasBeforeId)
	{
		string sql =
			"""
			SELECT id, kind, created_at_utc_ticks, preview_text, thumbnail,
				(paint_state IS NOT NULL OR EXISTS (
					SELECT 1 FROM clipboard_video_frame_edits WHERE history_id = clipboard_history.id
				)) AS has_paint_state
			FROM clipboard_history
			WHERE kind IN ($image_kind, $video_kind)
			""";
		if (hasBeforeId)
		{
			sql += " AND id < $before_id";
		}

		return sql + " ORDER BY id DESC LIMIT $limit;";
	}
}
