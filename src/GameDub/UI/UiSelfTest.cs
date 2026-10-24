using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using GameDub.Core;

namespace GameDub.UI;

/// <summary>
/// Drives the real UI with simulated gamepad presses and saves a PNG per step:
/// --test ui &lt;outDir&gt; [width height]. Restores every setting it touches.
/// </summary>
public static class UiSelfTest
{
    public static void Run(string dir, double width, double height, StringBuilder log)
    {
        Directory.CreateDirectory(dir);
        var profilesBefore = Directory.GetFiles(Paths.Profiles).ToHashSet();
        var configBefore = File.Exists(Paths.ConfigFile) ? File.ReadAllBytes(Paths.ConfigFile) : null;
        MainWindow.TestMode = true;
        var app = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
        var w = new MainWindow();
        if (width > 0)
        {
            w.WindowState = WindowState.Normal;
            w.Left = 0; w.Top = 0; w.Width = width; w.Height = height;
        }
        w.Topmost = true;
        var watchdog = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(90) };
        watchdog.Tick += (_, _) => { log.AppendLine("TIMEOUT"); Flush(log); app.Shutdown(); };
        watchdog.Start();
        w.ContentRendered += async (_, _) =>
        {
            try { await Script(w, dir, log); }
            catch (Exception ex) { log.AppendLine("SCRIPT FAIL: " + ex); w.Close(); }
        };
        try { app.Run(w); }
        finally
        {
            if (configBefore == null) File.Delete(Paths.ConfigFile);
            else File.WriteAllBytes(Paths.ConfigFile, configBefore);
            foreach (var f in Directory.GetFiles(Paths.Profiles).Where(f => !profilesBefore.Contains(f))) File.Delete(f);
        }
    }

    static async Task Script(MainWindow w, string dir, StringBuilder log)
    {
        int n = 0;
        if (!double.IsNaN(w.Width) && w.WindowState == WindowState.Maximized && w.Width < SystemParameters.PrimaryScreenWidth)
        {
            double width = w.Width, height = w.Height;
            w.WindowState = WindowState.Normal;
            w.Width = width; w.Height = height;
            await Task.Delay(500);
        }
        w.Activate();
        log.AppendLine($"window {w.ActualWidth}x{w.ActualHeight} (requested {w.Width}x{w.Height}, state {w.WindowState}) active={w.IsActive}");

        async Task Shot(string name)
        {
            await Task.Delay(400);
            var file = Path.Combine(dir, $"{++n:00}-{name}.png");
            Render(w, file);
            log.AppendLine($"{n:00}-{name}: focus = {Describe(Keyboard.FocusedElement)}");
            Flush(log);
        }

        async Task Pad(ushort b, int times = 1)
        {
            for (int i = 0; i < times; i++)
            {
                w.OnPad(b);
                await Task.Delay(140);
            }
        }

        await Shot("games");
        await Pad(Gamepad.Right, 2);
        await Pad(Gamepad.Down);
        await Shot("games-nav");
        await Pad(Gamepad.A);
        await Shot("game-toggled");
        await Pad(Gamepad.A); // restore
        await Pad(Gamepad.Y);
        await Shot("region-reset");
        await Pad(Gamepad.Down, 12);
        await Shot("games-down-to-end");
        await Pad(Gamepad.Right, 12);
        await Shot("games-last-tile");
        if (Describe(Keyboard.FocusedElement).Contains("⟳")) await Pad(Gamepad.Left);
        if (!Describe(Keyboard.FocusedElement).Contains("＋")) { log.AppendLine("cannot reach add tile"); Flush(log); w.Close(); return; }
        await Pad(Gamepad.A);
        await Shot("add-page");
        await Pad(Gamepad.B);
        await Shot("back-to-games");

        await Pad(Gamepad.RB);
        await Shot("settings");
        await Pad(Gamepad.Down, 2);
        await Pad(Gamepad.Right);
        await Shot("settings-rate-plus");
        await Pad(Gamepad.Left); // restore
        await Pad(Gamepad.Down, 3);
        await Shot("settings-hotkeys");
        await Pad(Gamepad.Down, 8);
        await Shot("settings-bottom");
        await Pad(Gamepad.Up);
        await Pad(Gamepad.A);
        await Shot("settings-edit-text");
        await Pad(Gamepad.B);
        await Shot("settings-edit-done");

        async Task FocusRow(string label)
        {
            var button = Buttons(w).First(b => FirstText(b) == label);
            w.Activate();
            button.BringIntoView();
            button.Focus();
            Keyboard.Focus(button);
            await Task.Delay(140);
            Check(Keyboard.FocusedElement == button, "Could not focus " + label);
        }

        foreach (string provider in new[] { "ElevenLabs", "Google" })
        {
            await FocusRow("Ключі " + provider);
            await Pad(Gamepad.A);
            await Shot(provider + "-keys-empty");
            Check(Buttons(w).Count(b => FirstText(b).StartsWith(provider + " ключ ", StringComparison.Ordinal)) == 5,
                provider + " must have exactly five editable slots");
            for (int i = 1; i <= 5; i++)
            {
                await FocusRow($"{provider} ключ {i}");
                await Pad(Gamepad.A);
                Check(Keyboard.FocusedElement is TextBox, provider + " key editing did not focus its field");
                ((TextBox)Keyboard.FocusedElement).Text = $"test-{provider}-{i}";
                await Pad(Gamepad.B);
            }
            var saved = Store.LoadSettings();
            var keys = provider == "Google" ? saved.GoogleApiKeys : saved.ElevenApiKeys;
            Check(keys.SequenceEqual(Enumerable.Range(1, 5).Select(i => $"test-{provider}-{i}")), provider + " did not save all five keys");
            Check(saved.TtsEngine == "auto", "Adding a key did not enable auto voices");
            await Shot(provider + "-keys-full");

            // Duplicate edits must preserve the original slot.
            await FocusRow($"{provider} ключ 5");
            await Pad(Gamepad.A);
            ((TextBox)Keyboard.FocusedElement).Text = $"test-{provider}-1";
            await Pad(Gamepad.B);
            saved = Store.LoadSettings();
            keys = provider == "Google" ? saved.GoogleApiKeys : saved.ElevenApiKeys;
            Check(keys.Count == 5 && keys[^1] == $"test-{provider}-5", provider + " accepted a duplicate key");

            await FocusRow($"{provider} ключ 3");
            await Pad(Gamepad.Y);
            saved = Store.LoadSettings();
            keys = provider == "Google" ? saved.GoogleApiKeys : saved.ElevenApiKeys;
            Check(keys.Count == 4 && !keys.Contains($"test-{provider}-3"), provider + " delete did not persist");
            await Shot(provider + "-key-deleted");
            await Pad(Gamepad.A);
            ((TextBox)Keyboard.FocusedElement).Text = $"test-{provider}-new";
            await Pad(Gamepad.B);
            // Clearing a field also removes the key; the paste action trims and saves its replacement.
            await FocusRow($"{provider} ключ 1");
            await Pad(Gamepad.A);
            ((TextBox)Keyboard.FocusedElement).Text = "";
            await Pad(Gamepad.B);
            saved = Store.LoadSettings();
            keys = provider == "Google" ? saved.GoogleApiKeys : saved.ElevenApiKeys;
            Check(keys.Count == 4, provider + " empty edit did not remove its key");
            var pasteBox = ((Grid)((Button)Keyboard.FocusedElement).Content).Children.OfType<TextBox>().Single();
            ((Action<string>)pasteBox.Tag)($" test-{provider}-pasted ");
            saved = Store.LoadSettings();
            keys = provider == "Google" ? saved.GoogleApiKeys : saved.ElevenApiKeys;
            Check(keys.Count == 5 && keys[0] == $"test-{provider}-pasted", provider + " paste did not trim and save its key");
            await Pad(Gamepad.B);
            Check(Describe(Keyboard.FocusedElement) == "button: Ключі " + provider, "Back did not restore the provider row");
            await Shot(provider + "-back-to-settings");
            log.AppendLine($"PASS: {provider} UI adds five keys, rejects duplicates, removes and replaces keys, and returns to settings");
        }
        await Pad(Gamepad.LB);
        await Shot("games-again");
        w.OnPad(Gamepad.B);
        log.AppendLine("closed by B: " + !w.IsVisible);
        log.AppendLine("ALL UI TESTS PASSED");
    }

    static void Check(bool ok, string message)
    {
        if (!ok) throw new InvalidOperationException(message);
    }

    static IEnumerable<Button> Buttons(DependencyObject parent)
    {
        if (parent is Button button) yield return button;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            foreach (var child in Buttons(VisualTreeHelper.GetChild(parent, i))) yield return child;
    }

    static void Flush(StringBuilder log)
    {
        try { File.WriteAllText(Path.Combine(Paths.Data, "test-out.txt"), log.ToString(), Encoding.UTF8); } catch { }
    }

    static string Describe(IInputElement? e)
    {
        if (e is not FrameworkElement fe) return "(none)";
        string text = fe switch
        {
            Button { Tag: GameEntry g } => "game tile: " + g.Name,
            Button b => "button: " + FirstText(b),
            TextBox t => "textbox: " + t.Text,
            _ => fe.GetType().Name,
        };
        return text;
    }

    static string FirstText(DependencyObject d)
    {
        if (d is TextBlock tb && tb.Text.Length > 0) return tb.Text;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(d); i++)
        {
            var s = FirstText(VisualTreeHelper.GetChild(d, i));
            if (s.Length > 0) return s;
        }
        return "";
    }

    static void Render(Window w, string path)
    {
        var root = (FrameworkElement)w.Content;
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            dc.DrawRectangle(w.Background, null, new Rect(0, 0, w.ActualWidth, w.ActualHeight));
            dc.DrawRectangle(new VisualBrush(root), null, new Rect(root.Margin.Left, root.Margin.Top, root.ActualWidth, root.ActualHeight));
        }
        var rtb = new RenderTargetBitmap((int)w.ActualWidth, (int)w.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(dv);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(rtb));
        using var fs = File.Create(path);
        enc.Save(fs);
    }
}
