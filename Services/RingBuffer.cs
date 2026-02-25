namespace ClaudeCommandCenter.Services;

/// <summary>
/// Thread-safe circular buffer for terminal output lines.
/// A background reader thread appends lines; CapturePaneContent reads them.
/// </summary>
public class RingBuffer(int capacity = 500)
{
    private readonly string[] _buffer = new string[capacity];
    private readonly Lock _lock = new();
    private int _head; // Next write position
    private int _count; // Number of lines stored
    private long _totalWritten; // Total lines ever written (monotonically increasing)

    /// <summary>
    /// Total number of lines ever written. Used as a position cursor for consumers.
    /// </summary>
    public long TotalWritten
    {
        get
        {
            lock (_lock)
                return _totalWritten;
        }
    }

    public void AppendLine(string line)
    {
        lock (_lock)
        {
            _buffer[_head] = line;
            _head = (_head + 1) % capacity;
            if (_count < capacity)
                _count++;
            _totalWritten++;
        }
    }

    public void AppendChunk(string chunk)
    {
        var lines = chunk.Split('\n');
        lock (_lock)
        {
            foreach (var line in lines)
            {
                _buffer[_head] = line;
                _head = (_head + 1) % capacity;
                if (_count < capacity)
                    _count++;
                _totalWritten++;
            }
        }
    }

    public string GetContent(int maxLines = 500)
    {
        lock (_lock)
        {
            var count = Math.Min(maxLines, _count);
            if (count == 0)
                return "";

            var start = (_head - count + capacity) % capacity;
            var lines = new string[count];
            for (var i = 0; i < count; i++)
                lines[i] = _buffer[(start + i) % capacity];

            return string.Join('\n', lines);
        }
    }

    /// <summary>
    /// Returns lines written since the given position, updating position to current.
    /// Returns null if no new content. Handles buffer wrap gracefully.
    /// </summary>
    public string? GetNewContent(ref long position)
    {
        lock (_lock)
        {
            if (_totalWritten == position)
                return null;

            var newLines = _totalWritten - position;
            // If more was written than the buffer holds, return everything in the buffer
            if (newLines > _count)
                newLines = _count;

            position = _totalWritten;

            if (newLines == 0)
                return null;

            var start = (_head - (int)newLines + capacity) % capacity;
            var lines = new string[(int)newLines];
            for (var i = 0; i < (int)newLines; i++)
                lines[i] = _buffer[(start + i) % capacity];

            return string.Join('\n', lines);
        }
    }

    public void Clear()
    {
        lock (_lock)
        {
            _head = 0;
            _count = 0;
            _totalWritten = 0;
        }
    }
}
