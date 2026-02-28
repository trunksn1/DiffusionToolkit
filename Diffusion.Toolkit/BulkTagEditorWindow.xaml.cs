using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using Diffusion.Toolkit.Classes;
using Diffusion.Toolkit.Models;
using Diffusion.Toolkit.Services;

namespace Diffusion.Toolkit
{
    public partial class BulkTagEditorWindow : BorderlessWindow
    {
        private readonly BulkTagEditorModel _model;
        private readonly List<int> _imageIds;

        public BulkTagEditorWindow(List<int> imageIds)
        {
            _imageIds = imageIds;
            _model = new BulkTagEditorModel();

            InitializeComponent();

            _model.StatusText = $"Editing tags for {imageIds.Count} image{(imageIds.Count != 1 ? "s" : "")}";
            _model.Tags = ServiceLocator.TagService.GetBulkImageTagViews(imageIds);
            _model.EscapeCommand = new RelayCommand<object>(o => Escape());
            _model.AddTagCommand = new RelayCommand<object>(o => AddNewTag());

            SubscribeToTagChanges();

            DataContext = _model;
        }

        private void SubscribeToTagChanges()
        {
            foreach (var tag in _model.Tags)
                tag.PropertyChanged += OnTagPropertyChanged;
        }

        private void OnTagPropertyChanged(object sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(BulkImageTagView.IsChecked))
                _model.HasChanges = _model.Tags.Any(t => t.IsChecked != t.OriginalState);
        }

        private void AddNewTag()
        {
            var name = _model.NewTagText?.Trim();
            if (string.IsNullOrEmpty(name)) return;

            if (_model.Tags.Any(t => t.Name.Equals(name, System.StringComparison.OrdinalIgnoreCase)))
            {
                return;
            }

            ServiceLocator.DataStore.CreateTag(name);

            var allTags = ServiceLocator.DataStore.GetTags();
            var newTag = allTags.FirstOrDefault(t => t.Name.Equals(name, System.StringComparison.OrdinalIgnoreCase));
            if (newTag != null)
            {
                var view = new BulkImageTagView
                {
                    Id = newTag.Id,
                    Name = newTag.Name,
                    IsChecked = false,
                    OriginalState = false,
                    IsReadOnly = false,
                };
                view.PropertyChanged += OnTagPropertyChanged;
                _model.Tags.Add(view);
            }

            _model.NewTagText = string.Empty;
        }

        private void Escape()
        {
            DialogResult = false;
            Close();
        }

        private void OK_OnClick(object sender, RoutedEventArgs e)
        {
            ApplyChanges();
            DialogResult = true;
            Close();
        }

        private void Cancel_OnClick(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }

        private void NewTagTextBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                AddNewTag();
                e.Handled = true;
            }
        }

        private void ApplyChanges()
        {
            foreach (var tag in _model.Tags)
            {
                if (tag.IsChecked == tag.OriginalState) continue;

                if (tag.IsChecked == true)
                {
                    ServiceLocator.DataStore.AddImagesTag(_imageIds, tag.Id);
                }
                else if (tag.IsChecked == false)
                {
                    ServiceLocator.DataStore.RemoveImagesTag(_imageIds, tag.Id);
                }
            }

            ServiceLocator.TagService.LoadTags?.Invoke();
            ServiceLocator.TagService.RefreshTagIcons?.Invoke(_imageIds);
        }
    }
}
