using Diffusion.Database;
using System.Windows.Controls;
using System.Windows;
using SQLite;
using System.Collections.Generic;
using System.Linq;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using Diffusion.Toolkit.Classes;
using Diffusion.Toolkit.Models;
using Diffusion.Toolkit.Services;
using Diffusion.Database.Models;

namespace Diffusion.Toolkit
{
    public partial class MainWindow
    {
        private void InitTags()
        {
            ServiceLocator.TagService.LoadTags = LoadTags;
            ServiceLocator.TagService.RefreshTagIcons = RefreshTagIcons;

            _model.CreateTagCommand = new AsyncCommand<object>(async (o) =>
            {
                var title = GetLocalizedText("Actions.Tags.Create.Title");

                var (result, name) = await ServiceLocator.MessageService.ShowInput(GetLocalizedText("Actions.Tags.Create.Message"), title);
                
                if (result == PopupResult.OK)
                {
                    name = name.Trim();

                    if (string.IsNullOrWhiteSpace(name))
                    {
                        await ServiceLocator.MessageService.Show(GetLocalizedText("Actions.Tags.CannotBeEmpty.Message"), title, PopupButtons.OK);
                        return;
                    }

                    ServiceLocator.TagService.CreateTag(name);

                    LoadTags();
                }
            });

          
            _model.RenameTagCommand = new AsyncCommand<TagFilterView>(async (tag) =>
            {
                var title = GetLocalizedText("Actions.Tags.Rename.Title");

                var (result, name) = await ServiceLocator.MessageService.ShowInput(GetLocalizedText("Actions.Tags.Rename.Message"), title, tag.Name);

                name = name.Trim();

                if (result == PopupResult.OK)
                {
                    if (string.IsNullOrWhiteSpace(name))
                    {
                        await ServiceLocator.MessageService.Show(GetLocalizedText("Actions.Tags.CannotBeEmpty.Message"), title, PopupButtons.OK);
                        return;
                    }

                    ServiceLocator.TagService.UpdateTag(tag.Id, name);

                    UpdateTagName(tag.Id, name);

                    LoadTags();
                }
            });

            _model.RemoveTagCommand = new AsyncCommand<TagFilterView>(async (tag) =>
            {
                var title = GetLocalizedText("Actions.Tags.Remove.Title");

                var result = await _messagePopupManager.Show(GetLocalizedText("Actions.Tags.Remove.Message").Replace("{tag}", tag.Name), title, PopupButtons.YesNo);

                if (result == PopupResult.Yes)
                {
                    _dataStore.RemoveTag(tag.Id);

                    if (_search.QueryOptions.TagIds is { Count: > 0 })
                    {
                        if (_search.QueryOptions.TagIds.Contains(tag.Id))
                        {
                            _search.QueryOptions.TagIds = _search.QueryOptions.TagIds.Except(new[] { tag.Id }).ToList();
                        }
                    }

                    LoadTags();

                    // TODO: Detect whether we need to refresh?
                    _search.ReloadMatches(null);
                }
            });


        }

        private void LoadTags()
        {
            _model.Tags = ServiceLocator.TagService.GetTagFilterViews();
        }

        public void UpdateTagName(int id, string name)
        {
            _model.Tags.First(d => d.Id == id).Name = name;
        }

        /// <summary>
        /// Refreshes TagIconIds on visible thumbnails so tag icons update immediately.
        /// If imageIds is null, refreshes all visible images (e.g. when a tag icon definition changes).
        /// If imageIds is specified, refreshes only those images (e.g. when tags are assigned/removed).
        /// </summary>
        private void RefreshTagIcons(IEnumerable<int>? imageIds)
        {
            Dispatcher.Invoke(() =>
            {
                // Collect all visible ImageEntry objects from search and prompts pages
                var visibleEntries = new List<ImageEntry>();

                if (_search?.Images != null)
                    visibleEntries.AddRange(_search.Images.Where(e => e.Id > 0));

                if (_prompts?.PromptsResultImages != null)
                    visibleEntries.AddRange(_prompts.PromptsResultImages.Where(e => e.Id > 0));

                if (visibleEntries.Count == 0) return;

                IEnumerable<ImageEntry> entriesToRefresh;

                if (imageIds != null)
                {
                    var idSet = new HashSet<int>(imageIds);
                    entriesToRefresh = visibleEntries.Where(e => idSet.Contains(e.Id));
                }
                else
                {
                    entriesToRefresh = visibleEntries;
                }

                var entriesToRefreshList = entriesToRefresh.ToList();
                if (entriesToRefreshList.Count == 0) return;

                var entryIds = entriesToRefreshList.Select(e => e.Id).Distinct().ToList();
                var updatedTagIconIds = _dataStore.GetTagIconIdsForImages(entryIds);

                // When imageIds is null, icon definitions changed (not tag assignments).
                // TagIconIds won't change (same tag IDs), so SetField won't fire PropertyChanged.
                // Force re-render by nulling first, then setting the real value.
                var forceRerender = imageIds == null;

                foreach (var entry in entriesToRefreshList)
                {
                    var newValue = updatedTagIconIds.TryGetValue(entry.Id, out var val) ? val : null;

                    if (forceRerender && entry.TagIconIds == newValue)
                    {
                        // Force PropertyChanged by setting null first
                        entry.TagIconIds = null;
                    }
                    entry.TagIconIds = newValue;
                }
            });
        }

    }
}