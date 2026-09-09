REIMAGINED LEVEL EDITOR

Windows x64 portable build. No .NET installation is required.
Extract the entire ZIP to a writable folder before running D2RLevel.App.exe.
Keep the Native folder beside the EXE. No game assets or personal settings are included.
Default packages include the 64-bit decoder at Native/granny2.dll for HD model
rendering. Keep it with the application. See Native/NOTICE.txt for provenance.

First run:
1. Choose Asset folder and select your extracted D2R data folder.
2. Choose Load Workspace and select your mod's data directory containing
   hd/env/preset and global/tiles. Both files must exist for a scene to appear.
3. Select a scene. Save Scene writes JSON, DS1 and link metadata back to the mod.
   Existing files receive .bak backups. Use a copy of your mod for testing.

Report the scene name, steps to reproduce, expected versus actual behavior,
a screenshot, and your Windows/GPU information. Include any error-toast text.
Settings and caches are stored per user, not in this ZIP.

Known scope: terrain rendering is approximate. Linked collision uses whole-tile
floor overrides; DT1/wall collision is separate.

Granny runtime redistribution rights have not been established by this project.
See Native/NOTICE.txt before distributing this package. This package is unsigned.
