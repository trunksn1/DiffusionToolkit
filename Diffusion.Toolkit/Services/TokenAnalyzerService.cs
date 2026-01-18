using System;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Diffusion.Toolkit.Services;

public class TokenAnalyzerService
{
    private const string PipeName = "CivTokenAnalyzerInstance";
    private const int PipeTimeoutMs = 1000;

    /// <summary>
    /// Sends an image path to the Token Analyzer application.
    /// If Token Analyzer is running, sends via named pipe; otherwise launches the application.
    /// </summary>
    public async Task<bool> SendToTokenAnalyzer(string imagePath)
    {
        if (string.IsNullOrEmpty(imagePath))
        {
            ServiceLocator.ToastService.Toast("Invalid selection", "No image path provided");
            return false;
        }

        if (!File.Exists(imagePath))
        {
            ServiceLocator.ToastService.Toast("File not found", $"Image file does not exist: {imagePath}");
            return false;
        }

        var tokenAnalyzerPath = ServiceLocator.Settings?.TokenAnalyzerPath;

        if (string.IsNullOrEmpty(tokenAnalyzerPath))
        {
            ServiceLocator.ToastService.Toast("Token Analyzer not configured",
                "Please set the Token Analyzer path in Settings > General");
            return false;
        }

        if (!File.Exists(tokenAnalyzerPath))
        {
            ServiceLocator.ToastService.Toast("Token Analyzer not found",
                $"The configured path does not exist: {tokenAnalyzerPath}");
            return false;
        }

        try
        {
            // Try to connect to running instance via named pipe
            if (await TrySendViaPipe(imagePath))
            {
                ServiceLocator.ToastService.Toast("Sent to Token Analyzer",
                    "Image path sent to running Token Analyzer instance");
                return true;
            }

            // No running instance found, launch the application and then send the path
            return await LaunchAndSendPath(tokenAnalyzerPath, imagePath);
        }
        catch (Exception ex)
        {
            ServiceLocator.ToastService.Toast("Error sending to Token Analyzer", ex.Message);
            return false;
        }
    }

    private async Task<bool> TrySendViaPipe(string imagePath)
    {
        try
        {
            using var cts = new CancellationTokenSource(PipeTimeoutMs);
            using var pipeClient = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);

            try
            {
                // Try to connect with cancellation token for reliable timeout
                await pipeClient.ConnectAsync(cts.Token);
            }
            catch (OperationCanceledException)
            {
                // Timeout - Token Analyzer not running
                return false;
            }

            if (pipeClient.IsConnected)
            {
                var bytes = Encoding.UTF8.GetBytes(imagePath);
                await pipeClient.WriteAsync(bytes, 0, bytes.Length, cts.Token);
                await pipeClient.FlushAsync(cts.Token);
                return true;
            }
        }
        catch (TimeoutException)
        {
            // Token Analyzer not running - this is expected
        }
        catch (OperationCanceledException)
        {
            // Timeout via cancellation - Token Analyzer not running
        }
        catch (IOException)
        {
            // Pipe not available - Token Analyzer not running
        }
        catch (Exception)
        {
            // Any other error - treat as not running
        }

        return false;
    }

    private async Task<bool> LaunchAndSendPath(string tokenAnalyzerPath, string imagePath)
    {
        // Launch without image path argument (avoids Python Qt issue)
        if (!LaunchTokenAnalyzer(tokenAnalyzerPath))
            return false;

        // Wait for Token Analyzer to start and create its named pipe server
        ServiceLocator.ToastService.Toast("Token Analyzer launching",
            "Waiting for Token Analyzer to start...");

        // Try to send the path multiple times as the app starts up
        for (int i = 0; i < 10; i++)
        {
            await Task.Delay(500); // Wait 500ms between attempts

            if (await TrySendViaPipe(imagePath))
            {
                ServiceLocator.ToastService.Toast("Sent to Token Analyzer",
                    "Image path sent successfully");
                return true;
            }
        }

        // App started but couldn't send path - user can try again
        ServiceLocator.ToastService.Toast("Token Analyzer started",
            "App started but couldn't send image path. Try clicking again.");
        return true;
    }

    private bool LaunchTokenAnalyzer(string tokenAnalyzerPath, string? imagePath = null)
    {
        try
        {
            var extension = Path.GetExtension(tokenAnalyzerPath).ToLowerInvariant();
            ProcessStartInfo startInfo;

            // Only include image path argument if provided
            var args = string.IsNullOrEmpty(imagePath) ? "" : $"\"{imagePath}\"";

            if (extension == ".bat" || extension == ".cmd")
            {
                // Launch batch script
                startInfo = new ProcessStartInfo
                {
                    FileName = tokenAnalyzerPath,
                    Arguments = args,
                    WorkingDirectory = Path.GetDirectoryName(tokenAnalyzerPath),
                    UseShellExecute = true
                };
            }
            else if (extension == ".py")
            {
                // Launch Python script
                var pyArgs = string.IsNullOrEmpty(imagePath)
                    ? $"\"{tokenAnalyzerPath}\""
                    : $"\"{tokenAnalyzerPath}\" \"{imagePath}\"";
                startInfo = new ProcessStartInfo
                {
                    FileName = "python",
                    Arguments = pyArgs,
                    WorkingDirectory = Path.GetDirectoryName(tokenAnalyzerPath),
                    UseShellExecute = false
                };
            }
            else if (extension == ".exe")
            {
                // Launch executable directly
                startInfo = new ProcessStartInfo
                {
                    FileName = tokenAnalyzerPath,
                    Arguments = args,
                    WorkingDirectory = Path.GetDirectoryName(tokenAnalyzerPath),
                    UseShellExecute = false
                };
            }
            else
            {
                ServiceLocator.ToastService.Toast("Unsupported file type",
                    "Token Analyzer path must be a .bat, .py, or .exe file");
                return false;
            }

            var process = Process.Start(startInfo);

            if (process == null)
            {
                ServiceLocator.ToastService.Toast("Failed to launch Token Analyzer",
                    "Process.Start returned null");
                return false;
            }

            return true;
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            // Python not found or other Win32 error
            ServiceLocator.ToastService.Toast("Failed to launch Token Analyzer",
                $"Could not start process: {ex.Message}. Is Python installed and in PATH?");
            return false;
        }
        catch (Exception ex)
        {
            ServiceLocator.ToastService.Toast("Failed to launch Token Analyzer", ex.Message);
            return false;
        }
    }
}
