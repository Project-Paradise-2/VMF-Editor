using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;
using VmfCore;

namespace VmfEditor;

public partial class MainWindow : Window
{
    public static readonly RoutedUICommand OpenCmd = new("Open", "Open", typeof(MainWindow));
    public static readonly RoutedUICommand SaveCmd = new("Save", "Save", typeof(MainWindow));
    public static readonly RoutedUICommand UndoCmd = new("Undo", "Undo", typeof(MainWindow));
    public static readonly RoutedUICommand RedoCmd = new("Redo", "Redo", typeof(MainWindow));
    public static readonly RoutedUICommand ScriptsCmd = new("Scripts", "Scripts", typeof(MainWindow));

    BankAssets _assets;
    ImageCache _images;
    bool _dirty, _syncing;
    ScriptWindow _scripts;

    // Navigation: Kette der betretenen Sprites (Id, 0 = Hauptzeitleiste) mit ihrem Bild
    readonly List<(int spriteId, int frame)> _path = new();
    int CurSprite => _path.Count > 0 ? _path[^1].spriteId : 0;
    int CurFrame { get => _path.Count > 0 ? _path[^1].frame : 0; set { if (_path.Count > 0) _path[^1] = (_path[^1].spriteId, value); } }

    // Rückgängig: je Schritt die geänderten Tags (vorher/nachher)
    sealed class Step { public string What; public List<(VmfTag tag, byte[] before, byte[] after)> Tags = new(); }
    readonly List<Step> _undo = new(), _redo = new();

    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    // ---- für das Skriptfenster
    public BankAssets Assets => _assets;
    public int CurrentSpriteId => _assets == null ? 0 : CurSprite;

    public MainWindow()
    {
        InitializeComponent();
        CommandBindings.Add(new CommandBinding(OpenCmd, (_, _) => OpenDialog()));
        CommandBindings.Add(new CommandBinding(SaveCmd, (_, _) => Save(), (_, e) => e.CanExecute = _assets != null));
        CommandBindings.Add(new CommandBinding(UndoCmd, (_, _) => Undo(), (_, e) => e.CanExecute = _undo.Count > 0));
        CommandBindings.Add(new CommandBinding(RedoCmd, (_, _) => Redo(), (_, e) => e.CanExecute = _redo.Count > 0));
        CommandBindings.Add(new CommandBinding(ScriptsCmd, (_, _) => OpenScripts(null), (_, e) => e.CanExecute = _assets != null));
        // Verschachtelte Sprites: angehalten das erste Bild mit stop() (fertig eingeblendet), beim Abspielen laufend
        Scene.ChildFrame = sp =>
        {
            var tl = sp.Timeline; int n = tl.FrameCount, stop = Avm1.StopFrame(tl);
            if (n <= 1) return 0;
            if (!_playing) return stop >= 0 ? stop : 0;
            return stop >= 0 ? Math.Min(_tick, stop) : _tick % n;
        };
        Scene.FrameOf = it => _frameOv.TryGetValue(ViewKey(it), out var f) ? f : -1;
        Scene.HiddenOf = it => _hideOv.Contains(ViewKey(it));
        _playTimer.Tick += (_, _) => PlayTick();
        Stage.SelectionChanged += _ => ShowProps();
        Stage.EnterRequested += it => { if (it.Char is VmfModel.Sprite sp) Enter(sp.Id, 0); };
        Stage.MoveCommitted += OnMoved;
        Loaded += (_, _) =>
        {
            var args = Environment.GetCommandLineArgs();
            if (args.Length > 1 && File.Exists(args[1])) Open(args[1]);
            // Testhilfe: VmfEditor.exe <bank> --shot <png> [sprite-id] [--scripts] -> Fensterbild speichern und beenden
            int si = Array.IndexOf(args, "--shot");
            if (si > 0 && si + 1 < args.Length)
            {
                int fi = Array.IndexOf(args, "--frame");
                int frame = fi > 0 && fi + 1 < args.Length && int.TryParse(args[fi + 1], out int fr) ? fr - 1 : 0;
                if (si + 2 < args.Length && int.TryParse(args[si + 2], out int sid)) ShowLocation(sid, frame);
                else if (fi > 0) ShowLocation(0, frame);
                if (args.Contains("--layers")) LeftTabs.SelectedIndex = 1;
                bool scripts = args.Contains("--scripts");
                if (scripts) OpenScripts(null);
                Dispatcher.BeginInvoke(() =>
                {
                    Window w = scripts ? _scripts : this;
                    var rtb = new System.Windows.Media.Imaging.RenderTargetBitmap((int)w.ActualWidth, (int)w.ActualHeight, 96, 96, PixelFormats.Pbgra32);
                    rtb.Render((Visual)w.Content);
                    var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
                    enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(rtb));
                    using (var fs = File.Create(args[si + 1])) enc.Save(fs);
                    _dirty = false;
                    Application.Current.Shutdown();
                }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            }
        };
        Closing += (_, e) =>
        {
            if (!_dirty) return;
            var r = MessageBox.Show("Save changes?", "VMF Editor", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
            if (r == MessageBoxResult.Cancel) e.Cancel = true;
            else if (r == MessageBoxResult.Yes) Save();
        };
    }

    // ------------------------------------------------------------------ Datei
    void OpenDialog()
    {
        if (_dirty && MessageBox.Show("Discard unsaved changes?", "VMF Editor", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
        var dlg = new OpenFileDialog { Filter = "TDU2 bank (*.bnk)|*.bnk|All files|*.*" };
        foreach (var d in new[] { @"E:\Unity3d\TDU2 To Unity3D\Client\Assets\StreamingAssets\Euro\Bnk\frontend\hires",
                                  Path.Combine(AppContext.BaseDirectory, @"..\..\..\..\..\..\Client\Assets\StreamingAssets\Euro\Bnk\frontend\hires") })
            if (Directory.Exists(d)) { dlg.InitialDirectory = Path.GetFullPath(d); break; }
        if (dlg.ShowDialog(this) == true) Open(dlg.FileName);
    }

    void Open(string path)
    {
        try
        {
            var bnk = BnkFile.Open(path);
            var vmf = bnk.Entries.Find(e => e.Extension == ".vmf");
            if (vmf == null) { MessageBox.Show("This bank contains no Flash movie (.vmf).", "VMF Editor"); return; }
            if (_playing) Play_Click(null, null);
            _assets = new BankAssets(bnk, vmf);
            _images = new ImageCache(_assets);
            Stage.Assets = _assets; Stage.Images = _images; Stage.Selected = null;
            _undo.Clear(); _redo.Clear(); _dirty = false;
            LoadView(path);
            _path.Clear(); _path.Add((0, _assets.Model.Root.Labels.FirstOrDefault()?.Frame ?? 0));   // am ersten Label beginnen
            BuildTree();
            RefreshTimeline(true);
            Stage.FitStage();
            Status.Text = "";
            UpdateTitle();
            _scripts?.Reload();
        }
        catch (Exception ex) { MessageBox.Show("Could not open the file:\n" + ex.Message, "VMF Editor"); }
    }

    void Save()
    {
        if (_assets == null) return;
        try
        {
            _assets.Save();
            _dirty = false;
            UpdateTitle();
            Status.Text = $"Saved: {_assets.Bank.FilePath}  (original backed up as {Path.GetFileName(_assets.Bank.FilePath)}.orig)";
        }
        catch (Exception ex) { MessageBox.Show("Could not save:\n" + ex.Message, "VMF Editor"); }
    }

    void UpdateTitle()
    {
        Title = _assets == null ? "TDU2 VMF Editor" : $"TDU2 VMF Editor – {Path.GetFileName(_assets.Bank.FilePath)} / {_assets.VmfEntry.Name}{(_dirty ? " *" : "")}";
        if (_assets != null && !Status.Text.StartsWith("Saved") && !Status.Text.StartsWith("Changed") && !Status.Text.StartsWith("Undo") && !Status.Text.StartsWith("Redo"))
            Status.Text = $"{_assets.Bank.FilePath} – {_assets.File.Tags.Count} tags, {_assets.Model.Chars.Count} characters, {_assets.Model.Root.FrameCount} frames" +
                          (_assets.Model.Errors > 0 ? $", {_assets.Model.Errors} unreadable tags (kept unchanged)" : "");
    }

    void Exit_Click(object s, RoutedEventArgs e) => Close();

    // ------------------------------------------------------------------ Baum
    TreeViewItem _rootNode, _spritesNode;
    readonly Dictionary<int, List<string>> _names = new();
    readonly Dictionary<int, int> _buttonOwner = new();   // Button-Id → Sprite, in dem er platziert ist (0 = Haupt)

    void BuildTree()
    {
        Tree.Items.Clear();
        _names.Clear(); _buttonOwner.Clear();
        var m = _assets.Model;
        void Collect(VmfModel.Timeline tl, int owner)
        {
            foreach (var f in tl.Frames) foreach (var op in f)
                if (op.Kind == VmfModel.OpKind.Place && op.HasChar)
                {
                    if (m.Chars.TryGetValue(op.CharId, out var ch) && ch is VmfModel.Button) _buttonOwner.TryAdd(op.CharId, owner);
                    if (!op.HasName || string.IsNullOrEmpty(op.Name)) continue;
                    if (!_names.TryGetValue(op.CharId, out var l)) _names[op.CharId] = l = new();
                    if (!l.Contains(op.Name)) l.Add(op.Name);
                }
        }
        Collect(m.Root, 0);
        foreach (var c in m.Chars.Values) if (c is VmfModel.Sprite sp) Collect(sp.Timeline, sp.Id);
        foreach (var kv in m.Exports)
        {
            if (!_names.TryGetValue(kv.Value, out var l)) _names[kv.Value] = l = new();
            l.Insert(0, "export:" + kv.Key);
        }
        _rootNode = new TreeViewItem { Header = $"Main timeline ({m.Root.FrameCount} frames)", Tag = 0, IsExpanded = true };
        AddLabels(_rootNode, m.Root, 0);
        Tree.Items.Add(_rootNode);
        _spritesNode = new TreeViewItem { Header = "Sprites", IsExpanded = true };
        Tree.Items.Add(_spritesNode);
        FillSprites();
    }

    public string SpriteName(int id) => $"Sprite {id}" + (_names.TryGetValue(id, out var l) ? " " + string.Join(", ", l.Take(2)) : "");
    public int SpriteOfButton(int id) => _buttonOwner.TryGetValue(id, out var s) ? s : 0;

    string SpriteTitle(VmfModel.Sprite sp) =>
        $"Sprite {sp.Id}" + (_names.TryGetValue(sp.Id, out var l) ? "  " + string.Join(", ", l.Take(3)) : "") + $"  ({sp.Timeline.FrameCount})";

    void FillSprites()
    {
        _spritesNode.Items.Clear();
        string f = Filter.Text.Trim();
        int n = 0;
        foreach (var c in _assets.Model.Chars.Values.OfType<VmfModel.Sprite>().OrderBy(x => x.Id))
        {
            string title = SpriteTitle(c);
            if (f.Length > 0 && title.IndexOf(f, StringComparison.OrdinalIgnoreCase) < 0) continue;
            var node = new TreeViewItem { Header = title, Tag = c.Id };
            if (c.Timeline.Labels.Count > 0) { node.Items.Add("…"); node.Expanded += SpriteExpanded; }
            _spritesNode.Items.Add(node);
            if (++n >= 3000) break;
        }
        _spritesNode.Header = $"Sprites ({n})";
    }

    void SpriteExpanded(object sender, RoutedEventArgs e)
    {
        var node = (TreeViewItem)sender;
        if (node.Items.Count == 1 && node.Items[0] is string && node.Tag is int id && _assets.Model.Chars.TryGetValue(id, out var c) && c is VmfModel.Sprite sp)
        { node.Items.Clear(); AddLabels(node, sp.Timeline, id); }
    }

    static void AddLabels(TreeViewItem node, VmfModel.Timeline tl, int spriteId)
    {
        foreach (var l in tl.Labels)
            node.Items.Add(new TreeViewItem { Header = $"◆ {l.Name}  (frame {l.Frame + 1})", Tag = (spriteId, l.Frame), Foreground = new SolidColorBrush(Color.FromRgb(0xE8, 0xC0, 0x60)) });
    }

    void Filter_TextChanged(object s, TextChangedEventArgs e) { if (_assets != null) FillSprites(); }

    void Tree_SelectedItemChanged(object s, RoutedPropertyChangedEventArgs<object> e)
    {
        if (_syncing || e.NewValue is not TreeViewItem node || _assets == null) return;
        if (node.Tag is int id) ShowLocation(id, 0);
        else if (node.Tag is ValueTuple<int, int> t) ShowLocation(t.Item1, t.Item2);
    }

    /// <summary>Zeitleiste (0 = Haupt) in einem Bild anzeigen.</summary>
    public void ShowLocation(int spriteId, int frame)
    {
        if (_assets == null) return;
        _path.Clear();
        if (spriteId == 0) _path.Add((0, frame));
        else { _path.Add((0, 0)); _path.Add((spriteId, frame)); }
        Stage.Selected = null;
        RefreshTimeline(true);
        Activate();
    }

    // ------------------------------------------------------------------ Zeitleiste
    VmfModel.Timeline CurTimeline =>
        CurSprite == 0 ? _assets.Model.Root : _assets.Model.Chars.TryGetValue(CurSprite, out var c) && c is VmfModel.Sprite sp ? sp.Timeline : _assets.Model.Root;

    void Enter(int spriteId, int frame) { _path.Add((spriteId, frame)); Stage.Selected = null; RefreshTimeline(true); }

    void Back_Click(object s, RoutedEventArgs e)
    {
        if (_path.Count <= 1) return;
        _path.RemoveAt(_path.Count - 1);
        Stage.Selected = null;
        RefreshTimeline(true);
    }

    void RefreshTimeline(bool controls)
    {
        if (_assets == null) return;
        var tl = CurTimeline;
        CurFrame = Math.Clamp(CurFrame, 0, Math.Max(0, tl.FrameCount - 1));
        if (controls)
        {
            _syncing = true;
            LabelBox.Items.Clear();
            LabelBox.Items.Add("–");
            foreach (var l in tl.Labels) LabelBox.Items.Add($"{l.Name}  ({l.Frame + 1})");
            FrameSlider.Maximum = Math.Max(0, tl.FrameCount - 1);
            FrameSlider.Value = CurFrame;
            _syncing = false;
            BackBtn.IsEnabled = _path.Count > 1;
            Crumb.Text = string.Join(" › ", _path.Select(p => p.spriteId == 0 ? "Main" : "Sprite " + p.spriteId));
            // Sprite: Ursprung in die Bühnenmitte (wie "Symbol bearbeiten" in Flash)
            Stage.Origin = CurSprite == 0 ? Matrix2D.Identity : new Matrix2D { A = 1, D = 1, Tx = 640, Ty = 480 };
        }
        FrameText.Text = $"{CurFrame + 1} / {tl.FrameCount}";
        _syncing = true;
        var lab = tl.Labels.LastOrDefault(l => l.Frame <= CurFrame);
        LabelBox.SelectedIndex = lab != null ? tl.Labels.IndexOf(lab) + 1 : 0;
        _syncing = false;
        var sel = SelectionPath(Stage.Selected);
        Stage.Items = Scene.Build(_assets.Model, tl, CurFrame);
        Stage.Selected = FindByPath(Stage.Items, sel);
        Stage.InvalidateVisual();
        if (!_playing || controls) { ShowProps(); BuildLayers(); }   // beim Abspielen nicht 30× je Sekunde neu bauen
    }

    static List<int> SelectionPath(SceneItem it)
    {
        if (it == null) return null;
        var l = new List<int>();
        for (var p = it; p != null; p = p.Parent) l.Insert(0, p.Depth);
        return l;
    }

    static SceneItem FindByPath(List<SceneItem> items, List<int> path)
    {
        if (path == null) return null;
        SceneItem cur = null; var list = items;
        foreach (int d in path)
        {
            cur = list?.Find(x => x.Depth == d);
            if (cur == null) return null;
            list = cur.Children;
        }
        return cur;
    }

    void FrameSlider_ValueChanged(object s, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_syncing || _assets == null) return;
        CurFrame = (int)FrameSlider.Value;
        RefreshTimeline(false);
    }

    void LabelBox_SelectionChanged(object s, SelectionChangedEventArgs e)
    {
        if (_syncing || _assets == null || LabelBox.SelectedIndex <= 0) return;
        var tl = CurTimeline;
        var l = tl.Labels[LabelBox.SelectedIndex - 1];
        CurFrame = l.Frame;
        SyncSlider();
        RefreshTimeline(false);
    }

    void View_Click(object s, RoutedEventArgs e)
    {
        Stage.ShowVariables = VarsBox.IsChecked == true;
        Stage.ShowAllBounds = BoundsBox.IsChecked == true;
        Stage.InvalidateVisual();
    }

    void Fit_Click(object s, RoutedEventArgs e) => Stage.FitStage();

    // ------------------------------------------------------------------ Abspielen
    readonly System.Windows.Threading.DispatcherTimer _playTimer = new();
    bool _playing;
    int _tick;

    void Play_Click(object s, RoutedEventArgs e)
    {
        if (_assets == null) return;
        _playing = !_playing;
        PlayBtn.Content = _playing ? "❚❚ Pause" : "▶ Play";
        if (_playing)
        {
            _tick = 0;
            _playTimer.Interval = TimeSpan.FromSeconds(1.0 / Math.Max(1f, _assets.Model.FrameRate));
            _playTimer.Start();
            // Wie gotoAndPlay: steht die Zeitleiste auf einem stop()-Bild, ab dem nächsten weiterlaufen
            if (FrameHasStop(CurTimeline, CurFrame) && CurFrame + 1 < CurTimeline.FrameCount) CurFrame++;
        }
        else _playTimer.Stop();
        SyncSlider();
        RefreshTimeline(false);
        if (!_playing) ShowProps();
    }

    static bool FrameHasStop(VmfModel.Timeline tl, int f) =>
        f >= 0 && f < tl.FrameCount && tl.Frames[f].Any(op => op.Kind == VmfModel.OpKind.Action && op.Actions != null && Avm1.HasStop(op.Actions));

    void PlayTick()
    {
        if (!_playing || _assets == null) return;
        _tick++;
        var tl = CurTimeline;
        if (!FrameHasStop(tl, CurFrame) && tl.FrameCount > 1) CurFrame = (CurFrame + 1) % tl.FrameCount;
        SyncSlider();
        RefreshTimeline(false);
    }

    void SyncSlider() { _syncing = true; FrameSlider.Value = CurFrame; _syncing = false; }

    // ------------------------------------------------------------------ Ansicht (nur Editor): ausblenden, Bild je Exemplar
    // Viele Menüs werden im Spiel per Skript umgeschaltet (_visible, gotoAndStop). Hier stellt man das von Hand ein;
    // gespeichert je Bank unter %APPDATA%\TDU2VmfEditor\views, nie in der Bank selbst.
    readonly Dictionary<string, int> _frameOv = new();
    readonly HashSet<string> _hideOv = new();
    readonly HashSet<string> _expanded = new();
    string _viewFile;

    string ViewKey(SceneItem it) => CurSprite + "|" + it.Key;

    void LoadView(string bankPath)
    {
        _frameOv.Clear(); _hideOv.Clear(); _expanded.Clear();
        try
        {
            string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "TDU2VmfEditor", "views");
            Directory.CreateDirectory(dir);
            _viewFile = Path.Combine(dir, Path.GetFileName(bankPath) + ".txt");
            if (!File.Exists(_viewFile)) return;
            foreach (var line in File.ReadAllLines(_viewFile))
            {
                var p = line.Split('\t');
                if (p.Length == 2 && p[0] == "H") _hideOv.Add(p[1]);
                else if (p.Length == 3 && p[0] == "F" && int.TryParse(p[2], out int f)) _frameOv[p[1]] = f;
            }
        }
        catch { }
    }

    void SaveView()
    {
        if (_viewFile == null) return;
        try
        {
            var lines = _hideOv.Select(k => "H\t" + k).Concat(_frameOv.Select(kv => $"F\t{kv.Key}\t{kv.Value}"));
            File.WriteAllLines(_viewFile, lines);
        }
        catch { }
    }

    void SetHidden(SceneItem it, bool hidden)
    {
        if (hidden) _hideOv.Add(ViewKey(it)); else _hideOv.Remove(ViewKey(it));
        SaveView();
        RefreshTimeline(false);
    }

    void SetShownFrame(SceneItem it, int frame)
    {
        if (frame < 0) _frameOv.Remove(ViewKey(it)); else _frameOv[ViewKey(it)] = frame;
        SaveView();
        RefreshTimeline(false);
    }

    void ResetView_Click(object s, RoutedEventArgs e)
    {
        if (_assets == null) return;
        // nur die Einstellungen der aktuellen Zeitleiste zurücksetzen
        string pre = CurSprite + "|";
        _hideOv.RemoveWhere(k => k.StartsWith(pre));
        foreach (var k in _frameOv.Keys.Where(k => k.StartsWith(pre)).ToList()) _frameOv.Remove(k);
        SaveView();
        RefreshTimeline(false);
    }

    string LayerText(SceneItem it)
    {
        string t = $"{it.Depth}  " + (string.IsNullOrEmpty(it.Name) ? "" : it.Name + "  ") + $"[{it.Char?.Kind ?? "?"} {it.Char?.Id}]";
        if (it.Char is VmfModel.Sprite sp && sp.Timeline.FrameCount > 1)
        {
            int f = Scene.FrameFor(it, sp);
            var lab = sp.Timeline.Labels.LastOrDefault(l => l.Frame <= f);
            t += $"  frame {f + 1}/{sp.Timeline.FrameCount}" + (lab != null ? $" ◆{lab.Name}" : "") + (_frameOv.ContainsKey(ViewKey(it)) ? " (set)" : "");
        }
        if (it.ClipDepth > 0) t += "  mask";
        return t;
    }

    void BuildLayers()
    {
        if (Layers == null) return;
        _syncing = true;
        Layers.Items.Clear();
        if (Stage.Items != null) AddLayerNodes(Layers.Items, Stage.Items);
        _syncing = false;
    }

    void AddLayerNodes(ItemCollection target, List<SceneItem> items)
    {
        for (int i = items.Count - 1; i >= 0; i--)   // oberste Ebene zuerst, wie in Flash
        {
            var it = items[i];
            var cb = new CheckBox { IsChecked = !it.Hidden, Margin = new Thickness(0, 0, 4, 0), VerticalAlignment = VerticalAlignment.Center, Focusable = false };
            cb.Click += (_, _) => SetHidden(it, cb.IsChecked != true);
            var head = new StackPanel { Orientation = Orientation.Horizontal };
            head.Children.Add(cb);
            head.Children.Add(new TextBlock { Text = LayerText(it), Foreground = it.Hidden ? (Brush)FindResource("Dim") : (Brush)FindResource("Fg") });
            var node = new TreeViewItem { Header = head, Tag = it };
            if (it.Children != null && it.Children.Count > 0)
            {
                if (_expanded.Contains(ViewKey(it))) { AddLayerNodes(node.Items, it.Children); node.IsExpanded = true; }
                else node.Items.Add("…");
                node.Expanded += (_, e) =>
                {
                    if (e.OriginalSource != node) return;
                    _expanded.Add(ViewKey(it));
                    if (node.Items.Count == 1 && node.Items[0] is string) { node.Items.Clear(); AddLayerNodes(node.Items, it.Children); }
                };
                node.Collapsed += (_, e) => { if (e.OriginalSource == node) _expanded.Remove(ViewKey(it)); };
            }
            target.Add(node);
        }
    }

    void Layers_SelectedItemChanged(object s, RoutedPropertyChangedEventArgs<object> e)
    {
        if (_syncing || e.NewValue is not TreeViewItem node || node.Tag is not SceneItem it) return;
        Stage.Selected = it;
        Stage.InvalidateVisual();
        ShowProps();
    }

    /// <summary>Eigenschaftenfeld: Ansichtseinstellungen des gewählten Exemplars.</summary>
    void ViewProps(SceneItem it)
    {
        Header("View (editor only)");
        var hide = new CheckBox { Content = "Hidden", IsChecked = it.Hidden, Margin = new Thickness(0, 2, 0, 2) };
        hide.Click += (_, _) => SetHidden(it, hide.IsChecked == true);
        Props.Children.Add(hide);
        if (it.Char is not VmfModel.Sprite sp || sp.Timeline.FrameCount <= 1) return;
        var tl = sp.Timeline;
        int auto = Math.Max(0, Avm1.StopFrame(tl));
        var choices = new List<(string text, int frame)> { ($"Auto (frame {auto + 1})", -1) };
        foreach (var l in tl.Labels) choices.Add(($"◆ {l.Name}  (frame {l.Frame + 1})", l.Frame));
        for (int f = 0; f < tl.FrameCount; f++)
            if (tl.Frames[f].Any(op => op.Kind == VmfModel.OpKind.Action && op.Actions != null && Avm1.HasStop(op.Actions)) && !tl.Labels.Any(l => l.Frame == f))
                choices.Add(($"stop() at frame {f + 1}", f));
        var box = new ComboBox { Margin = new Thickness(0, 2, 0, 2) };
        foreach (var c in choices) box.Items.Add(c.text);
        int cur = _frameOv.TryGetValue(ViewKey(it), out var ov) ? ov : -1;
        int idx = choices.FindIndex(c => c.frame == cur);
        box.SelectedIndex = idx >= 0 ? idx : 0;
        box.SelectionChanged += (_, _) => { if (box.SelectedIndex >= 0) SetShownFrame(it, choices[box.SelectedIndex].frame); };
        Row("Shown state", box);
        Num("Shown frame", Scene.FrameFor(it, sp) + 1, v => SetShownFrame(it, Math.Clamp((int)v - 1, 0, tl.FrameCount - 1)));
        Hint("In the game, scripts switch sub-menus and popups on and off. Hide or switch them here to see one screen at a time. Saved per bank, never written into the bank.");
    }

    // ------------------------------------------------------------------ Skripte
    void OpenScripts(Func<ScriptWindow.Block, bool> pick)
    {
        if (_assets == null) return;
        if (_scripts == null || !_scripts.IsLoaded)
        {
            _scripts = new ScriptWindow(this);
            _scripts.Closed += (_, _) => _scripts = null;
            _scripts.Show();
            _scripts.Reload();
        }
        if (pick != null) _scripts.FocusBlock(pick);
        _scripts.Activate();
    }

    void Scripts_Click(object s, RoutedEventArgs e) => OpenScripts(null);

    /// <summary>Neuen Inhalt eines Tags übernehmen (Skriptfenster), mit Rückgängig.</summary>
    public void ApplyTagBody(string what, VmfTag tag, byte[] body) => Change(what, new[] { tag }, () => tag.Body = body);

    // ------------------------------------------------------------------ Änderungen
    /// <summary>Änderung an Tags ausführen: vorher/nachher merken, Modell neu aufbauen, Ansicht erneuern.</summary>
    void Change(string what, IEnumerable<VmfTag> tags, Action mutate)
    {
        var list = tags.Where(t => t != null).Distinct().ToList();
        var before = list.Select(t => t.Body).ToList();
        mutate();
        var step = new Step { What = what };
        for (int i = 0; i < list.Count; i++)
            if (!list[i].Body.AsSpan().SequenceEqual(before[i])) step.Tags.Add((list[i], before[i], list[i].Body));
        if (step.Tags.Count == 0) return;
        _undo.Add(step); _redo.Clear();
        AfterChange("Changed: " + what);
    }

    void AfterChange(string what)
    {
        _dirty = true;
        _assets.Rebuild();
        Status.Text = what + "   (Ctrl+S saves to the bank, Ctrl+Z undoes)";
        RefreshTimeline(true);
        UpdateTitle();
        _scripts?.Reload();
    }

    void Undo()
    {
        if (_undo.Count == 0) return;
        var s = _undo[^1]; _undo.RemoveAt(_undo.Count - 1);
        foreach (var (tag, before, _) in s.Tags) tag.Body = before;
        _redo.Add(s);
        AfterChange("Undo: " + s.What);
    }

    void Redo()
    {
        if (_redo.Count == 0) return;
        var s = _redo[^1]; _redo.RemoveAt(_redo.Count - 1);
        foreach (var (tag, _, after) in s.Tags) tag.Body = after;
        _undo.Add(s);
        AfterChange("Redo: " + s.What);
    }

    void OnMoved(SceneItem it, Matrix2D old)
    {
        var op = it.MatrixOp;
        if (op?.Tag == null) return;
        var m = it.Matrix;
        it.Matrix = old;   // Modell wird nach der Änderung neu aufgebaut
        Change($"position of {Describe(it)} → {m.Tx:0.##}, {m.Ty:0.##}", new[] { op.Tag }, () =>
        {
            op.HasMatrix = true; op.Matrix = m;
            VmfEdit.Commit(op);
        });
    }

    static string Describe(SceneItem it) => string.IsNullOrEmpty(it.Name) ? $"{it.Char?.Kind} {it.Char?.Id} (depth {it.Depth})" : $"\"{it.Name}\"";

    // ------------------------------------------------------------------ Eigenschaften
    void ShowProps()
    {
        Props.Children.Clear();
        if (_assets == null) return;
        var it = Stage.Selected;
        var tl = CurTimeline;
        Header("Timeline");
        Info("Location", CurSprite == 0 ? "Main timeline" : SpriteName(CurSprite));
        Info("Frame", $"{CurFrame + 1} of {tl.FrameCount}");
        var lab = tl.Labels.LastOrDefault(l => l.Frame == CurFrame);
        if (lab != null) Field("Label", lab.Name, v => Change($"label \"{lab.Name}\" → \"{v}\"", new[] { lab.Tag }, () => VmfEdit.SetLabel(lab, v)));
        int nScripts = tl.Frames[CurFrame].Count(op => op.Kind == VmfModel.OpKind.Action);
        if (nScripts > 0)
        {
            var tags = tl.Frames[CurFrame].Where(op => op.Kind == VmfModel.OpKind.Action).Select(op => op.Tag).ToList();
            ActionButton($"Frame script ({nScripts})…", () => OpenScripts(b => tags.Contains(b.Tag)));
        }
        if (it == null) { Hint("Click an element on the stage (Alt = deepest, double-click = enter sprite)."); return; }

        Header("Element");
        Info("Type", $"{it.Char?.Kind ?? "?"} {it.Char?.Id}");
        Info("Depth", it.Depth.ToString());
        bool editable = it.Owner != null && it.MatrixOp?.Tag != null;
        if (!editable) Hint("Part of a button state – not editable here.");
        if (it.PlaceOp?.Tag != null)
            Field("Instance name", it.Name ?? "", v =>
            {
                var op = it.PlaceOp;
                Change($"name of {Describe(it)} → \"{v}\"", new[] { op.Tag }, () => { op.HasName = v.Length > 0; op.Name = v; VmfEdit.Commit(op); });
            });
        if (editable)
        {
            var m = it.Matrix;
            if (it.MatrixFrame != CurFrame) Hint($"Transform comes from frame {it.MatrixFrame + 1} – changes apply from there.");
            Num("X", m.Tx, v => SetMatrix(it, m, true, v, m.Ty));
            Num("Y", m.Ty, v => SetMatrix(it, m, true, m.Tx, v));
            Num("Scale X %", m.ScaleX * 100, v => SetMatrix(it, Matrix2D.Compose(m.Tx, m.Ty, v / 100f, m.ScaleY, m.RotationDeg), false, 0, 0));
            Num("Scale Y %", m.ScaleY * 100, v => SetMatrix(it, Matrix2D.Compose(m.Tx, m.Ty, m.ScaleX, v / 100f, m.RotationDeg), false, 0, 0));
            Num("Rotation °", m.RotationDeg, v => SetMatrix(it, Matrix2D.Compose(m.Tx, m.Ty, m.ScaleX, m.ScaleY, v), false, 0, 0));
            var cx = it.Cx;
            Num("Alpha %", cx.Ma / 2.56f, v => SetCx(it, c => c.Ma = (int)MathF.Round(Math.Clamp(v, 0, 100) * 2.56f)));
            Field("Color × (R G B %)", $"{cx.Mr / 2.56f:0} {cx.Mg / 2.56f:0} {cx.Mb / 2.56f:0}", v =>
            {
                var p = Nums(v); if (p.Length < 3) return;
                SetCx(it, c => { c.Mr = (int)MathF.Round(p[0] * 2.56f); c.Mg = (int)MathF.Round(p[1] * 2.56f); c.Mb = (int)MathF.Round(p[2] * 2.56f); });
            });
            Field("Color + (R G B A)", $"{cx.Ar} {cx.Ag} {cx.Ab} {cx.Aa}", v =>
            {
                var p = Nums(v); if (p.Length < 4) return;
                SetCx(it, c => { c.Ar = (int)p[0]; c.Ag = (int)p[1]; c.Ab = (int)p[2]; c.Aa = (int)p[3]; });
            });
        }

        ViewProps(it);
        if (it.Char is VmfModel.EditText et) TextProps(et);
        if (it.Char is VmfModel.Sprite sp)
        {
            Header("Sprite");
            Info("Frames / labels", $"{sp.Timeline.FrameCount} / {sp.Timeline.Labels.Count}");
            ActionButton("Enter sprite (double-click)", () => Enter(sp.Id, 0));
            int n = sp.Timeline.Frames.Sum(f => f.Count(op => op.Kind == VmfModel.OpKind.Action));
            if (n > 0) ActionButton($"Sprite scripts ({n})…", () => OpenScripts(b => b.SpriteId == sp.Id && b.ButtonIndex < 0));
        }
        if (it.Char is VmfModel.Button btn)
        {
            Header("Button");
            int n = VmfEdit.ReadButtonActions(btn.Tag.Body, out _).Count;
            if (n > 0) ActionButton($"Button scripts ({n})…", () => OpenScripts(b => b.Tag == btn.Tag));
            else Hint("This button has no scripts (handled by its parent's code).");
        }
        Header("Tag");
        Info("Placed in frame", (it.PlaceFrame + 1).ToString());
        if (it.PlaceOp != null) Info("PlaceObject2", $"{it.PlaceOp.Tag?.Body.Length ?? 0} bytes");
    }

    void ActionButton(string text, Action click)
    {
        var b = new Button { Content = text, Margin = new Thickness(0, 6, 0, 0), HorizontalAlignment = HorizontalAlignment.Left };
        b.Click += (_, _) => click();
        Props.Children.Add(b);
    }

    void TextProps(VmfModel.EditText et)
    {
        Header("Text field (applies to all instances)");
        var tag = et.Tag;
        void Edit(string what, Action<EditTextLayout> f) => Change(what, new[] { tag }, () => { var l = EditTextLayout.Read(tag.Body); f(l); tag.Body = l.Write(); });
        Field("Variable", et.Variable, v => Edit($"variable → \"{v}\"", l => l.Variable = v));
        var lay = EditTextLayout.Read(tag.Body);
        Field("Initial text", lay.Text ?? "", v => Edit("initial text", l => { l.HasText = v.Length > 0 || l.HasText; l.Text = v; }));
        Hint("Initial text starting with \"_\" = localization key (replaced in game).");
        Num("Font size", et.Height, v => Edit($"font size {v}", l => { l.Height = v; l.F1 |= 0x01; }));
        Field("Color (#RRGGBBAA)", et.Color.ToString(), v =>
        {
            var h = v.Trim().TrimStart('#');
            if (h.Length == 6) h += "FF";
            if (h.Length != 8 || !uint.TryParse(h, NumberStyles.HexNumber, Inv, out uint c)) return;
            Edit("text color", l => { l.Color = new Rgba((byte)(c >> 24), (byte)(c >> 16), (byte)(c >> 8), (byte)c); l.F1 |= 0x04; });
        });
        var align = new ComboBox { Margin = new Thickness(0, 2, 0, 2) };
        foreach (var a in new[] { "Left", "Right", "Center", "Justify" }) align.Items.Add(a);
        align.SelectedIndex = Math.Clamp(et.Align, 0, 3);
        align.SelectionChanged += (_, _) => Edit("alignment", l => { l.Align = align.SelectedIndex; l.F2 |= 0x20; });
        Row("Alignment", align);
        Num("Width", et.Bounds.W, v => Edit("width", l => l.Bounds = new RectF(l.Bounds.X, l.Bounds.Y, v, l.Bounds.H)));
        Num("Height", et.Bounds.H, v => Edit("height", l => l.Bounds = new RectF(l.Bounds.X, l.Bounds.Y, l.Bounds.W, v)));
    }

    void SetMatrix(SceneItem it, Matrix2D m, bool translateOnly, float x, float y)
    {
        var op = it.MatrixOp;
        if (op?.Tag == null) return;
        // Nur verschoben: übrige Matrix (inkl. Scherung) unverändert lassen
        if (translateOnly) { m = it.Matrix; m.Tx = x; m.Ty = y; }
        Change($"transform of {Describe(it)}", new[] { op.Tag }, () => { op.HasMatrix = true; op.Matrix = m; VmfEdit.Commit(op); });
    }

    void SetCx(SceneItem it, Action<CxFormBox> f)
    {
        var op = it.CxOp ?? it.PlaceOp;
        if (op?.Tag == null) return;
        var box = new CxFormBox { C = it.Cx };
        f(box);
        var c = box.C;
        Change($"color of {Describe(it)}", new[] { op.Tag }, () => { op.Cx = c; op.HasCx = !c.IsIdentity || op.HasCx; VmfEdit.Commit(op); });
    }

    sealed class CxFormBox
    {
        public CxForm C;
        public int Mr { set => C.Mr = value; } public int Mg { set => C.Mg = value; } public int Mb { set => C.Mb = value; } public int Ma { set => C.Ma = value; }
        public int Ar { set => C.Ar = value; } public int Ag { set => C.Ag = value; } public int Ab { set => C.Ab = value; } public int Aa { set => C.Aa = value; }
    }

    static float[] Nums(string s) =>
        s.Split(new[] { ' ', ';', ',' }, StringSplitOptions.RemoveEmptyEntries).Select(x => float.TryParse(x, NumberStyles.Float, Inv, out var f) ? f : float.NaN).Where(f => !float.IsNaN(f)).ToArray();

    // ---- kleine Bausteine des Eigenschaftenfelds
    void Header(string t) => Props.Children.Add(new TextBlock { Text = t, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 12, 0, 4), Foreground = (Brush)FindResource("Accent") });
    void Hint(string t) => Props.Children.Add(new TextBlock { Text = t, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 4), Foreground = (Brush)FindResource("Dim"), FontSize = 11 });
    void Info(string k, string v) => Row(k, new TextBlock { Text = v, TextWrapping = TextWrapping.Wrap });

    void Row(string k, FrameworkElement v)
    {
        var g = new Grid { Margin = new Thickness(0, 1, 0, 1) };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(120) });
        g.ColumnDefinitions.Add(new ColumnDefinition());
        var l = new TextBlock { Text = k, Foreground = (Brush)FindResource("Dim"), VerticalAlignment = VerticalAlignment.Center };
        g.Children.Add(l); Grid.SetColumn(v, 1); g.Children.Add(v);
        Props.Children.Add(g);
    }

    /// <summary>Textfeld: Übernehmen mit Enter oder beim Verlassen.</summary>
    void Field(string k, string value, Action<string> commit)
    {
        var tb = new TextBox { Text = value };
        bool done = false;
        void Apply() { if (done || tb.Text == value) return; done = true; Dispatcher.BeginInvoke(() => commit(tb.Text)); }
        tb.KeyDown += (_, e) => { if (e.Key == Key.Enter) Apply(); else if (e.Key == Key.Escape) tb.Text = value; };
        tb.LostKeyboardFocus += (_, _) => Apply();
        Row(k, tb);
    }

    void Num(string k, float value, Action<float> commit) =>
        Field(k, value.ToString("0.##", Inv), s => { if (float.TryParse(s.Replace(',', '.'), NumberStyles.Float, Inv, out var f)) commit(f); });
}
