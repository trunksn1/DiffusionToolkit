using System.Collections.Generic;
using System.Windows;
using Diffusion.Toolkit.Classes;
using Diffusion.Toolkit.Models;

namespace Diffusion.Toolkit.Windows
{
    public class UserMetadataEditModel : BaseNotify
    {
        private string _key;
        private string? _value;

        public string Key
        {
            get => _key;
            set => SetField(ref _key, value);
        }

        public string? Value
        {
            get => _value;
            set => SetField(ref _value, value);
        }

        public IEnumerable<string> SuggestedKeys { get; set; } = new[]
        {
            "Prompt", "Negative Prompt", "Steps", "Sampler", "CFG Scale", "Seed", "Model", "Size", "Clip Skip"
        };

        public System.Windows.Input.ICommand Escape { get; set; }
    }

    /// <summary>
    /// Small modal editor for adding or editing a single user-metadata overlay entry.
    /// Returns the entered Key/Value via <see cref="ResultKey"/> / <see cref="ResultValue"/>.
    /// </summary>
    public partial class UserMetadataEditWindow : BorderlessWindow
    {
        private readonly UserMetadataEditModel _model;

        public string ResultKey { get; private set; }
        public string? ResultValue { get; private set; }

        public UserMetadataEditWindow(string? key = null, string? value = null)
        {
            _model = new UserMetadataEditModel
            {
                Key = key ?? string.Empty,
                Value = value
            };

            InitializeComponent();

            _model.Escape = new RelayCommand<object>(o => Cancel());
            DataContext = _model;
        }

        private void Ok_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(_model.Key))
            {
                return;
            }

            ResultKey = _model.Key.Trim();
            ResultValue = _model.Value;
            DialogResult = true;
            Close();
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            Cancel();
        }

        private void Cancel()
        {
            DialogResult = false;
            Close();
        }
    }
}
