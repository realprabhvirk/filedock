using System.IO;
using System.Windows.Input;
using FileDock.Models;
using FileDock.Services;

namespace FileDock.Tests;

public class SettingsTests
{
    [Fact]
    public void Defaults_match_the_specification()
    {
        var settings = new AppSettings();
        Assert.Equal(DockEdge.Right, settings.Edge);
        Assert.Equal(0.5, settings.VerticalPosition);
        Assert.Null(settings.Display);
        Assert.InRange(settings.CollapseDelayMs, 700, 1200);
        Assert.True(settings.HotkeyEnabled);
        Assert.Equal("Ctrl+Alt+Space", settings.Hotkey);
        Assert.False(settings.ConfirmClear);
    }

    [Fact]
    public void Normalize_repairs_out_of_range_values()
    {
        var settings = new AppSettings
        {
            Edge = (DockEdge)42,
            VerticalPosition = 7,
            CollapseDelayMs = 1400,
            Hotkey = " ",
            Display = "",
        };
        settings.Normalize();

        Assert.Equal(DockEdge.Right, settings.Edge);
        Assert.Equal(1, settings.VerticalPosition);
        Assert.Equal(1500, settings.CollapseDelayMs);
        Assert.Equal(AppSettings.DefaultHotkey, settings.Hotkey);
        Assert.Null(settings.Display);

        settings.VerticalPosition = double.NaN;
        settings.Normalize();
        Assert.Equal(0.5, settings.VerticalPosition);
    }

    [Fact]
    public void Settings_round_trip_and_recover_from_corruption()
    {
        using var temp = new TempDirectory();
        string file = Path.Combine(temp.Path, "settings.json");

        var service = new SettingsService(file);
        service.Load();
        Assert.Equal(LoadStatus.NotFound, service.LoadStatus);
        service.Current.Edge = DockEdge.Left;
        service.Current.Hotkey = "Ctrl+Alt+D";
        service.Save();
        Assert.Contains("Ctrl+Alt+D", File.ReadAllText(file)); // stays human-readable

        var reloaded = new SettingsService(file);
        reloaded.Load();
        Assert.Equal(DockEdge.Left, reloaded.Current.Edge);
        Assert.Equal("Ctrl+Alt+D", reloaded.Current.Hotkey);

        File.WriteAllText(file, "{ \"Edge\": \"Top\" }");
        var damaged = new SettingsService(file);
        damaged.Load();
        Assert.Equal(LoadStatus.Corrupt, damaged.LoadStatus);
        Assert.Equal(DockEdge.Right, damaged.Current.Edge);
        Assert.Single(Directory.GetFiles(temp.Path, "settings.json.corrupt-*"));
    }

    [Theory]
    [InlineData("Ctrl+Alt+Space", ModifierKeys.Control | ModifierKeys.Alt, Key.Space)]
    [InlineData("ctrl + shift + F", ModifierKeys.Control | ModifierKeys.Shift, Key.F)]
    [InlineData("Win+Alt+1", ModifierKeys.Windows | ModifierKeys.Alt, Key.D1)]
    [InlineData("Control+F12", ModifierKeys.Control, Key.F12)]
    public void Parses_shortcuts(string text, ModifierKeys modifiers, Key key)
    {
        Assert.True(Shortcut.TryParse(text, out Shortcut? shortcut));
        Assert.Equal(new Shortcut(modifiers, key), shortcut);
    }

    [Theory]
    [InlineData("")]
    [InlineData("Space")]
    [InlineData("Shift+A")]
    [InlineData("Ctrl+Alt")]
    [InlineData("Ctrl+Banana")]
    [InlineData("Ctrl+A+B")]
    public void Rejects_unusable_shortcuts(string text)
    {
        Assert.False(Shortcut.TryParse(text, out _));
    }

    [Fact]
    public void Shortcut_text_round_trips()
    {
        foreach (string text in new[] { AppSettings.DefaultHotkey, AppSettings.FallbackHotkey, "Ctrl+Alt+1", "Win+Shift+F" })
        {
            Assert.True(Shortcut.TryParse(text, out Shortcut? shortcut));
            Assert.Equal(text, shortcut.ToString());
        }
    }
}
