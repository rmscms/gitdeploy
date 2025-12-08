using System;

namespace GitDeployPro.Models
{
    public class DatabaseCharsetInfo
    {
        public string Name { get; init; } = string.Empty;
        public string DefaultCollation { get; init; } = string.Empty;
        public string Description { get; init; } = string.Empty;

        public string DisplayLabel => string.IsNullOrWhiteSpace(Description)
            ? Name
            : $"{Name} · {Description}";

        public override string ToString() => DisplayLabel;

        public static DatabaseCharsetInfo Create(string name, string defaultCollation, string description) =>
            new()
            {
                Name = name,
                DefaultCollation = defaultCollation,
                Description = description
            };
    }
}


