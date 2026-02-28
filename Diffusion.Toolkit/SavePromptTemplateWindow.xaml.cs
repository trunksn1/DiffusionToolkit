using System.Collections.ObjectModel;
using System.Windows;
using Diffusion.Database.Models;
using Diffusion.Toolkit.Classes;
using Diffusion.Toolkit.Services;

namespace Diffusion.Toolkit
{
    public partial class SavePromptTemplateWindow : BorderlessWindow
    {
        private readonly SavePromptTemplateModel _model;

        public SavePromptTemplateWindow(Image? image = null)
        {
            _model = new SavePromptTemplateModel();
            _model.Escape = new RelayCommand<object>(o => Close());

            if (image != null)
            {
                _model.Prompt = image.Prompt;
                _model.NegativePrompt = image.NegativePrompt;
                _model.Model = image.Model;
                _model.Sampler = image.Sampler;
                _model.CFGScale = image.CFGScale;
                _model.Steps = image.Steps;
                _model.Name = $"Template from {System.IO.Path.GetFileNameWithoutExtension(image.FileName)}";
            }

            var categories = ServiceLocator.DataStore!.GetPromptTemplateCategories();
            _model.Categories = new ObservableCollection<string>(categories);

            InitializeComponent();
            DataContext = _model;
        }

        public SavePromptTemplateWindow(PromptTemplate template)
        {
            _model = new SavePromptTemplateModel();
            _model.Escape = new RelayCommand<object>(o => Close());

            _model.Name = template.Name;
            _model.Prompt = template.Prompt;
            _model.NegativePrompt = template.NegativePrompt;
            _model.Category = template.Category;
            _model.Notes = template.Notes;
            _model.Model = template.Model;
            _model.Sampler = template.Sampler;
            _model.CFGScale = template.CFGScale;
            _model.Steps = template.Steps;

            var categories = ServiceLocator.DataStore!.GetPromptTemplateCategories();
            _model.Categories = new ObservableCollection<string>(categories);

            InitializeComponent();
            DataContext = _model;

            Title = "Edit Prompt Template";
            EditingTemplateId = template.Id;
        }

        public int? EditingTemplateId { get; set; }

        private void Save_OnClick(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(_model.Name))
            {
                MessageBox.Show("Please enter a template name.", "Validation", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var template = new PromptTemplate
            {
                Name = _model.Name,
                Prompt = _model.Prompt,
                NegativePrompt = _model.NegativePrompt,
                Category = _model.Category,
                Notes = _model.Notes,
                Model = _model.Model,
                Sampler = _model.Sampler,
                CFGScale = _model.CFGScale,
                Steps = _model.Steps
            };

            if (EditingTemplateId.HasValue)
            {
                template.Id = EditingTemplateId.Value;
                ServiceLocator.DataStore!.UpdatePromptTemplate(template);
            }
            else
            {
                ServiceLocator.DataStore!.CreatePromptTemplate(template);
            }

            DialogResult = true;
            Close();
        }

        private void Cancel_OnClick(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }
    }
}
