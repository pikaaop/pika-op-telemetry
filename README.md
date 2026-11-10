# pika op

The C# desktop version is in `PikaOp\pika_op.exe`. Double-click `pika_op.bat` to launch it. Keep the two signed release-manifest files next to the executable. C# source, build instructions, security details, and email setup are in `csharp\README.md`. The remaining sections describe the preserved Python implementation.

Double-click `run_telemetry.bat`. Use a windowed or fullscreen-windowed local map. The launcher finds `cs2.exe`; it does not require a process ID.

## Controls

The window uses sidebar navigation and scrollable pages. Appearance settings save to `pika_op_settings.json` on exit. Tracking always starts disabled.

- **Overview:** relative world-XY radar, active weapon, player count, and memory-read time.

- **Visuals:** boxes, health, armor, player names, weapon/ammo labels, distance, optional snaplines, and team filtering.
- **Tracking:** left-mouse activation, optional Alt pre-alignment, head/chest approximation, radius, smoothing, calibration, and optional active-weapon presets. Tracking starts disabled and stops when CS2 loses focus.

The Tracking page has an **Allow aiming at teammates** option, disabled by default. It is independent of the Visuals page's teammate ESP setting and saves with preferences. The local player remains excluded.
- **Players:** a live player table that continues updating while the control panel has focus. Export the latest frame as JSON.
- **Practice:** copy supported local-server commands. Copying does not apply commands. The two recoil commands rejected by the installed build were removed.
- **Status:** select a different dump folder and export its schema inventory. The reader checks the running build against `info.json` before using offsets.
- **Recording:** explicitly start or stop a JSON Lines recording at up to 10 Hz. Export the current players as CSV. Existing recording and CSV files are never overwritten; select a new filename.

The Players page supports name/weapon search and sorting by column headers. Visual settings include palette, opacity, label size, stroke width, distance filtering, and an optional center crosshair. F6 pauses telemetry and input; F8 disables tracking. The position radar shows relative world XY coordinates, not map geometry or line of sight. Recordings contain raw snapshots, unaffected by display filters. Background sampling normally runs at up to 4 Hz, increasing to up to 10 Hz while recording. Pausing creates a gap; disconnecting stops the recording.

The default dump is `C:\Users\spide\Downloads\cs2-dumper-main\cs2-dumper-main\output`, build 14192. The checked inventory contains 22 JSON files, 18 schema modules, 3,300 classes, and 17,073 fields. The generated `dump_inventory.json` lists module totals.

## Validation and limits

Live read-only checks confirmed build 14192, a 112-byte entity stride, player positions, names, armor, weapon designer names, ammo, scope state, and mouse calibration. Player counts vary as objects spawn and disappear. These checks did not test firing, mouse tracking accuracy, or hit registration.

Skeleton rendering still requires a verified live bone-buffer layout and model joint map. The animation schema exports describe snapshots and assets but do not provide the complete path from a player to its live world-bone transforms. Skeleton rendering stays hidden without valid data. Head/chest points use collision bounds in the meantime.

No recoil compensation, random-spread prediction, visibility tracing, recoil-state evaluation, or magic bullet is implemented. Mouse tracking cannot guarantee hits. The projection-to-input conversion assumes a rectilinear camera, an orthonormal view, and default `m_yaw`/`m_pitch` of 0.022; use the calibration multiplier for the actual debug setup.

Snapshots are live memory samples, not atomic engine snapshots. Optional data can be unavailable during object destruction. The reader preserves other valid player fields when an optional read fails.

Run verification with `python -m unittest discover -v`.
