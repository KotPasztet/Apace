using System;
using System.ComponentModel;
using System.IO;

namespace MCEPatcher.UI.Utils;

// from: https://stackoverflow.com/a/3710257/15878562
public class StringWriterExt : StringWriter
{
    public class OnWriteEventArgs : EventArgs
    {
        public readonly string? Value;

        public OnWriteEventArgs(string? _value)
        {
            Value = _value;
        }
    }

    [EditorBrowsable(EditorBrowsableState.Never)]
    public delegate void OnWriteEventHandler(object sender, OnWriteEventArgs args);
    public event OnWriteEventHandler? OnWrite;

    public StringWriterExt()
        : base()
    {
    }

    protected void InvokeOnWrite(string? value)
    {
        OnWrite?.Invoke(this, new OnWriteEventArgs(value));
    }

    public override void Write(char value)
    {
        base.Write(value);
        InvokeOnWrite(value.ToString());
    }

    public override void Write(string? value)
    {
        base.Write(value);
        InvokeOnWrite(value);
    }

    public override void Write(char[] buffer, int index, int count)
    {
        base.Write(buffer, index, count);
        InvokeOnWrite(new string(buffer, index, count));
    }
}
