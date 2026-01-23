# Xiaomi Note Exporter (Enhanced)

Export your notes from Mi Cloud to local Markdown files (optional image download / JSON conversion / export verification).

## Highlights

- **Login once**: Uses a persistent Chrome profile; after the first successful login, subsequent runs reuse the login state.
- **Resume support**: In `--split` mode the exporter continues from an existing `exported_notes_*` directory (no need to start over).
- **More reliable scraping**: Improved loading detection, scroll stability and deduplication.
- **Verification**: `--verify` / `--expected` to check totals and duplicates.
- **Progress visibility**: Console progress bar + `progress.txt` in the export directory.

## Privacy & safety

- Your notes stay on your machine. Do **not** commit export directories (`exported_notes_*`, `images_*`, `manifest*`, `progress.txt`) to GitHub.
- Login state is stored under `%LOCALAPPDATA%\\xiaomiNoteExporter\\chrome_user_data` by default.

## Credits

This project is an evolved fork based on `nogiszd/xiaomi-note-exporter`. Thanks to the original author and contributors.

