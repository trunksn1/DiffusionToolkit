# CivitAI Upload Automation - Strategic Analysis & Plan

**Date**: 2026-01-02
**Objective**: Automatically upload images to CivitAI from Diffusion-Toolkit
**Constraint**: No official upload API available

---

## Problem Statement

CivitAI does not provide a public API for uploading images. Users must:
1. Navigate to https://civitai.com/posts/create
2. Be logged in (typically via Google OAuth)
3. Select an image file via file picker
4. Fill in metadata (title, description, tags)
5. Publish the post

**Challenge**: Google OAuth and CivitAI detect browser automation and block login/functionality.

---

## What We've Already Tried (Failures)

### ❌ Attempt 1: Playwright with Chromium
**Approach**: Use Playwright's bundled Chromium browser
```csharp
var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
{
    Headless = false
});
```

**Result**: FAILED
- Google OAuth shows "This browser or app may not be secure"
- CivitAI blocks login
- User cannot authenticate

**Root Cause**: Automation flags in browser (`navigator.webdriver === true`)

---

### ❌ Attempt 2: Playwright with Chrome Channel
**Approach**: Use system-installed Chrome instead of Chromium
```csharp
var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
{
    Channel = "chrome",
    Headless = false
});
```

**Result**: FAILED
- Same automation detection
- Google still blocks login
- Different Chrome profile than user's regular one

**Root Cause**: Playwright-launched Chrome still has automation flags

---

### ❌ Attempt 3: Playwright with User Profile
**Approach**: Launch Chrome with user's actual profile where they're already logged in
```csharp
var context = await playwright.Chromium.LaunchPersistentContextAsync(
    chromeUserData,
    new BrowserTypeLaunchPersistentContextOptions
    {
        Channel = "chrome",
        Args = new[] { "--disable-blink-features=AutomationControlled" }
    });
```

**Result**: FAILED
- Error: Chrome profile in use (user has Chrome open)
- When Chrome closed: Still detected as automation
- Google blocks login anyway

**Root Cause**:
1. Profile locking (can't use same profile twice)
2. Even with flags, automation still detectable

---

### ❌ Attempt 4: Keyboard Automation (InputSimulator)
**Approach**: Open real browser, then use Windows keyboard automation
```csharp
simulator.Keyboard.KeyPress(VirtualKeyCode.TAB);
simulator.Keyboard.TextEntry(imagePath);
```

**Result**: NOT TESTED (rejected before implementation)
- Too fragile (depends on exact page structure)
- Tab count varies by page changes
- No way to verify upload succeeded
- Not a reliable long-term solution

**Why Rejected**: Unmaintainable approach

---

## Viable Approaches to Explore

### Option 1: Selenium with Undetected ChromeDriver ⭐ RECOMMENDED

**Concept**: Use specialized ChromeDriver that bypasses automation detection

**Technology**:
- **Python**: `undetected-chromedriver` library (very effective)
- **.NET**: Could call Python script from C#, OR
- **.NET**: Use Selenium with custom Chrome flags and CDP commands

**How It Works**:
1. Launches Chrome with patches that hide automation flags
2. Removes `navigator.webdriver` property
3. Randomizes user agent and browser fingerprints
4. Uses real Chrome with user profile

**Implementation Path**:
```
Option A: Python Bridge
- Create Python script using undetected-chromedriver
- Call from C# using Process.Start()
- Pass image path as argument
- Python handles entire upload process

Option B: Pure .NET
- Use Selenium WebDriver for .NET
- Apply anti-detection techniques manually
- Connect to existing Chrome instance via debugging port
```

**Pros**:
- ✅ Proven to bypass Google/CivitAI detection
- ✅ Uses user's real Chrome profile (already logged in)
- ✅ Widely used for automation of protected sites
- ✅ Can verify upload success

**Cons**:
- ⚠️ Option A requires Python installation
- ⚠️ May break if CivitAI updates detection methods
- ⚠️ Requires ongoing maintenance

**Complexity**: Medium
**Success Probability**: 85%

---

### Option 2: Chrome DevTools Protocol (CDP) - Direct Attachment ⭐⭐ BEST TECHNICAL SOLUTION

**Concept**: Connect to user's ALREADY-RUNNING Chrome browser

**How It Works**:
1. User starts Chrome normally (with their profile, already logged in)
2. User launches Chrome with remote debugging: `chrome.exe --remote-debugging-port=9222`
3. .NET app connects to this Chrome instance via CDP
4. No automation flags because it's the user's real session
5. App can control the browser via CDP commands

**Implementation**:
```csharp
// Connect to existing Chrome
var selenium = new ChromeDriver(new ChromeOptions
{
    DebuggerAddress = "localhost:9222"
});

// Navigate and interact normally
selenium.Navigate().GoToUrl("https://civitai.com/posts/create");
var fileInput = selenium.FindElement(By.CssSelector("input[type='file']"));
fileInput.SendKeys(imagePath);
```

**Pros**:
- ✅ NO automation detection (it's user's real browser)
- ✅ User already logged in
- ✅ Full control via Selenium/CDP
- ✅ Can verify upload succeeded
- ✅ Most reliable long-term

**Cons**:
- ⚠️ Requires user to start Chrome with debug flag
- ⚠️ Extra setup step for users
- ⚠️ Only one app can connect at a time

**User Workflow**:
1. User starts Chrome once with: `chrome.exe --remote-debugging-port=9222` (can be automated via shortcut)
2. User logs into CivitAI normally (one-time)
3. From then on, app can automate uploads

**Complexity**: Medium
**Success Probability**: 95%

---

### Option 3: Puppeteer Sharp with Stealth Plugin

**Concept**: Use Puppeteer Sharp (.NET port of Puppeteer) with anti-detection

**Technology**:
- `PuppeteerSharp` NuGet package
- Apply stealth techniques similar to `puppeteer-extra-plugin-stealth`

**How It Works**:
1. Launch Chrome with Puppeteer Sharp
2. Apply JavaScript patches to hide automation
3. Use user data directory for existing session
4. Interact with page like Playwright

**Implementation**:
```csharp
var browser = await Puppeteer.LaunchAsync(new LaunchOptions
{
    Headless = false,
    UserDataDir = "path/to/chrome/profile",
    Args = new[] {
        "--disable-blink-features=AutomationControlled",
        "--disable-features=IsolateOrigins,site-per-process"
    }
});

// Apply stealth patches via JavaScript
await page.EvaluateFunctionOnNewDocumentAsync(@"
    Object.defineProperty(navigator, 'webdriver', { get: () => false });
");
```

**Pros**:
- ✅ Pure .NET solution
- ✅ Can use user profile
- ✅ Stealth techniques reduce detection

**Cons**:
- ⚠️ Stealth plugin not as mature as Python version
- ⚠️ May still get detected
- ⚠️ Requires profile not to be in use

**Complexity**: Medium
**Success Probability**: 60%

---

### Option 4: Hybrid Approach - Manual Login, Auto Upload

**Concept**: User handles login, automation handles upload

**How It Works**:
1. App opens CivitAI in user's browser
2. App waits for user to log in (if needed)
3. User clicks "Ready" button in app
4. App takes over and automates file selection
5. App pre-fills metadata
6. User clicks Publish

**Implementation**:
- Use any browser automation (Selenium, Playwright, Puppeteer)
- Don't worry about login detection
- Only automate after user confirms they're logged in

**Pros**:
- ✅ Bypasses login detection entirely
- ✅ Simpler to implement
- ✅ More reliable
- ✅ User has control

**Cons**:
- ⚠️ Not fully automated (user interaction required)
- ⚠️ Defeats purpose if user has to supervise

**Complexity**: Low
**Success Probability**: 100% (for upload part)

---

### Option 5: AutoIt/AutoHotkey for Windows UI Automation

**Concept**: Automate Windows UI instead of browser

**How It Works**:
1. User opens browser manually and logs in
2. App uses AutoIt/AutoHotkey to:
   - Click on upload area (by screen coordinates or image recognition)
   - Handle file picker dialog
   - Type in metadata fields
3. Pure Windows-level automation

**Implementation**:
```csharp
// Use AutoIt COM interface from C#
AutoItX3 au3 = new AutoItX3();
au3.WinActivate("CivitAI"); // Focus browser
au3.MouseClick("left", 500, 300); // Click upload (coordinates)
au3.WinWaitActive("Open"); // Wait for file dialog
au3.ControlSetText("Open", "", "Edit1", imagePath); // Set file path
au3.ControlClick("Open", "", "Button1"); // Click Open
```

**Pros**:
- ✅ No browser automation detection
- ✅ Works regardless of browser type
- ✅ User's real browser session

**Cons**:
- ❌ VERY fragile (depends on pixel coordinates)
- ❌ Breaks if window resized or moved
- ❌ Breaks if CivitAI changes UI layout
- ❌ Different on different screen resolutions
- ❌ Not maintainable

**Complexity**: High (to make reliable)
**Success Probability**: 40%

---

## Recommended Implementation Plan

### Phase 1: Quick Win - CDP with Existing Chrome (Recommended Start)

**Why**: Highest success rate, user already has Chrome

**Steps**:
1. Create a batch file/shortcut that starts Chrome with debugging:
   ```
   "C:\Program Files\Google\Chrome\Application\chrome.exe" --remote-debugging-port=9222 --user-data-dir="%USERPROFILE%\AppData\Local\Google\Chrome\User Data"
   ```

2. User runs this shortcut once (replaces normal Chrome launch)

3. App uses Selenium WebDriver to connect:
   ```csharp
   var options = new ChromeOptions();
   options.DebuggerAddress = "localhost:9222";
   var driver = new ChromeDriver(options);
   ```

4. App automates upload:
   - Navigate to CivitAI
   - Find file input: `driver.FindElement(By.CssSelector("input[type='file']"))`
   - Upload file: `fileInput.SendKeys(imagePath)`
   - Wait for upload to complete
   - Show success message

**Deliverables**:
- Chrome debug launcher (batch file)
- Updated CivitaiPostService.cs
- Setup instructions for users
- Error handling for connection failures

**Timeline**: 2-3 hours

---

### Phase 2: Fallback - Undetected ChromeDriver (If Phase 1 Fails)

**Why**: Proven to work with Google OAuth

**Steps**:
1. Create Python script using `undetected-chromedriver`:
   ```python
   import undetected_chromedriver as uc
   driver = uc.Chrome()
   driver.get('https://civitai.com/posts/create')
   # Upload automation
   ```

2. Package Python script with app (or require Python installation)

3. C# calls Python script:
   ```csharp
   var psi = new ProcessStartInfo
   {
       FileName = "python",
       Arguments = $"civitai_upload.py \"{imagePath}\"",
       RedirectStandardOutput = true
   };
   ```

**Deliverables**:
- Python upload script
- C# wrapper to call script
- Python installation check/instructions
- Error handling for Python not installed

**Timeline**: 4-5 hours

---

### Phase 3: Polish - Stealth Selenium (Advanced Option)

**Why**: Pure .NET, no external dependencies

**Steps**:
1. Use Selenium with custom options to hide automation
2. Load user profile manually
3. Apply JavaScript patches to remove webdriver flag
4. Test with CivitAI/Google detection

**Implementation requires**:
- Research current stealth techniques
- Test against Google's detection
- Possibly requires ongoing updates

**Timeline**: 6-8 hours (research intensive)

---

## Decision Matrix

| Approach | Success Rate | Complexity | User Setup | Maintenance | Recommended |
|----------|-------------|------------|------------|-------------|-------------|
| **CDP + Existing Chrome** | 95% | Medium | Medium | Low | ⭐⭐⭐ YES |
| **Undetected ChromeDriver (Python)** | 85% | Medium | High | Medium | ⭐⭐ Fallback |
| **Puppeteer Sharp Stealth** | 60% | High | Low | High | ⭐ Maybe |
| **Hybrid Manual Login** | 100% | Low | Low | Low | ⭐⭐ If automation fails |
| **AutoIt/Coordinates** | 40% | Very High | Low | Very High | ❌ No |

---

## Final Recommendation

### Start with Option 2: CDP Connection to Existing Chrome

**Reasoning**:
1. Highest success probability (95%)
2. Uses user's real browser (no detection issues)
3. Pure .NET solution (no Python dependency)
4. User stays logged in
5. Most maintainable long-term

**Implementation Order**:
1. ✅ **Week 1**: CDP + Selenium approach
2. ⏸️ **If CDP fails**: Undetected ChromeDriver (Python bridge)
3. ⏸️ **If both fail**: Hybrid manual login approach
4. ❌ **Never**: AutoIt/coordinate-based automation

**User Experience**:
- One-time setup: Install Chrome launcher shortcut
- Every use: Click "Post to CivitAI" in app
- Fully automated upload
- Can verify success

---

## Next Steps

1. **Get User Approval**: Confirm CDP approach is acceptable (requires Chrome debug mode)
2. **Prototype**: Build CDP connection and test file upload
3. **Refine**: Handle edge cases (Chrome not running, connection failures)
4. **Document**: Create user setup guide
5. **Test**: Verify with actual CivitAI upload

---

## Questions to Resolve Before Implementation

1. **Is requiring Chrome debug mode acceptable for users?**
   - Alternative: Would they prefer Python dependency instead?

2. **Should we auto-start Chrome in debug mode?**
   - Or provide a shortcut for users to launch manually?

3. **How to handle errors gracefully?**
   - If Chrome not in debug mode, show instructions?
   - Fallback to clipboard method?

4. **Should we verify upload succeeded?**
   - Wait for upload progress indicator?
   - Check for success message on page?

---

## References

- Chrome DevTools Protocol: https://chromedevtools.github.io/devtools-protocol/
- Selenium WebDriver .NET: https://www.selenium.dev/documentation/webdriver/
- Undetected ChromeDriver: https://github.com/ultrafunkamsterdam/undetected-chromedriver
- Puppeteer Sharp: https://www.puppeteersharp.com/
