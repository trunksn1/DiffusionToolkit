# CivitAI Post Feature - Setup Instructions

## Overview

The CivitAI post feature uses **browser automation** to automatically upload images to CivitAI. This eliminates the need for manual copy/paste and provides a seamless experience.

---

## Prerequisites

1. **.NET 6 SDK or higher** (you can use .NET 10)
   - Download: https://dotnet.microsoft.com/download

2. **Playwright Browsers** (installed after first build)

---

## Setup Steps

### Step 1: Build the Project

```bash
cd "E:\Clouding\Dropbox\INFORMATICA\GitHub\Diffusion-Toolkit"
dotnet build
```

This will:
- Download the Microsoft.Playwright NuGet package
- Build the application with browser automation support

### Step 2: Install Playwright Browsers

After building, you need to install the browser binaries (Chromium, Firefox, or WebKit).

**Option A: PowerShell (Recommended)**
```powershell
pwsh Diffusion.Toolkit\bin\Debug\net6.0-windows\playwright.ps1 install
```

**Option B: Command Line**
```bash
dotnet tool install --global Microsoft.Playwright.CLI
playwright install
```

**Note**: This only needs to be done once. The browsers will be installed in your user profile.

---

## How It Works

### User Workflow

1. **Select Image(s)**: In Diffusion-Toolkit, select one or more images
2. **Right-Click**: Open context menu
3. **Click "Post to CivitAI"**: Select the menu item
4. **Automated Upload**:
   - Browser window opens automatically (visible, not headless)
   - Navigates to CivitAI upload page
   - Automatically selects and uploads your image
   - Waits for you to complete the form
5. **Complete Details**: Fill in title, description, tags, etc.
6. **Publish**: Click the Publish button on CivitAI

### What's Automated

- ✅ Opens browser
- ✅ Navigates to CivitAI upload page
- ✅ Selects the image file
- ✅ Uploads the image

### What's Manual

- ⚠️ Title, description, tags (you fill these in)
- ⚠️ Publishing the post (you click Publish)

**Why not fully automated?** CivitAI requires you to be logged in, and they may have CAPTCHA or other protections. This hybrid approach gives you control while automating the tedious file upload part.

---

## Troubleshooting

### Error: "Playwright not installed"

**Solution**: Run the browser installation command:
```powershell
pwsh Diffusion.Toolkit\bin\Debug\net6.0-windows\playwright.ps1 install
```

### Error: "Could not find browser"

**Solution**: Make sure you ran the `playwright install` command after building.

### Error: "Browser automation failed"

**Possible causes**:
1. CivitAI changed their website structure (file input selector needs updating)
2. Network connection issue
3. Browser didn't launch properly

**Solution**: Check the error message in the toast notification. If it's a selector issue, you may need to update the code in `CivitaiPostService.cs` line 111.

### Browser doesn't find the upload field

CivitAI's website structure may have changed. You can update the selector:

1. Open `Diffusion.Toolkit\Services\CivitaiPostService.cs`
2. Find line ~111: `var fileInput = page.Locator("input[type='file']").First;`
3. Update the selector based on CivitAI's current HTML structure

**How to find the right selector**:
1. Go to https://civitai.com/posts/create in your browser
2. Right-click the image upload area → Inspect Element
3. Look for the `<input type="file">` element
4. Note any `id`, `class`, or `name` attributes
5. Update the selector accordingly

Examples:
```csharp
// By ID
var fileInput = page.Locator("#image-upload").First;

// By class
var fileInput = page.Locator(".upload-input").First;

// By multiple attributes
var fileInput = page.Locator("input[type='file'][accept*='image']").First;
```

---

## Advanced Configuration

### Use a Different Browser

By default, the automation uses **Chromium**. You can switch to Firefox or WebKit:

Edit `CivitaiPostService.cs` line ~92:

```csharp
// For Firefox
var browser = await playwright.Firefox.LaunchAsync(new BrowserTypeLaunchOptions
{
    Headless = false
});

// For WebKit (Safari engine)
var browser = await playwright.Webkit.LaunchAsync(new BrowserTypeLaunchOptions
{
    Headless = false
});
```

### Headless Mode (No Visible Browser)

If you want the browser to run in the background:

```csharp
var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
{
    Headless = true // Change to true
});
```

**Warning**: Headless mode may not work well with CivitAI if they have anti-bot protections.

---

## Future Enhancements

When CivitAI releases a direct upload API (they're working on it), this feature can be upgraded to:
- ✅ Fully automated upload (no browser)
- ✅ Batch upload multiple images
- ✅ Pre-fill metadata from image EXIF/parameters
- ✅ Get upload confirmation programmatically

---

## Technical Details

**Libraries Used**:
- **Microsoft.Playwright** (v1.49.0): Browser automation library
- Uses Chromium browser by default
- Launches visible browser for transparency

**Code Location**:
- Service: `Diffusion.Toolkit/Services/CivitaiPostService.cs`
- Command wiring: `Diffusion.Toolkit/Controls/ThumbnailView.xaml.cs`
- UI: `Diffusion.Toolkit/Controls/ThumbnailView.xaml` (context menu)

**Network Requirements**:
- Internet connection required
- Access to https://civitai.com
- No proxy configuration needed (uses system defaults)

---

## Support

If you encounter issues:

1. Check this troubleshooting guide first
2. Verify Playwright browsers are installed
3. Check the application's toast notifications for error details
4. Report issues with error messages and screenshots

---

## License

This feature is part of Diffusion-Toolkit, licensed under the same terms as the main application.
