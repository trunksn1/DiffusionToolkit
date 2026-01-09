using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using Diffusion.Database;
using Diffusion.Toolkit.Models;

namespace Diffusion.Toolkit
{
    public partial class CivitaiAlbumSelectionWindow : BorderlessWindow
    {
        public string? SelectedAlbumName { get; private set; }
        public bool RememberChoice { get; private set; }

        public CivitaiAlbumSelectionWindow(ObservableCollection<AlbumModel> albums, int imageCount)
        {
            InitializeComponent();

            ImageCountText.Text = $"{imageCount} new image(s) downloaded.";
            AlbumsListBox.ItemsSource = albums;
        }

        private void OkButton_Click(object sender, RoutedEventArgs e)
        {
            // Check which option was selected
            if (NewAlbumRadioButton.IsChecked == true)
            {
                if (string.IsNullOrWhiteSpace(NewAlbumTextBox.Text))
                {
                    MessageBox.Show("Please enter a name for the new album.", "Validation", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
                SelectedAlbumName = NewAlbumTextBox.Text.Trim();
            }
            else if (SkipRadioButton.IsChecked != true)
            {
                // One of the existing album radio buttons is checked
                var selectedRadioButton = FindCheckedRadioButton(AlbumsListBox);
                if (selectedRadioButton != null)
                {
                    SelectedAlbumName = selectedRadioButton.Tag as string;
                }
            }
            else
            {
                // Skip was selected
                SelectedAlbumName = null;
            }

            RememberChoice = RememberCheckBox.IsChecked == true;
            DialogResult = true;
            Close();
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }

        private void NewAlbumTextBox_GotFocus(object sender, RoutedEventArgs e)
        {
            NewAlbumRadioButton.IsChecked = true;
        }

        private RadioButton? FindCheckedRadioButton(ItemsControl itemsControl)
        {
            foreach (var item in itemsControl.Items)
            {
                var container = itemsControl.ItemContainerGenerator.ContainerFromItem(item) as FrameworkElement;
                if (container != null)
                {
                    var radioButton = FindVisualChild<RadioButton>(container);
                    if (radioButton?.IsChecked == true)
                    {
                        return radioButton;
                    }
                }
            }
            return null;
        }

        private static T? FindVisualChild<T>(DependencyObject parent) where T : DependencyObject
        {
            for (int i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(parent); i++)
            {
                var child = System.Windows.Media.VisualTreeHelper.GetChild(parent, i);
                if (child is T result)
                {
                    return result;
                }
                var childOfChild = FindVisualChild<T>(child);
                if (childOfChild != null)
                {
                    return childOfChild;
                }
            }
            return null;
        }
    }
}
