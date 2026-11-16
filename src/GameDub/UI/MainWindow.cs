using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using GameDub.Core;
using GameDub.Pipeline;
using GameDub.Tray;

namespace GameDub.UI;

/// <summary>Full-screen, gamepad-driven Xbox-style UI: games list and settings.</summary>
public sealed class MainWindow : Window
{
    static readonly Brush Bg = Hex("#0E1012"), Card = Hex("#1E2124"), Accent = Hex("#3DDC4A"), Dim = Hex("#8A9099"), White = Brushes.White;

    const string TileStyleXaml = @"
<Style TargetType='Button' xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml'>
  <Setter Property='FocusVisualStyle' Value='{x:Null}'/>
  <Setter Property='Foreground' Value='White'/>
  <Setter Property='Background' Value='#1E2124'/>
  <Setter Property='Margin' Value='10'/>
  <Setter Property='Cursor' Value='Hand'/>
  <Setter Property='RenderTransformOrigin' Value='0.5,0.5'/>
  <Setter Property='RenderTransform'><Setter.Value><ScaleTransform/></Setter.Value></Setter>
  <Setter Property='Template'><Setter.Value>
    <ControlTemplate TargetType='Button'>
      <Border x:Name='b' Background='{TemplateBinding Background}' BorderBrush='Transparent' BorderThickness='4' CornerRadius='8' Padding='{TemplateBinding Padding}' ClipToBounds='True'>
        <ContentPresenter HorizontalAlignment='{TemplateBinding HorizontalContentAlignment}' VerticalAlignment='{TemplateBinding VerticalContentAlignment}'/>
      </Border>
      <ControlTemplate.Triggers>
        <Trigger Property='IsMouseOver' Value='True'><Setter TargetName='b' Property='BorderBrush' Value='#4A5058'/></Trigger>
        <Trigger Property='IsKeyboardFocusWithin' Value='True'>
          <Setter TargetName='b' Property='BorderBrush' Value='White'/>
          <Setter Property='RenderTransform'><Setter.Value><ScaleTransform ScaleX='1.04' ScaleY='1.04'/></Setter.Value></Setter>
          <Setter Property='Panel.ZIndex' Value='10'/>
        </Trigger>
      </ControlTemplate.Triggers>
    </ControlTemplate>
  </Setter.Value></Setter>
</Style>";

    readonly Style _tile;
    readonly TextBlock _tabGames, _tabSettings, _status, _toast;
    readonly StackPanel _hints = new() { Orientation = Orientation.Horizontal };
    /// <summary>Set by the UI self-test: skips the touch keyboard.</summary>
    public static bool TestMode;
    string? _lastGameId;
    Action? _showToast;
    readonly ContentControl _content = new();
    readonly Gamepad _pad = new();
    readonly DispatcherTimer _toastTimer = new() { Interval = TimeSpan.FromSeconds(3) };
    AppSettings _s = Store.LoadSettings();
    List<GameEntry> _games = new();
    string _page = "games";
    TextBox? _editing;
    Action<string>? _commitEdit;

    sealed class Row
    {
        public Func<string> Value = () => "";
        public Action<int>? Change;   // -1 / +1
        public Action? Activate;
        public Action? Remove;
        public TextBlock? ValueText;
    }

    public MainWindow()
    {
        Title = "GameDub";
        Background = Bg;
        WindowStyle = WindowStyle.None;
        WindowState = WindowState.Maximized;
        FontFamily = new FontFamily("Segoe UI Variable Display, Segoe UI");
        Foreground = White;
        _tile = (Style)XamlReader.Parse(TileStyleXaml);

        var root = new Grid { Margin = new Thickness(56, 36, 56, 24) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var header = new DockPanel { Margin = new Thickness(10, 0, 10, 20), LastChildFill = false };
        header.Children.Add(Text("GameDub", 38, White, FontWeights.Bold, new Thickness(0, 0, 48, 0)));
        header.Children.Add(Bumper("LB", new Thickness(0, 6, 16, 0)));
        _tabGames = Text("Ігри", 28, White, FontWeights.SemiBold, new Thickness(0, 4, 32, 0));
        _tabSettings = Text("Налаштування", 28, Dim, FontWeights.SemiBold, new Thickness(0, 4, 16, 0));
        _tabGames.MouseLeftButtonUp += (_, _) => ShowGames(_lastGameId);
        _tabSettings.MouseLeftButtonUp += (_, _) => ShowSettings();
        header.Children.Add(_tabGames);
        header.Children.Add(_tabSettings);
        header.Children.Add(Bumper("RB", new Thickness(0, 6, 0, 0)));
        _status = Text("", 20, Dim, FontWeights.Normal, new Thickness(0, 12, 0, 0));
        DockPanel.SetDock(_status, Dock.Right);
        header.Children.Add(_status);
        root.Children.Add(header);

        Grid.SetRow(_content, 1);
        root.Children.Add(_content);

        var footer = new DockPanel { Margin = new Thickness(10, 16, 10, 0) };
        footer.Children.Add(_hints);
        Grid.SetRow(footer, 2);
        root.Children.Add(footer);

        // Toast floats over the bottom-right of the content, so it never collides with the button legend.
        _toast = Text("", 20, Accent, FontWeights.SemiBold, new Thickness(0));
        _toast.TextWrapping = TextWrapping.Wrap;
        var toastBox = new Border
        {
            Child = _toast, Background = Hex("#E6181B1F"), CornerRadius = new CornerRadius(8), Padding = new Thickness(18, 10, 18, 10),
            BorderBrush = Hex("#107C10"), BorderThickness = new Thickness(3, 0, 0, 0), MaxWidth = 560,
            HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 10, 6),
            Visibility = Visibility.Collapsed, IsHitTestVisible = false,
        };
        Grid.SetRow(toastBox, 1);
        root.Children.Add(toastBox);
        Content = root;

        _toastTimer.Tick += (_, _) => { toastBox.Visibility = Visibility.Collapsed; _toastTimer.Stop(); };
        _showToast = () => toastBox.Visibility = Visibility.Visible;
        var padTimer = new DispatcherTimer(DispatcherPriority.Input) { Interval = TimeSpan.FromMilliseconds(30) };
        padTimer.Tick += (_, _) => { if (IsActive) OnPad(_pad.Poll()); };
        padTimer.Start();
        var statusTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.5) };
        statusTimer.Tick += (_, _) => RefreshStatus();
        statusTimer.Start();
        PreviewKeyDown += OnKey;

        Loaded += (_, _) =>
        {
            if (!TestMode) EnsureService();
            _games = GameScanner.ScanAndMerge(); // local files only, fast; picks up new installs and covers
            ShowGames();
            RefreshStatus();
        };
    }

    // ---------- pages ----------

    void ShowGames(string? focusId = null)
    {
        _page = "games";
        SetTabs();
        Hints(("A", "Увімк./вимк. переклад"), ("X", "Мова субтитрів"), ("Y", "Скинути область"), ("B", "Закрити"));
        var wrap = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Center }; // no dead band on the right
        KeyboardNavigation.SetDirectionalNavigation(wrap, KeyboardNavigationMode.Contained);
        Button? focus = null;
        foreach (var g in _games)
        {
            var b = GameTile(g);
            wrap.Children.Add(b);
            if (g.Id == focusId) focus = b;
        }
        var add = ActionTile("＋", "Додати запущену гру", ShowAdd);
        wrap.Children.Add(add);
        if (focusId == AddTileId) focus = add;
        wrap.Children.Add(ActionTile("⟳", "Оновити список", () =>
        {
            _games = GameScanner.ScanAndMerge();
            ShowGames();
            Toast($"Знайдено ігор: {_games.Count}");
        }));
        _content.Content = Scroll(wrap);
        FocusLater(focus ?? (Button)wrap.Children[0]);
    }

    Button GameTile(GameEntry g)
    {
        var grid = new Grid { Width = 200, Height = 300 };
        ImageSource? cover = LoadCover(g.Cover);
        if (cover != null) grid.Children.Add(new Image { Source = cover, Stretch = Stretch.UniformToFill });
        else
        {
            grid.Background = new LinearGradientBrush(Color.FromRgb(0x23, 0x3A, 0x26), Color.FromRgb(0x14, 0x17, 0x1A), 60);
            grid.Children.Add(new TextBlock
            {
                Text = g.Name, FontSize = 24, FontWeight = FontWeights.SemiBold, Foreground = White, TextWrapping = TextWrapping.Wrap,
                TextAlignment = TextAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(14),
            });
        }
        var strip = new Border
        {
            VerticalAlignment = VerticalAlignment.Bottom,
            Background = new LinearGradientBrush(Color.FromArgb(0, 0, 0, 0), Color.FromArgb(235, 0, 0, 0), 90),
            Padding = new Thickness(10, 28, 10, 8),
            Child = new TextBlock { Text = g.Name, FontSize = 16, Foreground = White, TextTrimming = TextTrimming.CharacterEllipsis },
        };
        grid.Children.Add(strip);
        var badge = new Border
        {
            Width = 40, Height = 40, CornerRadius = new CornerRadius(20), Margin = new Thickness(8),
            HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top,
        };
        grid.Children.Add(badge);
        if (g.SubLang == "ru")
            grid.Children.Add(new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(200, 0, 0, 0)), CornerRadius = new CornerRadius(6), Padding = new Thickness(8, 3, 8, 3), Margin = new Thickness(8),
                HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top,
                Child = new TextBlock { Text = "RU → UA", FontSize = 14, FontWeight = FontWeights.Bold, Foreground = White },
            });
        var b = new Button { Style = _tile, Content = grid, Tag = g, Padding = new Thickness(0) };
        void Paint()
        {
            badge.Background = g.Enabled ? Hex("#107C10") : new SolidColorBrush(Color.FromArgb(160, 0, 0, 0));
            badge.BorderBrush = g.Enabled ? Hex("#107C10") : Dim;
            badge.BorderThickness = new Thickness(2);
            badge.Child = new TextBlock
            {
                Text = g.Enabled ? "✓" : "", FontSize = 22, FontWeight = FontWeights.Bold, Foreground = White,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            };
            grid.Opacity = g.Enabled ? 1 : 0.75;
        }
        Paint();
        b.Click += (_, _) =>
        {
            g.Enabled = !g.Enabled;
            if (g.Enabled) g.LastEnabledUtc = DateTime.UtcNow;
            Store.SaveGames(_games);
            _games = Store.LoadGames();
            _lastGameId = g.Id;
            ShowGames(g.Id);
            Toast(g.Enabled ? $"Переклад для «{g.Name}» увімкнено" : $"«{g.Name}» вимкнено");
        };
        return b;
    }

    Button ActionTile(string icon, string label, Action onClick)
    {
        var sp = new StackPanel { Width = 200, Height = 300, VerticalAlignment = VerticalAlignment.Center };
        sp.Children.Add(new TextBlock { Text = icon, FontSize = 64, Foreground = Accent, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 70, 0, 12) });
        sp.Children.Add(new TextBlock { Text = label, FontSize = 20, Foreground = White, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center, Margin = new Thickness(16, 0, 16, 0) });
        var b = new Button { Style = _tile, Content = sp };
        b.Click += (_, _) => onClick();
        return b;
    }

    void ShowAdd()
    {
        _page = "add";
        SetTabs();
        Hints(("A", "Додати"), ("B", "Назад"));
        var list = new StackPanel { MaxWidth = 1700 };
        KeyboardNavigation.SetDirectionalNavigation(list, KeyboardNavigationMode.Contained);
        list.Children.Add(Text("Запусти гру, перейди сюди (кнопка Xbox → GameDub) і вибери її вікно:", 22, Dim, FontWeights.Normal, new Thickness(10, 0, 10, 12)));
        var apps = GameScanner.RunningApps().Where(a => _games.All(g => !g.InstallDir.Equals(a.InstallDir, StringComparison.OrdinalIgnoreCase))).ToList();
        Button? first = null;
        foreach (var app in apps)
        {
            var b = WideButton(app.Name, app.InstallDir);
            b.Click += (_, _) =>
            {
                _games.Add(app);
                Store.SaveGames(_games);
                _games = Store.LoadGames();
                Toast($"«{app.Name}» додано");
                ShowGames(app.Id);
            };
            list.Children.Add(b);
            first ??= b;
        }
        if (first == null)
        {
            first = WideButton("Немає відкритих вікон ігор", "Запусти гру і відкрий цю сторінку знову");
            first.Click += (_, _) => ShowGames(AddTileId);
            list.Children.Add(first);
        }
        _content.Content = Scroll(list);
        FocusLater(first);
    }

    void ShowSettings(string? focusLabel = null)
    {
        _page = "settings";
        SetTabs();
        ShowSettingsHints();
        var list = new StackPanel { MaxWidth = 1700 }; // full width on the handheld, capped on a big TV
        KeyboardNavigation.SetDirectionalNavigation(list, KeyboardNavigationMode.Contained);

        // Free basics first; everything paid or technical lives below the section headers.
        list.Children.Add(Choice("Голос оповідача", () => _s.NarratorGender, v => _s.NarratorGender = v, ("m", "Чоловічий"), ("f", "Жіночий")));
        list.Children.Add(Number("Швидкість мовлення", () => _s.RatePercent, v => _s.RatePercent = v, -30, 60, 5, v => (v >= 0 ? "+" : "") + v + " %"));
        list.Children.Add(Number("Гучність", () => _s.Volume, v => _s.Volume = v, 10, 100, 10, v => v + " %"));
        list.Children.Add(ActionRow("Перевірити голоси", "▶", TestVoices));
        list.Children.Add(Toggle("Автозапуск служби з Windows", Autostart.IsEnabled, Autostart.Set));
        list.Children.Add(Info("Пауза перекладу (кнопка M1)", KeyNames(_s.HotkeyPause)));
        list.Children.Add(Info("Перевибір області (кнопка M2)", KeyNames(_s.HotkeyRegion)));

        list.Children.Add(Section("Підсвітка стіків"));
        Button LightToggle(string label, Func<bool> get, Action<bool> set, string note) => Toggle(label, get, v => { set(v); SaveSettings(); }, note);
        list.Children.Add(LightToggle("Підсвітка стіків", () => _s.LightsOn, v => _s.LightsOn = v,
            "Темні, поки нічого не відбувається; на телевізорі вимкнені"));
        list.Children.Add(LightToggle("Спалахи на звукові ефекти", () => _s.LightsSound, v => _s.LightsSound = v,
            "Удар — помаранчевий · сирена — червоний і синій · гудок — жовтий двічі · грім — білий · серце — червоний «тук-тук»"));
        list.Children.Add(LightToggle("Вібрація на вибухи й удари", () => _s.LightsRumble, v => _s.LightsRumble = v,
            "Коротка вібрація на удар і гудок, для ігор, які самі не вібрують"));
        list.Children.Add(LightToggle("Завантаження ігор", () => _s.LightsDownload, v => _s.LightsDownload = v,
            "Синє «дихання», поки лаунчер качає гру · зелене — скачалось, до дотику"));
        list.Children.Add(LightToggle("Мало заряду і зарядка", () => _s.LightsBattery, v => _s.LightsBattery = v,
            "Нижче 10 % — червоне «дихання» раз на 20 с · зарядка — зелений спалах"));
        list.Children.Add(LightToggle("Перегрів", () => _s.LightsHeat, v => _s.LightsHeat = v,
            "Від 85 °C — помаранчеве «дихання» раз на 20 с"));
        list.Children.Add(Number("Яскравість у спокої", () => _s.LightsBase, v => _s.LightsBase = v, 0, 50, 5, v => v + " %"));
        list.Children.Add(TextRow("Ігри зі своїм світлом", () => string.Join(", ", _s.LightsSkipGames),
            v => _s.LightsSkipGames = v.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(x => x.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? x[..^4] : x).Distinct(StringComparer.OrdinalIgnoreCase).ToList()));
        if (!Lights.SoundFx.ModelPresent)
            list.Children.Add(Info("Модель звуків (yamnet)", "немає: src\\tools\\get-models.ps1"));

        list.Children.Add(Section("Платні сервіси (необов'язково)"));
        list.Children.Add(Choice("Голоси", () => _s.TtsEngine, v => _s.TtsEngine = v,
            ("auto", "Авто (найкращий з доступних)"), ("edge", "Edge (безкоштовні)"), ("supertonic", "Supertonic (офлайн, безкоштовні)"), ("eleven", "ElevenLabs (живі, ~1 год/міс. безкоштовно)"),
            ("google", "Google HD (живі, 1 млн симв./міс. безкоштовно)"), ("openai", "OpenAI (живі, платні)")));
        void KeyRow(string label, Func<string> get, Action<string> set) => list.Children.Add(TextRow(label, get, v =>
        {
            if (get().Length == 0 && v.Length > 0 && _s.TtsEngine == "edge") _s.TtsEngine = "auto"; // first key: use it right away
            set(v);
            Dispatcher.BeginInvoke(RefreshRows);
        }, secret: true));
        list.Children.Add(RowButton("Ключі ElevenLabs", new Row
        {
            Value = () => $"{_s.ElevenApiKeys.Count} / {AppSettings.MaxApiKeys}  ›", Activate = () => ShowApiKeys(true),
        }));
        list.Children.Add(RowButton("Ключі Google", new Row
        {
            Value = () => $"{_s.GoogleApiKeys.Count} / {AppSettings.MaxApiKeys}  ›", Activate = () => ShowApiKeys(false),
        }));
        list.Children.Add(Choice("Переклад", () => _s.Translator, v => _s.Translator = v, ("google", "Google (безкоштовний)"), ("llm", "LLM (розумний, з емоціями)")));
        KeyRow("OpenAI ключ", () => _s.OpenAiApiKey, v => _s.OpenAiApiKey = v);
        list.Children.Add(Choice("LLM провайдер", () => _s.LlmProvider, v =>
        {
            _s.LlmProvider = v;
            if (v == "openai") _s.LlmModel = "gpt-6-luna";
            else if (v == "anthropic") _s.LlmModel = "claude-haiku-5-5";
            Dispatcher.BeginInvoke(() => ShowSettings("LLM провайдер")); // show/hide provider-specific rows
        }, ("openai", "OpenAI (той самий ключ)"), ("anthropic", "Anthropic (Claude)"), ("custom", "Інший OpenAI-сумісний")));
        list.Children.Add(TextRow("LLM модель", () => _s.LlmModel, v => _s.LlmModel = v));
        if (_s.LlmProvider != "openai")
            list.Children.Add(TextRow(_s.LlmProvider == "anthropic" ? "Ключ Anthropic" : "Ключ LLM", () => _s.LlmApiKey, v => _s.LlmApiKey = v, secret: true));
        if (_s.LlmProvider == "custom")
            list.Children.Add(TextRow("Адреса LLM", () => _s.LlmBaseUrl, v => _s.LlmBaseUrl = v));

        list.Children.Add(Section("Тонке налаштування"));
        list.Children.Add(Number("Частота читання субтитрів", () => _s.OcrIntervalMs, v => _s.OcrIntervalMs = v, 200, 1500, 100, v => v + " мс"));

        _content.Content = Scroll(list);
        var buttons = list.Children.OfType<Button>().ToList();
        FocusLater(buttons.FirstOrDefault(b => RowLabel(b) == focusLabel) ?? buttons[0]);
    }

    void ShowApiKeys(bool eleven)
    {
        _page = eleven ? "keys-eleven" : "keys-google";
        SetTabs();
        ShowSettingsHints();
        string provider = eleven ? "ElevenLabs" : "Google";
        var keys = (eleven ? _s.ElevenApiKeys : _s.GoogleApiKeys)
            .Concat(Enumerable.Repeat("", AppSettings.MaxApiKeys)).Take(AppSettings.MaxApiKeys).ToArray();
        var list = new StackPanel { MaxWidth = 1700 };
        KeyboardNavigation.SetDirectionalNavigation(list, KeyboardNavigationMode.Contained);
        var title = Section($"Ключі {provider} · {keys.Count(k => k.Length > 0)} / {AppSettings.MaxApiKeys}");
        list.Children.Add(title);
        var help = Text("Ключі використовуються по черзі: якщо один недоступний, програма спробує наступний. Щоб видалити ключ, очисти поле або натисни Y / Delete.",
            20, Dim, FontWeights.Normal, new Thickness(14, 6, 14, 18));
        help.TextWrapping = TextWrapping.Wrap;
        list.Children.Add(help);
        for (int i = 0; i < keys.Length; i++)
        {
            int index = i;
            list.Children.Add(TextRow($"{provider} ключ {i + 1}", () => keys[index], value =>
            {
                if (value.Length > 0 && keys.Where((_, n) => n != index).Contains(value, StringComparer.Ordinal))
                {
                    Toast("Цей ключ уже додано");
                    return;
                }
                keys[index] = value;
                var saved = keys.Where(k => k.Length > 0).ToList();
                if (eleven) _s.ElevenApiKeys = saved; else _s.GoogleApiKeys = saved;
                if (saved.Count > 0 && _s.TtsEngine == "edge") _s.TtsEngine = "auto";
                title.Text = $"КЛЮЧІ {provider.ToUpperInvariant()} · {saved.Count} / {AppSettings.MaxApiKeys}";
            }, secret: true, removable: true));
        }
        list.Children.Add(ActionRow("Назад до налаштувань", "‹", () => ShowSettings("Ключі " + provider)));
        _content.Content = Scroll(list);
        FocusLater(list.Children.OfType<Button>().First());
    }

    bool IsSettingsPage => _page == "settings" || _page.StartsWith("keys-", StringComparison.Ordinal);

    static string KeyNames(string combos) => combos.Replace("Scroll", "Scroll Lock").Replace(", ", "  або  ");

    static string? RowLabel(Button b) => ((b.Content as Grid)?.Children[0] as TextBlock)?.Text;

    static TextBlock Section(string title) => new()
    {
        Text = title.ToUpperInvariant(), FontSize = 16, FontWeight = FontWeights.SemiBold, Foreground = Dim,
        Margin = new Thickness(14, 26, 0, 6), Focusable = false,
    };

    // ---------- settings rows ----------

    Button RowButton(string label, Row row, UIElement? valueElement = null, string? note = null)
    {
        var g = new Grid { Height = note == null ? 64 : 88 };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var title = new TextBlock { Text = label, FontSize = 24, Foreground = White, TextTrimming = TextTrimming.CharacterEllipsis };
        if (note == null) { title.VerticalAlignment = VerticalAlignment.Center; title.Margin = new Thickness(0, 0, 20, 0); g.Children.Add(title); }
        else
        {
            // a second, dim line under the label: what this setting shows
            var two = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 20, 0) };
            two.Children.Add(title);
            two.Children.Add(new TextBlock { Text = note, FontSize = 17, Foreground = Dim, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 2, 0, 0) });
            g.Children.Add(two);
        }
        if (valueElement == null)
        {
            row.ValueText = new TextBlock { FontSize = 24, Foreground = Accent, VerticalAlignment = VerticalAlignment.Center, Text = row.Value() };
            valueElement = row.ValueText;
        }
        Grid.SetColumn(valueElement, 1);
        g.Children.Add(valueElement);
        var b = new Button { Style = _tile, Content = g, Tag = row, Padding = new Thickness(24, 0, 24, 0), HorizontalContentAlignment = HorizontalAlignment.Stretch, Margin = new Thickness(10, 5, 10, 5) };
        b.Click += (_, _) => { if (row.Activate != null) row.Activate(); else row.Change?.Invoke(+1); };
        return b;
    }

    Button Choice(string label, Func<string> get, Action<string> set, params (string key, string text)[] options)
    {
        var row = new Row();
        row.Value = () => "‹  " + (options.FirstOrDefault(o => o.key == get()).text ?? get()) + "  ›";
        row.Change = d =>
        {
            int i = Array.FindIndex(options, o => o.key == get());
            set(options[((i + d) % options.Length + options.Length) % options.Length].key);
            SaveSettings();
            RefreshRows();
        };
        return RowButton(label, row);
    }

    Button Number(string label, Func<int> get, Action<int> set, int min, int max, int step, Func<int, string> fmt)
    {
        var row = new Row { Value = () => "‹  " + fmt(get()) + "  ›" };
        row.Change = d => { set(Math.Clamp(get() + d * step, min, max)); SaveSettings(); RefreshRows(); };
        return RowButton(label, row);
    }

    Button Toggle(string label, Func<bool> get, Action<bool> set, string? note = null)
    {
        var row = new Row { Value = () => get() ? "Увімкнено" : "Вимкнено" };
        row.Change = _ =>
        {
            try { set(!get()); } catch (Exception ex) { Toast("Помилка: " + ex.Message); }
            RefreshRows();
        };
        return RowButton(label, row, note: note);
    }

    Button ActionRow(string label, string value, Action act) => RowButton(label, new Row { Value = () => value, Activate = act });

    Button Info(string label, string value) => RowButton(label, new Row { Value = () => value });

    Button TextRow(string label, Func<string> get, Action<string> set, bool secret = false, bool removable = false)
    {
        string Shown() => get().Length == 0 ? "не задано" : secret ? "••••••" + get()[Math.Max(0, get().Length - 4)..] : get();
        var box = new TextBox
        {
            FontSize = 22, MinWidth = 360, MaxWidth = 620, Background = Brushes.Transparent, Foreground = Accent, BorderThickness = new Thickness(0, 0, 0, 2),
            BorderBrush = Brushes.Transparent, CaretBrush = White, VerticalAlignment = VerticalAlignment.Center, TextAlignment = TextAlignment.Right,
            Focusable = false, IsTabStop = false, Text = Shown(),
        };
        var row = new Row { Value = Shown };
        Button? button = null;
        void Commit(string v)
        {
            set(v.Trim());
            SaveSettings();
            box.Text = Shown();
            box.BorderBrush = Brushes.Transparent;
            box.Focusable = false;
            _editing = null;
            button?.Focus();
            ShowSettingsHints();
        }
        row.Activate = () =>
        {
            box.Focusable = true;
            box.Text = get();
            box.BorderBrush = Accent;
            _editing = box;
            _commitEdit = Commit;
            box.Focus();
            box.SelectAll();
            Hints(("A", "Готово"), ("B", "Готово"));
            if (!TestMode) ShowTouchKeyboard();
        };
        row.Change = null;
        if (removable) row.Remove = () =>
        {
            if (get().Length == 0) return;
            Commit("");
            Toast($"{label}: видалено");
        };
        box.Tag = (Action<string>)(clip => { set(clip.Trim()); SaveSettings(); box.Text = Shown(); Toast($"{label}: вставлено"); });
        box.LostKeyboardFocus += (_, _) => { if (_editing == box) Commit(box.Text); };
        button = RowButton(label, row, box);
        return button;
    }

    void RefreshRows()
    {
        if (_content.Content is ScrollViewer { Content: Border { Child: Panel p } })
            foreach (var b in p.Children.OfType<Button>())
                if (b.Tag is Row { ValueText: { } t } r) t.Text = r.Value();
    }

    void SaveSettings()
    {
        Store.SaveSettings(_s);
    }

    async void TestVoices()
    {
        Toast("Синтез голосу…");
        try
        {
            var tts = Voices.CreateEngine(_s);
            var (m, f) = ("Привіт! Це чоловічий голос GameDub.", "А це жіночий голос.");
            var rm = new TtsRequest(m, "m", 0, "friendly, confident");
            var rf = new TtsRequest(f, "f", 0, "warm, playful");
            await AudioPlayer.PlayAsync(await tts.SynthesizeAsync(rm, CancellationToken.None), _s.Volume, CancellationToken.None);
            await AudioPlayer.PlayAsync(await tts.SynthesizeAsync(rf, CancellationToken.None), _s.Volume, CancellationToken.None);
            var used = tts.Describe(rf);
            Toast(used.StartsWith("eleven") ? "Голоси ElevenLabs працюють"
                : used.StartsWith("supertonic") ? "Голоси Supertonic (офлайн) працюють"
                : used.StartsWith("google") ? "Голоси Google HD працюють"
                : used.StartsWith("openai") ? "Голоси OpenAI працюють"
                : _s.TtsEngine == "edge" ? "Голоси Edge працюють"
                : "Працюють голоси Edge (платний рушій не відповів — перевір ключ)");
        }
        catch (Exception ex)
        {
            Log.Error("test voices", ex);
            Toast("Помилка озвучки: " + ex.Message);
        }
    }

    // ---------- input ----------

    internal void OnPad(ushort b)
    {
        if (b == 0) return;
        if (_editing != null)
        {
            if ((b & (Gamepad.B | Gamepad.A)) != 0) _commitEdit?.Invoke(_editing.Text);
            return;
        }
        var focused = Keyboard.FocusedElement as FrameworkElement;
        if ((b & Gamepad.LB) != 0) { SwitchTab(); return; }
        if ((b & Gamepad.RB) != 0) { SwitchTab(); return; }
        if ((b & Gamepad.A) != 0 && focused is Button btn) { btn.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); return; }
        if ((b & Gamepad.B) != 0) { Back(); return; }
        if ((b & Gamepad.Y) != 0 && _page == "games" && focused?.Tag is GameEntry g) { ResetRegion(g); return; }
        if ((b & Gamepad.X) != 0 && _page == "games" && focused?.Tag is GameEntry sub) { ToggleSubLang(sub); return; }
        if ((b & Gamepad.Y) != 0 && focused?.Tag is Row { Remove: { } remove }) { remove(); return; }
        if ((b & Gamepad.X) != 0 && focused is Button { Content: Grid grid } && grid.Children.OfType<TextBox>().FirstOrDefault()?.Tag is Action<string> paste)
        {
            if (Clipboard.ContainsText()) paste(Clipboard.GetText());
            return;
        }
        bool horizontal = (b & (Gamepad.Left | Gamepad.Right)) != 0;
        if (horizontal && focused?.Tag is Row { Change: { } change }) { change((b & Gamepad.Left) != 0 ? -1 : 1); return; }
        var dir = (b & Gamepad.Up) != 0 ? FocusNavigationDirection.Up
            : (b & Gamepad.Down) != 0 ? FocusNavigationDirection.Down
            : (b & Gamepad.Left) != 0 ? FocusNavigationDirection.Left
            : (b & Gamepad.Right) != 0 ? FocusNavigationDirection.Right : (FocusNavigationDirection?)null;
        if (dir != null && focused != null)
        {
            focused.MoveFocus(new TraversalRequest(dir.Value));
            // Like Xbox: "down" from a column with nothing below jumps to the last tile of the grid.
            if (Keyboard.FocusedElement == focused && dir == FocusNavigationDirection.Down && focused.Parent is WrapPanel wrap
                && wrap.Children[^1] is UIElement last && last != focused && wrap.Children.IndexOf(focused) < wrap.Children.Count - 1)
            {
                var lastRowTop = last.TranslatePoint(new Point(0, 0), wrap).Y;
                if (focused.TranslatePoint(new Point(0, 0), wrap).Y < lastRowTop - 1) last.Focus();
            }
            (Keyboard.FocusedElement as FrameworkElement)?.BringIntoView();
        }
    }

    void OnKey(object sender, KeyEventArgs e)
    {
        if (_editing != null)
        {
            if (e.Key is Key.Enter or Key.Escape) { _commitEdit?.Invoke(_editing.Text); e.Handled = true; }
            return;
        }
        var focused = Keyboard.FocusedElement as FrameworkElement;
        switch (e.Key)
        {
            case Key.Escape: Back(); e.Handled = true; break;
            case Key.PageUp or Key.PageDown or Key.Tab: SwitchTab(); e.Handled = true; break;
            case Key.Delete when focused?.Tag is Row { Remove: { } remove }:
                remove(); e.Handled = true; break;
            case Key.Left or Key.Right when focused?.Tag is Row { Change: { } change }:
                change(e.Key == Key.Left ? -1 : 1); e.Handled = true; break;
        }
    }

    void SwitchTab()
    {
        if (IsSettingsPage) ShowGames(_lastGameId);
        else
        {
            // Remember where we were, like the Xbox dashboard does.
            if (Keyboard.FocusedElement is Button { Tag: GameEntry g }) _lastGameId = g.Id;
            ShowSettings();
        }
    }

    void Back()
    {
        if (_page == "add") ShowGames(AddTileId);
        else if (_page.StartsWith("keys-", StringComparison.Ordinal)) ShowSettings(_page == "keys-eleven" ? "Ключі ElevenLabs" : "Ключі Google");
        else Close();
    }

    const string AddTileId = "\0add";

    void ShowSettingsHints()
    {
        if (_page.StartsWith("keys-", StringComparison.Ordinal))
            Hints(("A", "Змінити"), ("X", "Вставити з буфера"), ("Y", "Видалити ключ"), ("B", "Назад"));
        else Hints(("A", "Змінити"), ("◀▶", "Значення"), ("X", "Вставити з буфера"), ("B", "Закрити"));
    }

    /// <summary>Xbox-style button legend: colored round A/B/X/Y glyphs followed by labels.</summary>
    void Hints(params (string button, string label)[] items)
    {
        _hints.Children.Clear();
        foreach (var (button, label) in items)
        {
            (string bg, string fg) = button switch
            {
                "A" => ("#107C10", "#FFFFFF"),
                "B" => ("#D13438", "#FFFFFF"),
                "X" => ("#0078D4", "#FFFFFF"),
                "Y" => ("#FFB900", "#111111"),
                _ => ("#2C3036", "#FFFFFF"),
            };
            bool round = button.Length == 1;
            _hints.Children.Add(new Border
            {
                MinWidth = 30, Height = 30, CornerRadius = new CornerRadius(15), Background = Hex(bg),
                Padding = new Thickness(round ? 0 : 10, 0, round ? 0 : 10, 0), VerticalAlignment = VerticalAlignment.Center,
                Child = new TextBlock
                {
                    Text = button, FontSize = round ? 17 : 14, FontWeight = FontWeights.Bold, Foreground = Hex(fg),
                    HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                },
            });
            _hints.Children.Add(Text(label, 20, Dim, FontWeights.Normal, new Thickness(10, 0, 28, 0)));
        }
    }

    static Border Bumper(string text, Thickness margin) => new()
    {
        Height = 30, CornerRadius = new CornerRadius(8), Background = Hex("#2C3036"), Padding = new Thickness(10, 0, 10, 0),
        Margin = margin, VerticalAlignment = VerticalAlignment.Center,
        Child = new TextBlock { Text = text, FontSize = 14, FontWeight = FontWeights.Bold, Foreground = Hex("#C8CCD2"), VerticalAlignment = VerticalAlignment.Center },
    };

    void ToggleSubLang(GameEntry g)
    {
        var next = g.SubLang == "ru" ? "en" : "ru";
        if (!OcrService.IsAvailable(next)) { Toast("Немає пакета розпізнавання цієї мови у Windows"); return; }
        g.SubLang = next;
        Store.SaveGames(_games);
        _games = Store.LoadGames();
        ShowGames(g.Id);
        Toast($"«{g.Name}»: субтитри {(next == "ru" ? "російські" : "англійські")} → озвучка українська");
    }

    void ResetRegion(GameEntry g)
    {
        var p = Store.LoadProfile(g.Id);
        p.Region = null;
        Store.SaveProfile(g.Id, p);
        Toast($"Область субтитрів для «{g.Name}» визначиться заново");
    }

    // ---------- service / status ----------

    static void EnsureService()
    {
        if (TrayApp.IsRunning()) return;
        try { Process.Start(new ProcessStartInfo(Paths.Exe, "--tray") { UseShellExecute = false }); }
        catch (Exception ex) { Log.Error("start tray", ex); }
    }

    void RefreshStatus()
    {
        string text = "● Служба вимкнена";
        Brush color = Hex("#E05050");
        if (TrayApp.IsRunning())
        {
            text = "● Очікує гру";
            color = Dim;
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(Paths.StatusFile));
                var state = doc.RootElement.GetProperty("state").GetString();
                var game = doc.RootElement.GetProperty("game").GetString();
                if (state == "active") { text = "● Перекладає: " + game; color = Accent; }
                else if (state == "paused") { text = "❚❚ Пауза: " + game; color = Hex("#E0B040"); }
            }
            catch { }
        }
        _status.Text = text;
        _status.Foreground = color;
    }

    // ---------- helpers ----------

    void SetTabs()
    {
        _tabGames.Foreground = IsSettingsPage ? Dim : White;
        _tabSettings.Foreground = IsSettingsPage ? White : Dim;
    }

    void Toast(string msg)
    {
        _toast.Text = msg;
        _showToast?.Invoke();
        _toastTimer.Stop();
        _toastTimer.Start();
    }

    void FocusLater(UIElement e) => Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () => { e.Focus(); Keyboard.Focus(e); });

    static ScrollViewer Scroll(FrameworkElement content) => new()
    {
        // Room around the content so the enlarged focused tile is never clipped at the edges.
        Content = new Border { Child = content, Padding = new Thickness(12, 10, 12, 10) },
        VerticalScrollBarVisibility = ScrollBarVisibility.Hidden,
        HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        PanningMode = PanningMode.VerticalOnly,
        Focusable = false,
    };

    Button WideButton(string title, string sub)
    {
        var sp = new StackPanel { Margin = new Thickness(24, 12, 24, 12) };
        sp.Children.Add(new TextBlock { Text = title, FontSize = 24, Foreground = White });
        sp.Children.Add(new TextBlock { Text = sub, FontSize = 16, Foreground = Dim, TextTrimming = TextTrimming.CharacterEllipsis });
        return new Button { Style = _tile, Content = sp, HorizontalContentAlignment = HorizontalAlignment.Left, Margin = new Thickness(10, 5, 10, 5) };
    }

    static TextBlock Text(string s, double size, Brush color, FontWeight weight, Thickness margin) =>
        new() { Text = s, FontSize = size, Foreground = color, FontWeight = weight, Margin = margin, VerticalAlignment = VerticalAlignment.Center };

    static ImageSource? LoadCover(string? path)
    {
        if (path == null || !File.Exists(path)) return null;
        try
        {
            var img = new BitmapImage();
            img.BeginInit();
            img.UriSource = new Uri(path);
            img.DecodePixelWidth = 300;
            img.CacheOption = BitmapCacheOption.OnLoad;
            img.EndInit();
            img.Freeze();
            return img;
        }
        catch { return null; }
    }

    static void ShowTouchKeyboard()
    {
        try
        {
            var tabtip = @"C:\Program Files\Common Files\microsoft shared\ink\TabTip.exe";
            if (File.Exists(tabtip)) Process.Start(new ProcessStartInfo(tabtip) { UseShellExecute = true });
        }
        catch { }
    }

    static SolidColorBrush Hex(string hex)
    {
        var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        b.Freeze();
        return b;
    }
}
