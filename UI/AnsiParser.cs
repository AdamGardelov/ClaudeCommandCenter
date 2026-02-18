using System.Text.RegularExpressions;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace ClaudeCommandCenter.UI;

/// <summary>
/// Converts ANSI escape sequences from tmux capture-pane -e into Spectre.Console renderables.
/// </summary>
public static partial class AnsiParser
{
    // Matches CSI sequences: ESC [ (params) (final byte)
    [GeneratedRegex(@"\x1b\[([0-9;]*)([A-Za-z])")]
    private static partial Regex CsiRegex();

    private static readonly Color[] BasicColors =
    [
        Color.Black, Color.Maroon, Color.Green, Color.Olive,
        Color.Navy, Color.Purple, Color.Teal, Color.Silver
    ];

    private static readonly Color[] BrightColors =
    [
        Color.Grey, Color.Red, Color.Lime, Color.Yellow,
        Color.Blue, Color.Fuchsia, Color.Aqua, Color.White
    ];

    public static IRenderable ParseLine(string ansiLine, int maxWidth)
    {
        return new AnsiLineRenderable(ansiLine, maxWidth);
    }

    private sealed class AnsiLineRenderable(string ansiText, int maxWidth) : IRenderable
    {
        public Measurement Measure(RenderOptions options, int maxWidth1)
            => new(0, maxWidth);

        public IEnumerable<Segment> Render(RenderOptions options, int maxWidth1)
        {
            var effectiveMax = Math.Min(maxWidth, maxWidth1);

            // Leading space for padding
            yield return new Segment(" ");
            effectiveMax--;

            var state = new AnsiState();
            var visualWidth = 0;
            var lastEnd = 0;

            foreach (Match match in CsiRegex().Matches(ansiText))
            {
                if (visualWidth >= effectiveMax) break;

                // Text before this escape sequence
                if (match.Index > lastEnd)
                {
                    var text = ansiText[lastEnd..match.Index];
                    var remaining = effectiveMax - visualWidth;
                    if (text.Length > remaining) text = text[..remaining];
                    if (text.Length > 0)
                    {
                        yield return new Segment(text, state.ToStyle());
                        visualWidth += text.Length;
                    }
                }

                // Only process SGR sequences (final byte 'm')
                if (match.Groups[2].Value == "m")
                    ApplySgr(ref state, match.Groups[1].Value);

                lastEnd = match.Index + match.Length;
            }

            // Remaining text after last escape
            if (lastEnd < ansiText.Length && visualWidth < effectiveMax)
            {
                var text = ansiText[lastEnd..];
                var remaining = effectiveMax - visualWidth;
                if (text.Length > remaining) text = text[..remaining];
                if (text.Length > 0)
                    yield return new Segment(text, state.ToStyle());
            }

            yield return Segment.LineBreak;
        }
    }

    private struct AnsiState
    {
        public Color? Foreground;
        public Color? Background;
        public Decoration Decoration;

        public readonly Style ToStyle()
            => new(Foreground, Background, Decoration);
    }

    private static void ApplySgr(ref AnsiState state, string paramsStr)
    {
        if (string.IsNullOrEmpty(paramsStr))
        {
            // ESC[m is equivalent to ESC[0m (reset)
            state = default;
            return;
        }

        var parts = paramsStr.Split(';');
        var codes = new int[parts.Length];
        for (var i = 0; i < parts.Length; i++)
            codes[i] = int.TryParse(parts[i], out var v) ? v : 0;

        for (var i = 0; i < codes.Length; i++)
        {
            switch (codes[i])
            {
                case 0:
                    state = default;
                    break;
                case 1:
                    state.Decoration |= Decoration.Bold;
                    break;
                case 2:
                    state.Decoration |= Decoration.Dim;
                    break;
                case 3:
                    state.Decoration |= Decoration.Italic;
                    break;
                case 4:
                    state.Decoration |= Decoration.Underline;
                    break;
                case 22:
                    state.Decoration &= ~(Decoration.Bold | Decoration.Dim);
                    break;
                case 23:
                    state.Decoration &= ~Decoration.Italic;
                    break;
                case 24:
                    state.Decoration &= ~Decoration.Underline;
                    break;
                case >= 30 and <= 37:
                    state.Foreground = BasicColors[codes[i] - 30];
                    break;
                case 38:
                    i = ParseExtendedColor(codes, i, out state.Foreground);
                    break;
                case 39:
                    state.Foreground = null;
                    break;
                case >= 40 and <= 47:
                    state.Background = BasicColors[codes[i] - 40];
                    break;
                case 48:
                    i = ParseExtendedColor(codes, i, out state.Background);
                    break;
                case 49:
                    state.Background = null;
                    break;
                case >= 90 and <= 97:
                    state.Foreground = BrightColors[codes[i] - 90];
                    break;
                case >= 100 and <= 107:
                    state.Background = BrightColors[codes[i] - 100];
                    break;
            }
        }
    }

    private static int ParseExtendedColor(int[] codes, int i, out Color? color)
    {
        // 38;5;N — 256-color palette
        if (i + 2 < codes.Length && codes[i + 1] == 5)
        {
            color = ColorFromPalette(Math.Clamp(codes[i + 2], 0, 255));
            return i + 2;
        }

        // 38;2;R;G;B — true color
        if (i + 4 < codes.Length && codes[i + 1] == 2)
        {
            color = new Color(
                (byte)Math.Clamp(codes[i + 2], 0, 255),
                (byte)Math.Clamp(codes[i + 3], 0, 255),
                (byte)Math.Clamp(codes[i + 4], 0, 255));
            return i + 4;
        }

        color = null;
        return i;
    }

    private static Color ColorFromPalette(int n)
    {
        // 0-7: standard colors
        if (n < 8) return BasicColors[n];
        // 8-15: bright colors
        if (n < 16) return BrightColors[n - 8];
        // 16-231: 6x6x6 RGB cube
        if (n < 232)
        {
            var idx = n - 16;
            var b = idx % 6;
            var g = (idx / 6) % 6;
            var r = idx / 36;
            return new Color(
                (byte)(r == 0 ? 0 : 55 + 40 * r),
                (byte)(g == 0 ? 0 : 55 + 40 * g),
                (byte)(b == 0 ? 0 : 55 + 40 * b));
        }
        // 232-255: grayscale ramp
        var grey = (byte)(8 + 10 * (n - 232));
        return new Color(grey, grey, grey);
    }
}
