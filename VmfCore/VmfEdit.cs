namespace VmfCore;

/// <summary>Neukodieren einzelner Tags nach einer Änderung. Unveränderte Tags bleiben bytegleich (VmfFile).</summary>
public static class VmfEdit
{
    /// <summary>PlaceObject2 aus dem FrameOp kodieren (Felder je nach Has*-Flags; Clip-Aktionen roh).</summary>
    public static byte[] EncodePlace2(VmfModel.FrameOp op)
    {
        var w = new BitWriter();
        int f = (op.Move ? 1 : 0) | (op.HasChar ? 2 : 0) | (op.HasMatrix ? 4 : 0) | (op.HasCx ? 8 : 0) | (op.HasRatio ? 16 : 0)
              | (op.HasName ? 32 : 0) | (op.HasClip ? 64 : 0) | (op.ClipActionsRaw != null ? 128 : 0);
        w.U8(f); w.U16(op.Depth);
        if (op.HasChar) w.U16(op.CharId);
        if (op.HasMatrix) w.Matrix(op.Matrix);
        if (op.HasCx) w.CxForm(op.Cx, true);
        if (op.HasRatio) w.U16(op.Ratio);
        if (op.HasName) w.CString(op.Name ?? "");
        if (op.HasClip) w.U16(op.ClipDepth);
        if (op.ClipActionsRaw != null) w.Bytes(op.ClipActionsRaw);
        return w.ToArray();
    }

    /// <summary>Änderungen eines Place-Ops in seinen Tag übernehmen.</summary>
    public static void Commit(VmfModel.FrameOp op)
    {
        if (op?.Tag == null || op.Kind != VmfModel.OpKind.Place) return;
        op.Tag.Body = EncodePlace2(op);
    }

    public static void SetLabel(VmfModel.Label l, string name)
    {
        l.Name = name ?? "";
        var b = System.Text.Encoding.Latin1.GetBytes(l.Name);
        var body = new byte[b.Length + 1];
        Buffer.BlockCopy(b, 0, body, 0, b.Length);
        l.Tag.Body = body;
    }

    public static void SetActions(VmfModel.FrameOp op, byte[] code)
    {
        op.Actions = code;
        op.Tag.Body = code;
    }

    /// <summary>Aktionen eines DefineButton2-Tags (Bedingung + Bytecode) lesen.</summary>
    public static List<(int cond, byte[] code)> ReadButtonActions(byte[] d, out int actStart)
    {
        var r = new List<(int, byte[])>();
        int actOff = d.Length >= 5 ? Bin.U16(d, 3) : 0;
        actStart = actOff != 0 ? 3 + actOff : d.Length;
        int q = actStart;
        while (actOff != 0 && q + 4 <= d.Length)
        {
            int size = Bin.U16(d, q), cond = Bin.U16(d, q + 2);
            int s0 = q + 4, e = size == 0 ? d.Length : Math.Min(d.Length, q + size);
            r.Add((cond, d[s0..e]));
            if (size == 0) break;
            q += size;
        }
        return r;
    }

    /// <summary>DefineButton2 mit ersetzten Aktionen neu kodieren (Zustände/Records bleiben unverändert).</summary>
    public static byte[] WriteButtonActions(byte[] d, List<(int cond, byte[] code)> actions)
    {
        ReadButtonActions(d, out int actStart);
        if (actStart >= d.Length && actions.Count > 0) throw new InvalidOperationException("button has no action section");
        var o = new List<byte>(d[..actStart]);
        for (int i = 0; i < actions.Count; i++)
        {
            var (cond, code) = actions[i];
            int size = i == actions.Count - 1 ? 0 : 4 + code.Length;
            if (size > 0xFFFF) throw new InvalidOperationException("button action too large");
            o.Add((byte)size); o.Add((byte)(size >> 8)); o.Add((byte)cond); o.Add((byte)(cond >> 8));
            o.AddRange(code);
        }
        return o.ToArray();
    }
}
