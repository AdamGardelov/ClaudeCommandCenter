using Spectre.Console;

namespace ClaudeCommandCenter.UI;

public struct AnsiState
{
    public Color? Foreground;
    public Color? Background;
    public Decoration Decoration;

    public readonly Style ToStyle()
        => new(Foreground, Background, Decoration);
}
