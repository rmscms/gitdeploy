using System;

namespace GitDeployPro.Services.Theme
{
    public enum ThemeSourceKind
    {
        BuiltInPack,
        JsonPack
    }

    public sealed class AppThemeInfo
    {
        public string Id { get; set; } = string.Empty;
        public string DisplayName { get; set; } = string.Empty;
        public ThemeSourceKind Source { get; set; } = ThemeSourceKind.BuiltInPack;
        public Uri? ResourceUri { get; set; }
        public string? PackPath { get; set; }
        public ThemePack? Pack { get; set; }
        public bool IsBuiltIn => Source == ThemeSourceKind.BuiltInPack;

        public override string ToString() => DisplayName;
    }
}
