using System.Text;

namespace MCEPatcher.Core;

public static class HexDump
{
    public const int BytesPerLine = 16;
    public const int BytesPerLineHalf = BytesPerLine / 2;

    public static string Create(byte[] bytes)
    {
        StringBuilder sb = new StringBuilder();

        // TODO: only use getByte in last line
        int i;
        for (i = 0; i < bytes.Length; i += BytesPerLine)
        {
            // offset
            sb.Append(i.ToString("x").PadLeft(8, '0')).Append("  ");

            // bytes
            for (int j = 0; j < BytesPerLineHalf; j++)
            {
                sb.Append(GetByte(i + j)).Append(" ");
            }

            sb.Append(" ");

            for (int j = 0; j < BytesPerLineHalf; j++)
            {
                sb.Append(GetByte(i + j + BytesPerLineHalf)).Append(" ");
            }

            // text
            sb.Append(" |");
            for (int j = 0; j < BytesPerLine; j++)
            {
                sb.Append(GetChar(i + j));
            }

            sb.AppendLine("|");
        }

        sb.AppendLine(i.ToString("x").PadLeft(8, '0'));

        return sb.ToString();

        string GetByte(int index)
        {
            if (index < bytes.Length)
            {
                return bytes[index].ToString("x2");
            }
            else
            {
                return "  ";
            }
        }

        string GetChar(int index)
        {
            if (index < bytes.Length)
            {
                var c = (char)bytes[index];

                if (c is ' ')
                {
                    return " ";
                }
                else if (char.IsControl(c) || c > 0b_0111_1111)
                {
                    return ".";
                }
                else
                {
                    return
                    c.ToString();
                }
            }
            else
            {
                return string.Empty;
            }
        }
    }

    public static byte[] Undo(string hexdump)
    {
        var newLine = U.GetNewLine(hexdump);
        var lines = hexdump.Split(newLine);

        var bytes = new byte[(lines.Length - 3) * BytesPerLine];

        var done = 0;
        Parallel.For(0, lines.Length - 3, new ParallelOptions()
        {
            MaxDegreeOfParallelism = Environment.ProcessorCount
        }, i =>
        {
            var line = lines[i];

            var index = i * BytesPerLine;

            var split = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);

            for (var j = 0; j < BytesPerLine; j++)
            {
                try
                {
                    bytes[index + j] = Convert.ToByte(split[j + 1], 16);
                }
                catch
                {
                    break;
                }
            }

            done++;
        });

        var end = new List<byte>();
        for (var i = lines.Length - 3; i < lines.Length; i++)
        {
            var line = lines[i];

            if (string.IsNullOrEmpty(line))
            {
                continue;
            }

            var split = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);

            if (split.Length < 3)
            {
                continue;
            }

            for (var j = 1; j < split.Length - 1; j++)
            {
                try
                {
                    end.Add(Convert.ToByte(split[j], 16));
                }
                catch
                {
                    break;
                }
            }
        }

        if (end.Count is 0)
        {
            return bytes;
        }

        var ogLength = bytes.Length;
        Array.Resize(ref bytes, bytes.Length + end.Count);
        for (var i = 0; i < end.Count; i++)
        {
            bytes[i + ogLength] = end[i];
        }

        return bytes;
    }
}
