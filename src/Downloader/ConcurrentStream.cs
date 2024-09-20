﻿using Microsoft.Extensions.Logging;
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Downloader;

/// <summary>
/// Represents a stream that supports concurrent read and write operations with optional memory buffering.
/// </summary>
public class ConcurrentStream : TaskStateManagement, IDisposable, IAsyncDisposable
{
    private ConcurrentPacketBuffer<Packet> _inputBuffer;
    private volatile bool _disposed;
    private Stream _stream;
    private string _path;
    private CancellationTokenSource _watcherCancelSource;

    /// <summary>
    /// Gets or sets the path of the file associated with the stream.
    /// </summary>
    public string Path
    {
        get => _path;
        set
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                _path = value;
                _stream = new FileStream(_path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.ReadWrite);
            }
        }
    }

    /// <summary>
    /// Gets the data of the stream as a byte array if the stream is a MemoryStream.
    /// </summary>
    public byte[] Data
    {
        get
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(_stream));

            if (_stream is MemoryStream mem)
                return mem.ToArray();

            return null;
        }
        set
        {
            if (value != null)
            {
                // Don't pass straight value to MemoryStream,
                // because causes stream to be an immutable array
                _stream = new MemoryStream();
                _stream.Write(value, 0, value.Length);
            }
        }
    }

    /// <summary>
    /// Gets a value indicating whether the stream supports reading.
    /// </summary>
    public bool CanRead => _stream?.CanRead == true;

    /// <summary>
    /// Gets a value indicating whether the stream supports seeking.
    /// </summary>
    public bool CanSeek => _stream?.CanSeek == true;

    /// <summary>
    /// Gets a value indicating whether the stream supports writing.
    /// </summary>
    public bool CanWrite => _stream?.CanWrite == true;

    /// <summary>
    /// Gets the length of the stream in bytes.
    /// </summary>
    public long Length => _stream?.Length ?? 0;

    /// <summary>
    /// Gets or sets the current position within the stream.
    /// </summary>
    public long Position
    {
        get => _stream?.Position ?? 0;
        set => _stream.Position = value;
    }

    /// <summary>
    /// Gets or sets the maximum amount of memory, in bytes, that the stream is allowed to allocate for buffering.
    /// </summary>
    public long MaxMemoryBufferBytes
    {
        get => _inputBuffer.BufferSize;
        set => _inputBuffer.BufferSize = value;
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="ConcurrentStream"/> class with default settings.
    /// </summary>
    public ConcurrentStream() : this(0) { }

    /// <summary>
    /// Initializes a new instance of the <see cref="ConcurrentStream"/> class with the specified logger.
    /// </summary>
    /// <param name="logger">The logger to use for logging.</param>
    public ConcurrentStream(ILogger logger) : this(0, logger) { }

    /// <summary>
    /// Initializes a new instance of the <see cref="ConcurrentStream"/> class with the specified maximum memory buffer size and logger.
    /// </summary>
    /// <param name="maxMemoryBufferBytes">The maximum amount of memory, in bytes, that the stream is allowed to allocate for buffering.</param>
    /// <param name="logger">The logger to use for logging.</param>
    public ConcurrentStream(long maxMemoryBufferBytes = 0, ILogger logger = null) : base(logger)
    {
        _stream = new MemoryStream();
        Initial(maxMemoryBufferBytes);
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="ConcurrentStream"/> class with the specified stream and maximum memory buffer size.
    /// </summary>
    /// <param name="stream">The stream to use.</param>
    /// <param name="maxMemoryBufferBytes">The maximum amount of memory, in bytes, that the stream is allowed to allocate for buffering.</param>
    /// <param name="logger">The logger to use for logging.</param>
    public ConcurrentStream(Stream stream, long maxMemoryBufferBytes = 0, ILogger logger = null) : base(logger)
    {
        _stream = stream;
        Initial(maxMemoryBufferBytes);
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="ConcurrentStream"/> class with the specified file path, initial size, and maximum memory buffer size.
    /// </summary>
    /// <param name="filename">The path of the file to use.</param>
    /// <param name="initSize">The initial size of the file.</param>
    /// <param name="maxMemoryBufferBytes">The maximum amount of memory, in bytes, that the stream is allowed to allocate for buffering.</param>
    /// <param name="logger">The logger to use for logging.</param>
    public ConcurrentStream(string filename, long initSize, long maxMemoryBufferBytes = 0, ILogger logger = null) : base(logger)
    {
        _path = filename;
        _stream = new FileStream(filename, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read);

        if (initSize > 0)
            SetLength(initSize);

        Initial(maxMemoryBufferBytes);
    }

    /// <summary>
    /// Initializes the stream with the specified maximum memory buffer size.
    /// </summary>
    /// <param name="maxMemoryBufferBytes">The maximum amount of memory, in bytes, that the stream is allowed to allocate for buffering.</param>
    /// <param name="logger">The logger to use for logging.</param>
    private void Initial(long maxMemoryBufferBytes, ILogger logger = null)
    {
        _inputBuffer = new ConcurrentPacketBuffer<Packet>(maxMemoryBufferBytes, logger);
        _watcherCancelSource = new CancellationTokenSource();

        Task<Task> task = Task.Factory.StartNew(
            function: Watcher,
            cancellationToken: _watcherCancelSource.Token,
            creationOptions: TaskCreationOptions.LongRunning,
            scheduler: TaskScheduler.Default);

        task.Unwrap();
    }

    /// <summary>
    /// Opens the stream for reading.
    /// </summary>
    /// <returns>The stream for reading.</returns>
    public Stream OpenRead()
    {
        Seek(0, SeekOrigin.Begin);
        return _stream;
    }

    /// <summary>
    /// Reads a sequence of bytes from the stream and advances the position within the stream by the number of bytes read.
    /// </summary>
    /// <param name="buffer">An array of bytes to store the read data.</param>
    /// <param name="offset">The zero-based byte offset in buffer at which to begin storing the data read from the stream.</param>
    /// <param name="count">The maximum number of bytes to be read from the stream.</param>
    /// <returns>The total number of bytes read into the buffer.</returns>
    public int Read(byte[] buffer, int offset, int count)
    {
        var stream = OpenRead();
        return stream.Read(buffer, offset, count);
    }

    /// <summary>
    /// Writes a sequence of bytes to the stream asynchronously at the specified position.
    /// </summary>
    /// <param name="position">The position within the stream to write the data.</param>
    /// <param name="bytes">The data to write to the stream.</param>
    /// <param name="length">The number of bytes to write.</param>
    /// <param name="fireAndForget">A value indicating whether to wait for the write operation to complete.</param>
    /// <returns>A task that represents the asynchronous write operation.</returns>
    public async Task WriteAsync(long position, byte[] bytes, int length, bool fireAndForget = true)
    {
        if (bytes.Length < length)
            throw new ArgumentOutOfRangeException(nameof(length));

        if (IsFaulted && Exception is not null)
            throw Exception;

        await _inputBuffer.TryAdd(new Packet(position, bytes, length)).ConfigureAwait(false);

        if (fireAndForget == false)
        {
            // to ensure that the written packet is actually stored on the stream
            await FlushAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Watches for incoming packets and writes them to the stream.
    /// </summary>
    /// <returns>A task that represents the asynchronous watch operation.</returns>
    private async Task Watcher()
    {
        try
        {
            StartState();
            while (!_watcherCancelSource.IsCancellationRequested)
            {
                await _inputBuffer.WaitTryTakeAsync(_watcherCancelSource.Token, WritePacketOnFile).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is TaskCanceledException || ex is OperationCanceledException)
        {
            Logger?.LogError(ex, "ConcurrentStream: Call CancelState()");
            CancelState();
        }
        catch (Exception ex)
        {
            SetException(ex);
            _watcherCancelSource.Cancel(false);
        }
        finally
        {
            await Task.Yield();
        }
    }

    /// <summary>
    /// Sets the position within the current stream.
    /// </summary>
    /// <param name="offset">A byte offset relative to the origin parameter.</param>
    /// <param name="origin">A value of type SeekOrigin indicating the reference point used to obtain the new position.</param>
    /// <returns>The new position within the current stream.</returns>
    public long Seek(long offset, SeekOrigin origin)
    {
        if (offset != Position && CanSeek)
        {
            _stream.Seek(offset, origin);
        }

        return Position;
    }

    /// <summary>
    /// Sets the length of the current stream.
    /// </summary>
    /// <param name="value">The desired length of the current stream in bytes.</param>
    public void SetLength(long value)
    {
        _stream.SetLength(value);
    }

    /// <summary>
    /// Writes a packet to the stream.
    /// </summary>
    /// <param name="packet">The packet to write.</param>
    /// <returns>A task that represents the asynchronous write operation.</returns>
    private async Task WritePacketOnFile(Packet packet)
    {
        // seek with SeekOrigin.Begin is so faster than SeekOrigin.Current
        Seek(packet.Position, SeekOrigin.Begin);
        await _stream.WriteAsync(packet.Data).ConfigureAwait(false);
        packet.Dispose();
    }

    /// <summary>
    /// Flushes the stream asynchronously.
    /// </summary>
    /// <returns>A task that represents the asynchronous flush operation.</returns>
    public async Task FlushAsync()
    {
        await _inputBuffer.WaitToComplete().ConfigureAwait(false);

        if (_stream?.CanRead == true)
        {
            await _stream.FlushAsync().ConfigureAwait(false);
        }

        GC.Collect();
    }

    /// <summary>
    /// Releases the unmanaged resources used by the ConcurrentStream and optionally releases the managed resources.
    /// </summary>
    public void Dispose()
    {
        if (!_disposed)
        {
            _disposed = true;
            _watcherCancelSource.Cancel(); // request the cancellation
            _stream.Dispose();
            _inputBuffer.Dispose();
        }
    }

    /// <summary>
    /// Asynchronously releases the unmanaged resources used by the ConcurrentStream.
    /// </summary>
    /// <returns>A task that represents the asynchronous dispose operation.</returns>
    public async ValueTask DisposeAsync()
    {
        if (!_disposed)
        {
            _disposed = true;
            await _watcherCancelSource.CancelAsync().ConfigureAwait(false); // request the cancellation
            await _stream.DisposeAsync().ConfigureAwait(false);
            _inputBuffer.Dispose();
        }
    }
}