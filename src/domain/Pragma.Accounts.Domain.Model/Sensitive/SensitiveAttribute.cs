namespace Pragma.Accounts.Domain.Model.Sensitive;

/// <summary>Clasificacion del dato sensible.</summary>
public enum SensitiveClassification
{
    Pii,
    Financial,
    Secret,
}

/// <summary>Estrategia de enmascaramiento aplicada en logs y respuestas.</summary>
public enum MaskingStrategy
{
    Full,
    Email,
    LastFour,
}

/// <summary>Marca una propiedad como dato sensible.</summary>
[AttributeUsage(AttributeTargets.Property, AllowMultiple = false, Inherited = true)]
public sealed class SensitiveAttribute(SensitiveClassification classification, MaskingStrategy strategy) : Attribute
{
    public SensitiveClassification Classification { get; } = classification;

    public MaskingStrategy Strategy { get; } = strategy;
}
