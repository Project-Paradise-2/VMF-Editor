using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using VmfCore;

namespace VmfEditor;

/// <summary>Alle Skriptblöcke des Films (Bildaktionen, Button-Aktionen): suchen, als Bytecode-Text anzeigen, ändern.</summary>
public partial class ScriptWindow : Window
{
    /// <summary>Ein Skriptblock: Bildaktion (DoAction-Tag) oder eine Bedingung eines Buttons (Index in seinem Tag).</summary>
    public sealed class Block
    {
        public VmfTag Tag;
        public int ButtonIndex = -1;      // ≥ 0: Aktion Nr. im DefineButton2
        public int Cond;
        public int SpriteId, Frame;       // Ort (0 = Hauptzeitleiste); Buttons: Frame = -1
        public string Title = "", Summary = "";
        public byte[] Code
        {
            get
            {
                if (ButtonIndex < 0) return Tag.Body;
                var acts = VmfEdit.ReadButtonActions(Tag.Body, out _);
                return ButtonIndex < acts.Count ? acts[ButtonIndex].code : Array.Empty<byte>();
            }
        }
        string _text; byte[] _textOf;
        public string Text { get { var c = Code; if (!ReferenceEquals(c, _textOf) || _text == null) { _textOf = c; _text = Avm1.Disassemble(c); } return _text; } }
        public override string ToString() => Title + (Summary.Length > 0 ? "\n    " + Summary : "");
        public bool Same(Block o) => o != null && o.Tag == Tag && o.ButtonIndex == ButtonIndex;
    }

    const string HelpText = "AVM1 bytecode view. Jump labels L1…; Push strings found in the ConstantPool are encoded as pool references automatically, @\"…\" forces an inline string. 12 = integer, 1.5 = double, 1.5f = float, r:1 = register. \";\" starts a comment.";
    readonly MainWindow _main;
    List<Block> _all = new();
    Block _cur;
    bool _loading, _dirty;

    public ScriptWindow(MainWindow main)
    {
        InitializeComponent();
        _main = main;
        Owner = main;
        Msg.Text = HelpText;
        Closing += (_, e) => { if (_dirty && !ConfirmDiscard()) e.Cancel = true; };
    }

    /// <summary>Liste neu aufbauen (nach Öffnen/Änderung/Rückgängig); aktuelle Auswahl bleibt, wenn möglich.</summary>
    public void Reload(Block select = null)
    {
        var keep = select ?? _cur;
        _all = Collect();
        Fill(keep);
        if (!_dirty && _cur != null) ShowBlock(_cur);
    }

    List<Block> Collect()
    {
        var r = new List<Block>();
        var a = _main.Assets;
        if (a == null) return r;
        var m = a.Model;
        void FromTimeline(VmfModel.Timeline tl, int spriteId, string where)
        {
            for (int f = 0; f < tl.FrameCount; f++)
                foreach (var op in tl.Frames[f])
                    if (op.Kind == VmfModel.OpKind.Action && op.Tag != null)
                    {
                        var lab = tl.Labels.LastOrDefault(l => l.Frame <= f);
                        r.Add(new Block
                        {
                            Tag = op.Tag, SpriteId = spriteId, Frame = f,
                            Title = $"{where} · frame {f + 1}" + (lab != null ? $"  ◆ {lab.Name}" + (lab.Frame != f ? $"+{f - lab.Frame}" : "") : ""),
                        });
                    }
        }
        FromTimeline(m.Root, 0, "Main");
        foreach (var sp in m.Chars.Values.OfType<VmfModel.Sprite>().OrderBy(s => s.Id))
            FromTimeline(sp.Timeline, sp.Id, _main.SpriteName(sp.Id));
        foreach (var b in m.Chars.Values.OfType<VmfModel.Button>().OrderBy(s => s.Id))
        {
            var acts = VmfEdit.ReadButtonActions(b.Tag.Body, out _);
            for (int i = 0; i < acts.Count; i++)
                r.Add(new Block { Tag = b.Tag, ButtonIndex = i, Cond = acts[i].cond, SpriteId = _main.SpriteOfButton(b.Id), Frame = -1,
                                  Title = $"Button {b.Id} · {CondText(acts[i].cond)}" });
        }
        foreach (var x in r) x.Summary = Avm1.Summary(x.Code);
        return r;
    }

    static readonly string[] CondNames = { "RollOver", "RollOut", "Press", "Release", "DragOut", "DragOver", "ReleaseOutside", "DragOver(menu)", "DragOut(menu)" };
    static string CondText(int c)
    {
        var l = new List<string>();
        for (int i = 0; i < 9; i++) if ((c & (1 << i)) != 0) l.Add(CondNames[i]);
        int key = c >> 9;
        if (key != 0) l.Add(key >= 32 && key < 127 ? $"Key '{(char)key}'" : $"Key {key}");
        return l.Count > 0 ? string.Join("+", l) : $"0x{c:X4}";
    }

    void Fill(Block keep)
    {
        string q = Search.Text.Trim();
        bool here = OnlyHere.IsChecked == true;
        int curSprite = _main.CurrentSpriteId;
        var shown = _all.Where(b => (!here || b.SpriteId == curSprite) &&
                                    (q.Length == 0 || b.Title.Contains(q, StringComparison.OrdinalIgnoreCase) || b.Text.Contains(q, StringComparison.OrdinalIgnoreCase)))
                        .ToList();
        _loading = true;
        BlockList.ItemsSource = shown;
        var sel = keep != null ? shown.FirstOrDefault(b => b.Same(keep)) : shown.FirstOrDefault();
        BlockList.SelectedItem = sel;
        if (sel != null) { _cur = sel; BlockList.ScrollIntoView(sel); }
        _loading = false;
        Count.Text = $"{shown.Count} of {_all.Count} script blocks";
    }

    void Filter_Changed(object s, RoutedEventArgs e) => Fill(_cur);

    /// <summary>Von außen: bestimmten Block (oder den ersten passenden) anzeigen.</summary>
    public void FocusBlock(Func<Block, bool> pick)
    {
        var b = _all.FirstOrDefault(pick);
        if (b == null) return;
        if (_dirty && !ConfirmDiscard()) return;
        Search.Text = "";
        Fill(b);
        ShowBlock(b);
        Activate();
    }

    void List_SelectionChanged(object s, SelectionChangedEventArgs e)
    {
        if (_loading || BlockList.SelectedItem is not Block b || b.Same(_cur) && !_dirty) return;
        if (_dirty && !ConfirmDiscard()) { _loading = true; BlockList.SelectedItem = _all.FirstOrDefault(x => x.Same(_cur)); _loading = false; return; }
        ShowBlock(b);
    }

    void ShowBlock(Block b)
    {
        _cur = b;
        _loading = true;
        try { Code.Text = b.Text; }
        catch (Exception ex) { Code.Text = "; Unreadable: " + ex.Message; }
        _loading = false;
        SetDirty(false);
        Where.Text = b.Title + $"   ({b.Code.Length} bytes)";
        Msg.Text = HelpText;
    }

    bool ConfirmDiscard() =>
        MessageBox.Show(this, "Discard changes to this script?", "ActionScript", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;

    void SetDirty(bool d) { _dirty = d; ApplyBtn.IsEnabled = d; ResetBtn.IsEnabled = d; Title = "ActionScript" + (d ? " *" : ""); }

    void Code_TextChanged(object s, TextChangedEventArgs e) { if (!_loading && _cur != null) SetDirty(true); }

    void Code_PreviewKeyDown(object s, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.Control) { e.Handled = true; Apply(); }
    }

    void Apply_Click(object s, RoutedEventArgs e) => Apply();

    void Apply()
    {
        if (_cur == null || !_dirty) return;
        byte[] code;
        try { code = Avm1.Assemble(Code.Text); }
        catch (Avm1.AsmError ex)
        {
            Msg.Text = "Error: " + ex.Message;
            GoToLine(ex.Line);
            return;
        }
        if (code.Length == 0 || code[^1] != 0) { Msg.Text = "Note: the script did not end with \"End\" – added."; code = code.Append((byte)0).ToArray(); }
        var b = _cur;
        byte[] body;
        if (b.ButtonIndex < 0) body = code;
        else
        {
            var acts = VmfEdit.ReadButtonActions(b.Tag.Body, out _);
            acts[b.ButtonIndex] = (acts[b.ButtonIndex].cond, code);
            body = VmfEdit.WriteButtonActions(b.Tag.Body, acts);
        }
        SetDirty(false);
        _main.ApplyTagBody($"script {b.Title}", b.Tag, body);   // löst Reload aus
        Msg.Text = $"Applied ({code.Length} bytes). Ctrl+S in the main window saves the bank.";
    }

    void GoToLine(int line)
    {
        if (line < 1 || line > Code.LineCount) return;
        int idx = Code.GetCharacterIndexFromLineIndex(line - 1);
        Code.Focus();
        Code.Select(idx, Code.GetLineLength(line - 1));
        Code.ScrollToLine(line - 1);
    }

    void Reset_Click(object s, RoutedEventArgs e) { if (_cur != null) ShowBlock(_cur); }

    void Show_Click(object s, RoutedEventArgs e) { if (_cur != null) _main.ShowLocation(_cur.SpriteId, Math.Max(0, _cur.Frame)); }

    void List_DoubleClick(object s, MouseButtonEventArgs e) { if (BlockList.SelectedItem is Block b) _main.ShowLocation(b.SpriteId, Math.Max(0, b.Frame)); }
}
