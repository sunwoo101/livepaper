# livepaper

A live wallpaper manager for Wayland. Browse and download animated wallpapers from online sources and apply them to your desktop using [mpvpaper](https://github.com/GhostNaN/mpvpaper).

## Requirements

- Wayland compositor (Hyprland, Sway, GNOME on Wayland, etc.)
- [mpvpaper](https://github.com/GhostNaN/mpvpaper)
- .NET 10 SDK (for building from source)

## Installation

### AUR

```bash
yay -S livepaper-git
```

### From source

```bash
git clone https://github.com/sunwoo101/livepaper.git
cd livepaper
bash scripts/install.sh
```

Installs the binary to `~/.local/bin/livepaper` and registers the app in your launcher.

## Usage

```bash
livepaper            # open the app
livepaper --restore  # re-apply the last wallpaper without opening the app
livepaper --random   # apply a random wallpaper from your library
```

### Autostart

To restore your wallpaper on login, add to your compositor config:

**Hyprland** (`hyprland.conf`):
```
exec-once = livepaper --restore
```

**Sway** (`config`):
```
exec livepaper --restore
```

## Sources

- **motionbgs.com** — large collection of animated wallpapers
- **moewalls.com** — anime-style animated wallpapers
- **Wallpaper Engine** — your local Wallpaper Engine library (Steam workshop)

## Library

Downloaded wallpapers are saved to `~/.local/share/livepaper/library/`. Use the Library tab to apply, delete, or loop through them.

**Play All** loops your entire library continuously. Enable **Shuffle** to randomize the order.

## Building

```bash
dotnet run --project src/livepaper     # run
bash scripts/build-appimage.sh         # build AppImage
bash scripts/install.sh                # install system-wide
```
