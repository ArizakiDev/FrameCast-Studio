# FrameCast Studio pour Linux

Réécriture native pour Linux de FrameCast Studio (la version Windows reste dans `App/` + `Core/`).
Interface **Avalonia**, moteur **ffmpeg** embarqué, capture **Wayland** (xdg-desktop-portal + PipeWire) et **X11**.

## Installer

**AppImage (recommandé)** — fonctionne sur Ubuntu, Debian, Fedora, Arch, openSUSE, Mint, Pop!_OS, etc. :

```bash
chmod +x FrameCastStudio-1.1.0-linux-x64.AppImage
./FrameCastStudio-1.1.0-linux-x64.AppImage
```

Si l'AppImage ne démarre pas (pas de FUSE, ex. Ubuntu 22.04+ sans `libfuse2`) :
`./FrameCastStudio-….AppImage --appimage-extract-and-run`

**Archive `.tar.gz`** : extraire puis lancer `./run.sh`.

Versions : `linux-x64` (PC classiques) et `linux-arm64` (Raspberry Pi 5, serveurs ARM…).

Aucun runtime .NET ni ffmpeg à installer : tout est embarqué.

## Prérequis système (déjà présents sur un bureau standard)

| Besoin | Pour quoi | Paquets si absent |
|---|---|---|
| X11 ou XWayland, `libfontconfig` | Afficher la fenêtre | présents par défaut sur tout bureau |
| **PipeWire** ou **PulseAudio** | Audio (via `pactl` / `pipewire-pulse`) | `pipewire-pulse` ou `pulseaudio` |
| **GStreamer + plugin PipeWire** | Capture d'écran sous **Wayland** uniquement | Debian/Ubuntu : `gstreamer1.0-pipewire gstreamer1.0-plugins-good` · Fedora : `pipewire-gstreamer gstreamer1-plugins-good` · Arch : `gst-plugin-pipewire gst-plugins-good` |
| `xdg-desktop-portal` + backend du bureau (gnome / kde / wlr / hyprland) | Sélecteur d'écran Wayland | installé avec GNOME / KDE ; wlroots : `xdg-desktop-portal-wlr` |
| `wl-clipboard` ou `xclip` | Copie des captures dans le presse-papiers (facultatif) | `wl-clipboard` / `xclip` |

## Fonctionnement de la capture

* **Session X11** : `x11grab` de ffmpeg (aucune dépendance supplémentaire).
* **Session Wayland** : le portail affiche la fenêtre de partage d'écran du système, puis le flux PipeWire est lu par GStreamer et envoyé à ffmpeg. Le choix est mémorisé (jeton de restauration) : la fenêtre n'apparaît plus aux lancements suivants sur la plupart des bureaux.
* Réglages → *Méthode* permet de forcer `X11` ou `Portail`.

## Encodage

Détection automatique au démarrage (test réel de chaque encodeur) :

| Encodeur | Matériel | Prérequis |
|---|---|---|
| **NVENC** (`h264/hevc/av1_nvenc`) | NVIDIA GeForce/Quadro | pilote propriétaire NVIDIA |
| **VAAPI** (`h264/hevc/av1_vaapi`) | AMD (Mesa), Intel | `/dev/dri/renderD*` + pilote VA-API (`mesa-va-drivers`, `intel-media-va-driver`) |
| **Logiciel** (`libx264`, `libx265`, `libsvtav1`) | tout CPU | — |

« Auto » prend le GPU s'il fonctionne, sinon le processeur. Si un encodeur matériel échoue avec CBR, essaie VBR ou CQP.

## Raccourcis globaux

Wayland interdit aux applications de capter des touches globales. À la place, lie ces commandes à des raccourcis dans les paramètres de ton bureau (GNOME, KDE, Hyprland, Sway…). Elles s'adressent à l'instance déjà ouverte :

```
FrameCastStudio --toggle-record     démarre / arrête l'enregistrement
FrameCastStudio --toggle-live       démarre / arrête le direct
FrameCastStudio --save-replay       sauvegarde le buffer de replay
FrameCastStudio --screenshot        capture d'écran
FrameCastStudio --stop              arrête la session
```

(AppImage : `./FrameCastStudio-….AppImage --toggle-record`.)

## Fonctions de la 1.1.0 Linux

Enregistrement MP4/MKV (MP4 fragmenté, découpage par taille), direct RTMP/RTMPS (Twitch, YouTube, Kick, Facebook, perso), buffer de replay (sauvegarde sans ré-encodage), capture d'écran (PNG/JPEG/BMP + presse-papiers), région (recadrage), mixage audio (système + micro avec gain, balance, retard, passe-haut, gate, compresseur, ducking, limiteur), profils de réglages, mise à jour (l'AppImage peut se remplacer lui-même).

**Pas (encore) dans la version Linux** : aperçu intégré, pause, vumètres, mixeur par application, webcam, overlay, Discord RPC, détection de jeux, raccourcis globaux natifs (voir ci-dessus).

## Dépannage

* Journal : `~/.local/share/FrameCastStudio/framecast.log` (bouton *Ouvrir le dossier des logs*), et onglet *Studio*.
* Réglages : `~/.config/FrameCastStudio/settings.json`.
* « Aucun encodeur matériel » : normal sans pilote NVENC/VAAPI ; l'encodage logiciel reste disponible.
* « GStreamer … absent » sous Wayland : installe le paquet indiqué dans le message.
* Pas de son : vérifie `pactl list short sources` ; sous PipeWire installe `pipewire-pulse`.
* Pour utiliser ton propre ffmpeg : `FRAMECAST_FFMPEG=/chemin/vers/ffmpeg ./run.sh` (il doit inclure `x11grab`, `pulse`, `tee`, `libx264`).

## Compiler

Les paquets sont produits par GitHub Actions (`.github/workflows/linux.yml`, déclenché par un tag `v*` ou à la main). En local sous Linux :

```bash
cd Linux && ./packaging/build-linux.sh x64      # ou arm64
```

Depuis Windows, `Linux\publish-linux.bat` vérifie que le code compile (sans ffmpeg ni AppImage).
Prérequis : .NET 10 SDK.

## Licences

FrameCast Studio : MIT. Le ffmpeg embarqué est un build **GPL** de [BtbN/FFmpeg-Builds](https://github.com/BtbN/FFmpeg-Builds) (sources : https://ffmpeg.org et https://github.com/BtbN/FFmpeg-Builds).
