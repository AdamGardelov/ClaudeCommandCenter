using System.Text;

namespace ClaudeCommandCenter.Services.ConPty;

/// <summary>
/// Minimal virtual terminal screen buffer that interprets VT/ANSI escape sequences
/// from ConPTY output and maintains a 2D character grid with SGR attributes.
/// Replaces RingBuffer for ConPTY — RingBuffer naively splits on \n which produces
/// garbled output because ConPTY uses cursor movement to position text.
/// </summary>
internal class VtScreenBuffer
{
    private int _width;
    private int _height;
    private Cell[,] _cells;      // [row, col]
    private int _cursorRow;
    private int _cursorCol;
    private SgrState _sgr;       // Current SGR attributes
    private long _version;       // Incremented on every mutation for change detection
    private readonly Lock _lock = new();

    // Parser state
    private enum ParseState { Normal, Escape, Csi, OscString, CharsetSelect }
    private ParseState _parseState;
    private readonly StringBuilder _csiParams = new();

    // Scrollback: ring buffer of rendered line strings for lines that scrolled off the top
    private readonly string[] _scrollback;
    private int _scrollbackHead;
    private int _scrollbackCount;
    private const int ScrollbackCapacity = 200;

    // Alternate screen buffer support
    private Cell[,]? _savedCells;       // Saved main screen when in alt buffer
    private int _savedCursorRow;
    private int _savedCursorCol;
    private SgrState _savedSgr;
    private bool _inAlternateScreen;

    // Application cursor key mode (DECCKM) — when true, arrow keys send \eOA instead of \e[A
    internal bool ApplicationCursorKeys => _applicationCursorKeys;
    private bool _applicationCursorKeys;

    // Scroll region (DECSTBM) — top and bottom margins (0-based, inclusive)
    private int _scrollTop;
    private int _scrollBottom; // Initialized to _height - 1

    /// <summary>
    /// Structured SGR state — tracked as individual fields, serialized only when rendering.
    /// This avoids the accumulation bug where string concatenation grew SGR params endlessly.
    /// </summary>
    private struct SgrState
    {
        // Foreground: -1 = default, 0-255 = palette, 0x01RRGGBB = truecolor
        public int Fg;
        // Background: same encoding
        public int Bg;
        public bool Bold;
        public bool Dim;
        public bool Italic;
        public bool Underline;
        public bool Reverse;

        public static SgrState Default => new() { Fg = -1, Bg = -1 };

        public readonly bool IsDefault =>
            Fg == -1 && Bg == -1 && !Bold && !Dim && !Italic && !Underline && !Reverse;

        public readonly string Serialize()
        {
            if (IsDefault) return "";

            var sb = new StringBuilder();
            if (Bold) sb.Append("1;");
            if (Dim) sb.Append("2;");
            if (Italic) sb.Append("3;");
            if (Underline) sb.Append("4;");
            if (Reverse) sb.Append("7;");

            if (Fg >= 0 && Fg <= 255)
                sb.Append($"38;5;{Fg};");
            else if (Fg > 0x01000000)
            {
                var r = (Fg >> 16) & 0xFF;
                var g = (Fg >> 8) & 0xFF;
                var b = Fg & 0xFF;
                sb.Append($"38;2;{r};{g};{b};");
            }

            if (Bg >= 0 && Bg <= 255)
                sb.Append($"48;5;{Bg};");
            else if (Bg > 0x01000000)
            {
                var r = (Bg >> 16) & 0xFF;
                var g = (Bg >> 8) & 0xFF;
                var b = Bg & 0xFF;
                sb.Append($"48;2;{r};{g};{b};");
            }

            // Remove trailing semicolon
            if (sb.Length > 0) sb.Length--;
            return sb.ToString();
        }
    }

    private readonly struct Cell
    {
        public readonly char Char;
        public readonly string Sgr; // Serialized SGR string, "" for default

        public Cell(char c, string sgr) { Char = c; Sgr = sgr; }
        public static Cell Empty => new(' ', "");
    }

    public VtScreenBuffer(int width, int height)
    {
        _width = width;
        _height = height;
        _cells = new Cell[height, width];
        _scrollback = new string[ScrollbackCapacity];
        Clear();
        _scrollBottom = height - 1;
    }

    public long Version
    {
        get { lock (_lock) return _version; }
    }

    public void Feed(string text)
    {
        lock (_lock)
        {
            foreach (var ch in text)
                ProcessChar(ch);
            _version++;
        }
    }

    public void Resize(int newWidth, int newHeight)
    {
        lock (_lock)
        {
            if (newWidth == _width && newHeight == _height)
                return;

            var newCells = new Cell[newHeight, newWidth];
            var copyRows = Math.Min(_height, newHeight);
            var copyCols = Math.Min(_width, newWidth);

            for (var r = 0; r < copyRows; r++)
                for (var c = 0; c < copyCols; c++)
                    newCells[r, c] = _cells[r, c];

            _cells = newCells;
            _width = newWidth;
            _height = newHeight;
            _cursorRow = Math.Min(_cursorRow, newHeight - 1);
            _cursorCol = Math.Min(_cursorCol, newWidth - 1);
            _scrollTop = 0;
            _scrollBottom = newHeight - 1;
            _version++;
        }
    }

    public string GetContent(int maxLines = 500)
    {
        lock (_lock)
        {
            var sb = new StringBuilder();

            // Current screen rows only (skip scrollback — the Renderer takes
            // the bottom N lines anyway, and scrollback can be stale)
            var screenLines = Math.Min(_height, maxLines);
            for (var row = 0; row < screenLines; row++)
            {
                if (sb.Length > 0) sb.Append('\n');
                RenderRow(sb, row);
            }

            return sb.ToString();
        }
    }

    public void Clear()
    {
        lock (_lock)
        {
            for (var r = 0; r < _height; r++)
                for (var c = 0; c < _width; c++)
                    _cells[r, c] = Cell.Empty;
            _cursorRow = 0;
            _cursorCol = 0;
            _sgr = SgrState.Default;
            _parseState = ParseState.Normal;
            _csiParams.Clear();
            _scrollTop = 0;
            _scrollBottom = _height - 1;
            _version++;
        }
    }

    private void EnterAlternateScreen()
    {
        if (_inAlternateScreen)
            return;

        // Save main screen state
        _savedCells = (Cell[,])_cells.Clone();
        _savedCursorRow = _cursorRow;
        _savedCursorCol = _cursorCol;
        _savedSgr = _sgr;
        _inAlternateScreen = true;

        // Clear the screen for alt buffer (alt screen starts blank)
        for (var r = 0; r < _height; r++)
            for (var c = 0; c < _width; c++)
                _cells[r, c] = Cell.Empty;
        _cursorRow = 0;
        _cursorCol = 0;
        _sgr = SgrState.Default;
        _version++;
    }

    private void LeaveAlternateScreen()
    {
        if (!_inAlternateScreen)
            return;

        // Restore main screen state
        if (_savedCells != null)
        {
            var copyRows = Math.Min(_savedCells.GetLength(0), _height);
            var copyCols = Math.Min(_savedCells.GetLength(1), _width);
            for (var r = 0; r < _height; r++)
                for (var c = 0; c < _width; c++)
                    _cells[r, c] = Cell.Empty;
            for (var r = 0; r < copyRows; r++)
                for (var c = 0; c < copyCols; c++)
                    _cells[r, c] = _savedCells[r, c];
            _savedCells = null;
        }

        _cursorRow = Math.Min(_savedCursorRow, _height - 1);
        _cursorCol = Math.Min(_savedCursorCol, _width - 1);
        _sgr = _savedSgr;
        _inAlternateScreen = false;
        _version++;
    }

    private void HandlePrivateMode(string paramsStr, bool enabled)
    {
        foreach (var part in paramsStr.Split(';'))
        {
            if (!int.TryParse(part, out var mode))
                continue;

            switch (mode)
            {
                case 1049: // Alternate screen buffer (with save/restore cursor)
                case 47:   // Alternate screen buffer (without save/restore)
                    if (enabled)
                        EnterAlternateScreen();
                    else
                        LeaveAlternateScreen();
                    break;
                case 1: // Application cursor keys (DECCKM)
                    _applicationCursorKeys = enabled;
                    break;
            }
        }
    }

    private void SetScrollRegion(string paramsStr)
    {
        if (string.IsNullOrEmpty(paramsStr))
        {
            // Reset to full screen
            _scrollTop = 0;
            _scrollBottom = _height - 1;
        }
        else
        {
            ParseTwoParams(paramsStr, out var top, out var bottom, 1, _height);
            _scrollTop = Math.Clamp(top - 1, 0, _height - 1);
            _scrollBottom = Math.Clamp(bottom - 1, 0, _height - 1);
            if (_scrollTop >= _scrollBottom)
            {
                _scrollTop = 0;
                _scrollBottom = _height - 1;
            }
        }
        // DECSTBM resets cursor to home position
        _cursorRow = 0;
        _cursorCol = 0;
    }

    private void RenderRow(StringBuilder sb, int row)
    {
        var lastCol = _width - 1;
        while (lastCol >= 0 && _cells[row, lastCol].Char == ' ' && _cells[row, lastCol].Sgr == "")
            lastCol--;

        var activeSgr = "";
        for (var col = 0; col <= lastCol; col++)
        {
            var cell = _cells[row, col];
            if (cell.Sgr != activeSgr)
            {
                if (cell.Sgr == "")
                    sb.Append("\x1b[m");
                else
                    sb.Append($"\x1b[{cell.Sgr}m");
                activeSgr = cell.Sgr;
            }
            sb.Append(cell.Char);
        }

        if (activeSgr != "")
            sb.Append("\x1b[m");
    }

    private string RenderRowToString(int row)
    {
        var sb = new StringBuilder();
        RenderRow(sb, row);
        return sb.ToString();
    }

    private void ProcessChar(char ch)
    {
        switch (_parseState)
        {
            case ParseState.Normal:
                ProcessNormal(ch);
                break;
            case ParseState.Escape:
                ProcessEscape(ch);
                break;
            case ParseState.Csi:
                ProcessCsi(ch);
                break;
            case ParseState.OscString:
                ProcessOsc(ch);
                break;
            case ParseState.CharsetSelect:
                // Consume the charset designator char and return to normal
                _parseState = ParseState.Normal;
                break;
        }
    }

    private void ProcessNormal(char ch)
    {
        switch (ch)
        {
            case '\x1b':
                _parseState = ParseState.Escape;
                break;
            case '\r':
                _cursorCol = 0;
                break;
            case '\n':
                LineFeed();
                break;
            case '\t':
                var tabStop = ((_cursorCol / 8) + 1) * 8;
                _cursorCol = Math.Min(tabStop, _width - 1);
                break;
            case '\b':
                if (_cursorCol > 0) _cursorCol--;
                break;
            case '\a':
                break;
            default:
                if (ch >= ' ')
                {
                    if (_cursorCol >= _width)
                    {
                        _cursorCol = 0;
                        LineFeed();
                    }
                    _cells[_cursorRow, _cursorCol] = new Cell(ch, _sgr.Serialize());
                    _cursorCol++;
                }
                break;
        }
    }

    private void ProcessEscape(char ch)
    {
        switch (ch)
        {
            case '[':
                _parseState = ParseState.Csi;
                _csiParams.Clear();
                break;
            case ']':
                _parseState = ParseState.OscString;
                break;
            case '(' or ')' or '*' or '+':
                // Next char is the charset designator — consume it
                _parseState = ParseState.CharsetSelect;
                break;
            case 'M':
                // Reverse Index — move cursor up, scroll down if at top of scroll region
                if (_cursorRow == _scrollTop)
                    ScrollDown();
                else if (_cursorRow > 0)
                    _cursorRow--;
                _parseState = ParseState.Normal;
                break;
            default:
                _parseState = ParseState.Normal;
                break;
        }
    }

    private void ProcessCsi(char ch)
    {
        if (ch is (>= '0' and <= '9') or ';' or '?' or '!' or '>' or ' ')
        {
            _csiParams.Append(ch);
            return;
        }

        var paramsStr = _csiParams.ToString();
        ExecuteCsi(ch, paramsStr);
        _parseState = ParseState.Normal;
    }

    private void ProcessOsc(char ch)
    {
        if (ch == '\a')
        {
            _parseState = ParseState.Normal;
            return;
        }
        if (ch == '\x1b')
        {
            _parseState = ParseState.Escape;
            return;
        }
        // Consume and ignore OSC content
    }

    private void ExecuteCsi(char cmd, string paramsStr)
    {
        var isPrivate = paramsStr.Length > 0 && paramsStr[0] == '?';
        var effectiveParams = isPrivate ? paramsStr[1..] : paramsStr;

        switch (cmd)
        {
            case 'm':
                ApplySgr(paramsStr);
                break;

            case 'H' or 'f':
            {
                ParseTwoParams(effectiveParams, out var row, out var col, 1, 1);
                _cursorRow = Math.Clamp(row - 1, 0, _height - 1);
                _cursorCol = Math.Clamp(col - 1, 0, _width - 1);
                break;
            }

            case 'A':
            {
                var n = ParseOneParam(effectiveParams, 1);
                _cursorRow = Math.Max(0, _cursorRow - n);
                break;
            }

            case 'B':
            {
                var n = ParseOneParam(effectiveParams, 1);
                _cursorRow = Math.Min(_height - 1, _cursorRow + n);
                break;
            }

            case 'C':
            {
                var n = ParseOneParam(effectiveParams, 1);
                _cursorCol = Math.Min(_width - 1, _cursorCol + n);
                break;
            }

            case 'D':
            {
                var n = ParseOneParam(effectiveParams, 1);
                _cursorCol = Math.Max(0, _cursorCol - n);
                break;
            }

            case 'E':
            {
                var n = ParseOneParam(effectiveParams, 1);
                _cursorRow = Math.Min(_height - 1, _cursorRow + n);
                _cursorCol = 0;
                break;
            }

            case 'F':
            {
                var n = ParseOneParam(effectiveParams, 1);
                _cursorRow = Math.Max(0, _cursorRow - n);
                _cursorCol = 0;
                break;
            }

            case 'G':
            {
                var col = ParseOneParam(effectiveParams, 1);
                _cursorCol = Math.Clamp(col - 1, 0, _width - 1);
                break;
            }

            case 'd':
            {
                var row = ParseOneParam(effectiveParams, 1);
                _cursorRow = Math.Clamp(row - 1, 0, _height - 1);
                break;
            }

            case 'J':
            {
                var mode = ParseOneParam(effectiveParams, 0);
                EraseDisplay(mode);
                break;
            }

            case 'K':
            {
                var mode = ParseOneParam(effectiveParams, 0);
                EraseLine(mode);
                break;
            }

            case 'L':
                InsertLines(ParseOneParam(effectiveParams, 1));
                break;

            case 'M':
                DeleteLines(ParseOneParam(effectiveParams, 1));
                break;

            case 'S':
            {
                var n = ParseOneParam(effectiveParams, 1);
                for (var i = 0; i < n; i++) ScrollUp();
                break;
            }

            case 'T':
            {
                var n = ParseOneParam(effectiveParams, 1);
                for (var i = 0; i < n; i++) ScrollDown();
                break;
            }

            case 'X':
            {
                var n = ParseOneParam(effectiveParams, 1);
                for (var i = 0; i < n && _cursorCol + i < _width; i++)
                    _cells[_cursorRow, _cursorCol + i] = Cell.Empty;
                break;
            }

            case 'P':
            {
                var n = ParseOneParam(effectiveParams, 1);
                var row = _cursorRow;
                var col = _cursorCol;
                for (var i = col; i < _width; i++)
                    _cells[row, i] = (i + n < _width) ? _cells[row, i + n] : Cell.Empty;
                break;
            }

            case '@':
            {
                var n = ParseOneParam(effectiveParams, 1);
                var row = _cursorRow;
                var col = _cursorCol;
                for (var i = _width - 1; i >= col + n; i--)
                    _cells[row, i] = _cells[row, i - n];
                for (var i = col; i < col + n && i < _width; i++)
                    _cells[row, i] = Cell.Empty;
                break;
            }

            case 'h':
                if (isPrivate)
                    HandlePrivateMode(effectiveParams, enabled: true);
                break;

            case 'l':
                if (isPrivate)
                    HandlePrivateMode(effectiveParams, enabled: false);
                break;

            case 'r':
                if (!isPrivate) // Only handle DECSTBM, not private mode 'r'
                    SetScrollRegion(effectiveParams);
                break;

            case 'n' or 'c' or 't' or 'q':
                break;
        }
    }

    /// <summary>
    /// Parse SGR params and update the structured SGR state.
    /// Each \e[...m sequence is self-contained — it replaces/modifies individual attributes.
    /// </summary>
    private void ApplySgr(string paramsStr)
    {
        if (string.IsNullOrEmpty(paramsStr) || paramsStr == "0")
        {
            _sgr = SgrState.Default;
            return;
        }

        var codes = ParseSgrCodes(paramsStr);
        for (var i = 0; i < codes.Length; i++)
        {
            switch (codes[i])
            {
                case 0: _sgr = SgrState.Default; break;
                case 1: _sgr.Bold = true; break;
                case 2: _sgr.Dim = true; break;
                case 3: _sgr.Italic = true; break;
                case 4: _sgr.Underline = true; break;
                case 7: _sgr.Reverse = true; break;
                case 22: _sgr.Bold = false; _sgr.Dim = false; break;
                case 23: _sgr.Italic = false; break;
                case 24: _sgr.Underline = false; break;
                case 27: _sgr.Reverse = false; break;
                case >= 30 and <= 37: _sgr.Fg = codes[i] - 30; break;
                case 38:
                    i = ParseExtendedColor(codes, i, out _sgr.Fg);
                    break;
                case 39: _sgr.Fg = -1; break;
                case >= 40 and <= 47: _sgr.Bg = codes[i] - 40; break;
                case 48:
                    i = ParseExtendedColor(codes, i, out _sgr.Bg);
                    break;
                case 49: _sgr.Bg = -1; break;
                case >= 90 and <= 97: _sgr.Fg = codes[i] - 90 + 8; break;
                case >= 100 and <= 107: _sgr.Bg = codes[i] - 100 + 8; break;
            }
        }
    }

    private static int[] ParseSgrCodes(string paramsStr)
    {
        var parts = paramsStr.Split(';');
        var codes = new int[parts.Length];
        for (var i = 0; i < parts.Length; i++)
            codes[i] = int.TryParse(parts[i], out var v) ? v : 0;
        return codes;
    }

    private static int ParseExtendedColor(int[] codes, int i, out int color)
    {
        // 38;5;N — 256-color palette
        if (i + 2 < codes.Length && codes[i + 1] == 5)
        {
            color = Math.Clamp(codes[i + 2], 0, 255);
            return i + 2;
        }
        // 38;2;R;G;B — true color (encoded as 0x01RRGGBB)
        if (i + 4 < codes.Length && codes[i + 1] == 2)
        {
            var r = Math.Clamp(codes[i + 2], 0, 255);
            var g = Math.Clamp(codes[i + 3], 0, 255);
            var b = Math.Clamp(codes[i + 4], 0, 255);
            color = 0x01000000 | (r << 16) | (g << 8) | b;
            return i + 4;
        }
        color = -1;
        return i;
    }

    private void LineFeed()
    {
        if (_cursorRow == _scrollBottom)
            ScrollUpRegion();
        else if (_cursorRow < _height - 1)
            _cursorRow++;
    }

    private void ScrollUp()
    {
        ScrollUpRegion();
    }

    private void ScrollUpRegion()
    {
        // Save the top row to scrollback only if scrolling the full screen
        if (_scrollTop == 0)
        {
            _scrollback[_scrollbackHead] = RenderRowToString(0);
            _scrollbackHead = (_scrollbackHead + 1) % ScrollbackCapacity;
            if (_scrollbackCount < ScrollbackCapacity)
                _scrollbackCount++;
        }

        for (var r = _scrollTop; r < _scrollBottom; r++)
            for (var c = 0; c < _width; c++)
                _cells[r, c] = _cells[r + 1, c];

        for (var c = 0; c < _width; c++)
            _cells[_scrollBottom, c] = Cell.Empty;
    }

    private void ScrollDown()
    {
        for (var r = _scrollBottom; r > _scrollTop; r--)
            for (var c = 0; c < _width; c++)
                _cells[r, c] = _cells[r - 1, c];

        for (var c = 0; c < _width; c++)
            _cells[_scrollTop, c] = Cell.Empty;
    }

    private void EraseDisplay(int mode)
    {
        switch (mode)
        {
            case 0:
                for (var c = _cursorCol; c < _width; c++)
                    _cells[_cursorRow, c] = Cell.Empty;
                for (var r = _cursorRow + 1; r < _height; r++)
                    for (var c = 0; c < _width; c++)
                        _cells[r, c] = Cell.Empty;
                break;
            case 1:
                for (var r = 0; r < _cursorRow; r++)
                    for (var c = 0; c < _width; c++)
                        _cells[r, c] = Cell.Empty;
                for (var c = 0; c <= _cursorCol; c++)
                    _cells[_cursorRow, c] = Cell.Empty;
                break;
            case 2 or 3:
                for (var r = 0; r < _height; r++)
                    for (var c = 0; c < _width; c++)
                        _cells[r, c] = Cell.Empty;
                if (mode == 3)
                {
                    _scrollbackHead = 0;
                    _scrollbackCount = 0;
                }
                break;
        }
    }

    private void EraseLine(int mode)
    {
        switch (mode)
        {
            case 0:
                for (var c = _cursorCol; c < _width; c++)
                    _cells[_cursorRow, c] = Cell.Empty;
                break;
            case 1:
                for (var c = 0; c <= _cursorCol; c++)
                    _cells[_cursorRow, c] = Cell.Empty;
                break;
            case 2:
                for (var c = 0; c < _width; c++)
                    _cells[_cursorRow, c] = Cell.Empty;
                break;
        }
    }

    private void InsertLines(int n)
    {
        var bottom = _scrollBottom;
        for (var i = 0; i < n; i++)
        {
            for (var r = bottom; r > _cursorRow; r--)
                for (var c = 0; c < _width; c++)
                    _cells[r, c] = _cells[r - 1, c];
            for (var c = 0; c < _width; c++)
                _cells[_cursorRow, c] = Cell.Empty;
        }
    }

    private void DeleteLines(int n)
    {
        var bottom = _scrollBottom;
        for (var i = 0; i < n; i++)
        {
            for (var r = _cursorRow; r < bottom; r++)
                for (var c = 0; c < _width; c++)
                    _cells[r, c] = _cells[r + 1, c];
            for (var c = 0; c < _width; c++)
                _cells[bottom, c] = Cell.Empty;
        }
    }

    private static int ParseOneParam(string paramsStr, int defaultVal)
    {
        if (string.IsNullOrEmpty(paramsStr))
            return defaultVal;
        return int.TryParse(paramsStr, out var v) ? v : defaultVal;
    }

    private static void ParseTwoParams(string paramsStr, out int a, out int b, int defaultA, int defaultB)
    {
        a = defaultA;
        b = defaultB;
        if (string.IsNullOrEmpty(paramsStr))
            return;
        var idx = paramsStr.IndexOf(';');
        if (idx < 0)
        {
            if (int.TryParse(paramsStr, out var v))
                a = v;
            return;
        }
        if (idx > 0 && int.TryParse(paramsStr.AsSpan(0, idx), out var va))
            a = va;
        if (idx + 1 < paramsStr.Length && int.TryParse(paramsStr.AsSpan(idx + 1), out var vb))
            b = vb;
    }
}
