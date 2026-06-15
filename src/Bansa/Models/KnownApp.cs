namespace Bansa.Models;

/// <summary>An app Bansa knows from history, with a launchable path when one is known.</summary>
public sealed record KnownApp(string Name, string? Path)
{
    public bool HasPath => !string.IsNullOrEmpty(Path);

    public string Display => HasPath ? Name : $"{Name}  (locate…)";
}
