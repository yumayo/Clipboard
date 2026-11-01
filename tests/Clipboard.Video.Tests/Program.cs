using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Clipboard;
using Microsoft.Data.Sqlite;

int passed = 0;
int failed = 0;
string first = Path.Combine(Path.GetTempPath(), "動画 #1 [確認] \"a\".MP4");
string second = Path.Combine(Path.GetTempPath(), "another clip.webm");

Run("既存DBの種別番号を維持する", () =>
{
	Equal(0, (int)ClipboardHistoryKind.Image);
	Equal(1, (int)ClipboardHistoryKind.Html);
	Equal(2, (int)ClipboardHistoryKind.Rtf);
	Equal(3, (int)ClipboardHistoryKind.Text);
	Equal(4, (int)ClipboardHistoryKind.Unknown);
	Equal(5, (int)ClipboardHistoryKind.Video);
});

Run("複数動画・日本語・記号を含むパスを復元する", () =>
{
	string[] paths = ClipboardVideoFiles.Decode(ClipboardVideoFiles.Encode(new[] { first, second }));
	Equal(first, paths[0]);
	Equal(second, paths[1]);
	Equal(2, paths.Length);
});

Run("混在したファイルから動画だけを取り出し重複を除く", () =>
{
	string[] paths = ClipboardVideoFiles.Filter(new[]
	{
		first, first.ToLowerInvariant(), Path.ChangeExtension(first, ".png"), second,
		"relative.mp4", "https://example.invalid/movie.mp4", ""
	});
	Equal(2, paths.Length);
	Equal(first, paths[0]);
	Equal(second, paths[1]);
});

Run("破損した履歴は空として扱う", () =>
{
	foreach (string content in new[] { "", "null", "{}", "[", "[42]", "[{}]" })
	{
		Equal(0, ClipboardVideoFiles.Decode(Encoding.UTF8.GetBytes(content)).Length);
	}
	byte[] mixed = JsonSerializer.SerializeToUtf8Bytes(new string?[] { null, first, "relative.mp4" });
	Equal(first, ClipboardVideoFiles.Decode(mixed).Single());
});

Run("元ファイルがなくても履歴の参照を復元できる", () =>
{
	string missing = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".mkv");
	Equal(missing, ClipboardVideoFiles.Decode(ClipboardVideoFiles.Encode(new[] { missing })).Single());
});

Run("再生エンジンの位置が総時間を超えても表示位置は末尾に収まる", () =>
{
	TimeSpan duration = TimeSpan.FromSeconds(1);
	Equal(duration, ClipboardVideoTimeline.ClampPosition(TimeSpan.FromMilliseconds(1115), duration));
	Equal(duration, ClipboardVideoTimeline.ClampPosition(TimeSpan.MaxValue, duration));
});

Run("再生位置の補正は範囲内の時刻を保ち、負の位置や長さ不明にも対応する", () =>
{
	TimeSpan duration = TimeSpan.FromSeconds(1);
	foreach (TimeSpan position in new[] { TimeSpan.Zero, TimeSpan.FromTicks(1234567), duration })
	{
		Equal(position, ClipboardVideoTimeline.ClampPosition(position, duration));
	}
	Equal(TimeSpan.Zero, ClipboardVideoTimeline.ClampPosition(TimeSpan.FromMilliseconds(-1), duration));
	Equal(TimeSpan.Zero, ClipboardVideoTimeline.ClampPosition(duration, TimeSpan.Zero));
	Equal(TimeSpan.Zero, ClipboardVideoTimeline.ClampPosition(duration, TimeSpan.FromSeconds(-1)));
});

Run("30fpsの隣接コマへ進み、同じコマへ戻る", () =>
{
	TimeSpan duration = TimeSpan.FromSeconds(10);
	TimeSpan next = ClipboardVideoTimeline.Step(TimeSpan.Zero, duration, 30, 1);
	Equal(333333L, next.Ticks);
	Equal(TimeSpan.Zero, ClipboardVideoTimeline.Step(next, duration, 30, -1));
});

Run("24fpsの連続操作でコマが抜けたり停止したりしない", () =>
{
	TimeSpan position = TimeSpan.Zero;
	TimeSpan duration = TimeSpan.FromMinutes(2);
	for (int index = 1; index <= 2400; index++)
	{
		position = ClipboardVideoTimeline.Step(position, duration, 24, 1);
		Equal((long)Math.Round(index * TimeSpan.TicksPerSecond / 24.0), position.Ticks);
	}
	for (int index = 2399; index >= 0; index--)
	{
		position = ClipboardVideoTimeline.Step(position, duration, 24, -1);
		Equal((long)Math.Round(index * TimeSpan.TicksPerSecond / 24.0), position.Ticks);
	}
});

Run("29.97fpsで長くコマ送りしても丸め誤差が蓄積しない", () =>
{
	TimeSpan position = TimeSpan.Zero;
	for (int index = 0; index < 1800; index++)
	{
		position = ClipboardVideoTimeline.Step(position, TimeSpan.FromHours(1), 29.97, 1);
	}
	Equal(TimeSpan.FromTicks(600600601), position);
});

Run("動画の60・29.94・59.94fpsを補正せずにコマ送りする", () =>
{
	foreach (var (fps, frames, expectedSeconds) in new[]
	{
		(60.0, 600, 10),
		(29.94, 1497, 50),
		(59.94, 2997, 50)
	})
	{
		TimeSpan position = TimeSpan.Zero;
		TimeSpan duration = TimeSpan.FromMinutes(2);
		for (int index = 0; index < frames; index++)
		{
			position = ClipboardVideoTimeline.Step(position, duration, fps, 1);
		}
		Equal(TimeSpan.FromSeconds(expectedSeconds), position);
		for (int index = 0; index < frames; index++)
		{
			position = ClipboardVideoTimeline.Step(position, duration, fps, -1);
		}
		Equal(TimeSpan.Zero, position);
	}
});

Run("コマの途中から前後それぞれのコマへ移動する", () =>
{
	Equal(666667L, ClipboardVideoTimeline.Step(TimeSpan.FromMilliseconds(50), TimeSpan.FromSeconds(2), 30, 1).Ticks);
	Equal(333333L, ClipboardVideoTimeline.Step(TimeSpan.FromMilliseconds(50), TimeSpan.FromSeconds(2), 30, -1).Ticks);
});

Run("先頭と末尾を越えない", () =>
{
	TimeSpan duration = TimeSpan.FromSeconds(2);
	Equal(TimeSpan.Zero, ClipboardVideoTimeline.Step(TimeSpan.Zero, duration, 30, -1));
	Equal(19666667L, ClipboardVideoTimeline.Step(duration, duration, 30, 1).Ticks);
	Equal(19666667L, ClipboardVideoTimeline.Step(duration, duration, 30, -1).Ticks);
	Equal(TimeSpan.Zero, ClipboardVideoTimeline.Step(TimeSpan.Zero, TimeSpan.FromTicks(1), 30, 1));
});

Run("FPSや長さが不明でも不正な位置を返さない", () =>
{
	foreach (double fps in new[] { 0, -1, double.NaN, double.PositiveInfinity })
	{
		Equal(TimeSpan.Zero, ClipboardVideoTimeline.Step(TimeSpan.Zero, TimeSpan.FromSeconds(1), fps, 1));
	}
	Equal(TimeSpan.Zero, ClipboardVideoTimeline.Step(TimeSpan.Zero, TimeSpan.Zero, 30, 1));
});

Run("9コマが収まる幅では現在のコマを中央に前後4コマを表示する", () =>
{
	TimeSpan?[] positions = ClipboardVideoTimeline.PreviewPositions(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(3), 30, 1368);
	Equal(9, positions.Length);
	Equal(TimeSpan.FromSeconds(1), positions[4]!.Value);
	Equal(8666667L, positions[0]!.Value.Ticks);
	Equal(11333333L, positions[8]!.Value.Ticks);
});

Run("横幅を広げると中央を維持したまま左右のコマ数が増える", () =>
{
	foreach (var (width, count) in new[] { (320.0, 1), (684.0, 3), (1000.0, 5), (1400.0, 9), (2000.0, 13), (2800.0, 19) })
	{
		TimeSpan?[] positions = ClipboardVideoTimeline.PreviewPositions(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(3), 30, width);
		Equal(count, positions.Length);
		Equal(TimeSpan.FromSeconds(1), positions[count / 2]!.Value);
		Equal(true, count * ClipboardVideoTimeline.PreviewFrameWidth <= width);
	}
});

Run("先頭の前と末尾の後は空欄にし、現在位置を中央から動かさない", () =>
{
	TimeSpan duration = TimeSpan.FromSeconds(2);
	TimeSpan?[] start = ClipboardVideoTimeline.PreviewPositions(TimeSpan.Zero, duration, 30, 1368);
	Equal(true, start.Take(4).All(value => value == null));
	Equal(TimeSpan.Zero, start[4]!.Value);
	TimeSpan?[] end = ClipboardVideoTimeline.PreviewPositions(duration, duration, 30, 1368);
	Equal(19666667L, end[4]!.Value.Ticks);
	Equal(true, end.Skip(5).All(value => value == null));
});

Run("コマより短い動画でも中央の1コマだけを表示する", () =>
{
	TimeSpan?[] positions = ClipboardVideoTimeline.PreviewPositions(TimeSpan.Zero, TimeSpan.FromTicks(1), 30, 1368);
	Equal(9, positions.Length);
	Equal(1, positions.Count(value => value.HasValue));
	Equal(TimeSpan.Zero, positions[4]!.Value);
});

Run("コマの途中をシークするとその位置を含むコマが中央に来る", () =>
{
	TimeSpan?[] positions = ClipboardVideoTimeline.PreviewPositions(TimeSpan.FromMilliseconds(50), TimeSpan.FromSeconds(2), 30, 1368);
	Equal(333333L, positions[4]!.Value.Ticks);
	Equal(666667L, positions[5]!.Value.Ticks);
});

Run("小数FPSでコマ送りした位置も中央と前後のコマが一致する", () =>
{
	foreach (double fps in new[] { 24, 29.97, 59.94 })
	{
		TimeSpan duration = TimeSpan.FromSeconds(10);
		TimeSpan center = TimeSpan.FromTicks((long)Math.Round(100 * TimeSpan.TicksPerSecond / fps));
		TimeSpan?[] positions = ClipboardVideoTimeline.PreviewPositions(center, duration, fps, 1368);
		Equal(center, positions[4]!.Value);
		Equal(ClipboardVideoTimeline.Step(center, duration, fps, -1), positions[3]!.Value);
		Equal(ClipboardVideoTimeline.Step(center, duration, fps, 1), positions[5]!.Value);
	}
});

Run("FPS・長さ・幅が不明なときはコマ画像を要求しない", () =>
{
	foreach (double invalid in new[] { 0, -1, double.NaN, double.PositiveInfinity })
	{
		Equal(0, ClipboardVideoTimeline.PreviewPositions(TimeSpan.Zero, TimeSpan.FromSeconds(1), invalid, 684).Length);
		Equal(0, ClipboardVideoTimeline.PreviewPositions(TimeSpan.Zero, TimeSpan.FromSeconds(1), 30, invalid).Length);
	}
	Equal(0, ClipboardVideoTimeline.PreviewPositions(TimeSpan.Zero, TimeSpan.Zero, 30, 684).Length);
	Equal(1, ClipboardVideoTimeline.PreviewPositions(TimeSpan.Zero, TimeSpan.FromSeconds(1), 30, 20).Length);
});

Run("1088行にパディングされた動画から有効な1080行を選ぶ", () =>
{
	Equal(true, ClipboardVideoFrameLayout.TryReadAperture(Aperture(0, 0, 1920, 1080), 1920, 1088, out var layout));
	Equal(new ClipboardVideoFrameLayout(0, 0, 1920, 1080), layout);
});

Run("表示範囲の外にある右端・下端の緑色画素をコピーしない", () =>
{
	const int pitch = 16 * 4;
	byte[] buffer = new byte[pitch * 16];
	for (int row = 0; row < 16; row++)
	{
		for (int column = 0; column < 16; column++)
		{
			int offset = row * pitch + column * 4;
			bool visible = row < 9 && column < 13;
			buffer[offset] = visible ? (byte)10 : (byte)0;
			buffer[offset + 1] = visible ? (byte)20 : (byte)255;
			buffer[offset + 2] = visible ? (byte)30 : (byte)0;
		}
	}
	Equal(true, ClipboardVideoFrameLayout.TryReadAperture(Aperture(0, 0, 13, 9), 16, 16, out var layout));
	IntPtr data = Marshal.AllocHGlobal(buffer.Length);
	try
	{
		Marshal.Copy(buffer, 0, data, buffer.Length);
		byte[] pixels = layout.CopyBgr32(data, pitch);
		Equal(13 * 9 * 4, pixels.Length);
		for (int offset = 0; offset < pixels.Length; offset += 4)
		{
			Equal((byte)10, pixels[offset]);
			Equal((byte)20, pixels[offset + 1]);
			Equal((byte)30, pixels[offset + 2]);
		}
	}
	finally { Marshal.FreeHGlobal(data); }
});

Run("行幅の余白と上下反転がある画像でもオフセット付き表示範囲を正しく取り出す", () =>
{
	foreach (int pitch in new[] { 32, -32 })
	{
		byte[] buffer = new byte[32 * 5];
		for (int row = 0; row < 5; row++)
		{
			int physicalRow = pitch > 0 ? row : 4 - row;
			for (int column = 0; column < 6; column++)
				buffer[physicalRow * 32 + column * 4] = (byte)(row * 10 + column);
		}
		Equal(true, ClipboardVideoFrameLayout.TryReadAperture(Aperture(1, 1, 3, 2), 6, 5, out var layout));
		IntPtr data = Marshal.AllocHGlobal(buffer.Length);
		try
		{
			Marshal.Copy(buffer, 0, data, buffer.Length);
			byte[] pixels = layout.CopyBgr32(IntPtr.Add(data, pitch > 0 ? 0 : 4 * 32), pitch);
			Equal("11,12,13,21,22,23", string.Join(',', Enumerable.Range(0, 6).Select(index => pixels[index * 4])));
		}
		finally { Marshal.FreeHGlobal(data); }
	}
});

Run("不正な表示範囲と画像の外側を指す範囲は採用しない", () =>
{
	foreach (byte[] area in new[]
	{
		Array.Empty<byte>(), new byte[15], Aperture(-1, 0, 10, 10), Aperture(0, -1, 10, 10),
		Aperture(0, 0, 0, 10), Aperture(0, 0, 10, -1), Aperture(1, 0, 16, 16), Aperture(0, 1, 16, 16)
	})
		Equal(false, ClipboardVideoFrameLayout.TryReadAperture(area, 16, 16, out _));
});

Run("小数の表示開始位置では範囲内に収まる画素だけを取り出す", () =>
{
	byte[] area = Aperture(1, 2, 5, 6);
	BinaryPrimitives.WriteUInt16LittleEndian(area, 32768);
	BinaryPrimitives.WriteUInt16LittleEndian(area.AsSpan(4), 32768);
	Equal(true, ClipboardVideoFrameLayout.TryReadAperture(area, 16, 16, out var layout));
	Equal(new ClipboardVideoFrameLayout(2, 3, 4, 5), layout);
});

Run("不正な行幅では画像バッファを読み込まない", () =>
{
	try
	{
		new ClipboardVideoFrameLayout(1, 0, 3, 2).CopyBgr32(new IntPtr(1), 12);
		throw new Exception("不正な行幅が受理されました。");
	}
	catch (ArgumentException) { }
});

Run("表示領域が未確定の間は初期ズームと表示位置を確定しない", () =>
{
	foreach (double invalid in new[] { 0, -1, double.NaN, double.PositiveInfinity })
	{
		Equal(false, ImagePaintViewportLayout.TryFit(1024, 1024, 1920, 1080, invalid, 600, 0.05, out _));
		Equal(false, ImagePaintViewportLayout.TryFit(1024, 1024, 1920, 1080, 900, invalid, 0.05, out _));
	}
});

Run("作業領域の余白があってもフルHDのコマが画面中央に収まる", () =>
{
	Equal(true, ImagePaintViewportLayout.TryFit(1024, 1024, 1920, 1080, 992, 572, 0.05, out var layout));
	Near(0.5, layout.Zoom);
	Near(16, 1024 * layout.Zoom - layout.HorizontalOffset);
	Near(16, 1024 * layout.Zoom - layout.VerticalOffset);
	Near(976, (1024 + 1920) * layout.Zoom - layout.HorizontalOffset);
	Near(556, (1024 + 1080) * layout.Zoom - layout.VerticalOffset);
});

Run("縦長・4Kのコマと画面サイズ変更でも画像の中心と表示領域の中心が一致する", () =>
{
	foreach (var (width, height) in new[] { (3840, 2160), (1080, 1920), (1280, 720) })
	foreach (var (viewportWidth, viewportHeight) in new[] { (900, 350), (700, 500), (1400, 800) })
	{
		Equal(true, ImagePaintViewportLayout.TryFit(1024, 1024, width, height, viewportWidth, viewportHeight, 0.05, out var layout));
		Near(viewportWidth / 2.0, (1024 + width / 2.0) * layout.Zoom - layout.HorizontalOffset);
		Near(viewportHeight / 2.0, (1024 + height / 2.0) * layout.Zoom - layout.VerticalOffset);
		Equal(true, width * layout.Zoom <= viewportWidth - 32 + 0.0001);
		Equal(true, height * layout.Zoom <= viewportHeight - 32 + 0.0001);
	}
});

Run("画面上のドラッグ座標が余白と縮小率を含む元画像の画素座標に戻る", () =>
{
	Equal(true, ImagePaintViewportLayout.TryFit(1024, 1024, 1920, 1080, 992, 572, 0.05, out var layout));
	Near(200, (116 + layout.HorizontalOffset) / layout.Zoom - 1024);
	Near(100, (66 + layout.VerticalOffset) / layout.Zoom - 1024);
	Near(1000, (516 + layout.HorizontalOffset) / layout.Zoom - 1024);
	Near(600, (316 + layout.VerticalOffset) / layout.Zoom - 1024);
});

Run("小さい保存画像は拡大せずに中央へ表示する", () =>
{
	Equal(true, ImagePaintViewportLayout.TryFit(1024, 1024, 288, 162, 900, 500, 0.05, out var layout));
	Near(1, layout.Zoom);
	Near(306, 1024 - layout.HorizontalOffset);
	Near(169, 1024 - layout.VerticalOffset);
});

using var database = new SqliteConnection("Data Source=:memory:;Foreign Keys=True");
database.Open();
using (var command = database.CreateCommand())
{
	command.CommandText = """
		CREATE TABLE clipboard_history (
			id INTEGER PRIMARY KEY, kind INTEGER, created_at_utc_ticks INTEGER,
			preview_text TEXT, thumbnail BLOB, paint_state TEXT
		);
		INSERT INTO clipboard_history VALUES
			(1, 3, 0, 'text', NULL, NULL), (2, 0, 0, 'image', NULL, NULL),
			(3, 1, 0, 'html', NULL, NULL), (4, 5, 0, 'video', NULL, NULL),
			(5, 4, 0, 'unknown', NULL, NULL), (6, 0, 0, 'image', NULL, '{}'),
			(7, 5, 0, 'video', NULL, NULL), (8, 2, 0, 'rtf', NULL, NULL),
			(9, 5, 0, 'video', NULL, NULL), (10, 0, 0, 'image', NULL, NULL);
		""";
	command.ExecuteNonQuery();
}

Run("既存の履歴を保持してコマ編集用DBを追加できる", () =>
{
	using var transaction = database.BeginTransaction();
	ClipboardVideoFrameStore.Initialize(database, transaction);
	ClipboardVideoFrameStore.Initialize(database, transaction);
	transaction.Commit();
	using var command = database.CreateCommand();
	command.CommandText = "SELECT COUNT(*) FROM clipboard_history;";
	Equal(10L, (long)command.ExecuteScalar()!);
});

Run("ギャラリーは画像と動画を新しい順に取得する", () =>
{
	Equal("10,9,7,6,4,2", string.Join(',', LoadPage(null, 100)));
});

Run("画像と動画が混在してもページ間の欠落や重複がない", () =>
{
	long[] page1 = LoadPage(null, 3);
	long[] page2 = LoadPage(page1.Last(), 3);
	Equal("10,9,7", string.Join(',', page1));
	Equal("6,4,2", string.Join(',', page2));
	Equal(0, LoadPage(page2.Last(), 3).Length);
	Equal(0, LoadPage(null, 0).Length);
});

byte[] frameImage = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+/l9sAAAAASUVORK5CYII=");
byte[] thumbnail = frameImage.ToArray();
string paintState = "{\"Images\":[{\"ImageBytes\":\"" + Convert.ToBase64String(frameImage) + "\"}],\"Elements\":[{\"Kind\":\"outlineRectangle\"}]}";
TimeSpan frame1 = TimeSpan.FromTicks(333333);
TimeSpan frame2 = TimeSpan.FromTicks(666667);

Run("複数コマの編集画像・サムネイル・編集状態を独立して保存する", () =>
{
	using (var transaction = database.BeginTransaction())
	{
		ClipboardVideoFrameStore.Save(database, transaction, 9, frame2, frameImage, thumbnail, paintState);
		ClipboardVideoFrameStore.Save(database, transaction, 9, frame1, frameImage, thumbnail, "first state");
		ClipboardVideoFrameStore.Save(database, transaction, 7, frame1, frameImage, thumbnail, "another video");
		transaction.Commit();
	}
	List<ClipboardVideoFrameSummary> frames = ClipboardVideoFrameStore.LoadSummaries(database, 9);
	Equal("333333,666667", string.Join(',', frames.Select(frame => frame.Position.Ticks)));
	Equal(true, frames.All(frame => frame.ThumbnailBytes.SequenceEqual(thumbnail)));
	ClipboardVideoFrameEdit saved = ClipboardVideoFrameStore.Load(database, 9, frame2)!;
	Equal(true, saved.ImageBytes.SequenceEqual(frameImage));
	Equal(paintState, saved.PaintStateJson);
	Equal("another video", ClipboardVideoFrameStore.Load(database, 7, frame1)!.PaintStateJson);
	Equal<ClipboardVideoFrameEdit?>(null, ClipboardVideoFrameStore.Load(database, 9, TimeSpan.Zero));
});

Run("同じコマの再編集では上書きし、他のコマを変更しない", () =>
{
	using (var transaction = database.BeginTransaction())
	{
		ClipboardVideoFrameStore.Save(database, transaction, 9, frame1, frameImage, thumbnail, "re-edited state");
		transaction.Commit();
	}
	Equal(2, ClipboardVideoFrameStore.LoadSummaries(database, 9).Count);
	Equal("re-edited state", ClipboardVideoFrameStore.Load(database, 9, frame1)!.PaintStateJson);
	Equal(paintState, ClipboardVideoFrameStore.Load(database, 9, frame2)!.PaintStateJson);
});

Run("ギャラリーの編集済みフラグと保存サムネイルを復元する", () =>
{
	using var command = database.CreateCommand();
	command.CommandText = ClipboardGalleryHistoryQuery.Create(false);
	command.Parameters.AddWithValue("$image_kind", (int)ClipboardHistoryKind.Image);
	command.Parameters.AddWithValue("$video_kind", (int)ClipboardHistoryKind.Video);
	command.Parameters.AddWithValue("$limit", 100);
	var edited = new List<long>();
	using var reader = command.ExecuteReader();
	while (reader.Read())
	{
		if (reader.GetInt64(5) != 0) edited.Add(reader.GetInt64(0));
		if (reader.GetInt64(0) == 9) Equal(true, ((byte[])reader.GetValue(4)).SequenceEqual(thumbnail));
	}
	Equal("9,7,6", string.Join(',', edited));
});

Run("保存をロールバックするとコマとギャラリーのサムネイルを両方戻す", () =>
{
	using (var transaction = database.BeginTransaction())
	{
		ClipboardVideoFrameStore.Save(database, transaction, 4, frame1, frameImage, thumbnail, paintState);
		transaction.Rollback();
	}
	Equal(0, ClipboardVideoFrameStore.LoadSummaries(database, 4).Count);
	using var command = database.CreateCommand();
	command.CommandText = "SELECT thumbnail FROM clipboard_history WHERE id = 4;";
	Equal(DBNull.Value, command.ExecuteScalar());
});

Run("削除済みの履歴や画像履歴には動画のコマを保存しない", () =>
{
	foreach (long id in new long[] { 999, 6 })
	{
		using var transaction = database.BeginTransaction();
		bool rejected = false;
		try { ClipboardVideoFrameStore.Save(database, transaction, id, frame1, frameImage, thumbnail, paintState); }
		catch (InvalidOperationException) { rejected = true; }
		Equal(true, rejected);
		transaction.Rollback();
		Equal(0, ClipboardVideoFrameStore.LoadSummaries(database, id).Count);
	}
});

Run("動画履歴の削除で編集コマも消え、他の動画の編集は残る", () =>
{
	using var command = database.CreateCommand();
	command.CommandText = "DELETE FROM clipboard_history WHERE id = 9;";
	command.ExecuteNonQuery();
	Equal(0, ClipboardVideoFrameStore.LoadSummaries(database, 9).Count);
	Equal("another video", ClipboardVideoFrameStore.Load(database, 7, frame1)!.PaintStateJson);
});

Run("DBを開き直し元動画を削除しても編集画像と再編集用データが残る", () =>
{
	string directory = Path.Combine(Path.GetTempPath(), "clipboard-frame-test-" + Guid.NewGuid());
	Directory.CreateDirectory(directory);
	string videoPath = Path.Combine(directory, "source.mp4");
	string connectionString = $"Data Source={Path.Combine(directory, "history.db")};Foreign Keys=True;Pooling=False";
	try
	{
		File.WriteAllBytes(videoPath, [1, 2, 3]);
		using (var persistent = new SqliteConnection(connectionString))
		{
			persistent.Open();
			using var command = persistent.CreateCommand();
			command.CommandText = "CREATE TABLE clipboard_history (id INTEGER PRIMARY KEY, kind INTEGER, content BLOB, thumbnail BLOB); INSERT INTO clipboard_history (id, kind, content) VALUES (1, 5, $content);";
			command.Parameters.AddWithValue("$content", ClipboardVideoFiles.Encode([videoPath]));
			command.ExecuteNonQuery();
			using var transaction = persistent.BeginTransaction();
			ClipboardVideoFrameStore.Initialize(persistent, transaction);
			ClipboardVideoFrameStore.Save(persistent, transaction, 1, frame1, frameImage, thumbnail, paintState);
			transaction.Commit();
		}
		File.Delete(videoPath);
		using var reopened = new SqliteConnection(connectionString);
		reopened.Open();
		ClipboardVideoFrameEdit saved = ClipboardVideoFrameStore.Load(reopened, 1, frame1)!;
		Equal(true, saved.ImageBytes.SequenceEqual(frameImage));
		Equal(paintState, saved.PaintStateJson);
		Equal(true, ClipboardVideoFrameStore.LoadSummaries(reopened, 1).Single().ThumbnailBytes.SequenceEqual(thumbnail));
		using var lookup = reopened.CreateCommand();
		lookup.CommandText = "SELECT content FROM clipboard_history WHERE id = 1;";
		Equal(videoPath, ClipboardVideoFiles.Decode((byte[])lookup.ExecuteScalar()!).Single());
	}
	finally { Directory.Delete(directory, recursive: true); }
});

Console.WriteLine($"{passed} checks passed; {failed} failed.");
Environment.ExitCode = failed == 0 ? 0 : 1;

long[] LoadPage(long? beforeId, int count)
{
	using var command = database.CreateCommand();
	command.CommandText = ClipboardGalleryHistoryQuery.Create(beforeId.HasValue);
	command.Parameters.AddWithValue("$image_kind", (int)ClipboardHistoryKind.Image);
	command.Parameters.AddWithValue("$video_kind", (int)ClipboardHistoryKind.Video);
	command.Parameters.AddWithValue("$limit", count);
	if (beforeId.HasValue)
	{
		command.Parameters.AddWithValue("$before_id", beforeId.Value);
	}
	var result = new List<long>();
	using var reader = command.ExecuteReader();
	while (reader.Read())
	{
		result.Add(reader.GetInt64(0));
	}
	return result.ToArray();
}

void Run(string name, Action test)
{
	try
	{
		test();
		passed++;
		Console.WriteLine($"PASS {name}");
	}
	catch (Exception ex)
	{
		failed++;
		Console.Error.WriteLine($"FAIL {name}: {ex.Message}");
	}
}

static byte[] Aperture(short x, short y, int width, int height)
{
	byte[] area = new byte[16];
	BinaryPrimitives.WriteInt16LittleEndian(area.AsSpan(2), x);
	BinaryPrimitives.WriteInt16LittleEndian(area.AsSpan(6), y);
	BinaryPrimitives.WriteInt32LittleEndian(area.AsSpan(8), width);
	BinaryPrimitives.WriteInt32LittleEndian(area.AsSpan(12), height);
	return area;
}

static void Near(double expected, double actual)
{
	if (!double.IsFinite(actual) || Math.Abs(expected - actual) > 0.0001)
		throw new Exception($"Expected {expected}; got {actual}");
}

static void Equal<T>(T expected, T actual)
{
	if (!EqualityComparer<T>.Default.Equals(expected, actual))
	{
		throw new Exception($"Expected {expected}; got {actual}");
	}
}
