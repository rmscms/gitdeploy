using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using GitDeployPro.Models;

namespace GitDeployPro.Services
{
    public static class TerminalPresetStore
    {
        private static readonly ConfigurationService _configService = new();

        public static event Action? PresetsChanged;

        public static ObservableCollection<TerminalCommandPreset> LoadPresets()
        {
            var config = _configService.LoadGlobalConfig();
            var list = config.TerminalPresets ?? new List<TerminalCommandPreset>();
            return new ObservableCollection<TerminalCommandPreset>(list.Select(ClonePreset));
        }

        public static void SavePresets(IEnumerable<TerminalCommandPreset> presets)
        {
            var snapshot = presets?.Select(ClonePreset).ToList() ?? new List<TerminalCommandPreset>();
            _configService.UpdateGlobalConfig(cfg =>
            {
                cfg.TerminalPresets = snapshot;
            });
            PresetsChanged?.Invoke();
        }

        private static TerminalCommandPreset ClonePreset(TerminalCommandPreset preset)
        {
            return new TerminalCommandPreset
            {
                Id = string.IsNullOrWhiteSpace(preset.Id) ? Guid.NewGuid().ToString() : preset.Id,
                Title = preset.Title ?? string.Empty,
                Command = preset.Command ?? string.Empty
            };
        }
    }
}

