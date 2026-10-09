namespace FrameCastStudio.Linux.ViewModels;

/// <summary>Marque une propriété du ViewModel comme réglage à sauvegarder dans settings.json.</summary>
[AttributeUsage(AttributeTargets.Property)]
public sealed class PersistAttribute : Attribute { }
