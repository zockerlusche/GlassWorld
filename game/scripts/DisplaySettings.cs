using GlassWorld.Presentation.Ui;
using Godot;

namespace GlassWorld.Game;

/// <summary>
/// A1748 (D957 v4 section 10): puts the graphics settings into effect - window mode and size, V-Sync, frame rate limit.
/// 'As the game starts' (null) leaves the window as it is; nothing happens without a screen (headless runs).
/// </summary>
public static class DisplaySettings
{
    /// <summary>The window as the game started (mode and size), so 'as the game starts' can go back to it.</summary>
    private static (DisplayServer.WindowMode Mode, Vector2I Size)? _start;

    /// <summary>Applies the settings' graphics part.</summary>
    public static void Apply(PlayerSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (DisplayServer.GetName() == "headless")
        {
            return;
        }

        _start ??= (DisplayServer.WindowGetMode(), DisplayServer.WindowGetSize());
        var mode = settings.WindowMode switch
        {
            DisplayChoices.Windowed => DisplayServer.WindowMode.Windowed,
            DisplayChoices.Fullscreen => DisplayServer.WindowMode.ExclusiveFullscreen,
            DisplayChoices.Borderless => DisplayServer.WindowMode.Fullscreen,
            _ => _start.Value.Mode,
        };
        if (DisplayServer.WindowGetMode() != mode)
        {
            DisplayServer.WindowSetMode(mode);
        }

        // The size counts in a framed window; full screen takes the screen's own.
        if (mode is DisplayServer.WindowMode.Windowed or DisplayServer.WindowMode.Maximized)
        {
            var size = DisplayChoices.Size(settings.Resolution) is { } s ? new Vector2I(s.Width, s.Height) : _start.Value.Size;
            if (DisplayServer.WindowGetSize() != size)
            {
                if (mode == DisplayServer.WindowMode.Maximized)
                {
                    DisplayServer.WindowSetMode(DisplayServer.WindowMode.Windowed);
                }

                DisplayServer.WindowSetSize(size);
                var screen = DisplayServer.ScreenGetUsableRect(DisplayServer.WindowGetCurrentScreen());
                DisplayServer.WindowSetPosition(screen.Position + ((screen.Size - size) / 2));
            }
        }

        DisplayServer.WindowSetVsyncMode(settings.Vsync ? DisplayServer.VSyncMode.Enabled : DisplayServer.VSyncMode.Disabled);
        Engine.MaxFps = settings.MaxFps;
    }
}
