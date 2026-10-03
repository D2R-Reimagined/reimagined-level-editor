# Mod Studio integration

Studio can open a selected level, preset variant, or scene in this editor. The **Level** panel provides **Edit level in Mod Studio**, **Edit preset in Mod Studio**, and individual Vis/Warp cell links. **Mod Studio → Open Mod Studio** returns to the linked project without selecting a record.

**Mod Studio → Companion applications** shows the detected launch application and provides Browse, Save, Test connection, and Reset to automatic. Launch portable applications once to register them. Manual selections override detection. **Link Mod Studio project** associates a standalone workspace with a Studio project; links from Studio establish the association automatically. An unlinked workspace that is not inside a Studio project uses the project Mod Studio last opened (or the only project in its projects folder), and the folder picker appears only when neither exists. Mod Studio is launched if it is not already running.

In a linked session, the selected Studio profile's compiled table snapshot supplies level properties, DS1 tileset context, NPC tables, and other game-data reads. Scene assets stay in the mod project's editable files. The status beneath the toolbar identifies the linked profile and indicates an offline or failed table refresh. Saved Studio table changes refresh properties without discarding scene edits. Reload scene assets when a change affects the rendered scene/tileset.

Connection table editing belongs to Studio in linked mode: use the Vis/Warp links. Standalone native workspaces retain their existing connection editor. Scene saves preserve the existing paired-file/sidecar transaction and external-change checks. Navigation does not deploy a mod or launch the game.

Protocol version 1 supports fresh and already-running applications, exact project/profile navigation, stable Studio source record IDs, capability probing, request acknowledgements, and manual executable selection. One UI instance is used per installation. Project leases prevent another installation from editing that same project; switching projects/scenes prompts for unsaved work normally.

Keep `src/D2RLevel.Core/Companion/EditorIntegration.cs` byte-identical to `src/ModStudio.Core/Companion/EditorIntegration.cs` in Mod Studio. Both repositories compile this UI-independent source locally and can build independently. Registration/configuration live in the user's local application-data directory, outside Velopack's replaceable installation contents.

Level Editor remains Windows-only. Studio's Linux/macOS builds explain this in their companion controls. The shared launcher has AppImage/native executable/macOS bundle support for future ports; it does not invoke Wine or perform remote launches.

Run Studio's `scripts/Test-CompanionIntegration.ps1` with both built executables and extracted game data to verify the real Windows round trip. It uses a disposable project and isolated preferences, edits a copied scene, follows an exact Studio cell link, checks saved table refresh and existing-process reuse, and captures both windows. `--integration-ui-smoke` is an internal smoke mode and restricts scene edits to its supplied fixture directory. No game assets are embedded in the test sources.
