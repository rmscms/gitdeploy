using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using GitDeployPro.Models;

namespace GitDeployPro.Services
{
    public static class BackupHistoryStore
    {
        private const int MaxEntries = 200;
        public static event Action? HistoryChanged;

        public static ObservableCollection<BackupHistoryEntry> LoadHistory()
        {
            var state = BackupStateStore.LoadState();
            var items = state.BackupHistory ?? new List<BackupHistoryEntry>();
            var ordered = items
                .OrderByDescending(h => h.StartedUtc)
                .ToList();
            return new ObservableCollection<BackupHistoryEntry>(ordered);
        }

        public static void AddEntry(BackupHistoryEntry entry)
        {
            if (entry == null) return;

            var state = BackupStateStore.LoadState();
            state.BackupHistory ??= new List<BackupHistoryEntry>();
            state.BackupHistory.Insert(0, entry);
            while (state.BackupHistory.Count > MaxEntries)
            {
                state.BackupHistory.RemoveAt(state.BackupHistory.Count - 1);
            }
            BackupStateStore.SaveState(state);

            HistoryChanged?.Invoke();
        }
    }
}

