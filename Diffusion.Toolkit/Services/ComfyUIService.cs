using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using System.Net.Http;
using System.Runtime.InteropServices;
using Diffusion.Common;
using Diffusion.Toolkit.Configuration;

namespace Diffusion.Toolkit.Services;

public class ComfyUIService
{
    private readonly Settings _settings;
    private string? _detectedServerUrl; // Cache the working URL

    // P/Invoke declarations for window management
    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr FindWindow(string? lpClassName, string? lpWindowName);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    private const int SW_RESTORE = 9;
    private const int SW_SHOW = 5;

    // P/Invoke for low-level keyboard input (works with Electron apps, unlike SendKeys)
    [DllImport("user32.dll")]
    private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);

    private const byte VK_CONTROL = 0x11;
    private const byte VK_V = 0x56;
    private const uint KEYEVENTF_KEYUP = 0x0002;

    public ComfyUIService()
    {
        _settings = ServiceLocator.Settings;
    }

    private enum WorkflowFormat { Invalid, PromptApi, Graph }
    private WorkflowFormat _currentWorkflowFormat = WorkflowFormat.Invalid;

    // Store the current image path for file-based loading
    private string? _currentImagePath;

    /// <summary>
    /// Launches ComfyUI and loads the workflow from the specified image.
    /// </summary>
    public async Task<bool> LaunchComfyUIWithImage(string imagePath)
    {
        try
        {
            Logger.Log("==========================================");
            Logger.Log($"ComfyUIService.LaunchComfyUIWithImage: STARTING for path: {imagePath}");
            Logger.Log("==========================================");

            if (string.IsNullOrEmpty(imagePath))
            {
                ServiceLocator.ToastService.Toast("No image selected", "ComfyUI");
                return false;
            }

            if (!File.Exists(imagePath))
            {
                ServiceLocator.ToastService.Toast("Image file not found", "ComfyUI");
                return false;
            }

            // Store the image path for later use in file-based loading
            _currentImagePath = imagePath;

            var launcherPath = _settings.ComfyUILauncherPath;

            if (string.IsNullOrEmpty(launcherPath))
            {
                ServiceLocator.ToastService.Toast(
                    "ComfyUI not configured. Please set the path in Settings > General",
                    "ComfyUI");
                return false;
            }

            if (!File.Exists(launcherPath))
            {
                ServiceLocator.ToastService.Toast(
                    $"ComfyUI launcher not found: {launcherPath}",
                    "ComfyUI");
                return false;
            }

            // Extract workflow from image
            var workflow = await ExtractWorkflowFromImage(imagePath);

            if (workflow == null)
            {
                Logger.Log("ComfyUIService: No workflow found in image");
                ServiceLocator.ToastService.Toast(
                    "This image does not contain workflow metadata. ComfyUI will launch without a workflow.",
                    "No Workflow Found");
                // Continue anyway - user might want to use ComfyUI
            }
            else
            {
                _currentWorkflowFormat = DetectWorkflowFormat(workflow);
                if (_currentWorkflowFormat == WorkflowFormat.Invalid)
                {
                    Logger.Log("ComfyUIService: Workflow is not ComfyUI format");
                    ServiceLocator.ToastService.Toast(
                        "This image's workflow is not in ComfyUI format (might be A1111/InvokeAI). ComfyUI will launch without loading it.",
                        "Not a ComfyUI Workflow");
                    workflow = null;
                }
            }

            // Prepare launch parameters
            var workingDir = Path.GetDirectoryName(launcherPath);
            string arguments = _settings.ComfyUILauncherArgs ?? "";

            // If launcher is python.exe, add main.py
            if (Path.GetFileName(launcherPath).Equals("python.exe", StringComparison.OrdinalIgnoreCase))
            {
                var mainPyPath = Path.Combine(workingDir, "main.py");
                if (File.Exists(mainPyPath))
                {
                    arguments = $"\"{mainPyPath}\" {arguments}";
                }
            }

            Logger.Log($"ComfyUIService: Launcher: {launcherPath}");
            Logger.Log($"ComfyUIService: Arguments: {arguments}");
            Logger.Log($"ComfyUIService: Working Directory: {workingDir}");

            // Check if using Electron (desktop app)
            var isElectron = launcherPath.Contains("ComfyUI.exe", StringComparison.OrdinalIgnoreCase);

            // Launch ComfyUI and wait for server
            var launched = await LaunchAndWaitForComfyUI(launcherPath, workingDir, arguments);

            if (!launched)
            {
                ServiceLocator.ToastService.Toast(
                    "ComfyUI failed to start or took too long. Please try launching it manually.",
                    "Launch Failed");
                return false;
            }

            // Load workflow if we extracted one
            if (workflow != null)
            {
                var loaded = await LoadWorkflowInComfyUI(workflow);

                if (loaded)
                {
                    Logger.Log("ComfyUIService: SUCCESS - Workflow loaded!");
                    // Toast message is already shown in LoadWorkflowViaFile for Electron
                    if (!isElectron)
                    {
                        ServiceLocator.ToastService.Toast(
                            "Workflow loaded successfully! Check your browser.",
                            "ComfyUI");
                    }
                }
                else
                {
                    // Failed to load workflow
                    Logger.Log("ComfyUIService: Failed to load workflow");

                    if (!isElectron)
                    {
                        // Browser-based: try opening URL anyway
                        Process.Start(new ProcessStartInfo
                        {
                            FileName = _settings.ComfyUIServerUrl ?? "http://localhost:8188",
                            UseShellExecute = true
                        });
                    }

                    ServiceLocator.ToastService.Toast(
                        isElectron
                            ? "ComfyUI is running. Please drag the workflow file shown in the previous notification into ComfyUI."
                            : "ComfyUI launched, but workflow loading failed. Drag the image into ComfyUI manually.",
                        "Workflow Load Failed");
                }
            }
            else
            {
                // No workflow found
                Logger.Log("ComfyUIService: No workflow to load");

                if (!isElectron)
                {
                    // Browser-based: open URL
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = _settings.ComfyUIServerUrl ?? "http://localhost:8188",
                        UseShellExecute = true
                    });
                }

                ServiceLocator.ToastService.Toast(
                    isElectron ? "ComfyUI launched" : "ComfyUI opened in browser",
                    "ComfyUI");
            }

            Logger.Log("ComfyUIService: COMPLETED");
            return true;
        }
        catch (Exception ex)
        {
            Logger.Log($"ComfyUIService: ERROR - {ex.Message}");
            Logger.Log($"ComfyUIService: Stack trace - {ex.StackTrace}");
            ServiceLocator.ToastService.Toast($"Error launching ComfyUI: {ex.Message}", "Error");
            return false;
        }
    }

    private async Task<string?> ExtractWorkflowFromImage(string imagePath)
    {
        try
        {
            Logger.Log($"ComfyUIService.ExtractWorkflowFromImage: Looking up path: {imagePath}");

            // FIRST: Try to read the "workflow" chunk directly from the PNG file
            // This is the graph format that ComfyUI expects for Ctrl+V paste
            var workflowFromFile = await ReadWorkflowChunkFromPng(imagePath);
            if (!string.IsNullOrEmpty(workflowFromFile))
            {
                Logger.Log($"ComfyUIService.ExtractWorkflowFromImage: Got workflow from PNG file ({workflowFromFile.Length} chars)");
                return workflowFromFile;
            }

            // FALLBACK: Get from database (this is the "prompt" format, may not work with Ctrl+V)
            var image = ServiceLocator.DataStore.GetImageByPath(imagePath);

            if (image != null)
            {
                Logger.Log($"ComfyUIService.ExtractWorkflowFromImage: Found image ID={image.Id}, Path={image.Path}");

                if (!string.IsNullOrEmpty(image.Workflow))
                {
                    Logger.Log($"ComfyUIService.ExtractWorkflowFromImage: Using workflow from database ({image.Workflow.Length} chars)");
                    return image.Workflow;
                }
                else
                {
                    Logger.Log("ComfyUIService.ExtractWorkflowFromImage: Image found but Workflow field is empty");
                }
            }
            else
            {
                Logger.Log($"ComfyUIService.ExtractWorkflowFromImage: No image found in database for path: {imagePath}");
            }

            return null;
        }
        catch (Exception ex)
        {
            Logger.Log($"ComfyUIService.ExtractWorkflowFromImage: Failed - {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Reads the "workflow" text chunk directly from a PNG file.
    /// This is the graph format that ComfyUI uses for the canvas (nodes, links, etc.)
    /// </summary>
    private async Task<string?> ReadWorkflowChunkFromPng(string imagePath)
    {
        try
        {
            if (!File.Exists(imagePath))
                return null;

            if (!imagePath.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
            {
                Logger.Log("ComfyUIService.ReadWorkflowChunkFromPng: Not a PNG file, skipping");
                return null;
            }

            // Read PNG and look for tEXt/iTXt chunks with keyword "workflow"
            using var stream = File.OpenRead(imagePath);
            using var reader = new BinaryReader(stream);

            // Check PNG signature
            var signature = reader.ReadBytes(8);
            if (signature.Length != 8 || signature[0] != 0x89 || signature[1] != 0x50 ||
                signature[2] != 0x4E || signature[3] != 0x47)
            {
                Logger.Log("ComfyUIService.ReadWorkflowChunkFromPng: Invalid PNG signature");
                return null;
            }

            // Read chunks until we find "workflow" or reach end
            while (stream.Position < stream.Length - 12)
            {
                // Read chunk length (big-endian)
                var lengthBytes = reader.ReadBytes(4);
                if (BitConverter.IsLittleEndian)
                    Array.Reverse(lengthBytes);
                var length = BitConverter.ToInt32(lengthBytes, 0);

                // Read chunk type
                var typeBytes = reader.ReadBytes(4);
                var chunkType = Encoding.ASCII.GetString(typeBytes);

                if (length < 0 || length > 100_000_000) // Sanity check
                {
                    Logger.Log($"ComfyUIService.ReadWorkflowChunkFromPng: Invalid chunk length {length}");
                    break;
                }

                // Read chunk data
                var data = reader.ReadBytes(length);

                // Skip CRC
                reader.ReadBytes(4);

                // Check for tEXt chunk
                if (chunkType == "tEXt" && data.Length > 0)
                {
                    // tEXt format: keyword\0text
                    var nullIndex = Array.IndexOf(data, (byte)0);
                    if (nullIndex > 0)
                    {
                        var keyword = Encoding.Latin1.GetString(data, 0, nullIndex);
                        if (keyword == "workflow")
                            {
                            var text = Encoding.Latin1.GetString(data, nullIndex + 1, data.Length - nullIndex - 1);
                            Logger.Log($"ComfyUIService.ReadWorkflowChunkFromPng: Found 'workflow' tEXt chunk ({text.Length} chars)");
                            return text;
                        }
                    }
                }
                // Check for iTXt chunk (international text, UTF-8)
                else if (chunkType == "iTXt" && data.Length > 0)
                {
                    // iTXt format: keyword\0compression_flag\0compression_method\0language_tag\0translated_keyword\0text
                    var nullIndex = Array.IndexOf(data, (byte)0);
                    if (nullIndex > 0)
                    {
                        var keyword = Encoding.Latin1.GetString(data, 0, nullIndex);
                        if (keyword == "workflow" && data.Length > nullIndex + 5)
                        {
                            // Skip compression flag, method, language tag, translated keyword
                            var pos = nullIndex + 1;
                            var compressionFlag = data[pos++];
                            pos++; // compression method

                            // Skip language tag (null-terminated)
                            while (pos < data.Length && data[pos] != 0) pos++;
                            pos++; // skip null

                            // Skip translated keyword (null-terminated)
                            while (pos < data.Length && data[pos] != 0) pos++;
                            pos++; // skip null

                            if (pos < data.Length)
                            {
                                var text = Encoding.UTF8.GetString(data, pos, data.Length - pos);
                                Logger.Log($"ComfyUIService.ReadWorkflowChunkFromPng: Found 'workflow' iTXt chunk ({text.Length} chars)");
                                return text;
                            }
                        }
                    }
                }

                // Stop at IEND chunk
                if (chunkType == "IEND")
                    break;
            }

            Logger.Log("ComfyUIService.ReadWorkflowChunkFromPng: No 'workflow' chunk found in PNG");
            return null;
        }
        catch (Exception ex)
        {
            Logger.Log($"ComfyUIService.ReadWorkflowChunkFromPng: Error reading PNG - {ex.Message}");
            return null;
        }
    }

    private WorkflowFormat DetectWorkflowFormat(string workflowJson)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(workflowJson))
            {
                Logger.Log("ComfyUIService.DetectWorkflowFormat: Workflow is null or empty");
                return WorkflowFormat.Invalid;
            }

            var workflow = JsonNode.Parse(workflowJson);

            if (workflow is not JsonObject workflowObj)
            {
                Logger.Log("ComfyUIService.DetectWorkflowFormat: Workflow is not a JSON object");
                return WorkflowFormat.Invalid;
            }

            // Check for graph format: has "nodes" array (canvas/graph layout)
            if (workflowObj.ContainsKey("nodes") && workflowObj["nodes"] is JsonArray nodesArray)
            {
                Logger.Log($"ComfyUIService.DetectWorkflowFormat: Graph format detected ({nodesArray.Count} nodes)");
                return WorkflowFormat.Graph;
            }

            // Check for prompt/API format: numeric string keys with class_type + inputs
            var nodeCount = 0;
            foreach (var kvp in workflowObj)
            {
                if (!int.TryParse(kvp.Key, out _))
                    continue;

                if (kvp.Value is JsonObject node && node.ContainsKey("class_type") && node.ContainsKey("inputs"))
                    nodeCount++;
            }

            if (nodeCount > 0)
            {
                Logger.Log($"ComfyUIService.DetectWorkflowFormat: Prompt/API format detected ({nodeCount} nodes)");
                return WorkflowFormat.PromptApi;
            }

            Logger.Log("ComfyUIService.DetectWorkflowFormat: No valid ComfyUI workflow format recognized");
            return WorkflowFormat.Invalid;
        }
        catch (JsonException ex)
        {
            Logger.Log($"ComfyUIService.DetectWorkflowFormat: Invalid JSON - {ex.Message}");
            return WorkflowFormat.Invalid;
        }
        catch (Exception ex)
        {
            Logger.Log($"ComfyUIService.DetectWorkflowFormat: Detection failed - {ex.Message}");
            return WorkflowFormat.Invalid;
        }
    }

    private async Task<bool> LaunchAndWaitForComfyUI(string launcherPath, string workingDir, string args)
    {
        try
        {
            Logger.Log("ComfyUIService.LaunchAndWaitForComfyUI: Starting launch sequence");

            // Check if this is ComfyUI Electron
            var isElectron = launcherPath.Contains("ComfyUI.exe", StringComparison.OrdinalIgnoreCase);

            if (isElectron)
            {
                Logger.Log("ComfyUIService: Detected ComfyUI Electron");

                // FIRST: Check if ComfyUI.exe process is actually running
                var comfyProcesses = Process.GetProcessesByName("ComfyUI");
                Logger.Log($"ComfyUIService: Found {comfyProcesses.Length} ComfyUI.exe processes");

                // Kill zombie processes if there are too many (Electron can leave orphans)
                if (comfyProcesses.Length > 6)
                {
                    Logger.Log($"ComfyUIService: Too many ComfyUI processes ({comfyProcesses.Length})! Killing all zombie processes...");

                    foreach (var proc in comfyProcesses)
                    {
                        try
                        {
                            proc.Kill();
                            Logger.Log($"ComfyUIService: Killed process {proc.Id}");
                        }
                        catch (Exception ex)
                        {
                            Logger.Log($"ComfyUIService: Failed to kill process {proc.Id}: {ex.Message}");
                        }
                    }

                    // Wait a moment for processes to die
                    await Task.Delay(2000);

                    // Re-check processes
                    comfyProcesses = Process.GetProcessesByName("ComfyUI");
                    Logger.Log($"ComfyUIService: After cleanup, {comfyProcesses.Length} processes remain");
                }

                bool processRunning = comfyProcesses.Length > 0;

                if (processRunning)
                {
                    // Process is running - check if server is already responding
                    if (await IsComfyUIRunning(forceElectronMode: true))
                    {
                        Logger.Log("ComfyUIService: ComfyUI Electron already running and responding!");

                        // Bring the window to foreground using the process's main window
                        var mainWindow = comfyProcesses[0].MainWindowHandle;
                        if (mainWindow != IntPtr.Zero)
                        {
                            Logger.Log($"ComfyUIService: Bringing ComfyUI window to foreground (handle: {mainWindow})");
                            if (IsIconic(mainWindow))
                            {
                                ShowWindow(mainWindow, SW_RESTORE);
                            }
                            ShowWindow(mainWindow, SW_SHOW);
                            SetForegroundWindow(mainWindow);
                        }

                        return true;
                    }

                    // Process running but server not ready - wait for it
                    Logger.Log("ComfyUIService: ComfyUI Electron process found, waiting for server...");
                    for (int i = 0; i < 30; i++)
                    {
                        await Task.Delay(1000);
                        if (await IsComfyUIRunning(forceElectronMode: true))
                        {
                            Logger.Log($"ComfyUIService: Electron server ready after {i + 1} seconds");
                            return true;
                        }
                    }

                    Logger.Log("ComfyUIService: WARNING - Electron process running but server not responding");
                    return true; // Try anyway
                }

                Logger.Log("ComfyUIService: Launching ComfyUI Electron...");
                var electronProcessInfo = new ProcessStartInfo()
                {
                    FileName = launcherPath,
                    WorkingDirectory = workingDir,
                    UseShellExecute = true,
                    CreateNoWindow = false
                };

                Process.Start(electronProcessInfo);
                Logger.Log("ComfyUIService: ComfyUI Electron process started, waiting for server at port 8000...");

                // Wait for the Electron server to be ready (can take 15-30+ seconds on first launch)
                int electronTimeout = Math.Max(_settings.ComfyUIStartupTimeout, 60);
                Logger.Log($"ComfyUIService: Will poll for up to {electronTimeout} seconds...");

                for (int i = 0; i < electronTimeout; i++)
                {
                    await Task.Delay(1000);

                    if (i % 5 == 0 && i > 0)
                    {
                        Logger.Log($"ComfyUIService: Still waiting for Electron server... ({i}/{electronTimeout} seconds)");
                    }

                    if (await IsComfyUIRunning(forceElectronMode: true))
                    {
                        Logger.Log($"ComfyUIService: Electron server responded after {i + 1} seconds");
                        // Extra delay for API to fully initialize
                        await Task.Delay(2000);
                        Logger.Log("ComfyUIService: Electron server ready!");
                        return true;
                    }
                }

                Logger.Log($"ComfyUIService: Timeout after {electronTimeout} seconds waiting for Electron server");
                // Still return true to attempt workflow loading anyway
                return true;
            }

            // Browser-based ComfyUI - check HTTP
            Logger.Log("ComfyUIService.LaunchAndWaitForComfyUI: Checking if HTTP server is running...");
            if (await IsComfyUIRunning())
            {
                Logger.Log("ComfyUIService: ComfyUI HTTP server is already running");
                await Task.Delay(1000);
                return true;
            }
            Logger.Log("ComfyUIService.LaunchAndWaitForComfyUI: HTTP server not responding");

            // Check if process is running
            Logger.Log("ComfyUIService.LaunchAndWaitForComfyUI: Checking if ComfyUI process is running...");
            if (IsComfyUIProcessRunning())
            {
                Logger.Log("ComfyUIService: ComfyUI process detected, waiting for HTTP server...");

                for (int i = 0; i < 15; i++)
                {
                    await Task.Delay(1000);
                    if (await IsComfyUIRunning())
                    {
                        Logger.Log($"ComfyUIService: HTTP server now responding after {i + 1} seconds");
                        return true;
                    }
                }

                Logger.Log("ComfyUIService: WARNING - Process running but HTTP server not responding");
                return true; // Try anyway
            }

            Logger.Log("ComfyUIService: Launching new ComfyUI instance...");

            var processInfo = new ProcessStartInfo()
            {
                FileName = launcherPath,
                Arguments = args,
                WorkingDirectory = workingDir,
                UseShellExecute = true,
                CreateNoWindow = false
            };

            Process.Start(processInfo);
            Logger.Log("ComfyUIService: Process started, waiting for server...");

            int timeout = _settings.ComfyUIStartupTimeout;
            if (timeout < 30)
            {
                timeout = 60;
                Logger.Log($"ComfyUIService: Timeout was {_settings.ComfyUIStartupTimeout}s, using {timeout}s for safety");
            }

            Logger.Log($"ComfyUIService: Will poll for up to {timeout} seconds...");

            for (int i = 0; i < timeout; i++)
            {
                await Task.Delay(1000);

                if (i % 5 == 0 && i > 0)
                {
                    Logger.Log($"ComfyUIService: Still waiting... ({i}/{timeout} seconds)");
                }

                if (await IsComfyUIRunning())
                {
                    Logger.Log($"ComfyUIService: Server responded after {i + 1} seconds");
                    Logger.Log("ComfyUIService: Waiting 3 more seconds for API to fully initialize...");
                    await Task.Delay(3000);
                    Logger.Log("ComfyUIService: Server ready!");
                    return true;
                }
            }

            Logger.Log($"ComfyUIService: Timeout after {timeout} seconds waiting for server");
            return false;
        }
        catch (Exception ex)
        {
            Logger.Log($"ComfyUIService: Failed to launch - {ex.Message}");
            Logger.Log($"ComfyUIService: Stack trace - {ex.StackTrace}");
            return false;
        }
    }

    private bool IsComfyUIProcessRunning()
    {
        try
        {
            // For ComfyUI Electron, just check for ComfyUI.exe
            var processes = Process.GetProcessesByName("ComfyUI");
            if (processes.Length > 0)
            {
                Logger.Log($"ComfyUIService.IsComfyUIProcessRunning: Found {processes.Length} ComfyUI process(es)");
                return true;
            }

            // For python-based ComfyUI (optional, slower)
            // Skip this check for speed if using Electron
            if (_settings.ComfyUILauncherPath?.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) == true)
            {
                // Using .exe launcher, don't check for python
                Logger.Log("ComfyUIService.IsComfyUIProcessRunning: No ComfyUI.exe process found");
                return false;
            }

            // Check for python processes (only if not using .exe)
            var pythonProcesses = Process.GetProcessesByName("python");
            if (pythonProcesses.Length > 0)
            {
                Logger.Log($"ComfyUIService.IsComfyUIProcessRunning: Found {pythonProcesses.Length} python process(es) - might be ComfyUI");
                return true; // Assume it might be ComfyUI
            }

            Logger.Log("ComfyUIService.IsComfyUIProcessRunning: No ComfyUI-related processes found");
            return false;
        }
        catch (Exception ex)
        {
            Logger.Log($"ComfyUIService.IsComfyUIProcessRunning: Error - {ex.Message}");
            return false;
        }
    }

    private async Task<bool> IsComfyUIRunning(bool? forceElectronMode = null)
    {
        // Determine if we're in Electron mode
        var isElectron = forceElectronMode ??
            _settings.ComfyUILauncherPath?.Contains("ComfyUI.exe", StringComparison.OrdinalIgnoreCase) == true;

        // If we already detected a working URL, try it first
        if (!string.IsNullOrEmpty(_detectedServerUrl))
        {
            if (await TryConnectToComfyUI(_detectedServerUrl))
            {
                return true;
            }
            else
            {
                _detectedServerUrl = null;
            }
        }

        // For Electron, use port 8000; otherwise use configured or default 8188
        var defaultPort = isElectron ? 8000 : 8188;
        var serverUrl = _settings.ComfyUIServerUrl ?? $"http://localhost:{defaultPort}";

        // If Electron and user has default port configured, override to 8000
        if (isElectron && serverUrl.Contains(":8188"))
        {
            serverUrl = serverUrl.Replace(":8188", ":8000");
            Logger.Log($"ComfyUIService.IsComfyUIRunning: Electron detected, using port 8000");
        }

        if (await TryConnectToComfyUI(serverUrl))
        {
            _detectedServerUrl = serverUrl;
            Logger.Log($"ComfyUIService.IsComfyUIRunning: Connected to {serverUrl}");
            return true;
        }

        // Quick check of 127.0.0.1 variant if localhost was used
        if (serverUrl.Contains("localhost"))
        {
            var altUrl = serverUrl.Replace("localhost", "127.0.0.1");
            if (await TryConnectToComfyUI(altUrl))
            {
                _detectedServerUrl = altUrl;
                Logger.Log($"ComfyUIService.IsComfyUIRunning: Connected to {altUrl}");
                return true;
            }
        }

        // For Electron, also try port 8188 as fallback
        if (isElectron && !serverUrl.Contains(":8188"))
        {
            var fallbackUrl = serverUrl.Replace(":8000", ":8188");
            Logger.Log($"ComfyUIService.IsComfyUIRunning: Trying fallback port 8188...");
            if (await TryConnectToComfyUI(fallbackUrl))
            {
                _detectedServerUrl = fallbackUrl;
                Logger.Log($"ComfyUIService.IsComfyUIRunning: Connected to {fallbackUrl}");
                return true;
            }
        }

        return false;
    }

    private async Task<bool> TryConnectToComfyUI(string url)
    {
        try
        {
            using var httpClient = new HttpClient();
            httpClient.Timeout = TimeSpan.FromMilliseconds(800); // Faster timeout

            var response = await httpClient.GetAsync(url);
            var isRunning = response.IsSuccessStatusCode;

            Logger.Log($"ComfyUIService.TryConnectToComfyUI: {url} => HTTP {response.StatusCode}, IsRunning = {isRunning}");
            return isRunning;
        }
        catch (TaskCanceledException)
        {
            // Timeout - don't log, this is expected when server isn't running
            return false;
        }
        catch (HttpRequestException ex)
        {
            // Connection refused - don't log, this is expected when server isn't running
            if (!ex.Message.Contains("refused"))
            {
                Logger.Log($"ComfyUIService.TryConnectToComfyUI: {url} => {ex.Message}");
            }
            return false;
        }
        catch (Exception ex)
        {
            Logger.Log($"ComfyUIService.TryConnectToComfyUI: {url} => Unexpected error: {ex.Message}");
            return false;
        }
    }

    private async Task<bool> LoadWorkflowInComfyUI(string workflowJson)
    {
        try
        {
            // Check if using ComfyUI Electron
            var isElectron = _settings.ComfyUILauncherPath?.Contains("ComfyUI.exe", StringComparison.OrdinalIgnoreCase) == true;

            if (isElectron)
            {
                Logger.Log("ComfyUIService: Detected ComfyUI Electron - using file-based workflow loading");
                return await LoadWorkflowViaFile(workflowJson);
            }

            // Use detected URL if available, otherwise fall back to configured or default
            var serverUrl = _detectedServerUrl ?? _settings.ComfyUIServerUrl ?? "http://localhost:8188";
            Logger.Log($"ComfyUIService.LoadWorkflowInComfyUI: Using server URL: {serverUrl}");

            var minified = workflowJson
                .Replace("\r\n", "")
                .Replace("\n", "")
                .Replace("  ", "")
                .Replace("\t", "");

            Logger.Log($"ComfyUIService: Workflow size: {minified.Length} characters");

            if (minified.Length < 6000)
            {
                Logger.Log("ComfyUIService: Using URL parameter method");

                var encoded = System.Uri.EscapeDataString(minified);
                var urlWithWorkflow = $"{serverUrl}/?workflow={encoded}";

                Logger.Log($"ComfyUIService: Final URL length: {urlWithWorkflow.Length} characters");

                Process.Start(new ProcessStartInfo
                {
                    FileName = urlWithWorkflow,
                    UseShellExecute = true
                });

                Logger.Log("ComfyUIService: Workflow loaded via URL parameter");
                return true;
            }
            else if (_currentWorkflowFormat == WorkflowFormat.Graph)
            {
                Logger.Log($"ComfyUIService: Workflow too large ({minified.Length} chars) and is graph format - /api/prompt requires prompt format, falling through to clipboard/file methods");
                return await LoadWorkflowViaFile(workflowJson);
            }
            else
            {
                Logger.Log($"ComfyUIService: Workflow too large ({minified.Length} chars), using API method");

                await Task.Delay(3000);
                return await LoadWorkflowViaAPI(workflowJson, serverUrl);
            }
        }
        catch (Exception ex)
        {
            Logger.Log($"ComfyUIService: Failed to load workflow - {ex.Message}");
            Logger.Log($"ComfyUIService: Stack trace - {ex.StackTrace}");
            return false;
        }
    }

    private async Task<bool> LoadWorkflowViaFile(string workflowJson)
    {
        try
        {
            Logger.Log("ComfyUIService.LoadWorkflowViaFile: Starting cascading approach for Electron");

            // Minify workflow JSON
            var minified = workflowJson
                .Replace("\r\n", "")
                .Replace("\n", "")
                .Replace("  ", "")
                .Replace("\t", "");

            Logger.Log($"ComfyUIService.LoadWorkflowViaFile: Workflow size: {minified.Length} characters");

            // ============================================
            // APPROACH 1: Try URL method at port 8000
            // ============================================
            if (minified.Length < 6000)
            {
                Logger.Log("ComfyUIService.LoadWorkflowViaFile: APPROACH 1 - Trying URL method at port 8000");

                var urlLoaded = await TryLoadWorkflowViaUrl(minified);
                if (urlLoaded)
                {
                    Logger.Log("ComfyUIService.LoadWorkflowViaFile: SUCCESS via URL method!");
                    ServiceLocator.ToastService.Toast(
                        "Workflow loaded in ComfyUI!",
                        "ComfyUI",
                        3);
                    return true;
                }

                Logger.Log("ComfyUIService.LoadWorkflowViaFile: URL method failed, trying next approach");
            }
            else
            {
                Logger.Log($"ComfyUIService.LoadWorkflowViaFile: Workflow too large ({minified.Length} chars) for URL method, skipping");
            }

            // ============================================
            // APPROACH 2: Clipboard + Window Focus + Ctrl+V
            // ============================================
            Logger.Log("ComfyUIService.LoadWorkflowViaFile: APPROACH 2 - Trying clipboard + Ctrl+V method");

            var clipboardLoaded = await TryLoadWorkflowViaClipboard(workflowJson);
            if (clipboardLoaded)
            {
                Logger.Log("ComfyUIService.LoadWorkflowViaFile: SUCCESS via clipboard method!");
                ServiceLocator.ToastService.Toast(
                    "Workflow pasted into ComfyUI!",
                    "ComfyUI",
                    3);
                return true;
            }

            Logger.Log("ComfyUIService.LoadWorkflowViaFile: Clipboard method failed, using fallback");

            // ============================================
            // APPROACH 3: Fallback - Save file, open Explorer, show instructions
            // ============================================
            Logger.Log("ComfyUIService.LoadWorkflowViaFile: APPROACH 3 - Fallback to manual drag-drop");

            return await FallbackToManualDragDrop(workflowJson);
        }
        catch (Exception ex)
        {
            Logger.Log($"ComfyUIService.LoadWorkflowViaFile: Exception - {ex.Message}");
            Logger.Log($"ComfyUIService.LoadWorkflowViaFile: Stack trace - {ex.StackTrace}");
            return false;
        }
    }

    /// <summary>
    /// Attempt 1: Load workflow via URL parameter at port 8000
    /// </summary>
    private async Task<bool> TryLoadWorkflowViaUrl(string minifiedWorkflow)
    {
        try
        {
            // Try port 8000 first (ComfyUI Desktop default), then 8188 as fallback
            var ports = new[] { 8000, 8188 };

            foreach (var port in ports)
            {
                var serverUrl = $"http://localhost:{port}";

                Logger.Log($"ComfyUIService.TryLoadWorkflowViaUrl: Checking if server responds at {serverUrl}");

                // Quick check if server is running on this port
                if (!await TryConnectToComfyUI(serverUrl))
                {
                    Logger.Log($"ComfyUIService.TryLoadWorkflowViaUrl: Server not responding at port {port}");
                    continue;
                }

                Logger.Log($"ComfyUIService.TryLoadWorkflowViaUrl: Server found at port {port}, loading workflow via URL");

                var encoded = Uri.EscapeDataString(minifiedWorkflow);
                var urlWithWorkflow = $"{serverUrl}/?workflow={encoded}";

                Logger.Log($"ComfyUIService.TryLoadWorkflowViaUrl: Final URL length: {urlWithWorkflow.Length} characters");

                // Open URL in default browser
                Process.Start(new ProcessStartInfo
                {
                    FileName = urlWithWorkflow,
                    UseShellExecute = true
                });

                // Cache the working URL
                _detectedServerUrl = serverUrl;

                // Give it a moment to load
                await Task.Delay(1500);

                return true;
            }

            Logger.Log("ComfyUIService.TryLoadWorkflowViaUrl: No server responded on any port");
            return false;
        }
        catch (Exception ex)
        {
            Logger.Log($"ComfyUIService.TryLoadWorkflowViaUrl: Exception - {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Attempt 2: Copy workflow to clipboard, focus ComfyUI window, send Ctrl+V
    /// </summary>
    private async Task<bool> TryLoadWorkflowViaClipboard(string workflowJson)
    {
        try
        {
            // Use EnumWindows to find the ComfyUI window by title
            // This works for Electron apps where multiple processes exist but only one has the visible window
            IntPtr windowHandle = IntPtr.Zero;

            for (int attempt = 0; attempt < 5; attempt++)
            {
                windowHandle = FindComfyUIWindowHandle();
                if (windowHandle != IntPtr.Zero)
                {
                    Logger.Log($"ComfyUIService.TryLoadWorkflowViaClipboard: Found window on attempt {attempt + 1}");
                    break;
                }

                if (attempt < 4)
                {
                    Logger.Log($"ComfyUIService.TryLoadWorkflowViaClipboard: Window not ready, waiting... (attempt {attempt + 1}/5)");
                    await Task.Delay(1000);
                }
            }

            if (windowHandle == IntPtr.Zero)
            {
                Logger.Log("ComfyUIService.TryLoadWorkflowViaClipboard: Could not find ComfyUI window after 5 attempts");
                return false;
            }

            Logger.Log($"ComfyUIService.TryLoadWorkflowViaClipboard: Using window handle: {windowHandle}");

            // Copy workflow JSON to clipboard (must be on UI thread)
            bool clipboardSet = false;
            System.Windows.Application.Current.Dispatcher.Invoke(() =>
            {
                try
                {
                    System.Windows.Clipboard.SetText(workflowJson);
                    clipboardSet = true;
                    Logger.Log("ComfyUIService.TryLoadWorkflowViaClipboard: Workflow copied to clipboard");
                }
                catch (Exception clipEx)
                {
                    Logger.Log($"ComfyUIService.TryLoadWorkflowViaClipboard: Clipboard error - {clipEx.Message}");
                }
            });

            if (!clipboardSet)
            {
                return false;
            }

            // Restore window if minimized
            if (IsIconic(windowHandle))
            {
                Logger.Log("ComfyUIService.TryLoadWorkflowViaClipboard: Window is minimized, restoring...");
                ShowWindow(windowHandle, SW_RESTORE);
                await Task.Delay(500);
            }

            // Force window to front with multiple methods for Electron compatibility
            Logger.Log("ComfyUIService.TryLoadWorkflowViaClipboard: Bringing ComfyUI to foreground...");
            ShowWindow(windowHandle, SW_SHOW);
            SetForegroundWindow(windowHandle);
            await Task.Delay(300);

            // Second call to SetForegroundWindow - sometimes needed for Electron
            SetForegroundWindow(windowHandle);
            await Task.Delay(500);

            // Use low-level keyboard events that Electron can receive (SendKeys doesn't work with Electron)
            Logger.Log("ComfyUIService.TryLoadWorkflowViaClipboard: Sending Ctrl+V via keybd_event...");

            // Press Ctrl
            keybd_event(VK_CONTROL, 0, 0, UIntPtr.Zero);
            await Task.Delay(50);

            // Press V
            keybd_event(VK_V, 0, 0, UIntPtr.Zero);
            await Task.Delay(50);

            // Release V
            keybd_event(VK_V, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
            await Task.Delay(50);

            // Release Ctrl
            keybd_event(VK_CONTROL, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);

            // Wait a moment to see if it worked
            await Task.Delay(1000);

            Logger.Log("ComfyUIService.TryLoadWorkflowViaClipboard: Ctrl+V sent via keybd_event successfully");
            return true;
        }
        catch (Exception ex)
        {
            Logger.Log($"ComfyUIService.TryLoadWorkflowViaClipboard: Exception - {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Find the ComfyUI window handle - prioritizes the actual ComfyUI.exe process window
    /// </summary>
    private IntPtr FindComfyUIWindowHandle()
    {
        // BEST: Get the main window handle directly from ComfyUI.exe process
        var comfyProcesses = Process.GetProcessesByName("ComfyUI");
        if (comfyProcesses.Length > 0)
        {
            var mainWindow = comfyProcesses[0].MainWindowHandle;
            if (mainWindow != IntPtr.Zero)
            {
                Logger.Log($"ComfyUIService.FindComfyUIWindowHandle: Found ComfyUI.exe main window (handle: {mainWindow})");
                return mainWindow;
            }
            Logger.Log("ComfyUIService.FindComfyUIWindowHandle: ComfyUI.exe process found but no main window handle");
        }

        // FALLBACK: EnumWindows but exclude our own app's windows
        IntPtr foundHandle = IntPtr.Zero;

        EnumWindows((hWnd, lParam) =>
        {
            if (!IsWindowVisible(hWnd)) return true; // continue enumeration

            var sb = new StringBuilder(256);
            GetWindowText(hWnd, sb, 256);
            var title = sb.ToString();

            // Must contain "ComfyUI" but NOT be from our app
            // Our app has windows like "ComfyUI Electron Integration", "Diffusion Toolkit"
            if (title.Contains("ComfyUI", StringComparison.OrdinalIgnoreCase) &&
                !title.Contains("Integration", StringComparison.OrdinalIgnoreCase) &&
                !title.Contains("Toolkit", StringComparison.OrdinalIgnoreCase) &&
                !title.Contains("Diffusion", StringComparison.OrdinalIgnoreCase))
            {
                Logger.Log($"ComfyUIService.FindComfyUIWindowHandle: Found window via EnumWindows: '{title}' (handle: {hWnd})");
                foundHandle = hWnd;
                return false; // stop enumeration
            }
            return true; // continue enumeration
        }, IntPtr.Zero);

        if (foundHandle == IntPtr.Zero)
        {
            Logger.Log("ComfyUIService.FindComfyUIWindowHandle: No ComfyUI window found");
        }

        return foundHandle;
    }

    /// <summary>
    /// Find the ComfyUI process (Electron or Python-based)
    /// </summary>
    private Process? FindComfyUIProcess()
    {
        try
        {
            // Try ComfyUI Electron first
            var comfyProcesses = Process.GetProcessesByName("ComfyUI");
            if (comfyProcesses.Length > 0)
            {
                Logger.Log($"ComfyUIService.FindComfyUIProcess: Found {comfyProcesses.Length} ComfyUI.exe process(es)");
                return comfyProcesses[0];
            }

            // Try finding by window title containing "ComfyUI"
            foreach (var process in Process.GetProcesses())
            {
                try
                {
                    if (!string.IsNullOrEmpty(process.MainWindowTitle) &&
                        process.MainWindowTitle.Contains("ComfyUI", StringComparison.OrdinalIgnoreCase))
                    {
                        Logger.Log($"ComfyUIService.FindComfyUIProcess: Found process by window title: {process.ProcessName} - {process.MainWindowTitle}");
                        return process;
                    }
                }
                catch
                {
                    // Some processes may not be accessible
                }
            }

            Logger.Log("ComfyUIService.FindComfyUIProcess: No ComfyUI process found");
            return null;
        }
        catch (Exception ex)
        {
            Logger.Log($"ComfyUIService.FindComfyUIProcess: Exception - {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Fallback: Save workflow to temp file, open Explorer, copy path to clipboard
    /// </summary>
    private async Task<bool> FallbackToManualDragDrop(string workflowJson)
    {
        try
        {
            // Create temp JSON file
            var tempFile = Path.Combine(Path.GetTempPath(), $"comfyui_workflow_{Guid.NewGuid():N}.json");

            Logger.Log($"ComfyUIService.FallbackToManualDragDrop: Writing workflow to {tempFile}");
            await File.WriteAllTextAsync(tempFile, workflowJson);

            // Copy file path to clipboard for easy access
            try
            {
                System.Windows.Application.Current.Dispatcher.Invoke(() =>
                {
                    System.Windows.Clipboard.SetText(tempFile);
                });
                Logger.Log("ComfyUIService.FallbackToManualDragDrop: File path copied to clipboard");
            }
            catch (Exception clipEx)
            {
                Logger.Log($"ComfyUIService.FallbackToManualDragDrop: Clipboard failed - {clipEx.Message}");
            }

            // Open Explorer with the temp file selected
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    Arguments = $"/select,\"{tempFile}\"",
                    UseShellExecute = true
                });
                Logger.Log($"ComfyUIService.FallbackToManualDragDrop: Opened Explorer with file selected");
            }
            catch (Exception explorerEx)
            {
                Logger.Log($"ComfyUIService.FallbackToManualDragDrop: Explorer failed - {explorerEx.Message}");
            }

            ServiceLocator.ToastService.Toast(
                $"Automatic loading failed.\n\nDrag this file into ComfyUI:\n{Path.GetFileName(tempFile)}\n\n(Path copied to clipboard)",
                "ComfyUI - Drag File",
                15);

            // Keep the temp file around longer for manual drag
            ScheduleTempFileCleanup(tempFile, 120000); // 2 minutes

            return true; // Return true since we created the file successfully
        }
        catch (Exception ex)
        {
            Logger.Log($"ComfyUIService.FallbackToManualDragDrop: Exception - {ex.Message}");
            return false;
        }
    }

    private void ScheduleTempFileCleanup(string tempFile, int delayMs = 30000)
    {
        _ = Task.Run(async () =>
        {
            await Task.Delay(delayMs);
            try
            {
                if (File.Exists(tempFile))
                {
                    File.Delete(tempFile);
                    Logger.Log($"ComfyUIService: Deleted temp workflow file: {tempFile}");
                }
            }
            catch (Exception cleanupEx)
            {
                Logger.Log($"ComfyUIService: Failed to delete temp file: {cleanupEx.Message}");
            }
        });
    }

    private async Task<bool> LoadWorkflowViaAPI(string workflowJson, string serverUrl)
    {
        try
        {
            using var httpClient = new HttpClient();
            httpClient.Timeout = TimeSpan.FromSeconds(10);

            var content = new StringContent(
                $"{{\"prompt\": {workflowJson}}}",
                Encoding.UTF8,
                "application/json"
            );

            Logger.Log($"ComfyUIService.LoadWorkflowViaAPI: Posting to {serverUrl}/api/prompt");
            var response = await httpClient.PostAsync($"{serverUrl}/api/prompt", content);

            var responseText = await response.Content.ReadAsStringAsync();
            Logger.Log($"ComfyUIService.LoadWorkflowViaAPI: Response status: {response.StatusCode}");
            Logger.Log($"ComfyUIService.LoadWorkflowViaAPI: Response body: {responseText}");

            if (response.IsSuccessStatusCode)
            {
                if (responseText.Contains("\"error\""))
                {
                    Logger.Log("ComfyUIService.LoadWorkflowViaAPI: API returned error in response body");
                    return false;
                }

                Logger.Log("ComfyUIService.LoadWorkflowViaAPI: Workflow queued successfully");
                return true;
            }
            else
            {
                Logger.Log($"ComfyUIService.LoadWorkflowViaAPI: HTTP error - {response.StatusCode}");
                return false;
            }
        }
        catch (Exception ex)
        {
            Logger.Log($"ComfyUIService.LoadWorkflowViaAPI: Exception - {ex.Message}");
            Logger.Log($"ComfyUIService.LoadWorkflowViaAPI: Stack trace - {ex.StackTrace}");
            return false;
        }
    }
}
