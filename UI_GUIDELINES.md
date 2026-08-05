# Diffusion Toolkit — UI Guidelines

How a window, dialog or panel in this app has to be built so it comes out
looking like the rest of the app. Written after a new dialog shipped with
black text on a navy background: everything in here is a mistake that has
actually been made in this codebase, not general advice.

Read this **before** writing XAML, not after the screenshot comes back wrong.

---

## The one-minute version

1. Theme resources come from `Themes/Common.xaml`. **If a key is not in there,
   it does not exist** and your `DynamicResource` silently resolves to nothing.
2. The implicit `TextBlock` style sets **no `Foreground`**. An unqualified
   `TextBlock` is **black**, which is invisible on the dark theme.
3. Dialogs are `local:BorderlessWindow` with
   `Style="{StaticResource BorderlessWindowStyle}"`, not bare `Window`.
4. Never hardcode a colour that has to work in both themes.
5. A local value on an element **beats every Style trigger**. This has broken
   working triggers twice.
6. Render it before you claim it works. There is a harness for that — see
   [Verifying](#verifying-before-you-say-it-works).

---

## 1. The brushes that exist

Defined in `Themes/Common.xaml`, colours swapped at runtime by
`Themes/ThemeManager.cs` between `Themes/Dark.xaml` and `Themes/Light.xaml`.

| Brush | Use for |
| --- | --- |
| `PrimaryBrush` | Window / page background. This is the app's "Background" colour. |
| `ControlBackgroundBrush` | Raised surfaces: cards, info boxes, input rows. |
| `PreviewBackgroundBrush` | Image preview areas. |
| `ForegroundBrush` | **All** text and glyphs. |
| `DisabledForegroundBrush`, `DisabledBackgroundBrush` | Disabled states. |
| `SecondaryBrush` / `SplitterBrush` | Borders, separators, splitters. |
| `HighlightBrush`, `HighlightFocusedBrush`, `FocusedBrush`, `BgHighlightBrush` | Selection and hover. |
| `ToastBrush` | Toast notifications. |
| `NormalBorderBrush` | Control borders (defined in `Themes/Scrollbars.xaml`). |

### Keys that DO NOT exist — do not use them

- **`BackgroundBrush`** — never defined. The window still looks right, which is
  what makes this so easy to miss: the implicit `Style TargetType="Grid"` in
  `Common.xaml` paints every `Grid` with `PrimaryBrush`, so the background
  arrives anyway and the broken reference goes unnoticed. Use `PrimaryBrush`.
- **`MahApps.Brushes.*`** (e.g. `MahApps.Brushes.Gray1`) — MahApps is not in
  this app. These resolve to null and render **black**.

Both were live in shipped windows. `CivitaiAlbumSelectionWindow.xaml` and
`TagManagerWindow.xaml` still reference `BackgroundBrush`; they render by
accident, per above. Fix them if you are in there anyway.

> **Rule:** before using a resource key, grep it:
> `grep -rn 'x:Key="YourBrush"' Diffusion.Toolkit/Themes/`
> No hit means no brush. WPF will not warn you.

---

## 2. Text is black unless you say otherwise

`Themes/Common.xaml` has:

```xml
<Style TargetType="TextBlock">
    <Setter Property="Background" Value="Transparent"/>
    <Setter Property="VerticalAlignment" Value="Center"/>
</Style>
```

No `Foreground`. So every `TextBlock` inherits WPF's default black. On the dark
theme (`#182B3C` navy) that is unreadable — the exact bug this document exists
for.

Confusingly, **some** text is fine without any effort, which is why the mistake
survives review: the `CheckBox` / `RadioButton` control templates set
`TextBlock.Foreground="{DynamicResource ForegroundBrush}"` on their
`ContentPresenter`, so control *labels* inherit a correct colour while the
paragraph underneath them stays black. If half your window looks right, that is
this.

**Preferred fix — one style per window, not one attribute per element:**

```xml
<Window.Resources>
    <Style TargetType="TextBlock" BasedOn="{StaticResource {x:Type TextBlock}}">
        <Setter Property="Foreground" Value="{DynamicResource ForegroundBrush}"/>
    </Style>
</Window.Resources>
```

Derive the rest from it (`BasedOn="{StaticResource {x:Type TextBlock}}"` keeps
the app's setters):

```xml
<Style x:Key="OptionDetail" TargetType="TextBlock" BasedOn="{StaticResource {x:Type TextBlock}}">
    <Setter Property="Foreground" Value="{DynamicResource ForegroundBrush}"/>
    <Setter Property="Opacity" Value="0.75"/>
</Style>
```

Setting `Foreground` on each element also works and is what
`Pages/CivitaiCalendar.xaml` does — fine for a page you are editing, but on a
new window one forgotten element is one unreadable paragraph.

Applies equally to `Label`, `Run`, and anything else that renders text.

### Dimming secondary text

Use `Opacity` on `ForegroundBrush` (0.6–0.8), never a literal grey. A grey
chosen against navy vanishes on the light theme. `Opacity` stays correct in
both because it dims whatever the theme's foreground currently is.

---

## 3. Window chrome

Dialogs use the app's own chrome, not the OS title bar:

```xml
<local:BorderlessWindow x:Class="Diffusion.Toolkit.YourWindow"
        xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
        xmlns:local="clr-namespace:Diffusion.Toolkit"
        WindowStartupLocation="CenterOwner"
        ShowInTaskbar="False"
        MenuWidth="0"
        TitleWidth="*"
        WindowStyle="ToolWindow"
        Style="{StaticResource BorderlessWindowStyle}"
        Title="Your Window" Height="400" Width="520">
```

and the code-behind class must derive from `BorderlessWindow`, not `Window`.

- The template draws a 36px header (centred title + window buttons) **over**
  the content, so the root grid needs a top margin of ~20–28:
  `<Grid Margin="16,28,16,16">`. Existing windows use `Margin="5,20,5,5"` or
  `Margin="0,20,0,0"`.
- `BorderlessWindowStyle` sets `Title` to `{Binding Title}` for MVVM windows. A
  literal `Title="…"` on your element overrides it (local value beats style
  setter — see §4), so plain dialogs work without a view model.
- `MinHeight` 320 / `MinWidth` 480 come from the style.
- Give `Cancel` `IsCancel="True"` and the primary action `IsDefault="True"`;
  that wires Esc and Enter without a `KeyBinding`.

A bare `Window` gets the native Windows title bar and looks pasted in from
another application. Compare `AlbumSortWindow.xaml` (correct) with what a plain
`<Window>` produces.

---

## 4. Local values beat Style triggers

WPF's dependency-property precedence puts a **local value** (an attribute
written on the element) above **style triggers**. So this silently does nothing:

```xml
<!-- WRONG: BorderBrush is set locally, the trigger can never win -->
<Border BorderBrush="{DynamicResource NormalBorderBrush}">
    <Border.Style>
        <Style TargetType="Border">
            <Style.Triggers>
                <DataTrigger Binding="{Binding IsToday}" Value="True">
                    <Setter Property="BorderBrush" Value="{StaticResource TodayBrush}"/>
                </DataTrigger>
            </Style.Triggers>
        </Style>
    </Border.Style>
</Border>
```

Put defaults in `Style` setters instead:

```xml
<Border>
    <Border.Style>
        <Style TargetType="Border">
            <Setter Property="BorderBrush" Value="{DynamicResource NormalBorderBrush}"/>
            <Style.Triggers>
                <DataTrigger Binding="{Binding IsToday}" Value="True">
                    <Setter Property="BorderBrush" Value="{StaticResource TodayBrush}"/>
                </DataTrigger>
            </Style.Triggers>
        </Style>
    </Border.Style>
</Border>
```

**The tell:** some triggered properties work and others don't. In the calendar's
day cell, `Opacity` (no local value) dimmed correctly while `BorderBrush`,
`Background` and `BorderThickness` (all local) did nothing — so "today" and the
selected day looked identical to every other cell. If a trigger appears to run
but change nothing, look for a local value on the element before doubting the
binding.

---

## 5. Layout conventions

- Dialog padding: `16`. Section spacing: `8–14`. Related controls: `4–8`.
- Section headings: `FontSize="14" FontWeight="SemiBold"`, `Margin="0,0,0,8"`.
- Body text: default size. Explanatory/secondary text: `FontSize="11"` at
  `Opacity` 0.75, `TextWrapping="Wrap"`.
- Buttons: `Padding="16,4"`, `8` between them, right-aligned, destructive or
  cancelling action on the left of the primary one.
- Info/summary boxes: `Border` with `ControlBackgroundBrush` background,
  `SecondaryBrush` border, `CornerRadius="3"`, `Padding="10,8"`.
- Fixed-size dialogs (`ResizeMode="NoResize"` or a `Height`) must fit their
  content at 100% **and** 125% DPI. Prefer `Height="Auto"`-ish layouts, or
  leave a star row so growth is absorbed.
- Any text that can grow (a status line, a path, a server message) needs
  `TextWrapping="Wrap"` or `TextTrimming="CharacterEllipsis"`.

### Colours that are deliberately not themed

Semantic accents — the calendar's today-blue `#2E8BFF`, viewing-green `#22C55E`,
scheduled-amber `#E0A800` — are fixed by design: they must stay recognisable in
both themes, and the alpha channel does the blending against either background.
Keep them in one `Resources` block with a comment saying why, as
`Pages/CivitaiCalendar.xaml` does. This is the exception, not licence to
hardcode.

### Unstyled controls

`DatePicker` has no theme style in this app and renders in the default Windows
look. That is a known inconsistency, present in every dialog that uses one. Do
not fix it locally in one window — that makes it *more* inconsistent.

---

## 6. Wording

The calendar dialogs set the standard, and it is worth keeping:

- Say what an action **costs** before the user picks it — "one extra request for
  every post in the period", not "may be slow".
- Never show a number you do not have. A counter the API did not report is
  hidden, not rendered as `0`; a `0` on screen must mean a real zero. See
  `ResolvedPostImage.ShowComments` / `StatsAreImageWide`.
- Error and empty states say what to do next, and name the button that does it.

---

## Verifying before you say it works

Launching the whole app is heavy and touches the user's real library. To check
one window, render it in isolation against the real theme dictionaries:

1. Create a small WPF harness project referencing `Diffusion.Toolkit.csproj`.
2. Build an `Application`, merge
   `pack://application:,,,/Diffusion.Toolkit;component/Themes/{Dark|Light}.xaml`
   plus `Common.xaml`, `SWStyles.xaml`, `Window.xaml`.
3. Show your window, then on `ContentRendered` draw `window.Background` into a
   `RenderTargetBitmap` and render the content visual over it — the background
   lives in the control template, so without that first fill the PNG is
   transparent and any contrast judgement is worthless.
4. Save a PNG for **both** themes and look at them.

Use `RenderTargetBitmap`, **not** `Graphics.CopyFromScreen`: a screen grab
captures whatever else is on the user's desktop.

Checklist for the render:

- [ ] Every piece of text is legible, in Dark **and** Light.
- [ ] No element is invisible against its own background.
- [ ] Nothing is clipped at the fixed window size.
- [ ] The window has the app's chrome, not the OS title bar.
- [ ] Disabled controls look disabled rather than broken.
