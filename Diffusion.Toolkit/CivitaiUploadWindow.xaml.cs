using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace Diffusion.Toolkit.Windows;

public partial class CivitaiUploadWindow : Window
{
    private readonly string _imagePath;
    private bool _uploadAttempted = false;

    public CivitaiUploadWindow(string imagePath)
    {
        InitializeComponent();
        _imagePath = imagePath;

        Loaded += CivitaiUploadWindow_Loaded;
    }

    private async void CivitaiUploadWindow_Loaded(object sender, RoutedEventArgs e)
    {
        await InitializeWebView();
    }

    private async Task InitializeWebView()
    {
        try
        {
            // Set up WebView2 environment with persistent user data
            var userDataFolder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "DiffusionToolkit",
                "CivitAI");

            Directory.CreateDirectory(userDataFolder);

            var environment = await CoreWebView2Environment.CreateAsync(
                userDataFolder: userDataFolder);

            await WebView.EnsureCoreWebView2Async(environment);

            // Subscribe to navigation events
            WebView.CoreWebView2.NavigationCompleted += CoreWebView2_NavigationCompleted;
            WebView.CoreWebView2.DOMContentLoaded += CoreWebView2_DOMContentLoaded;

            StatusText.Text = "Browser ready. Navigating to CivitAI...";

            // Navigate to CivitAI upload page
            WebView.CoreWebView2.Navigate("https://civitai.com/posts/create");
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Error initializing browser: {ex.Message}";
            ProgressBar.Visibility = Visibility.Collapsed;
            MessageBox.Show($"Failed to initialize browser:\n{ex.Message}",
                          "Error",
                          MessageBoxButton.OK,
                          MessageBoxImage.Error);
        }
    }

    private async void CoreWebView2_NavigationCompleted(object sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        if (e.IsSuccess)
        {
            StatusText.Text = "Page loaded successfully";
            ProgressBar.Visibility = Visibility.Collapsed;

            // Check if we're on the upload page and haven't uploaded yet
            var url = WebView.CoreWebView2.Source;
            if (url.Contains("civitai.com/posts/create") && !_uploadAttempted)
            {
                // Wait a bit for any dynamic content to load
                await Task.Delay(2000);
                await AttemptUpload();
            }
        }
        else
        {
            StatusText.Text = $"Navigation failed: {e.WebErrorStatus}";
            ProgressBar.Visibility = Visibility.Collapsed;
        }
    }

    private async void CoreWebView2_DOMContentLoaded(object sender, CoreWebView2DOMContentLoadedEventArgs e)
    {
        StatusText.Text = "Page content loaded...";
    }

    private async Task AttemptUpload()
    {
        _uploadAttempted = true;
        StatusText.Text = "Attempting to upload image...";
        ProgressBar.Visibility = Visibility.Visible;

        try
        {
            // Read the image file as base64
            var imageBytes = await File.ReadAllBytesAsync(_imagePath);
            var base64Image = Convert.ToBase64String(imageBytes);
            var fileName = Path.GetFileName(_imagePath);
            var mimeType = GetMimeType(_imagePath);

            // JavaScript to inject file into file input
            var script = $@"
                (async function() {{
                    try {{
                        // Find the file input element (adjust selector as needed)
                        const fileInput = document.querySelector('input[type=""file""]');

                        if (!fileInput) {{
                            return 'ERROR: File input not found on page';
                        }}

                        // Convert base64 to blob
                        const base64Data = '{base64Image}';
                        const binaryData = atob(base64Data);
                        const arrayBuffer = new ArrayBuffer(binaryData.length);
                        const uint8Array = new Uint8Array(arrayBuffer);
                        for (let i = 0; i < binaryData.length; i++) {{
                            uint8Array[i] = binaryData.charCodeAt(i);
                        }}
                        const blob = new Blob([uint8Array], {{ type: '{mimeType}' }});

                        // Create File object
                        const file = new File([blob], '{fileName}', {{ type: '{mimeType}' }});

                        // Create DataTransfer to set files
                        const dataTransfer = new DataTransfer();
                        dataTransfer.items.add(file);
                        fileInput.files = dataTransfer.files;

                        // Trigger change event
                        const event = new Event('change', {{ bubbles: true }});
                        fileInput.dispatchEvent(event);

                        return 'SUCCESS: File uploaded';
                    }} catch (error) {{
                        return 'ERROR: ' + error.message;
                    }}
                }})();
            ";

            var result = await WebView.CoreWebView2.ExecuteScriptAsync(script);

            // Remove quotes from JSON string result
            result = result.Trim('"');

            if (result.StartsWith("SUCCESS"))
            {
                StatusText.Text = "✓ Image uploaded! Please fill in title, description, and publish.";
                HelpText.Text = "Image has been uploaded. Complete the form and click Publish when ready.";
                ProgressBar.Visibility = Visibility.Collapsed;
            }
            else if (result.StartsWith("ERROR"))
            {
                StatusText.Text = result;
                ProgressBar.Visibility = Visibility.Collapsed;

                // Show fallback instructions
                MessageBox.Show(
                    "Automatic upload failed. The upload page structure may have changed.\n\n" +
                    "Please manually click the upload area and select your image:\n" +
                    _imagePath,
                    "Manual Upload Required",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Upload error: {ex.Message}";
            ProgressBar.Visibility = Visibility.Collapsed;

            MessageBox.Show(
                $"Failed to upload image automatically:\n{ex.Message}\n\n" +
                "Please manually upload the image:\n" + _imagePath,
                "Upload Error",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }

    private string GetMimeType(string filePath)
    {
        var extension = Path.GetExtension(filePath).ToLowerInvariant();
        return extension switch
        {
            ".png" => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".gif" => "image/gif",
            ".webp" => "image/webp",
            ".bmp" => "image/bmp",
            _ => "application/octet-stream"
        };
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }
}
