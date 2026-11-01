using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Clipboard;

// デコード専用のMTAスレッドで読み込み、再生用のMediaElementには触れない。
internal sealed class ClipboardVideoFrameReader : IDisposable
{
	private const int VideoStream = unchecked((int)0xfffffffc);
	private const int AllStreams = unchecked((int)0xfffffffe);
	private readonly BlockingCollection<Request> _requests = new();
	private readonly Dictionary<long, BitmapSource> _cache = new();
	private readonly Queue<long> _cacheOrder = new();
	private readonly string _path;
	private readonly Task _worker;
	private int _disposed;

	public ClipboardVideoFrameReader(string path)
	{
		_path = path;
		_worker = Task.Factory.StartNew(Run, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
	}

	public Task<IReadOnlyDictionary<long, BitmapSource>> ReadAsync(TimeSpan[] positions, double frameRate, CancellationToken cancellationToken)
	{
		var completion = new TaskCompletionSource<IReadOnlyDictionary<long, BitmapSource>>(TaskCreationOptions.RunContinuationsAsynchronously);
		_requests.Add(new Request(positions, frameRate, cancellationToken, completion));
		return completion.Task;
	}

	private void Run()
	{
		IMFSourceReader? reader = null;
		bool started = false;
		int apartment = CoInitializeEx(IntPtr.Zero, 0);
		try
		{
			Marshal.ThrowExceptionForHR(apartment);
			Marshal.ThrowExceptionForHR(MFStartup(0x20070, 0));
			started = true;
			reader = Open();
			foreach (Request request in _requests.GetConsumingEnumerable())
			{
				try
				{
					request.CancellationToken.ThrowIfCancellationRequested();
					request.Completion.TrySetResult(Read(reader, request));
				}
				catch (OperationCanceledException)
				{
					request.Completion.TrySetCanceled(request.CancellationToken);
				}
				catch (Exception ex)
				{
					request.Completion.TrySetException(ex);
				}
			}
		}
		catch (Exception ex)
		{
			// 初期化失敗も要求元へ返し、コマ表示だけを無効にする。
			foreach (Request request in _requests.GetConsumingEnumerable())
			{
				request.Completion.TrySetException(ex);
			}
		}
		finally
		{
			Release(reader);
			if (started) MFShutdown();
			if (apartment >= 0) CoUninitialize();
			_cache.Clear();
			_cacheOrder.Clear();
			_requests.Dispose();
		}
	}

	private IMFSourceReader Open()
	{
		IMFAttributes? attributes = null;
		IMFMediaType? output = null;
		IMFSourceReader? reader = null;
		try
		{
			Marshal.ThrowExceptionForHR(MFCreateAttributes(out attributes, 1));
			Guid processing = new("fb394f3d-ccf1-42ee-bbb3-f9b845d5681d"); // MF_SOURCE_READER_ENABLE_VIDEO_PROCESSING
			Marshal.ThrowExceptionForHR(attributes.SetUINT32(ref processing, 1));
			Marshal.ThrowExceptionForHR(MFCreateSourceReaderFromURL(_path, attributes, out reader));
			Marshal.ThrowExceptionForHR(reader.SetStreamSelection(AllStreams, false));
			Marshal.ThrowExceptionForHR(reader.SetStreamSelection(VideoStream, true));
			Marshal.ThrowExceptionForHR(MFCreateMediaType(out output));
			Guid majorKey = new("48eba18e-f8c9-4687-bf11-0a74c9f96a8f");
			Guid video = new("73646976-0000-0010-8000-00aa00389b71");
			Guid subtypeKey = new("f7e34c9a-42e8-4714-b74b-cb29d72c35e5");
			Guid rgb32 = new("00000016-0000-0010-8000-00aa00389b71");
			Marshal.ThrowExceptionForHR(output.SetGUID(ref majorKey, ref video));
			Marshal.ThrowExceptionForHR(output.SetGUID(ref subtypeKey, ref rgb32));
			Marshal.ThrowExceptionForHR(reader.SetCurrentMediaType(VideoStream, IntPtr.Zero, output));
			return reader;
		}
		catch
		{
			Release(reader);
			throw;
		}
		finally
		{
			Release(output);
			Release(attributes);
		}
	}

	private IReadOnlyDictionary<long, BitmapSource> Read(IMFSourceReader reader, Request request)
	{
		long[] missing = request.Positions.Select(position => position.Ticks).Distinct()
			.Where(ticks => !_cache.ContainsKey(ticks)).Order().ToArray();
		if (missing.Length > 0)
		{
			Guid timeFormat = Guid.Empty;
			var start = new PropVariant { Type = 20, Int64Value = missing[0] }; // VT_I8, 100ns
			Marshal.ThrowExceptionForHR(reader.SetCurrentPosition(ref timeFormat, ref start));
			int index = 0;
			long tolerance = Math.Max(1, (long)Math.Round(TimeSpan.TicksPerSecond / request.FrameRate / 2));
			while (index < missing.Length)
			{
				request.CancellationToken.ThrowIfCancellationRequested();
				IMFSample? sample = null;
				try
				{
					Marshal.ThrowExceptionForHR(reader.ReadSample(VideoStream, 0, out _, out int flags, out long timestamp, out sample));
					if ((flags & 1) != 0) throw new COMException("動画のコマを読み込めませんでした。");
					if (sample != null && timestamp + tolerance >= missing[index])
					{
						BitmapSource image = ReadBitmap(reader, sample);
						do
						{
							long ticks = missing[index++];
							_cache[ticks] = image;
							_cacheOrder.Enqueue(ticks);
						}
						while (index < missing.Length && timestamp + tolerance >= missing[index]);
					}
					if ((flags & 2) != 0) break; // MF_SOURCE_READERF_ENDOFSTREAM
				}
				finally
				{
					Release(sample);
				}
			}
		}

		var result = new Dictionary<long, BitmapSource>();
		foreach (TimeSpan position in request.Positions)
		{
			if (_cache.TryGetValue(position.Ticks, out BitmapSource? image))
			{
				result[position.Ticks] = image;
			}
		}
		while (_cacheOrder.Count > 192)
		{
			_cache.Remove(_cacheOrder.Dequeue());
		}
		return result;
	}

	private static BitmapSource ReadBitmap(IMFSourceReader reader, IMFSample sample)
	{
		IMFMediaType? type = null;
		IMFMediaBuffer? buffer = null;
		try
		{
			Marshal.ThrowExceptionForHR(reader.GetCurrentMediaType(VideoStream, out type));
			Guid sizeKey = new("1652c33d-d6b2-4012-b834-72030849a37d");
			Marshal.ThrowExceptionForHR(type.GetUINT64(ref sizeKey, out long size));
			int width = checked((int)(size >> 32));
			int height = checked((int)(size & uint.MaxValue));
			Guid strideKey = new("644b4e48-1e02-4516-b0eb-c01ca9d49ac6");
			if (type.GetUINT32(ref strideKey, out int stride) < 0) stride = checked(width * 4);
			Marshal.ThrowExceptionForHR(sample.ConvertToContiguousBuffer(out buffer));
			int rowSize = checked(width * 4);
			byte[] pixels = new byte[checked(rowSize * height)];
			if (buffer is IMF2DBuffer buffer2D)
			{
				Marshal.ThrowExceptionForHR(buffer2D.Lock2D(out IntPtr scanline, out int pitch));
				try
				{
					for (int row = 0; row < height; row++)
						Marshal.Copy(IntPtr.Add(scanline, checked(row * pitch)), pixels, row * rowSize, rowSize);
				}
				finally { buffer2D.Unlock2D(); }
			}
			else
			{
				Marshal.ThrowExceptionForHR(buffer.Lock(out IntPtr data, out _, out int length));
				try
				{
					if (Math.Abs((long)stride) < rowSize || (height - 1L) * Math.Abs((long)stride) + rowSize > length)
						throw new InvalidOperationException("動画の画像サイズが不正です。");
					int origin = stride < 0 ? checked((height - 1) * -stride) : 0;
					for (int row = 0; row < height; row++)
						Marshal.Copy(IntPtr.Add(data, checked(origin + row * stride)), pixels, row * rowSize, rowSize);
				}
				finally { buffer.Unlock(); }
			}
			var source = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgr32, null, pixels, rowSize);
			double scale = Math.Min(1, Math.Min(144.0 / width, 82.0 / height));
			// 小さくした画素だけを保持し、元解像度のバッファをキャッシュに残さない。
			var thumbnail = new WriteableBitmap(new TransformedBitmap(source, new ScaleTransform(scale, scale)));
			thumbnail.Freeze();
			return thumbnail;
		}
		finally
		{
			Release(buffer);
			Release(type);
		}
	}

	public void Dispose()
	{
		if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
		_requests.CompleteAdding();
		// COMの解放も読み込みスレッドで行う。UIスレッドでは待機しない。
	}

	private static void Release(object? value)
	{
		if (value != null) Marshal.ReleaseComObject(value);
	}

	private sealed record Request(TimeSpan[] Positions, double FrameRate, CancellationToken CancellationToken,
		TaskCompletionSource<IReadOnlyDictionary<long, BitmapSource>> Completion);

	[StructLayout(LayoutKind.Explicit, Size = 24)]
	private struct PropVariant
	{
		[FieldOffset(0)] public ushort Type;
		[FieldOffset(8)] public long Int64Value;
	}

	[DllImport("ole32.dll")] private static extern int CoInitializeEx(IntPtr reserved, int concurrency);
	[DllImport("ole32.dll")] private static extern void CoUninitialize();
	[DllImport("mfplat.dll")] private static extern int MFStartup(int version, int flags);
	[DllImport("mfplat.dll")] private static extern int MFShutdown();
	[DllImport("mfplat.dll")] private static extern int MFCreateAttributes(out IMFAttributes attributes, int initialSize);
	[DllImport("mfplat.dll")] private static extern int MFCreateMediaType(out IMFMediaType type);
	[DllImport("mfreadwrite.dll", CharSet = CharSet.Unicode)]
	private static extern int MFCreateSourceReaderFromURL(string url, IMFAttributes attributes, out IMFSourceReader reader);

	// IMFMediaType / IMFSampleの先頭にはIMFAttributesの全メソッドが並ぶ。
	[ComImport, Guid("2cd2d921-c447-44a7-a13c-4adabfc247e3"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
	private interface IMFAttributes
	{
		[PreserveSig] int GetItem(ref Guid key, IntPtr value);
		[PreserveSig] int GetItemType(ref Guid key, out int type);
		[PreserveSig] int CompareItem(ref Guid key, IntPtr value, out int result);
		[PreserveSig] int Compare(IntPtr other, int matchType, out int result);
		[PreserveSig] int GetUINT32(ref Guid key, out int value);
		[PreserveSig] int GetUINT64(ref Guid key, out long value);
		[PreserveSig] int GetDouble(ref Guid key, out double value);
		[PreserveSig] int GetGUID(ref Guid key, out Guid value);
		[PreserveSig] int GetStringLength(ref Guid key, out int length);
		[PreserveSig] int GetString(ref Guid key, IntPtr value, int size, out int length);
		[PreserveSig] int GetAllocatedString(ref Guid key, out IntPtr value, out int length);
		[PreserveSig] int GetBlobSize(ref Guid key, out int size);
		[PreserveSig] int GetBlob(ref Guid key, IntPtr buffer, int size, out int length);
		[PreserveSig] int GetAllocatedBlob(ref Guid key, out IntPtr buffer, out int size);
		[PreserveSig] int GetUnknown(ref Guid key, ref Guid id, out IntPtr value);
		[PreserveSig] int SetItem(ref Guid key, IntPtr value);
		[PreserveSig] int DeleteItem(ref Guid key);
		[PreserveSig] int DeleteAllItems();
		[PreserveSig] int SetUINT32(ref Guid key, int value);
		[PreserveSig] int SetUINT64(ref Guid key, long value);
		[PreserveSig] int SetDouble(ref Guid key, double value);
		[PreserveSig] int SetGUID(ref Guid key, ref Guid value);
		[PreserveSig] int SetString(ref Guid key, [MarshalAs(UnmanagedType.LPWStr)] string value);
		[PreserveSig] int SetBlob(ref Guid key, IntPtr buffer, int size);
		[PreserveSig] int SetUnknown(ref Guid key, IntPtr value);
		[PreserveSig] int LockStore();
		[PreserveSig] int UnlockStore();
		[PreserveSig] int GetCount(out int count);
		[PreserveSig] int GetItemByIndex(int index, out Guid key, IntPtr value);
		[PreserveSig] int CopyAllItems(IntPtr destination);

	}

	[ComImport, Guid("44ae0fa8-ea31-4109-8d2e-4cae4997c555"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
	private interface IMFMediaType
	{
		[PreserveSig] int GetItem(ref Guid key, IntPtr value);
		[PreserveSig] int GetItemType(ref Guid key, out int type);
		[PreserveSig] int CompareItem(ref Guid key, IntPtr value, out int result);
		[PreserveSig] int Compare(IntPtr other, int matchType, out int result);
		[PreserveSig] int GetUINT32(ref Guid key, out int value);
		[PreserveSig] int GetUINT64(ref Guid key, out long value);
		[PreserveSig] int GetDouble(ref Guid key, out double value);
		[PreserveSig] int GetGUID(ref Guid key, out Guid value);
		[PreserveSig] int GetStringLength(ref Guid key, out int length);
		[PreserveSig] int GetString(ref Guid key, IntPtr value, int size, out int length);
		[PreserveSig] int GetAllocatedString(ref Guid key, out IntPtr value, out int length);
		[PreserveSig] int GetBlobSize(ref Guid key, out int size);
		[PreserveSig] int GetBlob(ref Guid key, IntPtr buffer, int size, out int length);
		[PreserveSig] int GetAllocatedBlob(ref Guid key, out IntPtr buffer, out int size);
		[PreserveSig] int GetUnknown(ref Guid key, ref Guid id, out IntPtr value);
		[PreserveSig] int SetItem(ref Guid key, IntPtr value);
		[PreserveSig] int DeleteItem(ref Guid key);
		[PreserveSig] int DeleteAllItems();
		[PreserveSig] int SetUINT32(ref Guid key, int value);
		[PreserveSig] int SetUINT64(ref Guid key, long value);
		[PreserveSig] int SetDouble(ref Guid key, double value);
		[PreserveSig] int SetGUID(ref Guid key, ref Guid value);
		[PreserveSig] int SetString(ref Guid key, [MarshalAs(UnmanagedType.LPWStr)] string value);
		[PreserveSig] int SetBlob(ref Guid key, IntPtr buffer, int size);
		[PreserveSig] int SetUnknown(ref Guid key, IntPtr value);
		[PreserveSig] int LockStore();
		[PreserveSig] int UnlockStore();
		[PreserveSig] int GetCount(out int count);
		[PreserveSig] int GetItemByIndex(int index, out Guid key, IntPtr value);
		[PreserveSig] int CopyAllItems(IntPtr destination);

	}

	[ComImport, Guid("70ae66f2-c809-4e4f-8915-bdcb406b7993"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
	private interface IMFSourceReader
	{
		[PreserveSig] int GetStreamSelection(int stream, [MarshalAs(UnmanagedType.Bool)] out bool selected);
		[PreserveSig] int SetStreamSelection(int stream, [MarshalAs(UnmanagedType.Bool)] bool selected);
		[PreserveSig] int GetNativeMediaType(int stream, int index, out IMFMediaType type);
		[PreserveSig] int GetCurrentMediaType(int stream, out IMFMediaType type);
		[PreserveSig] int SetCurrentMediaType(int stream, IntPtr reserved, IMFMediaType type);
		[PreserveSig] int SetCurrentPosition(ref Guid format, ref PropVariant position);
		[PreserveSig] int ReadSample(int stream, int control, out int actualStream, out int flags, out long timestamp, out IMFSample? sample);
	}

	[ComImport, Guid("c40a00f2-b93a-4d80-ae8c-5a1c634f58e4"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
	private interface IMFSample
	{
		[PreserveSig] int GetItem(ref Guid key, IntPtr value);
		[PreserveSig] int GetItemType(ref Guid key, out int type);
		[PreserveSig] int CompareItem(ref Guid key, IntPtr value, out int result);
		[PreserveSig] int Compare(IntPtr other, int matchType, out int result);
		[PreserveSig] int GetUINT32(ref Guid key, out int value);
		[PreserveSig] int GetUINT64(ref Guid key, out long value);
		[PreserveSig] int GetDouble(ref Guid key, out double value);
		[PreserveSig] int GetGUID(ref Guid key, out Guid value);
		[PreserveSig] int GetStringLength(ref Guid key, out int length);
		[PreserveSig] int GetString(ref Guid key, IntPtr value, int size, out int length);
		[PreserveSig] int GetAllocatedString(ref Guid key, out IntPtr value, out int length);
		[PreserveSig] int GetBlobSize(ref Guid key, out int size);
		[PreserveSig] int GetBlob(ref Guid key, IntPtr buffer, int size, out int length);
		[PreserveSig] int GetAllocatedBlob(ref Guid key, out IntPtr buffer, out int size);
		[PreserveSig] int GetUnknown(ref Guid key, ref Guid id, out IntPtr value);
		[PreserveSig] int SetItem(ref Guid key, IntPtr value);
		[PreserveSig] int DeleteItem(ref Guid key);
		[PreserveSig] int DeleteAllItems();
		[PreserveSig] int SetUINT32(ref Guid key, int value);
		[PreserveSig] int SetUINT64(ref Guid key, long value);
		[PreserveSig] int SetDouble(ref Guid key, double value);
		[PreserveSig] int SetGUID(ref Guid key, ref Guid value);
		[PreserveSig] int SetString(ref Guid key, [MarshalAs(UnmanagedType.LPWStr)] string value);
		[PreserveSig] int SetBlob(ref Guid key, IntPtr buffer, int size);
		[PreserveSig] int SetUnknown(ref Guid key, IntPtr value);
		[PreserveSig] int LockStore();
		[PreserveSig] int UnlockStore();
		[PreserveSig] int GetCount(out int count);
		[PreserveSig] int GetItemByIndex(int index, out Guid key, IntPtr value);
		[PreserveSig] int CopyAllItems(IntPtr destination);

		[PreserveSig] int GetSampleFlags(out int flags);
		[PreserveSig] int SetSampleFlags(int flags);
		[PreserveSig] int GetSampleTime(out long time);
		[PreserveSig] int SetSampleTime(long time);
		[PreserveSig] int GetSampleDuration(out long duration);
		[PreserveSig] int SetSampleDuration(long duration);
		[PreserveSig] int GetBufferCount(out int count);
		[PreserveSig] int GetBufferByIndex(int index, out IMFMediaBuffer buffer);
		[PreserveSig] int ConvertToContiguousBuffer(out IMFMediaBuffer buffer);
	}

	[ComImport, Guid("045fa593-8799-42b8-bc8d-8968c6453507"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
	private interface IMFMediaBuffer
	{
		[PreserveSig] int Lock(out IntPtr buffer, out int maximumLength, out int currentLength);
		[PreserveSig] int Unlock();
	}

	[ComImport, Guid("7dc9d5f9-9ed9-44ec-9bbf-0600bb589fbb"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
	private interface IMF2DBuffer
	{
		[PreserveSig] int Lock2D(out IntPtr scanline, out int pitch);
		[PreserveSig] int Unlock2D();
	}
}
