# Dolphin patch: label the challenge indicators

Dolphin already draws a badge in the bottom-right corner for every RetroAchievement
that is active where you are standing — its **challenge indicators**. It draws the icon
and nothing else, so you cannot tell what the achievement is or how to earn it.

This patch draws the achievement's **name and unlock condition** beside the badge.

Because the indicator is rendered by Dolphin's own ImGui overlay *inside the emulated
frame*, it works in every display mode, **including exclusive fullscreen** — which no
external overlay can manage. Dolphin's fullscreen is exclusive on both Vulkan
(`vkAcquireFullScreenExclusiveModeEXT`) and D3D (`IDXGISwapChain::SetFullscreenState`).

## What it changes

Three files, ~36 lines:

| File | Change |
|---|---|
| `Core/AchievementManager.h` | declare `GetAchievementInfo(AchievementId)` |
| `Core/AchievementManager.cpp` | implement it via `rc_client_get_achievement_info` |
| `VideoCommon/OnScreenUI.cpp` | draw title + description beside each badge |

The data was always there — `GetActiveChallenges()` returns achievement IDs and
rcheevos exposes `title`/`description` for each. Stock Dolphin simply never draws them.

## Building

```powershell
.\Build-PatchedDolphin.ps1 -Install "C:\Dolphin-x64"
```

Needs **Visual Studio Build Tools 2022** with "Desktop development with C++", plus git
and ~10 GB of disk. The build takes 30-60 minutes on a laptop. The original executable
is kept as `Dolphin.exe.stock`.

Tested against Dolphin release **2606a**. For a newer release pass `-Tag`; if Dolphin's
achievement UI has changed the patch will refuse to apply rather than corrupt anything.

## Caveats

- **Dolphin's auto-updater will overwrite the patched executable.** Re-run the script
  after an update, or set `AutoUpdateTrack` to empty in `Dolphin.ini` to stay put.
- Challenge indicators only appear for achievements rcheevos marks as *primed*, which
  is a subset of the set (those whose triggers use the `T:` flag). This patch labels
  whatever Dolphin already decided to show; it does not change which achievements
  appear.
