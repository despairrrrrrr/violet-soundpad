using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using NAudio.CoreAudioApi;

namespace VioletSoundpad;

public partial class MainWindow : Window
{
    private Library library = new();
    private AudioEngine? audio;
    private SoundFolder? folder;
    private SoundClip? currentClip;
    private bool loading = true;
    private bool selectingFolder;
    private bool refreshingDevices;
    private bool changingVoice;
    private bool testingMicrophone;
    private bool forwardingVoice;
    private bool importing;
    private readonly DispatcherTimer timer;
    private readonly DispatcherTimer saveTimer;

    public MainWindow()
    {
        InitializeComponent();
        timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(80) };
        timer.Tick += (_, _) => UpdatePlayer();
        saveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(350) };
        saveTimer.Tick += (_, _) =>
        {
            saveTimer.Stop();
            SaveLibrary();
        };
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (!loading)
            return;
        try { library = Library.Load(); }
        catch (Exception ex) { Status("Не удалось открыть библиотеку. " + ex.Message, true); }

        try
        {
            audio = new AudioEngine();
            audio.DeviceError += (error, capture) =>
            {
                Dispatcher.BeginInvoke(new Action(() => HandleAudioError(error, capture)));
            };
        }
        catch (Exception ex)
        {
            Status("Аудиосистема Windows недоступна. " + ex.Message, true);
        }

        VolumeSlider.Value = Math.Max(0, Math.Min(1, library.Volume)) * 100;
        if (audio is not null)
            audio.Volume = (float)(VolumeSlider.Value / 100);
        SelectFolder(null);
        RefreshDevices();
        loading = false;
        UpdatePlayer();
        timer.Start();
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        int dark = 1;
        IntPtr handle = new WindowInteropHelper(this).Handle;
        if (DwmSetWindowAttribute(handle, 20, ref dark, sizeof(int)) != 0)
            DwmSetWindowAttribute(handle, 19, ref dark, sizeof(int));
    }

    private string? CableId => (CableCombo.SelectedItem as AudioDevice)?.Id;
    private string? MonitorId => (MonitorCombo.SelectedItem as AudioDevice)?.Id;
    private string? MicrophoneId => (MicrophoneCombo.SelectedItem as AudioDevice)?.Id;
    private bool WithMonitor => RouteCombo.SelectedIndex == 1;

    private void OnRefreshDevices(object sender, RoutedEventArgs e)
    {
        RefreshDevices();
        ApplyDeviceChanges();
    }

    private void RefreshDevices()
    {
        refreshingDevices = true;
        try
        {
            RouteCombo.SelectedIndex = library.MonitorEnabled ? 1 : 0;
            if (audio is null)
                return;
            List<AudioDevice> outputs = audio.ListDevices(DataFlow.Render);
            List<AudioDevice> inputs = audio.ListDevices(DataFlow.Capture);
            BindDevices(CableCombo, outputs.Where(device => device.IsCable), library.CableId);
            BindDevices(MonitorCombo, outputs.Where(device => !device.IsCable), library.MonitorId,
                audio.DefaultDevice(DataFlow.Render));
            BindDevices(MicrophoneCombo, inputs.Where(device => !device.IsCable), library.MicrophoneId,
                audio.DefaultDevice(DataFlow.Capture));
        }
        catch (Exception ex)
        {
            Status("Не удалось обновить аудиоустройства. " + ex.Message, true);
        }
        finally
        {
            refreshingDevices = false;
            UpdateDeviceIndicators();
        }
    }

    private static void BindDevices(ComboBox box, IEnumerable<AudioDevice> devices, string? preferred, string? fallback = null)
    {
        var options = devices.ToList();
        box.ItemsSource = options;
        box.SelectedItem = options.Find(device => device.Id == preferred) ??
                           options.Find(device => device.Id == fallback) ??
                           options.FirstOrDefault();
        box.IsEnabled = options.Count > 0;
    }

    private void UpdateDeviceIndicators()
    {
        CableHelp.Visibility = CableId is null ? Visibility.Visible : Visibility.Collapsed;
        RouteStatus.Text = CableId is null ? "Кабель не найден" : "Кабель выбран";
        RouteDot.Fill = BrushFor(CableId is null ? "Warning" : "Success");
        MonitorPanel.Visibility = WithMonitor ? Visibility.Visible : Visibility.Collapsed;
        MicTestButton.IsEnabled = MicrophoneId is not null || testingMicrophone;
        VoiceCheck.IsEnabled = forwardingVoice || (MicrophoneId is not null && CableId is not null);
    }

    private void OnDeviceSelection(object sender, SelectionChangedEventArgs e)
    {
        if (loading || refreshingDevices)
            return;
        if (sender == CableCombo)
            library.CableId = CableId;
        if (sender == MonitorCombo)
            library.MonitorId = MonitorId;
        if (sender == MicrophoneCombo)
            library.MicrophoneId = MicrophoneId;
        library.MonitorEnabled = WithMonitor;
        UpdateDeviceIndicators();
        ApplyDeviceChanges();
        ScheduleSave();
    }

    private void ApplyDeviceChanges()
    {
        if (audio is null)
            return;
        if (audio.HasClip || forwardingVoice)
        {
            try { RouteAudio(); }
            catch (Exception ex) { HandleAudioError(ex, false); }
        }
        else
            audio.ReleaseOutputs();

        try { ApplyMicrophone(); }
        catch (Exception ex) { HandleAudioError(ex, true); }
    }

    private AudioEngine GetAudio() => audio ?? throw new InvalidOperationException("Аудиосистема Windows недоступна.");

    private void RouteAudio()
    {
        string cable = CableId ?? throw new InvalidOperationException("Установите VB-CABLE и обновите список устройств.");
        string? monitor = WithMonitor
            ? MonitorId ?? throw new InvalidOperationException("Выберите подключённые наушники.")
            : null;
        GetAudio().Route(cable, monitor);
    }

    private void OnMicTest(object sender, RoutedEventArgs e)
    {
        testingMicrophone = !testingMicrophone;
        try
        {
            ApplyMicrophone();
            Status(testingMicrophone ? "Говорите в микрофон и следите за уровнем сигнала." : "Проверка микрофона завершена.");
        }
        catch (Exception ex) { HandleAudioError(ex, true); }
    }

    private void OnVoiceChanged(object sender, RoutedEventArgs e)
    {
        if (loading || changingVoice)
            return;
        forwardingVoice = VoiceCheck.IsChecked == true;
        if (forwardingVoice)
        {
            try { RouteAudio(); }
            catch (Exception ex)
            {
                HandleAudioError(ex, false);
                return;
            }
        }
        try
        {
            ApplyMicrophone();
            if (!forwardingVoice && audio?.HasClip == false)
                audio.ReleaseOutputs();
            Status(forwardingVoice ? "Голос передаётся в виртуальный микрофон." : "Передача голоса выключена.");
        }
        catch (Exception ex) { HandleAudioError(ex, true); }
    }

    private void ApplyMicrophone()
    {
        if (testingMicrophone || forwardingVoice)
        {
            string id = MicrophoneId ?? throw new InvalidOperationException("Подключите и выберите физический микрофон.");
            GetAudio().SetMicrophone(id, forwardingVoice);
        }
        else
            audio?.SetMicrophone(null, false);
        MicTestButton.Content = testingMicrophone ? "Завершить проверку" : "Проверить микрофон";
        UpdateDeviceIndicators();
    }

    private void HandleAudioError(Exception error, bool capture)
    {
        if (!IsLoaded)
            return;
        if (capture)
            testingMicrophone = false;

        changingVoice = true;
        forwardingVoice = false;
        VoiceCheck.IsChecked = false;
        changingVoice = false;
        if (!capture)
        {
            audio?.Stop();
            audio?.ReleaseOutputs();
        }
        try { ApplyMicrophone(); }
        catch
        {
            testingMicrophone = false;
            audio?.SetMicrophone(null, false);
            MicTestButton.Content = "Проверить микрофон";
        }
        UpdatePlayer();
        Status((capture ? "Микрофон недоступен. " : "") + error.Message, true);
    }

    private void OnCableHelp(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo("https://vb-audio.com/Cable/") { UseShellExecute = true });
        }
        catch (Exception ex) { Status(ex.Message, true); }
    }

    private void SelectFolder(SoundFolder? target)
    {
        selectingFolder = true;
        folder = target;
        FolderList.ItemsSource = library.Folders.OrderBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        FolderList.SelectedItem = folder;
        LibraryList.SelectedIndex = folder is null ? 0 : -1;
        selectingFolder = false;
        RefreshClips();
    }

    private void OnLibrarySelection(object sender, SelectionChangedEventArgs e)
    {
        if (!loading && !selectingFolder && LibraryList.SelectedIndex == 0)
            SelectFolder(null);
    }

    private void OnFolderSelection(object sender, SelectionChangedEventArgs e)
    {
        if (!loading && !selectingFolder && FolderList.SelectedItem is SoundFolder selected)
            SelectFolder(selected);
    }

    private void RefreshClips()
    {
        var selected = ClipList.SelectedItem as SoundClip;
        var clips = library.Clips.Where(clip => folder is null || clip.FolderId == folder.Id)
            .OrderBy(clip => clip.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        ClipList.ItemsSource = clips;
        ClipList.SelectedItem = clips.Contains(selected!) ? selected : null;
        LibraryTitle.Text = folder?.Name ?? "Все звуки";
        ClipCount.Text = clips.Count + " " + CountWord(clips.Count);
        FolderEmpty.Visibility = library.Folders.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        FolderMenuButton.Visibility = folder is null ? Visibility.Collapsed : Visibility.Visible;
        EmptyLibrary.Visibility = clips.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        TableHeader.Visibility = clips.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        UpdatePlayer();
    }

    private static string CountWord(int count)
    {
        if (count % 100 is >= 11 and <= 14)
            return "звуков";
        return (count % 10) switch { 1 => "звук", >= 2 and <= 4 => "звука", _ => "звуков" };
    }

    private void OnAddFolder(object sender, RoutedEventArgs e)
    {
        string? name = AskName("Новая папка", "", "Создать");
        if (name is null || !CheckFolderName(name, null))
            return;
        var next = new SoundFolder { Name = name };
        library.Folders.Add(next);
        if (!SaveLibrary())
        {
            library.Folders.Remove(next);
            return;
        }
        SelectFolder(next);
        Status("Папка создана.");
    }

    private bool CheckFolderName(string name, SoundFolder? original)
    {
        if (!library.Folders.Any(item => item != original &&
            string.Equals(item.Name, name, StringComparison.CurrentCultureIgnoreCase)))
            return true;
        Status("Папка с таким названием уже есть.", true);
        return false;
    }

    private void OnFolderMenu(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || folder is not SoundFolder selected)
            return;
        var menu = MakeMenu(button);
        AddMenuItem(menu, "Переименовать", () =>
        {
            string? name = AskName("Переименовать папку", selected.Name, "Сохранить");
            if (name is null || !CheckFolderName(name, selected))
                return;
            string previous = selected.Name;
            selected.Name = name;
            if (!SaveLibrary())
                selected.Name = previous;
            SelectFolder(selected);
        });
        AddMenuItem(menu, "Удалить папку", () =>
        {
            if (!Confirm("Удалить папку «" + selected.Name + "»? Звуки останутся в библиотеке."))
                return;
            var clips = library.Clips.Where(clip => clip.FolderId == selected.Id).ToList();
            library.Folders.Remove(selected);
            clips.ForEach(clip => clip.FolderId = null);
            if (!SaveLibrary())
            {
                library.Folders.Add(selected);
                clips.ForEach(clip => clip.FolderId = selected.Id);
                return;
            }
            SelectFolder(null);
            Status("Папка удалена.");
        });
        menu.IsOpen = true;
    }

    private void OnClipMenu(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || button.DataContext is not SoundClip clip)
            return;
        ClipList.SelectedItem = clip;
        var menu = MakeMenu(button);
        AddMenuItem(menu, "Переименовать", () =>
        {
            string? name = AskName("Переименовать звук", clip.Name, "Сохранить");
            if (name is null)
                return;
            string previous = clip.Name;
            clip.Name = name;
            if (!SaveLibrary())
                clip.Name = previous;
            RefreshClips();
        });
        var move = new MenuItem { Header = "Переместить в папку" };
        if (clip.FolderId is not null)
            AddMenuItem(move, "Без папки", () => MoveClip(clip, null));
        foreach (var target in library.Folders.Where(item => item.Id != clip.FolderId)
                     .OrderBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase))
            AddMenuItem(move, target.Name.Replace("_", "__"), () => MoveClip(clip, target.Id));
        move.IsEnabled = move.HasItems;
        menu.Items.Add(move);
        AddMenuItem(menu, "Удалить звук", () =>
        {
            if (!Confirm("Удалить звук «" + clip.Name + "» из библиотеки?"))
                return;
            library.Clips.Remove(clip);
            if (!SaveLibrary())
            {
                library.Clips.Add(clip);
                return;
            }
            if (currentClip == clip)
            {
                audio?.Stop();
                currentClip = null;
            }
            Library.TryDeleteFile(clip);
            RefreshClips();
            Status("Звук удалён.");
        });
        menu.IsOpen = true;
    }

    private void MoveClip(SoundClip clip, string? to)
    {
        string? previous = clip.FolderId;
        clip.FolderId = to;
        if (!SaveLibrary())
            clip.FolderId = previous;
        else
            Status("Звук перемещён.");
        RefreshClips();
    }

    private static ContextMenu MakeMenu(Button button) => new()
    {
        PlacementTarget = button,
        Placement = PlacementMode.Bottom,
        FontFamily = new FontFamily("Segoe UI")
    };

    private static void AddMenuItem(ItemsControl menu, string header, Action action)
    {
        var item = new MenuItem { Header = header };
        item.Click += (_, _) => action();
        menu.Items.Add(item);
    }

    private string? AskName(string title, string initial, string action)
    {
        var input = new TextBox
        {
            Text = initial, MaxLength = 120, Padding = new Thickness(9, 7, 9, 7), Background = BrushFor("Background"),
            Foreground = BrushFor("Text"), BorderBrush = BrushFor("Stroke"), CaretBrush = BrushFor("Text"),
            Margin = new Thickness(0, 6, 0, 17)
        };
        AutomationProperties.SetName(input, "Название");
        var ok = new Button { Content = action, IsDefault = true, Style = (Style)FindResource("PrimaryButton") };
        var cancel = new Button { Content = "Отмена", IsCancel = true, Margin = new Thickness(0, 0, 8, 0) };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(cancel);
        buttons.Children.Add(ok);
        var content = new StackPanel { Margin = new Thickness(19) };
        content.Children.Add(new TextBlock { Text = "Название", Foreground = BrushFor("Muted") });
        content.Children.Add(input);
        content.Children.Add(buttons);
        var dialog = new Window
        {
            Title = title, Owner = this, Icon = Icon, ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ResizeMode = ResizeMode.NoResize, Width = 365, SizeToContent = SizeToContent.Height,
            Background = BrushFor("Panel"), Foreground = BrushFor("Text"), Content = content
        };
        dialog.SourceInitialized += OnSourceInitializedForDialog;
        dialog.Loaded += (_, _) => { input.Focus(); input.SelectAll(); };
        ok.IsEnabled = !string.IsNullOrWhiteSpace(input.Text);
        input.TextChanged += (_, _) => ok.IsEnabled = !string.IsNullOrWhiteSpace(input.Text);
        ok.Click += (_, _) => dialog.DialogResult = true;
        return dialog.ShowDialog() == true ? input.Text.Trim() : null;
    }

    private static void OnSourceInitializedForDialog(object? sender, EventArgs e)
    {
        if (sender is Window window)
        {
            int dark = 1;
            DwmSetWindowAttribute(new WindowInteropHelper(window).Handle, 20, ref dark, sizeof(int));
        }
    }

    private bool Confirm(string question) =>
        MessageBox.Show(this, question, "Violet Soundpad", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No)
        == MessageBoxResult.Yes;

    private async void OnAddFiles(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Добавить звуки",
            Filter = "Аудиофайлы|*.wav;*.mp3;*.flac;*.m4a;*.aac;*.wma;*.aiff;*.aif|Все файлы|*.*",
            Multiselect = true
        };
        if (dialog.ShowDialog(this) == true)
            await ImportFiles(dialog.FileNames);
    }

    private void OnDragOver(object sender, DragEventArgs e)
    {
        e.Effects = !importing && e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private async void OnDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] files)
            await ImportFiles(files);
    }

    private async Task ImportFiles(string[] files)
    {
        if (importing || files.Length == 0)
            return;
        importing = true;
        ImportButton.IsEnabled = EmptyImportButton.IsEnabled = false;
        string? target = folder?.Id;
        Status("Добавление файлов…");
        try
        {
            var result = await Task.Run(() =>
            {
                var imported = new List<SoundClip>();
                var errors = new List<string>();
                foreach (string path in files)
                {
                    try { imported.Add(Library.Import(path, target)); }
                    catch (Exception ex) { errors.Add(Path.GetFileName(path) + " — " + ex.Message); }
                }
                return Tuple.Create(imported, errors);
            });
            var importedClips = result.Item1;
            if (!library.Folders.Any(item => item.Id == target))
                importedClips.ForEach(clip => clip.FolderId = null);
            library.Clips.AddRange(importedClips);
            if (importedClips.Count > 0 && !SaveLibrary())
            {
                foreach (var clip in importedClips)
                {
                    library.Clips.Remove(clip);
                    Library.TryDeleteFile(clip);
                }
                return;
            }
            RefreshClips();
            string message = "Добавлено " + importedClips.Count + " " + CountWord(importedClips.Count) + ".";
            if (result.Item2.Count > 0)
                message += " Ошибок: " + result.Item2.Count + ". " + result.Item2[0];
            Status(message, result.Item2.Count > 0);
        }
        finally
        {
            importing = false;
            ImportButton.IsEnabled = EmptyImportButton.IsEnabled = true;
        }
    }

    private void OnRowPlay(object sender, RoutedEventArgs e)
    {
        if (sender is Button button && button.DataContext is SoundClip clip)
        {
            ClipList.SelectedItem = clip;
            PlayClip(clip);
        }
    }

    private void OnDoubleClickClip(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject source &&
            ItemsControl.ContainerFromElement(ClipList, source) is ListBoxItem row &&
            row.DataContext is SoundClip clip && !IsButtonInRow(source, row))
            PlayClip(clip);
    }

    private static bool IsButtonInRow(DependencyObject? source, ListBoxItem row)
    {
        while (source is Visual && source != row)
        {
            if (source is Button)
                return true;
            source = VisualTreeHelper.GetParent(source);
        }
        return false;
    }

    private void OnClipKey(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && ClipList.SelectedItem is SoundClip clip)
        {
            PlayClip(clip);
            e.Handled = true;
        }
    }

    private void OnClipSelection(object sender, SelectionChangedEventArgs e)
    {
        if (!loading)
            UpdatePlayer();
    }

    private void OnPlayPause(object sender, RoutedEventArgs e)
    {
        var selected = ClipList.SelectedItem as SoundClip;
        if (audio?.HasClip == true)
            audio.TogglePause();
        else if ((selected ?? currentClip) is SoundClip clip)
            PlayClip(clip);
        UpdatePlayer();
    }

    private void PlayClip(SoundClip clip)
    {
        try
        {
            RouteAudio();
            GetAudio().Play(Library.GetPath(clip));
            currentClip = clip;
            Status("Воспроизведение в виртуальный микрофон.");
        }
        catch (Exception ex)
        {
            if (!forwardingVoice && audio?.HasClip == false)
                audio.ReleaseOutputs();
            Status("Не удалось воспроизвести звук. " + ex.Message, true);
        }
        UpdatePlayer();
    }

    private void OnStop(object sender, RoutedEventArgs e)
    {
        audio?.Stop();
        if (!forwardingVoice)
            audio?.ReleaseOutputs();
        Status("Воспроизведение остановлено.");
        UpdatePlayer();
    }

    private void UpdatePlayer()
    {
        var selected = ClipList.SelectedItem as SoundClip;
        bool active = audio?.HasClip == true;
        bool paused = audio?.IsPaused == true;
        var position = audio?.Position ?? TimeSpan.Zero;
        var duration = audio?.Duration ?? TimeSpan.Zero;
        var displayed = active ? currentClip : selected ?? currentClip;
        if (displayed != currentClip)
        {
            position = TimeSpan.Zero;
            duration = TimeSpan.FromSeconds(displayed?.Duration ?? 0);
        }
        NowPlaying.Text = displayed?.Name ?? "Клип не выбран";
        PlayState.Text = paused ? "На паузе" : active ? "Воспроизводится" : "Остановлено";
        bool showPause = active && !paused;
        PlayGlyph.Text = showPause ? "\uE769" : "\uE768";
        string action = showPause ? "Пауза" : "Воспроизвести";
        PlayPauseButton.ToolTip = action;
        AutomationProperties.SetName(PlayPauseButton, action);
        PlayPauseButton.IsEnabled = active || selected is not null || currentClip is not null;
        StopButton.IsEnabled = active || position > TimeSpan.Zero;
        Elapsed.Text = SoundClip.FormatTime(position);
        Total.Text = SoundClip.FormatTime(duration);
        PlaybackProgress.Value = duration.TotalSeconds > 0 ? position.TotalSeconds / duration.TotalSeconds : 0;
        if (audio is not null && (testingMicrophone || forwardingVoice))
        {
            try
            {
                MicLevel.Value = Math.Max(audio.MicrophonePeak, MicLevel.Value * .7);
                MicLevelText.Text = Math.Round(MicLevel.Value * 100) + " %";
            }
            catch (Exception ex) { Dispatcher.BeginInvoke(new Action(() => HandleAudioError(ex, true))); }
        }
        else
        {
            MicLevel.Value = 0;
            MicLevelText.Text = "—";
        }
    }

    private void OnVolumeChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (VolumeText is not null)
            VolumeText.Text = Math.Round(e.NewValue) + " %";
        library.Volume = e.NewValue / 100;
        if (audio is not null)
            audio.Volume = (float)library.Volume;
        if (!loading)
            ScheduleSave();
    }

    private void ScheduleSave()
    {
        saveTimer.Stop();
        saveTimer.Start();
    }

    private bool SaveLibrary()
    {
        try { library.Save(); return true; }
        catch (Exception ex)
        {
            Status("Не удалось сохранить библиотеку. " + ex.Message, true);
            return false;
        }
    }

    private Brush BrushFor(string name) => (Brush)FindResource(name);

    private void Status(string message, bool error = false)
    {
        StatusText.Text = message;
        StatusText.Foreground = BrushFor(error ? "Warning" : "Muted");
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (importing)
        {
            e.Cancel = true;
            Status("Дождитесь окончания импорта звуков.");
            return;
        }
        base.OnClosing(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        timer.Stop();
        if (saveTimer.IsEnabled)
        {
            saveTimer.Stop();
            SaveLibrary();
        }
        audio?.Dispose();
        base.OnClosed(e);
    }
}
