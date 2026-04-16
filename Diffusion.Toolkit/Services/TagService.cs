using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using Diffusion.Toolkit.Models;

namespace Diffusion.Toolkit.Services;

public class TagService
{
    public Action LoadTags;

    /// <summary>
    /// Called after tag assignments or icon changes to update visible thumbnails.
    /// Accepts a list of affected image IDs (null = refresh all visible).
    /// </summary>
    public Action<IEnumerable<int>?> RefreshTagIcons;

    public void CreateTag(string name)
    {
        ServiceLocator.DataStore.CreateTag(name);
    }

    public void UpdateTag(int id, string name)
    {
        ServiceLocator.DataStore.UpdateTag(id, name);
    }

    public void RemoveTag(int id, string name)
    {
        ServiceLocator.DataStore.UpdateTag(id, name);
    }

    public void UpdateTagIcon(int id, string? icon)
    {
        ServiceLocator.DataStore.UpdateTagIcon(id, icon);
        TagIconCache.RefreshTagMappings();
        RefreshTagIcons?.Invoke(null); // null = refresh all visible
    }

    public ObservableCollection<TagFilterView> GetTagFilterViews()
    {
        var allTags = ServiceLocator.DataStore.GetTagsWithCount();

        return new ObservableCollection<TagFilterView>(allTags.Select(d => new TagFilterView()
        {
            Id = d.Id,
            Name = d.Name,
            TagCount = d.Count
        }));
    }

    public ObservableCollection<BulkImageTagView> GetBulkImageTagViews(IEnumerable<int> imageIds)
    {
        var idList = imageIds.ToList();
        var imageCount = idList.Count;
        var allTags = ServiceLocator.DataStore.GetTags();
        var tagCounts = ServiceLocator.DataStore.GetTagCountsForImages(idList);

        return new ObservableCollection<BulkImageTagView>(allTags.Select(tag =>
        {
            tagCounts.TryGetValue(tag.Id, out var count);

            bool? isChecked;
            bool isReadOnly;

            if (count == imageCount)
            {
                isChecked = true;
                isReadOnly = false;
            }
            else if (count > 0)
            {
                isChecked = null;
                isReadOnly = false;
            }
            else
            {
                isChecked = false;
                isReadOnly = false;
            }

            return new BulkImageTagView
            {
                Id = tag.Id,
                Name = tag.Name,
                IsChecked = isChecked,
                OriginalState = isChecked,
                IsReadOnly = isReadOnly,
                IconPreview = TagIconCache.ResolveIcon(tag.Icon),
            };
        }));
    }

    public IReadOnlyCollection<ImageTagView> GetImageTagViews(int imageModelId)
    {
        var allTags = ServiceLocator.DataStore.GetTags();
        var imageTags = ServiceLocator.DataStore.GetImageTags(imageModelId).ToHashSet();

        return allTags.Select(d =>
        {
            var imageTag = new ImageTagView()
            {
                Id = d.Id,
                IsTicked = imageTags.Contains(d.Id),
                Name = d.Name,
            };

            imageTag.PropertyChanged += (sender, args) =>
            {
                if (ServiceLocator.MainModel.SelectedImages.Count > 1)
                {
                    if (args.PropertyName == nameof(ImageTagView.IsTicked))
                    {
                        var ids = ServiceLocator.MainModel.SelectedImages.Select(d => d.Id).ToList();

                        if (imageTag.IsTicked)
                        {
                            ServiceLocator.DataStore.AddImagesTag(ids, imageTag.Id);
                        }
                        else
                        {
                            ServiceLocator.DataStore.RemoveImagesTag(ids, imageTag.Id);
                        }

                        UpdateSidebarTagCount(imageTag.Id);
                        RefreshTagIcons?.Invoke(ids);
                    }
                }
                else
                {
                    if (args.PropertyName == nameof(ImageTagView.IsTicked))
                    {
                        if (imageTag.IsTicked)
                        {
                            ServiceLocator.DataStore.AddImageTag(imageModelId, imageTag.Id);
                        }
                        else
                        {
                            ServiceLocator.DataStore.RemoveImageTag(imageModelId, imageTag.Id);
                        }

                        UpdateSidebarTagCount(imageTag.Id);
                        RefreshTagIcons?.Invoke(new[] { imageModelId });
                    }
                }

            };

            return imageTag;
        }).ToList();
    }

    private void UpdateSidebarTagCount(int tagId)
    {
        var sidebarTag = ServiceLocator.MainModel.Tags?.FirstOrDefault(t => t.Id == tagId);
        if (sidebarTag == null) return;

        sidebarTag.TagCount = ServiceLocator.DataStore.GetTagCount(tagId);
    }
}