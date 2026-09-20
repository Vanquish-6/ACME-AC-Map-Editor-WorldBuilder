using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using WorldBuilder.Shared.Documents;
using WorldBuilder.ViewModels;

namespace WorldBuilder.Editors.Dungeon {
    public partial class DungeonHistoryPanelViewModel : ViewModelBase {
        private readonly DungeonCommandHistory _commandHistory;
        private readonly Func<DungeonDocument?> _getDocument;
        private readonly Action _refreshRendering;
        private bool _isUpdatingSelection;

        [ObservableProperty] private ObservableCollection<HistoryListItem> _historyItems = new();
        [ObservableProperty] private HistoryListItem? _selectedHistory;
        [ObservableProperty] private bool _canRevert;

        public DungeonHistoryPanelViewModel(
            DungeonCommandHistory commandHistory,
            Func<DungeonDocument?> getDocument,
            Action refreshRendering) {
            _commandHistory = commandHistory;
            _getDocument = getDocument;
            _refreshRendering = refreshRendering;

            _commandHistory.Changed += OnHistoryChanged;
            UpdateHistoryList();
        }

        private void OnHistoryChanged(object? sender, EventArgs e) {
            UpdateHistoryList();
        }

        private void UpdateHistoryList() {
            var newItems = _commandHistory.GetHistoryList();
            UpdateCollection(HistoryItems, newItems);

            _isUpdatingSelection = true;
            try {
                SelectedHistory = HistoryItems.FirstOrDefault(i => i.IsCurrent)
                                  ?? HistoryItems.FirstOrDefault(i => i.Index == -1);
            }
            finally {
                _isUpdatingSelection = false;
            }

            UpdateCanRevert();
        }

        private void UpdateCollection(ObservableCollection<HistoryListItem> current, List<HistoryListItem> incoming) {
            for (int i = current.Count - 1; i >= 0; i--) {
                if (!incoming.Any(n => n.Index == current[i].Index))
                    current.RemoveAt(i);
            }

            for (int i = 0; i < incoming.Count; i++) {
                var item = incoming[i];
                if (i < current.Count && current[i].Index == item.Index) {
                    current[i].Description = item.Description;
                    current[i].Timestamp = item.Timestamp;
                    current[i].IsCurrent = item.IsCurrent;
                    current[i].IsDimmed = item.IsDimmed;
                }
                else {
                    current.Insert(i, item);
                }
            }

            while (current.Count > incoming.Count)
                current.RemoveAt(current.Count - 1);
        }

        private void UpdateCanRevert() {
            CanRevert = SelectedHistory != null && !SelectedHistory.IsCurrent;
        }

        [RelayCommand]
        private void SelectHistoryItem(HistoryListItem? item) {
            if (item == null || _isUpdatingSelection) return;
            var doc = _getDocument();
            if (doc == null) return;

            _isUpdatingSelection = true;
            try {
                SelectedHistory = item;
                _commandHistory.JumpToHistory(item.Index, doc);
                _refreshRendering();
                UpdateCanRevert();
            }
            finally {
                _isUpdatingSelection = false;
            }
        }

        [RelayCommand]
        private void RevertToState(HistoryListItem? item) {
            SelectHistoryItem(item);
        }

        public void Dispose() {
            _commandHistory.Changed -= OnHistoryChanged;
        }
    }
}
