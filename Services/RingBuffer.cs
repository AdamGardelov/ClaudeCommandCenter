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

    public void AppendLine(string line)
    {
        lock (_lock)
        {
            _buffer[_head] = line;
            _head = (_head + 1) % capacity;
            if (_count < capacity)
                _count++;
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

    public void Clear()
    {
        lock (_lock)
        {
            _head = 0;
            _count = 0;
        }
    }
}
