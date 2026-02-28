using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Diffusion.Database.Models;
using Diffusion.Toolkit.Classes;
using Diffusion.Toolkit.Services;

namespace Diffusion.Toolkit
{
    public partial class PromptLibraryWindow : BorderlessWindow
    {
        private readonly PromptLibraryModel _model;

        public PromptLibraryWindow()
        {
            _model = new PromptLibraryModel();
            _model.Escape = new RelayCommand<object>(o => Close());

            InitializeComponent();
            DataContext = _model;

            RefreshTemplates();
            RefreshCategories();
        }

        private void RefreshTemplates()
        {
            var dataStore = ServiceLocator.DataStore!;
            List<PromptTemplate> templates;

            if (!string.IsNullOrWhiteSpace(_model.SearchQuery))
            {
                templates = dataStore.SearchPromptTemplates(_model.SearchQuery);
            }
            else if (!string.IsNullOrWhiteSpace(_model.SelectedCategory))
            {
                templates = dataStore.GetPromptTemplatesByCategory(_model.SelectedCategory);
            }
            else
            {
                templates = dataStore.GetAllPromptTemplates();
            }

            _model.Templates = new ObservableCollection<PromptTemplate>(templates);
        }

        private void RefreshCategories()
        {
            var categories = ServiceLocator.DataStore!.GetPromptTemplateCategories();
            _model.Categories = new ObservableCollection<string>(new[] { "" }.Concat(categories));
        }

        private void SearchBox_OnKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                RefreshTemplates();
            }
        }

        private void CategoryFilter_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            RefreshTemplates();
        }

        private void ClearFilter_OnClick(object sender, RoutedEventArgs e)
        {
            _model.SearchQuery = null;
            _model.SelectedCategory = null;
            RefreshTemplates();
        }

        private void NewTemplate_OnClick(object sender, RoutedEventArgs e)
        {
            var window = new SavePromptTemplateWindow();
            window.Owner = this;
            if (window.ShowDialog() == true)
            {
                RefreshTemplates();
                RefreshCategories();
            }
        }

        private void Templates_OnDoubleClick(object sender, MouseButtonEventArgs e)
        {
            CopyPromptToClipboard();
        }

        private void CopyPrompt_OnClick(object sender, RoutedEventArgs e)
        {
            CopyPromptToClipboard();
        }

        private void CopyPromptWithSettings_OnClick(object sender, RoutedEventArgs e)
        {
            if (_model.SelectedTemplate == null) return;

            var t = _model.SelectedTemplate;
            var text = t.Prompt ?? "";
            if (!string.IsNullOrEmpty(t.NegativePrompt))
                text += $"\nNegative prompt: {t.NegativePrompt}";
            if (t.Steps.HasValue)
                text += $"\nSteps: {t.Steps}";
            if (!string.IsNullOrEmpty(t.Sampler))
                text += $", Sampler: {t.Sampler}";
            if (t.CFGScale.HasValue)
                text += $", CFG scale: {t.CFGScale}";
            if (!string.IsNullOrEmpty(t.Model))
                text += $", Model: {t.Model}";

            Clipboard.SetText(text);
            ServiceLocator.DataStore!.IncrementTemplateUseCount(t.Id);
            ServiceLocator.ToastService.Toast("Prompt with settings copied to clipboard", "");
        }

        private void CopyPromptToClipboard()
        {
            if (_model.SelectedTemplate == null) return;

            var prompt = _model.SelectedTemplate.Prompt;
            if (!string.IsNullOrEmpty(prompt))
            {
                Clipboard.SetText(prompt);
                ServiceLocator.DataStore!.IncrementTemplateUseCount(_model.SelectedTemplate.Id);
                ServiceLocator.ToastService.Toast("Prompt copied to clipboard", "");
            }
        }

        private void EditTemplate_OnClick(object sender, RoutedEventArgs e)
        {
            if (_model.SelectedTemplate == null) return;

            var window = new SavePromptTemplateWindow(_model.SelectedTemplate);
            window.Owner = this;
            if (window.ShowDialog() == true)
            {
                RefreshTemplates();
                RefreshCategories();
            }
        }

        private void DeleteTemplate_OnClick(object sender, RoutedEventArgs e)
        {
            if (_model.SelectedTemplate == null) return;

            var result = MessageBox.Show(
                $"Delete template '{_model.SelectedTemplate.Name}'?",
                "Confirm Delete", MessageBoxButton.YesNo, MessageBoxImage.Question);

            if (result == MessageBoxResult.Yes)
            {
                ServiceLocator.DataStore!.DeletePromptTemplate(_model.SelectedTemplate.Id);
                RefreshTemplates();
            }
        }
    }
}
