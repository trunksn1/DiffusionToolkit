using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using Diffusion.Toolkit.Classes;
using Diffusion.Toolkit.Models;
using Diffusion.Toolkit.Services;
using Microsoft.Win32;

namespace Diffusion.Toolkit
{
    public partial class TagManagerWindow : BorderlessWindow
    {
        private readonly TagManagerModel _model;

        public TagManagerWindow()
        {
            _model = new TagManagerModel();
            _model.Escape = new RelayCommand<object>(o => Close());

            InitializeComponent();
            DataContext = _model;

            LoadTags();
            LoadBuiltinIcons();
        }

        private void LoadTags()
        {
            var tags = ServiceLocator.DataStore!.GetTagsWithCount();
            _model.Tags = new ObservableCollection<TagManagerItemView>(tags.Select(t => new TagManagerItemView
            {
                Id = t.Id,
                Name = t.Name,
                Icon = t.Icon,
                ImageCount = t.Count,
                IconPreview = TagIconCache.ResolveIcon(t.Icon),
            }));
        }

        private void LoadBuiltinIcons()
        {
            _model.BuiltinIcons = new ObservableCollection<BuiltinIconItem>(
                TagIconCache.GetAllBuiltinIcons().Select(kvp => new BuiltinIconItem
                {
                    Key = kvp.Key,
                    Preview = kvp.Value,
                    IsSelected = false,
                }));
        }

        private void TagList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            UpdateBuiltinSelection();
            UpdateEmojiField();
            UpdateColorField();
            UpdateNoIconText();
        }

        private void UpdateBuiltinSelection()
        {
            var selectedTag = _model.SelectedTag;
            foreach (var item in _model.BuiltinIcons)
            {
                item.IsSelected = selectedTag?.Icon == $"builtin:{item.Key}";
            }
        }

        private void UpdateEmojiField()
        {
            var selectedTag = _model.SelectedTag;
            if (selectedTag?.Icon != null && selectedTag.Icon.StartsWith("emoji:"))
            {
                _model.EmojiText = selectedTag.Icon.Substring(6);
            }
            else
            {
                _model.EmojiText = "";
            }
        }

        private void UpdateColorField()
        {
            _model.SelectedColor = GetCurrentColor() ?? "#FFFFFF";
        }

        private void UpdateNoIconText()
        {
            NoIconText.Visibility = _model.SelectedTag?.IconPreview == null
                ? Visibility.Visible
                : Visibility.Collapsed;
        }

        private void SetTagIcon(string? iconRef)
        {
            if (_model.SelectedTag == null) return;

            _model.SelectedTag.Icon = iconRef;
            _model.SelectedTag.IconPreview = TagIconCache.ResolveIcon(iconRef);
            UpdateNoIconText();
        }

        /// <summary>
        /// Gets the current icon ref with a color suffix appended/replaced.
        /// </summary>
        private string? ApplyColorToCurrentIcon(string color)
        {
            var icon = _model.SelectedTag?.Icon;
            if (string.IsNullOrEmpty(icon)) return null;

            // Strip any existing color suffix
            var stripped = StripColorSuffix(icon);
            // Don't append color if it's white (default)
            if (color == "#FFFFFF")
                return stripped;
            return $"{stripped}:{color}";
        }

        private string StripColorSuffix(string iconRef)
        {
            // Look for color suffix ":#RRGGBB" at the end
            var lastColon = iconRef.LastIndexOf(":#");
            if (lastColon >= 0 && iconRef.Length - lastColon >= 8)
                return iconRef.Substring(0, lastColon);
            return iconRef;
        }

        private string? GetCurrentColor()
        {
            var icon = _model.SelectedTag?.Icon;
            if (string.IsNullOrEmpty(icon)) return null;
            var lastColon = icon.LastIndexOf(":#");
            if (lastColon >= 0 && icon.Length - lastColon >= 8)
                return icon.Substring(lastColon + 1);
            return null;
        }

        private void BuiltinIcon_Click(object sender, RoutedEventArgs e)
        {
            if (_model.SelectedTag == null) return;

            var button = sender as System.Windows.Controls.Primitives.ToggleButton;
            var item = button?.DataContext as BuiltinIconItem;
            if (item == null) return;

            // Deselect all others
            foreach (var bi in _model.BuiltinIcons)
                bi.IsSelected = bi == item && item.IsSelected;

            if (item.IsSelected)
            {
                SetTagIcon($"builtin:{item.Key}");
                _model.EmojiText = "";
            }
            else
            {
                SetTagIcon(null);
            }
        }

        private void SetEmoji_Click(object sender, RoutedEventArgs e)
        {
            if (_model.SelectedTag == null) return;

            var emoji = _model.EmojiText?.Trim();
            if (string.IsNullOrEmpty(emoji)) return;

            // Deselect built-in icons
            foreach (var bi in _model.BuiltinIcons)
                bi.IsSelected = false;

            SetTagIcon($"emoji:{emoji}");
        }

        private void QuickEmoji_Click(object sender, RoutedEventArgs e)
        {
            if (_model.SelectedTag == null) return;

            var button = sender as Button;
            var emoji = button?.Tag as string;
            if (string.IsNullOrEmpty(emoji)) return;

            // Deselect built-in icons
            foreach (var bi in _model.BuiltinIcons)
                bi.IsSelected = false;

            _model.EmojiText = emoji;
            SetTagIcon($"emoji:{emoji}");
        }

        private void ColorSwatch_Click(object sender, RoutedEventArgs e)
        {
            if (_model.SelectedTag == null || string.IsNullOrEmpty(_model.SelectedTag.Icon)) return;

            var button = sender as Button;
            var color = button?.Tag as string;
            if (string.IsNullOrEmpty(color)) return;

            _model.SelectedColor = color;
            var newIcon = ApplyColorToCurrentIcon(color);
            SetTagIcon(newIcon);
        }

        private void ApplyCustomColor_Click(object sender, RoutedEventArgs e)
        {
            if (_model.SelectedTag == null || string.IsNullOrEmpty(_model.SelectedTag.Icon)) return;

            var color = _model.SelectedColor?.Trim();
            if (string.IsNullOrEmpty(color)) return;
            if (!color.StartsWith("#")) color = "#" + color;

            var newIcon = ApplyColorToCurrentIcon(color);
            SetTagIcon(newIcon);
        }

        private void BrowseIcon_Click(object sender, RoutedEventArgs e)
        {
            if (_model.SelectedTag == null) return;

            var dialog = new OpenFileDialog
            {
                Filter = "Image Files|*.png;*.jpg;*.jpeg;*.bmp|All Files|*.*",
                Title = "Select Tag Icon"
            };

            if (dialog.ShowDialog() == true)
            {
                // Deselect built-in icons
                foreach (var bi in _model.BuiltinIcons)
                    bi.IsSelected = false;
                _model.EmojiText = "";

                SetTagIcon($"file:{dialog.FileName}");
            }
        }

        private void ClearIcon_Click(object sender, RoutedEventArgs e)
        {
            if (_model.SelectedTag == null) return;

            foreach (var bi in _model.BuiltinIcons)
                bi.IsSelected = false;
            _model.EmojiText = "";

            SetTagIcon(null);
        }

        private void Save_Click(object sender, RoutedEventArgs e)
        {
            if (_model.SelectedTag == null) return;

            ServiceLocator.TagService.UpdateTagIcon(_model.SelectedTag.Id, _model.SelectedTag.Icon);

            // Refresh the preview in case cache changed
            _model.SelectedTag.IconPreview = TagIconCache.ResolveIcon(_model.SelectedTag.Icon);

            ServiceLocator.ToastService?.Toast("Tag icon saved", "");
        }
    }
}
