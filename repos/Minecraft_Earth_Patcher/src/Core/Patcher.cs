using DiffPatch.Data;
using Serilog;
using System.Text;

namespace MCEPatcher.Core;

public sealed class Patcher
{
    private string patchesLoation;
    private string filesLocation;

    private HashSet<string> hexDumpFiles = new HashSet<string>();

    public Patcher(string _patchesLoation, string _filesLocation = "")
    {
        patchesLoation = _patchesLoation;
        filesLocation = _filesLocation;
    }

    public void Patch(IEnumerable<string> patchesNames, Dictionary<string, string> variables, Dictionary<string, PatchInfo>? appliedPatches = null)
    {
        var context = new PatchContext(appliedPatches ?? new(), variables);

        PatchAll(patchesNames, context);

        if (hexDumpFiles.Count is not 0)
        {
            Log.Information("Undoing hex dumps");
            foreach (var file in hexDumpFiles)
            {
                File.WriteAllBytes(file.Substring(0, file.LastIndexOf('.')), HexDump.Undo(File.ReadAllText(file)));

                // delete the original file
                File.Delete(file);
            }

            hexDumpFiles.Clear();
            Log.Debug("Done");
        }

        bool patchedVariables = false;

        var files = new Dictionary<string, byte[]>();
        foreach (var (name, info) in context.AppliedPatches)
        {
            if (info.BinaryVariables.Count is 0)
            {
                continue;
            }

            Log.Information($"Patching variables for patch '{name}'");
            patchedVariables = true;

            foreach (var variable in info.BinaryVariables)
            {
                var val = variable.TemplateString;

                foreach (var varName in info.VariablesUsed)
                {
                    if (context.Variables.TryGetValue(varName, out string? value))
                    {
                        val = val.Replace($"${{{varName}}}", value.ToLowerInvariant());
                    }
                    else
                    {
                        throw new Exception($"Variable '{varName}' doesn't exist");
                    }
                }

                var valBytes = Encoding.ASCII.GetBytes(val + "\0");
                var lengthBytes = BitConverter.GetBytes(valBytes.Length - 1);

                if (valBytes.Length > 8 * 16 - 4)
                {
                    throw new Exception($"String '{val}' is too long, max length allowed is: {8 * 16 - 4}");
                }

                var file = Path.Combine(filesLocation, variable.File);
                if (!files.TryGetValue(file, out byte[]? bytes))
                {
                    bytes = File.ReadAllBytes(file);
                    files.Add(file, bytes);
                }

                var index = variable.Address;

                // write length
                for (var i = 0; i < 4; i++)
                {
                    bytes[index++] = lengthBytes[i];
                }

                // write the string
                for (var i = 0; i < valBytes.Length; i++)
                {
                    bytes[index++] = valBytes[i];
                }
            }
        }
        if (patchedVariables)
        {
            Log.Debug("Done");
        }

        if (files.Count is not 0)
        {
            Log.Debug("Writing files");
            foreach (var (file, bytes) in files)
            {
                File.WriteAllBytes(file, bytes);
            }

            Log.Debug("Done");
        }
    }

    private void PatchAll(IEnumerable<string> patchNames, PatchContext context)
    {
        var patches = LoadPatches(patchNames);

        foreach (var (name, info) in patches)
        {
            if (context.AppliedPatches.ContainsKey(name))
            {
                continue;
            }

            Patch(name, info, context);
        }
    }

    private void Patch(string patchName, PatchInfo info, PatchContext context)
    {
        foreach (var pre in info.Prerequisites)
        {
            if (!pre.Check(context, out var patches))
            {
                PatchAll(patches, context); // TODO: this could cause infinite loop, add depth (limit)? or check for it in Patch?
            }
        }

        if (patchName == ExtendLibgenoa.Name)
        {
            Log.Information($"Applying patch '{patchName}'");
            ExtendLibgenoa.Extent(new DirectoryInfo(filesLocation));
            Log.Debug("Done");
            context.AppliedPatches.Add(patchName, info);
            return;
        }

        string path = Path.Combine(patchesLoation, $"{patchName}.patch");
        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"Patch '{patchName}' doesn't exist");
        }

        Log.Information($"Applying patch '{patchName}'");
        Patch(File.ReadAllText(path), patchName, context.Variables.Where(variable => info.VariablesUsed.Contains(variable.Key)).ToDictionary(item => item.Key, item => item.Value));
        Log.Debug($"Done");

        context.AppliedPatches.Add(patchName, info);
    }

    private void Patch(string patch, string patchName, Dictionary<string, string>? variables = null)
    {
        var newLine = U.GetNewLine(patch);
        var filesToPatch = Parse(patch);

        if (variables is not null && variables.Count is not 0)
        {
            var textPatches = filesToPatch.Where(filePatch => !filePatch.IsHex());
            var hexPatches = filesToPatch.Where(filePatch => filePatch.IsHex());

            var sb = new StringBuilder();

            foreach (var filePatch in textPatches)
            {
                string str = U.ToString(filePatch);

                foreach (var item in variables)
                {
                    var (name, value) = (item.Key, item.Value);

                    str = str.Replace($"${{{name}}}", value);
                }

                sb.Append(str);
            }

            foreach (var filePatch in hexPatches)
            {
                sb.Append(U.ToString(filePatch));
            }

            filesToPatch = Parse(sb.ToString());
        }

        foreach (var filePatch in filesToPatch)
        {
            PatchFile(filePatch, patchName);
        }
    }

    private void PatchFile(FileDiff patch, string patchName)
    {
        if (patch.From != patch.To)
        {
            throw new InvalidDataException($"patch.From ({patch.From}) must match patch.To ({patch.To})");
        }

        var file = new FileInfo(Path.Combine(filesLocation, patch.From));

        file.Directory?.Create();

        var hex = patch.IsHex();

        if (hex && !file.Exists)
        {
            // TODO: check if dumping libgenoa and if extend-libgenoa is needed, do it
            string _from = file.FullName.Substring(0, file.FullName.LastIndexOf('.')); // remove .hexdump
            Log.Information($"Creating hexdump for '{_from}'");
            File.WriteAllText(file.FullName, HexDump.Create(File.ReadAllBytes(_from)));
            Log.Debug("Done");
            file.Refresh();
        }

        if (!file.Exists)
        {
            throw new IOException($"File '{file.FullName}' doesn't exist, it is used by patch '{patchName}'");
        }

        List<string> lines = File.ReadAllLines(file.FullName).ToList();

        foreach (var chunk in patch.Chunks)
        {
            PatchChunk(chunk, lines);
        }

        File.WriteAllLines(file.FullName, lines);

        if (hex)
        {
            hexDumpFiles.Add(file.FullName);
        }
    }

    private void PatchChunk(Chunk chunk, List<string> lines)
    {
        ChunkRange range = chunk.RangeInfo.NewRange;

        int lineIndex = range.StartLine - 1;
        foreach (var change in chunk.Changes)
        {
            switch (change.Type)
            {
                case LineChangeType.Normal:
                    lineIndex++;
                    break;
                case LineChangeType.Add:
                    lines.Insert(lineIndex++, change.Content.Replace("\r", string.Empty).Replace("\n", string.Empty));
                    break;
                case LineChangeType.Delete:
                    lines.RemoveAt(lineIndex);
                    break;
                default:
                    throw new InvalidDataException($"Unknown {nameof(LineChangeType)}: '{change.Type}'");
            }
        }
    }

    private static Dictionary<string, PatchInfo> LoadPatches(IEnumerable<string> patchNames)
    {
        var patches = new Dictionary<string, PatchInfo>();

        foreach (string name in patchNames)
        {
            if (patches.ContainsKey(name))
            {
                continue;
            }

            string path = Path.Combine("Patches", $"{name}.patch.info");

            patches.Add(name, PatchInfo.Load(path, name));
        }

        return patches;
    }

    private static IEnumerable<FileDiff> Parse(string patch)
    {
        if (string.IsNullOrWhiteSpace(patch))
        {
            return [];
        }

        IEnumerable<string> enumerable = SplitLines(patch);
        if (!enumerable.Any())
        {
            return [];
        }

        return new DiffParser().Run(enumerable);

        IEnumerable<string> SplitLines(string input)
        {
            if (string.IsNullOrWhiteSpace(input))
            {
                return [];
            }

            string[] array = input.Split(["\r\n", "\n"], StringSplitOptions.None);
            if (array.Length is not 0)
            {
                return array;
            }

            return [];
        }
    }

    public sealed class PatchContext
    {
        public readonly Dictionary<string, PatchInfo> AppliedPatches;
        public readonly Dictionary<string, string> Variables;

        public PatchContext(Dictionary<string, PatchInfo> _appliedPatches, Dictionary<string, string> _variables)
        {
            AppliedPatches = _appliedPatches;
            Variables = _variables;
        }
    }
}
