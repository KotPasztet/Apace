using System.Diagnostics.CodeAnalysis;
using System.Text.Json.Serialization;

namespace MCEPatcher.Core;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum PrerequisiteType
{
    Basic,
    Conditional,
}

[JsonPolymorphic(TypeDiscriminatorPropertyName = "Type")]
[JsonDerivedType(typeof(BasicPrerequisite), nameof(PrerequisiteType.Basic))]
[JsonDerivedType(typeof(ConditionalPrerequisite), nameof(PrerequisiteType.Conditional))]
public abstract class Prerequisite
{
    /// <summary>
    /// 
    /// </summary>
    /// <param name="patches">Patches to apply before this one</param>
    /// <returns>
    /// <see href="true"/> when the condition for this patch is satisfied<br></br>
    /// <see href="false"/> if not
    /// </returns>
    public abstract bool Check(Patcher.PatchContext context, [NotNullWhen(false)] out List<string>? patches);
}

public sealed class BasicPrerequisite : Prerequisite
{
    public string RequiredPatch { get; init; }

    public BasicPrerequisite()
        : this(string.Empty)
    {
    }

    public BasicPrerequisite(string requiredPatch)
    {
        RequiredPatch = requiredPatch;
    }

    public override bool Check(Patcher.PatchContext context, [NotNullWhen(false)] out List<string>? patches)
    {
        patches = null;

        if (context.AppliedPatches.ContainsKey(RequiredPatch))
        {
            return true;
        }

        patches =
        [
            RequiredPatch
        ];

        return false;
    }
}

/// <summary>
/// <see cref="RequiredPatch"/> is required if <see cref="VariableName"/> == <see cref="VariableValue"/>
/// </summary>
public sealed class ConditionalPrerequisite : Prerequisite
{
    public string RequiredPatch { get; init; }

    public string VariableName { get; init; }
    public string VariableValue { get; init; }
    public EqualityType? Equality { get; init; }

    public ConditionalPrerequisite()
        : this(string.Empty, string.Empty, string.Empty, EqualityType.Equal)
    {
    }

    public ConditionalPrerequisite(string requiredPatch, string variableName, string variableValue, EqualityType? equality)
    {
        RequiredPatch = requiredPatch;
        VariableName = variableName;
        VariableValue = variableValue;
        Equality = equality;
    }

    public override bool Check(Patcher.PatchContext context, [NotNullWhen(false)] out List<string>? patches)
    {
        patches = null;

        if (context.AppliedPatches.ContainsKey(RequiredPatch) || !context.Variables.TryGetValue(VariableName, out string? val))
        {
            return true;
        }

        if (!(Equality switch
        {
            EqualityType.NotEqual => val != VariableValue,
            EqualityType.Contains => val.Contains(VariableValue),
            EqualityType.StartsWith => val.StartsWith(VariableValue),
            EqualityType.EndsWith => val.EndsWith(VariableValue),
            EqualityType.Equal or _ => val == VariableValue,
        }))
        {
            return true;
        }

        patches =
        [
            RequiredPatch
        ];

        return false;
    }

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public enum EqualityType
    {
        Equal,
        NotEqual,
        Contains,
        StartsWith,
        EndsWith,
    }
}
