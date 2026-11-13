using Smart_Tools.WPF.Models;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;

namespace Smart_Tools.WPF.Controllers
{
    /// <summary>
    /// The one list of sections and tools. The sidebar, the Home page, the
    /// page header and search are all built from it - to move a tool to
    /// another section, change its CategoryId here; nothing else.
    /// Also: search, the Recent list and the shell settings.
    /// </summary>
    public static class ToolCatalogManager
    {
        public const string HomeId = "home";
        public const string SettingsId = "settings";
        public const int MaxRecent = 10;


        // =================================================================
        //  Sections (sidebar order)
        // =================================================================

        public static readonly IReadOnlyList<ToolCategoryInfo> Categories = new[]
        {
            new ToolCategoryInfo { Id = "data-quality", Keywords = "datos calidad verificar", Title = "Data Quality", Icon = "ShieldCheckOutline", AccentHex = "#3B82F6", Blurb = "Check, de-duplicate and compare" },
            new ToolCategoryInfo { Id = "files", Keywords = "archivos carpetas", Title = "Files", Icon = "FolderOutline", AccentHex = "#F59E0B", Blurb = "Rename, sort and search" },
            new ToolCategoryInfo { Id = "media", Keywords = "multimedia reproductor visor ver reproducir fotos videos musica player viewer", Title = "Media", Icon = "PlayBoxMultipleOutline", AccentHex = "#D946EF", Blurb = "View and play pictures, videos and music" },
            new ToolCategoryInfo { Id = "utilities", Keywords = "utilidades", Title = "Utilities", Icon = "NotebookOutline", AccentHex = "#84CC16", Blurb = "Everyday helpers" },
            new ToolCategoryInfo { Id = "calculators", Keywords = "calculadora calcular convertir unidades fecha red", Title = "Calculators", Icon = "Calculator", AccentHex = "#EAB308", Blurb = "Work things out" },
            new ToolCategoryInfo { Id = "convert", Keywords = "convertir formato", Title = "Convert", Icon = "SwapHorizontal", AccentHex = "#0EA5E9", Blurb = "Change file formats" },
            new ToolCategoryInfo { Id = "pdf", Keywords = "pdf documento documentos pdfs", Title = "PDF", Icon = "FilePdfBox", AccentHex = "#DC2626", Blurb = "Make, edit and protect PDFs" },
            new ToolCategoryInfo { Id = "video", Keywords = "videos pelicula", Title = "Video", Icon = "MovieOpenOutline", AccentHex = "#EF4444", Blurb = "Watch and edit" },
            new ToolCategoryInfo { Id = "audio", Keywords = "sonido sound", Title = "Audio", Icon = "VolumeHigh", AccentHex = "#8B5CF6", Blurb = "Play, record and transform sound" },
            new ToolCategoryInfo { Id = "images", Keywords = "imagen imagenes foto fotos colores", Title = "Images", Icon = "ImageOutline", AccentHex = "#14B8A6", Blurb = "View, edit and clean up pictures" },
            new ToolCategoryInfo { Id = "music", Keywords = "musica instrumento", Title = "Music", Icon = "MusicNote", AccentHex = "#EC4899", Blurb = "Tune and play instruments" },
            new ToolCategoryInfo { Id = "colors", Keywords = "color colores colour paleta palette gradiente", Title = "Colors", Icon = "Palette", AccentHex = "#F43F5E", Blurb = "Pick colors, build palettes and gradients" },
            new ToolCategoryInfo { Id = "storage", Keywords = "disco almacenamiento espacio", Title = "Storage", Icon = "Harddisk", AccentHex = "#6366F1", Blurb = "Free up space, check your drives" },
            new ToolCategoryInfo { Id = "performance", Keywords = "rendimiento monitor sensores temperatura", Title = "Performance", Icon = "Gauge", AccentHex = "#FB923C", Blurb = "Live readings across your whole PC" },
            new ToolCategoryInfo { Id = "system", Keywords = "sistema windows servicios inicio variables eventos arranque", Title = "System", Icon = "CogOutline", AccentHex = "#78716C", Blurb = "Manage Windows itself" },
            new ToolCategoryInfo { Id = "hardware", Keywords = "hardware dispositivos pruebas periféricos test", Title = "Hardware Tests", Icon = "Chip", AccentHex = "#22C55E", Blurb = "Test keyboards, mice, screens, controllers and sound" },
            new ToolCategoryInfo { Id = "security", Keywords = "seguridad privacidad", Title = "Security", Icon = "ShieldLockOutline", AccentHex = "#10B981", Blurb = "Protect, destroy, make passwords" },
            new ToolCategoryInfo { Id = "network", Keywords = "red internet wifi", Title = "Network", Icon = "Lan", AccentHex = "#06B6D4", Blurb = "Diagnose your connection" },
            new ToolCategoryInfo { Id = "developer", Keywords = "desarrollador programacion", Title = "Developer", Icon = "CodeBraces", AccentHex = "#64748B", Blurb = "Test, inspect and format" },
            new ToolCategoryInfo { Id = "generators", Keywords = "generar crear", Title = "Generators", Icon = "Creation", AccentHex = "#F97316", Blurb = "Make files and codes" },
            new ToolCategoryInfo { Id = "art", Keywords = "arte art pixel sprite juegos game dibujo", Title = "Art", Icon = "GridLarge", AccentHex = "#A855F7", Blurb = "Draw pixel art, pack sprite sheets and build texture packs" },
        };

        // =================================================================
        //  Tools (order within a section = sidebar order)
        // =================================================================

        public static readonly IReadOnlyList<ToolInfo> Tools = new[]
        {
            // Media
            new ToolInfo { Id = "media-viewer", Name = "Media Viewer", Icon = "PlayBoxMultipleOutline", CategoryId = "media", Description = "Play videos and music and view pictures: one mixed queue, or full Photos, Videos and Music pages", Keywords = "media viewer player movies tv photos pictures images video audio music slideshow gallery widget mini player floating visor reproductor multimedia peliculas fotos musica minirreproductor flotante video replay image viewer audio replay player watch mp4 mkv movie listen mp3 wav flac visualizer vlc zoom reproductor de video visor de imagenes reproductor de audio" },

            // Data Quality
            new ToolInfo { Id = "validate-data", Name = "Validate Data", Icon = "ShieldCheck", CategoryId = "data-quality", Description = "Check files for broken or invalid data", Keywords = "verify check corrupt integrity csv json xml" },
            new ToolInfo { Id = "duplicate-data", Name = "Duplicate Data", Icon = "Broom", CategoryId = "data-quality", Description = "Find and remove duplicate files", Keywords = "duplicates clean same copies hash" },
            new ToolInfo { Id = "find-similar", Name = "Find Similar Files", Icon = "ImageMultipleOutline", CategoryId = "data-quality", Description = "Spot near-identical files and images", Keywords = "similar look alike photos compare" },
            new ToolInfo { Id = "file-differences", Name = "File Differences", Icon = "FileCompare", CategoryId = "data-quality", Description = "Compare two files side by side", Keywords = "diff compare changes text" },
            new ToolInfo { Id = "text-diff", Name = "Text Difference", Icon = "CompareHorizontal", CategoryId = "data-quality", Description = "Paste two texts and see what changed, line by line", Keywords = "text diff compare paste difference changes lines words side by side patch unified comparar textos diferencias" },
            new ToolInfo { Id = "hash-verifier", Name = "Hash & Checksum Verifier", Icon = "Pound", CategoryId = "data-quality", Description = "Check a file is what it claims to be", Keywords = "hash checksum md5 sha1 sha256 sha512 crc32 verify integrity sha256sums sfv" },

            // Files
            new ToolInfo { Id = "rename-files", Name = "Rename Files", Icon = "FileEdit", CategoryId = "files", Description = "Rename many files at once", Keywords = "batch rename pattern numbering" },
            new ToolInfo { Id = "organize-files", Name = "Organize Files", Icon = "FolderFile", CategoryId = "files", Description = "Sort files into folders", Keywords = "sort tidy folders move by type date" },
            new ToolInfo { Id = "search-files", Name = "Search Files", Icon = "FileSearch", CategoryId = "files", Description = "Find text and patterns inside files", Keywords = "grep find text regex content" },
            new ToolInfo { Id = "copy-queue", Name = "Copy Queue", Icon = "FolderMoveOutline", CategoryId = "files", Description = "Copy or move big sets of files safely, with pause and verify", Keywords = "copy move batch queue backup transfer verify pause resume checksum copiar mover cola respaldo" },
            new ToolInfo { Id = "folder-tree", Name = "Folder Tree Exporter", Icon = "FileTreeOutline", CategoryId = "files", Description = "Export a folder's structure as a text tree, Markdown, JSON or CSV", Keywords = "folder tree structure export directory listing map tree command markdown json csv arbol carpetas estructura exportar" },
            new ToolInfo { Id = "metadata-extract", Name = "Metadata Extractor", Icon = "TagMultiple", CategoryId = "files", Description = "Read the hidden details in files", Keywords = "metadata exif tags properties" },
              
            // Utilities
            new ToolInfo { Id = "notes", Name = "Notes", Icon = "Notebook", CategoryId = "utilities", Description = "Write quick notes", Keywords = "notes notepad text memo" },
            new ToolInfo { Id = "clipboard-history", Name = "Clipboard History", Icon = "ClipboardTextOutline", CategoryId = "utilities", Description = "Everything you copied, searchable, with pins. Saved only if you turn it on", Keywords = "clipboard history paste copy pin portapapeles historial copiar" },
            new ToolInfo { Id = "translate", Name = "Translate", Icon = "Translate", CategoryId = "utilities", Description = "Translate text offline between English and Spanish", Keywords = "translate translator language english spanish offline traducir traductor idioma ingles espanol" },
            new ToolInfo { Id = "screen-ocr", Name = "Screen OCR", Title = "Screen Text Extractor", Icon = "TextRecognition", CategoryId = "utilities", Description = "Extract text from a screenshot or screen region", Keywords = "ocr text extract screenshot capture recognize image to text" },
            new ToolInfo { Id = "timer-stopwatch", Name = "Timer & Stopwatch", Icon = "TimerOutline", CategoryId = "utilities", Description = "Stopwatch with laps, countdown timer and a Pomodoro cycle", Keywords = "timer stopwatch countdown pomodoro lap alarm focus clock cronometro temporizador cuenta atras" },
            new ToolInfo { Id = "send-to-phone", Name = "Send to Phone", Icon = "CellphoneLink", CategoryId = "utilities", Description = "Move files and text between this PC and your phone over your own network: scan a QR code, no app, no cloud, encrypted end to end", Keywords = "phone send transfer share qr code wifi lan files photos mobile android iphone airdrop encrypted telefono enviar transferir compartir archivos fotos celular" },

            // Convert
            new ToolInfo { Id = "convert-files", Name = "Convert Files", Icon = "SwapHorizontal", CategoryId = "convert", Description = "Convert files between formats", Keywords = "converter format mp3 mp4 docx png" },
            new ToolInfo { Id = "csv-json", Name = "CSV / JSON Converter", Icon = "CodeJson", CategoryId = "convert", Description = "Turn CSV into JSON and JSON back into CSV, with quotes and separators handled", Keywords = "csv json convert converter table spreadsheet excel delimiter semicolon tab comma convertir tabla" },
            new ToolInfo { Id = "watermark", Name = "Watermarker", Title = "Batch Watermarker", Icon = "Stamper", CategoryId = "convert", Description = "Stamp text or a logo on many pictures, videos and PDFs", Keywords = "watermark stamp logo copyright text draft confidential brand marca de agua sello pictures photos videos pdf batch" },
            new ToolInfo { Id = "unit-converter", Name = "Unit & Currency Converter", Icon = "SwapHorizontalBold", CategoryId = "convert", Description = "Convert length, weight, temperature, data size, money, and more", Keywords = "unit converter currency exchange rate length weight mass temperature volume speed energy pressure data size dollar euro metric imperial convertidor unidades divisas moneda" },

            // Calculators
            new ToolInfo { Id = "subnet-calc", Name = "Subnet Calculator", Title = "Subnet / CIDR Calculator", Icon = "Calculator", CategoryId = "calculators", Description = "Work out network, mask, range and host count for IPv4 and IPv6, and split into subnets", Keywords = "subnet cidr mask netmask wildcard broadcast ip ipv4 ipv6 prefix range hosts calculadora subred mascara" },
            new ToolInfo { Id = "date-time", Name = "Date & Time Toolkit", Icon = "CalendarClock", CategoryId = "calculators", Description = "Timestamps, time zones and date maths in one place", Keywords = "unix timestamp epoch timezone time zone date difference duration add subtract days business utc iso fecha hora" },

            // PDF
            new ToolInfo { Id = "images-to-pdf", Name = "Images to PDF", Icon = "FileSwap", CategoryId = "pdf", Description = "Turn images into a PDF", Keywords = "jpg png pdf combine scan" },
            new ToolInfo { Id = "pdf-reader", Name = "PDF Reader & Signer", Icon = "FileDocumentEditOutline", CategoryId = "pdf", Description = "Read a PDF, then add your signature, text, a date or a check mark and save a signed copy", Keywords = "pdf reader viewer sign signature fill form text date check mark annotate firmar firma rellenar leer visor" },
            new ToolInfo { Id = "pdf-toolkit", Name = "PDF Merge & Split", Icon = "FileSync", CategoryId = "pdf", Description = "Join PDFs, cut one into parts, or make it smaller", Keywords = "pdf merge join combine split cut extract pages compress smaller reduce size unir dividir comprimir" },
            new ToolInfo { Id = "pdf-pages", Name = "PDF Pages & Protect", Icon = "FileCogOutline", CategoryId = "pdf", Description = "Rotate, remove or number pages; edit details; add or remove a password", Keywords = "pdf rotate pages remove delete extract crop page numbers password protect unlock encrypt metadata author title permissions girar paginas contraseña proteger numeros" },

            // Video
            new ToolInfo { Id = "video-editor", Name = "Video Stitcher", Icon = "FilmstripBoxMultiple", CategoryId = "video", Description = "Join video clips into one, with optional trims and fades", Keywords = "stitch join merge combine concat clips edit cut trim fade unir juntar" },
            new ToolInfo { Id = "video-to-audio", Name = "Video to Audio", Title = "Video to Audio Extractor", Icon = "FileMusicOutline", CategoryId = "video", Description = "Pull the audio track out of videos, in bulk", Keywords = "extract audio video mp3 m4a wav flac sound track rip" },
            new ToolInfo { Id = "video-compressor", Name = "Video Compressor", Title = "Video Compressor & Trimmer", Icon = "VideoMinusOutline", CategoryId = "video", Description = "Make videos smaller, or cut out the part you want", Keywords = "compress compressor reduce shrink smaller size trim cut clip speed resize 720p 1080p mp4 comprimir reducir recortar cortar peso" },
            new ToolInfo { Id = "gif-maker", Name = "GIF Maker", Icon = "FileGifBox", CategoryId = "video", Description = "Create optimized GIFs from video or images", Keywords = "gif animation video images palette loop meme" },
            new ToolInfo { Id = "frame-extractor", Name = "Video Frame Extractor", Icon = "ImageMultipleOutline", CategoryId = "video", Description = "Save still pictures from a video: one frame, one every few seconds, or an even spread", Keywords = "video frame extract screenshot still thumbnail capture image png jpg ffmpeg fotograma extraer captura" },
            new ToolInfo { Id = "screen-recorder", Name = "Screen Recorder", Icon = "RecordCircleOutline", CategoryId = "video", Description = "Record a screen, a window, or an area to an MP4, with your microphone if you like", Keywords = "screen recorder record capture video desktop window area mp4 microphone grabar pantalla captura" },

            // Audio
            new ToolInfo { Id = "audio-recorder", Name = "Audio Recorder", Icon = "Microphone", CategoryId = "audio", Description = "Record from your microphone", Keywords = "mic record voice capture" },
            new ToolInfo { Id = "spectrogram", Name = "Spectrogram", Title = "Spectrogram Generator", Icon = "Waveform", CategoryId = "audio", Description = "See the frequencies in a sound", Keywords = "frequency analysis waveform fft" },
            new ToolInfo { Id = "voice-changer", Name = "Voice Changer", Icon = "Tune", CategoryId = "audio", Description = "Change how a voice sounds", Keywords = "pitch effects robot voice" },
            new ToolInfo { Id = "speech-to-text", Name = "Speech to Text", Icon = "Subtitles", CategoryId = "audio", Description = "Turn speech in audio or video into text and subtitles, offline", Keywords = "transcribe transcription subtitles captions srt vtt whisper dictation speech recognition voz transcribir subtitulos texto" },
            new ToolInfo { Id = "text-to-speech", Name = "Text to Speech", Icon = "Speaker", CategoryId = "audio", Description = "Read text aloud or save it as audio", Keywords = "tts speak read aloud narrator" },
            new ToolInfo { Id = "loudness-meter", Name = "Loudness Meter & Normalizer", Icon = "Speedometer", CategoryId = "audio", Description = "Measure LUFS and peak, and make normalized copies for YouTube, Spotify or podcasts", Keywords = "loudness lufs normalize volume level peak ebu r128 replaygain podcast youtube spotify nivelar volumen sonoridad" },
            new ToolInfo { Id = "audio-trimmer", Name = "Audio Trimmer & Joiner", Icon = "ContentCut", CategoryId = "audio", Description = "Cut, join, fade and normalize audio", Keywords = "audio trim cut join merge fade normalize waveform mp3 wav flac split recortar unir" },

            // Images
            new ToolInfo { Id = "screenshot", Name = "Screenshot", Title = "Screenshot & Markup", Icon = "Camera", CategoryId = "images", Description = "Capture an area, a window, or the whole screen, mark it up, then copy or save", Keywords = "screenshot screen capture snip snipping print screen grab region window annotate arrow blur pixelate highlight text copy captura pantalla recorte" },
            new ToolInfo { Id = "image-editor", Name = "Image Editor", Icon = "ImageEditOutline", CategoryId = "images", Description = "Crop, resize and adjust images", Keywords = "edit crop resize rotate photo" },
            new ToolInfo { Id = "bulk-image-resizer", Name = "Bulk Image Resizer", Icon = "ImageMultipleOutline", CategoryId = "images", Description = "Shrink and recompress many pictures at once, with a target file size", Keywords = "resize compress shrink batch bulk images photos jpg png webp quality size redimensionar comprimir imagenes" },
            new ToolInfo { Id = "background-remover", Name = "Background Remover", Icon = "ImageMinusOutline", CategoryId = "images", Description = "Cut the background out of a picture", Keywords = "remove background transparent png cutout" },
            new ToolInfo { Id = "svg-tool", Name = "SVG Viewer & Optimizer", Icon = "VectorSquare", CategoryId = "images", Description = "Preview SVG files, make them smaller and export PNGs", Keywords = "svg vector optimize minify png svgo viewer export" },
            new ToolInfo { Id = "icon-maker", Name = "Icon Maker", Icon = "ImageSizeSelectActual", CategoryId = "images", Description = "Turn any picture into a Windows .ico or a full website favicon set", Keywords = "icon ico favicon app icon windows png sizes apple touch manifest web icono favicon crear" },

            // Music
            new ToolInfo { Id = "midi-instruments", Name = "MIDI Instruments", Icon = "Midi", CategoryId = "music", Description = "Play 128 instruments and drum kits, record to .mid", Keywords = "midi instruments drums drum kit record mid general gm keyboard orchestra violin trumpet sax instrumentos bateria teclado musica grabar" },
            new ToolInfo { Id = "midi-playalong", Name = "MIDI Play-Along", Icon = "MusicClefTreble", CategoryId = "music", Description = "Play a .mid file with a click, silence any part and play along", Keywords = "midi play along backing track metronome click mute solo guitar drums bass practice song karaoke tempo loop acompanamiento pista practicar silenciar guitarra bateria metronomo cancion" },
            new ToolInfo { Id = "instrument-tuner", Name = "Instrument Tuner", Icon = "MusicClefTreble", CategoryId = "music", Description = "Tune guitar, bass, violin and more", Keywords = "tuner guitar bass violin ukulele pitch note afinador" },
            new ToolInfo { Id = "metronome", Name = "Metronome", Title = "Metronome & Tap Tempo", Icon = "Metronome", CategoryId = "music", Description = "A steady click at any tempo, with beats per bar and tap tempo", Keywords = "metronome tempo bpm beat click tap rhythm practice time signature metronomo ritmo compas" },
            new ToolInfo { Id = "synthesizer", Name = "Synthesizer", Icon = "Piano", CategoryId = "music", Description = "Play notes and shape sounds", Keywords = "synth piano keyboard notes oscillator" },
          
            // Colors
            new ToolInfo { Id = "color-picker", Name = "Color Picker", Title = "Color Picker & Converter", Icon = "Palette", CategoryId = "colors", Description = "Pick colors and convert color codes", Keywords = "colour hex rgb hsl eyedropper" },
            new ToolInfo { Id = "screen-color-picker", Name = "Screen Color Picker", Title = "Screen Color Picker / Eyedropper", Icon = "Eyedropper", CategoryId = "colors", Description = "Pick any color from anywhere on screen with a magnifier", Keywords = "eyedropper screen colour color pixel hex rgb picker magnifier" },
            new ToolInfo { Id = "color-wheel", Name = "Color Wheel", Title = "Color Wheel & Gradients", Icon = "PaletteOutline", CategoryId = "colors", Description = "Build palettes and gradients", Keywords = "colour palette gradient harmony" },
            new ToolInfo { Id = "palette-extractor", Name = "Palette Extractor", Title = "Image Color Palette Extractor", Icon = "PaletteSwatchOutline", CategoryId = "colors", Description = "Pull the main colors out of a picture", Keywords = "colour color palette extract dominant image picture swatch kmeans median cut contrast wcag hex css paleta colores imagen" },
            new ToolInfo { Id = "contrast-checker", Name = "Contrast Checker", Title = "Text & Background Contrast Checker", Icon = "ContrastBox", CategoryId = "colors", Description = "Check WCAG contrast between two colors and find the closest passing text color", Keywords = "contrast wcag accessibility aa aaa text background readable ratio contraste accesibilidad legible" },

            // Storage
            new ToolInfo { Id = "drive-health", Name = "Drive Health", Title = "Drive Health Monitor", Icon = "Harddisk", CategoryId = "storage", Description = "Check the health of your drives and track it over time", Keywords = "smart ssd hdd health temperature" },
            new ToolInfo { Id = "disk-space", Name = "Disk Space Analyzer", Icon = "ChartDonut", CategoryId = "storage", Description = "See what's using your disk", Keywords = "space usage folders size" },
            new ToolInfo { Id = "large-files", Name = "Large File Finder", Icon = "FileFindOutline", CategoryId = "storage", Description = "Find the biggest files", Keywords = "big large size free space" },
            new ToolInfo { Id = "empty-folders", Name = "Empty Folder Cleaner", Icon = "FolderRemoveOutline", CategoryId = "storage", Description = "Find folders with nothing in them and send them to the Recycle Bin", Keywords = "empty folders clean delete remove directories tidy unused carpetas vacias limpiar" },
            new ToolInfo { Id = "junk-cleaner", Name = "Junk File Cleaner", Icon = "DeleteSweep", CategoryId = "storage", Description = "Clear temporary files and caches", Keywords = "junk temp cache clean free space" },
            new ToolInfo { Id = "disk-speed", Name = "Disk Speed Test", Icon = "SpeedometerMedium", CategoryId = "storage", Description = "Measure read and write speed", Keywords = "benchmark ssd speed read write" },

            // Performance
            new ToolInfo { Id = "system-monitor", Name = "System Monitor", Icon = "MonitorDashboard", CategoryId = "performance", Description = "Live CPU, memory, disks, network and GPU, like Task Manager", Keywords = "cpu gpu ram memory disk network usage specs task manager performance rendimiento" },
            new ToolInfo { Id = "hardware-sensors", Name = "Hardware Sensors", Icon = "Thermometer", CategoryId = "performance", Description = "Temperatures, fans, clocks, voltages and power, part by part", Keywords = "temperature sensors fans voltage clock power hardware monitor librehardwaremonitor temperatura sensores ventiladores" },
            new ToolInfo { Id = "process-killer", Name = "Process Killer", Icon = "SkullCrossbonesOutline", CategoryId = "performance", Description = "See every running process and end the stuck ones safely", Keywords = "process kill task manager end pid port lock file locked cpu memory proceso finalizar tarea" },
            new ToolInfo { Id = "resource-hogs", Name = "Resource Hogs History", Icon = "ChartTimelineVariant", CategoryId = "performance", Description = "Which programs used the most CPU, memory and disk over the last hours", Keywords = "resource hogs history cpu memory ram disk usage programs apps heavy slow top consumers rendimiento historial programas consumo" },

            // System
            new ToolInfo { Id = "startup-apps", Name = "Startup Apps", Icon = "RocketLaunchOutline", CategoryId = "system", Description = "See what starts with Windows and switch each app on or off", Keywords = "startup apps boot login run programs autostart task manager disable arranque inicio programas aplicaciones" },
            new ToolInfo { Id = "services", Name = "Services", Icon = "CogSyncOutline", CategoryId = "system", Description = "See every Windows service and start, stop, restart, or change how each one starts", Keywords = "services windows service start stop restart pause resume startup type automatic manual disabled services.msc background servicios iniciar detener reiniciar pausar" },
            new ToolInfo { Id = "environment-variables", Name = "Environment Variables", Icon = "VariableBox", CategoryId = "system", Description = "Edit user and system variables, and clean up the PATH folder by folder", Keywords = "environment variables env path system user registry java home variables de entorno" },
            new ToolInfo { Id = "event-log-explorer", Name = "Event Log Explorer", Icon = "TextBoxSearchOutline", CategoryId = "system", Description = "Browse, filter and export Windows event logs", Keywords = "event viewer windows logs errors warnings crash evtx security application system" },
            new ToolInfo { Id = "system-snapshot", Name = "System Snapshot", Icon = "ClipboardTextClockOutline", CategoryId = "system", Description = "Scan everything about this PC and export it to rebuild the same setup on another PC", Keywords = "systeminfo inventory inventario snapshot configuration configuracion migrate migrar replicate replicar legacy firewall rules ports puertos services servicios startup scheduled tasks programs installed software dependencies runtimes hosts environment variables network report export html json" },
            new ToolInfo { Id = "network-adapters", Name = "Network Adapters", Icon = "IpNetworkOutline", CategoryId = "system", Description = "Every adapter (network and Bluetooth), its addresses, edit IP, enable or disable", Keywords = "ip address adapter ethernet wifi usb dhcp static dns disable enable public ip network tools bluetooth radio paired devices adaptador" },

            // Hardware Tests
            new ToolInfo { Id = "audio-output-test", Name = "Audio Output Test", Title = "Audio Output Tester", Icon = "Headphones", CategoryId = "hardware", Description = "Check every channel, phase and level of speakers, headphones and IEMs", Keywords = "audio speaker headphone iem channel left right phase polarity sweep tone pink noise test sound dac" },
            new ToolInfo { Id = "game-controller", Name = "Game Controller Test", Title = "Game Controller & Joystick Calibration", Icon = "Gamepad", CategoryId = "hardware", Description = "Read gamepads and joysticks: dead zones, drift, range and triggers", Keywords = "gamepad joystick controller xbox playstation xinput directinput deadzone drift stick trigger vibration calibration usb bluetooth" },
            new ToolInfo { Id = "monitor-test", Name = "Monitor Test", Title = "Monitor Test & Pixel Inspector", Icon = "Monitor", CategoryId = "hardware", Description = "Full-screen patterns for dead pixels, light bleed, banding, calibration and refresh rate", Keywords = "monitor screen display dead pixel stuck bleed backlight burn-in retention gradient banding gamma calibration refresh rate hz ufo oled lcd test" },
            new ToolInfo { Id = "monitor-hdr", Name = "Monitor HDR & Color Depth", Title = "Monitor HDR & Color Depth Checker", Icon = "Hdr", CategoryId = "hardware", Description = "See what each screen supports: HDR, bits per color, brightness and color range", Keywords = "monitor screen display hdr hdr10 10-bit 8-bit color depth bit depth gamut srgb p3 rec2020 nits brightness banding gradient ycbcr chroma edid dxgi" },
            new ToolInfo { Id = "keyboard-mouse-test", Name = "Keyboard & Mouse Test", Title = "Keyboard & Mouse Event Tester", Icon = "Keyboard", CategoryId = "hardware", Description = "Key map, ghosting, rollover, mouse polling rate, chatter and reaction time", Keywords = "keyboard mouse test ghosting rollover nkro polling rate hz dpi chatter double click latency reaction wheel switch key" },
            new ToolInfo { Id = "mic-test", Name = "Microphone Test", Title = "Microphone Level Tester", Icon = "MicrophoneOutline", CategoryId = "hardware", Description = "Check your microphone: live level, peak, clipping, and hear yourself", Keywords = "microphone mic test level meter db peak clipping noise record input headset usb privacy micrófono prueba nivel" },
            new ToolInfo { Id = "webcam-test", Name = "Webcam Test", Title = "Webcam Tester", Icon = "Webcam", CategoryId = "hardware", Description = "See your camera live: resolution, frame rate, brightness, and a snapshot", Keywords = "webcam camera test preview resolution fps frame rate brightness snapshot usb privacy video cámara prueba" },
            new ToolInfo { Id = "battery-health", Name = "Battery Health", Icon = "BatteryHeartVariant", CategoryId = "hardware", Description = "See how worn your laptop battery is: design vs. full-charge capacity and cycles", Keywords = "battery health wear capacity cycles laptop charge report powercfg bateria salud desgaste ciclos" },

            // Security
            new ToolInfo { Id = "file-shredder", Name = "Secure File Shredder", Icon = "Shredder", CategoryId = "security", Description = "Delete files so they can't be recovered", Keywords = "shred wipe erase secure delete" },
            new ToolInfo { Id = "file-encryption", Name = "File Encryption", Icon = "FileLock", CategoryId = "security", Description = "Lock files with a password", Keywords = "encrypt decrypt aes password protect" },
            new ToolInfo { Id = "password-generator", Name = "Password Generator", Icon = "Key", CategoryId = "security", Description = "Make strong passwords and passphrases", Keywords = "password passphrase random secure contraseña" },
            new ToolInfo { Id = "password-vault", Name = "Password Vault", Icon = "ShieldKeyOutline", CategoryId = "security", Description = "Encrypted password manager with 2FA codes, a health check and a quick-access widget", Keywords = "password manager vault passwords logins totp 2fa authenticator argon2 aes keepass bitwarden contraseñas gestor bóveda" },
            new ToolInfo { Id = "totp-code", Name = "2FA Code Generator", Title = "2FA Code Generator (TOTP)", Icon = "ShieldKeyOutline", CategoryId = "security", Description = "Make the 6-digit two-factor codes from a secret key or otpauth link, without saving it", Keywords = "2fa totp otp authenticator two factor code google authenticator secret otpauth autenticador codigo verificacion" },
            new ToolInfo { Id = "image-metadata-cleaner", Name = "Image Metadata Cleaner", Icon = "EyeOffOutline", CategoryId = "security", Description = "Remove location, camera and date details from many photos before you share them", Keywords = "metadata exif gps location privacy strip remove clean photos camera date iptc xmp share limpiar metadatos ubicacion privacidad fotos" },

            // Network
            new ToolInfo { Id = "wifi-analyzer", Name = "Wi-Fi Analyzer", Icon = "Wifi", CategoryId = "network", Description = "See nearby networks, signal and channels, and which channel is least crowded", Keywords = "wifi wi-fi wireless signal channel router 2.4 5 ghz saved password interference analizador red inalambrica canal senal" },
            new ToolInfo { Id = "ping", Name = "Ping", Icon = "Pulse", CategoryId = "network", Description = "Is a host reachable, and how fast - from the adapter you choose", Keywords = "ping latency reachable jitter packet loss network tools" },
            new ToolInfo { Id = "port-scanner", Name = "Port Scanner", Icon = "LanConnect", CategoryId = "network", Description = "Find which TCP ports are open on a host", Keywords = "port scan open ports tcp services network tools" },
            new ToolInfo { Id = "network-connections", Name = "Network Connections", Icon = "AccessPoint", CategoryId = "network", Description = "See which apps are connected outside your PC, and to where", Keywords = "connections netstat internet outgoing outside apps processes remote address port tcp udp conexiones red exterior aplicaciones" },
            new ToolInfo { Id = "dns-tools", Name = "DNS, WHOIS & Traceroute", Icon = "Dns", CategoryId = "network", Description = "Look up DNS records, see who owns a name, and trace the path to a host", Keywords = "dns lookup nslookup dig whois domain owner registrar expiry traceroute tracert route hops mx txt records nameserver resolver consulta dominio ruta" },
            new ToolInfo { Id = "hosts-editor", Name = "Hosts File Editor", Icon = "FileDocumentEditOutline", CategoryId = "network", Description = "Edit the hosts file safely: switch lines on and off, with a backup on every save", Keywords = "hosts file dns block website redirect localhost flush archivo hosts bloquear sitio" },
            new ToolInfo { Id = "tcp-udp-client", Name = "TCP / UDP Client", Icon = "SwapHorizontal", CategoryId = "network", Description = "Send text or bytes to a device or server", Keywords = "tcp udp socket client hercules terminal device network tools" },
            new ToolInfo { Id = "device-scanner", Name = "Device Scanner", Icon = "Radar", CategoryId = "network", Description = "Find devices on your network", Keywords = "lan scan ip devices" },
            new ToolInfo { Id = "internet-speed", Name = "Internet Speed Test", Icon = "Speedometer", CategoryId = "network", Description = "Measure download and upload speed", Keywords = "speed test bandwidth ping wifi" },
            new ToolInfo { Id = "net-speed", Name = "Network Speed", Icon = "SpeedometerMedium", CategoryId = "network", Description = "A live download and upload meter that can float on your screen, with the adapter's manufacturer", Keywords = "network speed widget floating upload download bandwidth monitor mac manufacturer oui adapter overlay" },
            new ToolInfo { Id = "bandwidth-per-app", Name = "Bandwidth per App", Icon = "LanConnect", CategoryId = "network", Description = "How much data each program sent and received over the last hours", Keywords = "bandwidth data usage per app program upload download network traffic who is using internet consumo datos programa red trafico" },

            // Developer
            new ToolInfo { Id = "log-viewer", Name = "Log Viewer", Icon = "TextBoxSearch", CategoryId = "developer", Description = "Open and filter log files", Keywords = "log logs filter errors tail" },
            new ToolInfo { Id = "regex-tester", Name = "Regex Tester", Icon = "Regex", CategoryId = "developer", Description = "Build and test regular expressions", Keywords = "regex regular expression pattern match" },
            new ToolInfo { Id = "api-tester", Name = "API Tester", Icon = "Webhook", CategoryId = "developer", Description = "Send web requests and read the replies", Keywords = "http rest request json postman" },
            new ToolInfo { Id = "jwt-decoder", Name = "JWT Decoder", Icon = "KeyChainVariant", CategoryId = "developer", Description = "Read a token's header, payload, and expiry. Offline, and the signature is not checked", Keywords = "jwt json web token decode bearer claims exp expiry auth oauth token decodificar" },
            new ToolInfo { Id = "data-formatter", Name = "JSON / YAML / XML Formatter", Icon = "CodeJson", CategoryId = "developer", Description = "Format, minify, validate, and convert between JSON, YAML, and XML", Keywords = "json yaml yml xml format pretty minify validate convert beautify formatear convertir" },
            new ToolInfo { Id = "code-toolkit", Name = "Code Toolkit", Icon = "CodeBraces", CategoryId = "developer", Description = "Format, encode and convert code", Keywords = "json format base64 encode hash" },
            new ToolInfo { Id = "md-html-editor", Name = "MD & HTML Editor", Title = "MD & HTML Previewer / Editor", Icon = "LanguageMarkdownOutline", CategoryId = "developer", Description = "Write Markdown or HTML with a live preview, export to HTML or PDF", Keywords = "markdown md html preview editor readme webview2 pdf export" },
            new ToolInfo { Id = "db-migrate", Name = "Database Migrator", Icon = "DatabaseSyncOutline", CategoryId = "developer", Description = "Copy a SQL Server database to a newer or older server version, no backup file needed", Keywords = "sql server database migrate migration copy move restore bak backup version upgrade downgrade downlevel schema data transfer base de datos migrar copiar mover versión" },
            new ToolInfo { Id = "encode-decode", Name = "Base64 & URL Encoder", Title = "Base64 & URL Encoder / Decoder", Icon = "SwapHorizontal", CategoryId = "developer", Description = "Encode and decode Base64, URLs, HTML entities and files", Keywords = "base64 url percent encode decode html entities data uri urlencode escape unescape codificar decodificar" },

            // Generators
            new ToolInfo { Id = "file-generator", Name = "File Generator", Icon = "FilePlus", CategoryId = "generators", Description = "Make test files of any size", Keywords = "dummy test files size generate" },
            new ToolInfo { Id = "barcode-generator", Name = "Barcode Generator", Icon = "Barcode", CategoryId = "generators", Description = "Create barcodes", Keywords = "barcode ean code128 upc" },
            new ToolInfo { Id = "qr-generator", Name = "QR Code Generator", Icon = "Qrcode", CategoryId = "generators", Description = "Create QR codes", Keywords = "qr code link wifi" },
            new ToolInfo { Id = "mock-data", Name = "Mock Data Generator", Icon = "DatabaseCogOutline", CategoryId = "generators", Description = "Generate fake data for testing", Keywords = "fake data csv json sql names emails test dummy seed" },
            new ToolInfo { Id = "id-generator", Name = "ID & Lorem Generator", Icon = "Identifier", CategoryId = "generators", Description = "Make UUIDs (v4, v7), ULIDs, random hex and lorem ipsum text", Keywords = "uuid guid ulid id unique random hex lorem ipsum placeholder text generator identificador aleatorio" },

            // Art
            new ToolInfo { Id = "pixel-art", Name = "Pixel Art Editor", Icon = "GridLarge", CategoryId = "art", Description = "Draw on a small grid, animate frames and export sharp PNGs", Keywords = "pixel art sprite draw png animation frames palette editor dibujar" },
            new ToolInfo { Id = "image-to-pixel", Name = "Image to Pixel Art", Icon = "ImageFilterFrames", CategoryId = "art", Description = "Shrink a picture to a pixel grid with a limited palette and optional dithering", Keywords = "pixel art pixelate convert photo retro palette dither 8 bit sprite pixelar imagen" },
            new ToolInfo { Id = "ascii-art", Name = "ASCII Art Maker", Icon = "FormatLetterCase", CategoryId = "art", Description = "Turn a picture into text art with classic, letter or symbol characters, in black and white or colour, or draw it by hand", Keywords = "ascii art text picture image characters letters symbols rtf color colour draw dibujo texto arte caracteres imagen" },
            new ToolInfo { Id = "sprite-packer", Name = "Sprite Sheet Packer", Title = "Image Sprite Sheet Packer", Icon = "ViewGridPlusOutline", CategoryId = "art", Description = "Pack images into a sprite sheet with coordinates, or slice a sheet back into frames", Keywords = "sprite sheet atlas pack slice frames texture json css xml trim padding power of two hoja sprites cortar" },
            new ToolInfo { Id = "texture-pack", Name = "Texture Pack Builder", Title = "Minecraft Texture Pack Builder", Icon = "PackageVariantClosed", CategoryId = "art", Description = "Turn your PNGs into a Minecraft Java resource pack (.zip) with pack.mcmeta", Keywords = "minecraft texture pack resource pack java mcmeta pack.png blocks items skin mod paquete texturas recursos" },

            // Options
        };

        /// <summary>Pinned at the bottom of the sidebar, not in a section.</summary>
        public static readonly ToolInfo Settings = new() { Id = SettingsId, Name = "Settings", Icon = "Cog", CategoryId = "", Description = "Theme, language and preferences" };

        private static readonly Dictionary<string, ToolInfo> ById;

        static ToolCatalogManager()
        {
            var byCat = Categories.ToDictionary(c => c.Id);
            foreach (var t in Tools) t.Category = byCat[t.CategoryId];
            foreach (var c in Categories) c.Tools = Tools.Where(t => t.CategoryId == c.Id).ToList();
            Settings.Category = new ToolCategoryInfo { Id = "options", Title = "Options", Icon = "Cog", AccentHex = "#64748B" };
            ById = Tools.Append(Settings).ToDictionary(t => t.Id, StringComparer.OrdinalIgnoreCase);
        }

        public static ToolInfo? Find(string? id) => id != null && ById.TryGetValue(id, out var t) ? t : null;

        /// <summary>Problems with the catalog (duplicate ids, unknown section, empty section) - empty when fine. Used by the tests.</summary>
        public static List<string> CheckCatalog()
        {
            var problems = new List<string>();
            foreach (var g in Tools.GroupBy(t => t.Id, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1)) problems.Add($"duplicate id {g.Key}");
            foreach (var t in Tools.Where(t => Categories.All(c => c.Id != t.CategoryId))) problems.Add($"{t.Id}: unknown section {t.CategoryId}");
            foreach (var c in Categories.Where(c => c.Tools.Count == 0)) problems.Add($"empty section {c.Id}");
            foreach (var t in Tools.Where(t => string.IsNullOrWhiteSpace(t.Name) || string.IsNullOrWhiteSpace(t.Icon))) problems.Add($"{t.Id}: missing name or icon");
            return problems;
        }

        // =================================================================
        //  Search
        // =================================================================

        /// <summary>
        /// Tools matching every word typed, best first: name starts with it,
        /// a word in the name starts with it, name contains it, then the
        /// section, keywords and description. Accents and case ignored
        /// ("musica" finds Music).
        /// </summary>
        public static List<ToolInfo> Search(string? query)
        {
            var words = Fold(query ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (words.Length == 0) return new List<ToolInfo>();

            var scored = new List<(ToolInfo Tool, int Score, int Order)>();
            for (int i = 0; i < Tools.Count; i++)
            {
                var t = Tools[i];
                int total = 0;
                bool all = true;
                foreach (var w in words)
                {
                    int s = Score(t, w);
                    if (s == 0)
                    {
                        all = false;
                        break;
                    }
                    total += s;
                }
                if (all) scored.Add((t, total, i));
            }
            return scored.OrderByDescending(x => x.Score).ThenBy(x => x.Order).Select(x => x.Tool).ToList();
        }

        private static int Score(ToolInfo t, string w)
        {
            string name = Fold(t.Name), title = Fold(t.HeaderTitle);
            if (name.StartsWith(w, StringComparison.Ordinal)) return 100;
            if (name.Split(' ').Any(p => p.StartsWith(w, StringComparison.Ordinal)) || title.Split(' ').Any(p => p.StartsWith(w, StringComparison.Ordinal))) return 80;
            if (name.Contains(w, StringComparison.Ordinal) || title.Contains(w, StringComparison.Ordinal)) return 60;
            if (Fold(t.CategoryTitle).Split(' ').Any(p => p.StartsWith(w, StringComparison.Ordinal))) return 40;
            if (t.Category != null && Fold(t.Category.Keywords).Split(' ').Any(p => p.StartsWith(w, StringComparison.Ordinal))) return 35;
            // The English names count as keywords, so "pdf toolkit" still finds a tool in Spanish mode.
            if (Fold(t.Keywords + " " + t.EnglishNames).Split(' ').Any(p => p.StartsWith(w, StringComparison.Ordinal))) return 30;
            if (Fold(t.Description).Contains(w, StringComparison.Ordinal)) return 10;
            return 0;
        }

        /// <summary>Lower case, accents removed, punctuation to spaces.</summary>
        public static string Fold(string text)
        {
            var sb = new StringBuilder(text.Length);
            foreach (char c in text.Normalize(NormalizationForm.FormD))
            {
                var cat = CharUnicodeInfo.GetUnicodeCategory(c);
                if (cat == UnicodeCategory.NonSpacingMark) continue;
                sb.Append(char.IsLetterOrDigit(c) ? char.ToLowerInvariant(c) : ' ');
            }
            return sb.ToString().Normalize(NormalizationForm.FormC);
        }

        // =================================================================
        //  Recent + sections
        // =================================================================

        /// <summary>Moves the tool to the front of the list (no duplicates, at most <see cref="MaxRecent"/>, Settings not included).</summary>
        public static void PushRecent(List<string> recent, string id)
        {
            if (Find(id) is not { } tool || tool.Id == SettingsId) return;
            recent.RemoveAll(r => string.Equals(r, tool.Id, StringComparison.OrdinalIgnoreCase));
            recent.Insert(0, tool.Id);
            if (recent.Count > MaxRecent) recent.RemoveRange(MaxRecent, recent.Count - MaxRecent);
        }

        /// <summary>Pins the tool, or unpins it when it already is. Returns true when it is pinned afterwards.</summary>
        public static bool ToggleFavorite(List<string> favorites, string id)
        {
            if (Find(id) is not { } tool || tool.Id == SettingsId) return false;
            if (favorites.RemoveAll(f => string.Equals(f, tool.Id, StringComparison.OrdinalIgnoreCase)) > 0) return false;
            favorites.Add(tool.Id);
            return true;
        }

        public static bool IsFavorite(IEnumerable<string> favorites, string id) =>
            favorites.Any(f => string.Equals(f, id, StringComparison.OrdinalIgnoreCase));

        /// <summary>The pinned tools in pinned order; ids that no longer exist are skipped.</summary>
        public static List<ToolInfo> FavoriteTools(IEnumerable<string> ids) =>
            ids.Select(Find).Where(t => t != null && t.Id != SettingsId).Select(t => t!).Distinct().ToList();

        public static List<ToolInfo> RecentTools(IEnumerable<string> ids) =>
            ids.Select(Find).Where(t => t != null && t.Id != SettingsId).Select(t => t!).Distinct().Take(MaxRecent).ToList();

        // =================================================================
        //  Settings (%AppData%\Smart Tools\shell.json - tool ids only)
        // =================================================================

        private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

        private static string SettingsPath => Path.Combine(AppPaths.DataRoot, "shell.json");

        /// <summary>Unknown ids (a tool renamed or removed) are dropped.</summary>
        public static ShellSettings LoadSettings(string? path = null)
        {
            try
            {
                string file = path ?? SettingsPath;
                if (!File.Exists(file)) return new ShellSettings();
                var s = JsonSerializer.Deserialize<ShellSettings>(File.ReadAllText(file), Json) ?? new ShellSettings();
                s.RecentToolIds = RecentTools(s.RecentToolIds ?? new()).Select(t => t.Id).ToList();
                s.FavoriteToolIds = FavoriteTools(s.FavoriteToolIds ?? new()).Select(t => t.Id).ToList();
                s.ExpandedCategories = (s.ExpandedCategories ?? new()).Where(id => Categories.Any(c => c.Id == id)).Distinct().ToList();
                _baseline[file] = Snapshot(s);
                return s;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
            {
                return new ShellSettings();
            }
        }

        // What each file held when this copy of the app last read or wrote it. Another copy of the app (or a second
        // build) shares the same file; a plain overwrite from here would undo what that copy saved.
        private static readonly Dictionary<string, ShellSettings> _baseline = new(StringComparer.OrdinalIgnoreCase);
        private static readonly object _saveLock = new();

        private static ShellSettings Snapshot(ShellSettings s) => new()
        {
            RecentToolIds = new(s.RecentToolIds),
            FavoriteToolIds = new(s.FavoriteToolIds),
            ExpandedCategories = new(s.ExpandedCategories),
            LastSeenVersion = s.LastSeenVersion,
        };

        /// <summary>
        /// Saves, but only the fields this copy changed since it last read the file; the others keep what is on disk now.
        /// The merged result is also put back into <paramref name="settings"/> so this copy picks up the other's changes.
        /// </summary>
        public static void SaveSettings(ShellSettings settings, string? path = null)
        {
            string file = path ?? SettingsPath;
            lock (_saveLock)
            {
                try
                {
                    if (_baseline.TryGetValue(file, out var mine) && File.Exists(file))
                    {
                        var disk = LoadSettings(file);   // also resets the baseline; set again below
                        if (settings.RecentToolIds.SequenceEqual(mine.RecentToolIds)) settings.RecentToolIds = disk.RecentToolIds;
                        if (settings.FavoriteToolIds.SequenceEqual(mine.FavoriteToolIds)) settings.FavoriteToolIds = disk.FavoriteToolIds;
                        if (settings.ExpandedCategories.SequenceEqual(mine.ExpandedCategories)) settings.ExpandedCategories = disk.ExpandedCategories;
                        if (settings.LastSeenVersion == mine.LastSeenVersion) settings.LastSeenVersion = disk.LastSeenVersion;
                    }
                    AtomicFile.WriteAllText(file, JsonSerializer.Serialize(settings, Json));
                    _baseline[file] = Snapshot(settings);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Non-fatal: the recent list and open sections just aren't remembered this time.
                }
            }
        }
    }
}
