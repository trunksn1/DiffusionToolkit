using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using Diffusion.Database;
using Diffusion.Toolkit.Models;
using Diffusion.Toolkit.Classes;
using Diffusion.Toolkit.Services;

namespace Diffusion.Toolkit
{
    public partial class CivitaiAlbumSelectionWindow : Window
    {
        public string? SelectedAlbumName { get; private set; }
        public bool RememberChoice { get; private set; }
        private ObservableCollection<AlbumModel> _albums;

        public CivitaiAlbumSelectionWindow(ObservableCollection<AlbumModel> albums)
        {
            try
            {
                InitializeComponent();

                _albums = albums ?? new ObservableCollection<AlbumModel>();

                if (AlbumsListBox != null)
                {
                    AlbumsListBox.ItemsSource = _albums;
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error initializing album selection window: {ex.Message}\n\nStack trace: {ex.StackTrace}", "Initialization Error", MessageBoxButton.OK, MessageBoxImage.Error);
                throw;
            }
        }

        private async void OkButton_Click(object sender, RoutedEventArgs e)
        {
            // Check which option was selected
            if (NewAlbumRadioButton.IsChecked == true)
            {
                if (string.IsNullOrWhiteSpace(NewAlbumTextBox.Text))
                {
                    MessageBox.Show("Please enter a name for the new album.", "Validation", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                var newAlbumName = NewAlbumTextBox.Text.Trim();

                // Check if album already exists
                if (_albums.Any(a => a.Name.Equals(newAlbumName, StringComparison.OrdinalIgnoreCase)))
                {
                    MessageBox.Show($"An album named '{newAlbumName}' already exists. Please choose a different name.", "Album Exists", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                // Create the new album in the database
                try
                {
                    var newAlbum = new Diffusion.Database.Models.Album
                    {
                        Name = newAlbumName,
                        LastUpdated = DateTime.Now
                    };

                    ServiceLocator.DataStore.CreateAlbum(newAlbum);
                    SelectedAlbumName = newAlbumName;
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"Error creating album: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }
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
