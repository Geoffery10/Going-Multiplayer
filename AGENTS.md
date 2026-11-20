# Going Multiplayer

Goal: online co-op for Going Under, supporting 4+ players with 2–4 recommended.
Multi-controller split-screen is secondary. Read `local/SESSION.md` when resuming.

Follow `local/coop-anything/AGENTS.md` and its relevant playbooks. Do not modify the
original Steam install or saves. Source, tooling and design live here; game
copies, decompiled code, dumps and machine-specific notes stay in ignored `local/`.

## Permissions already granted

- Project-local .NET SDK, ILSpy and BepInEx setup.
- Inspect/unpack game data and run isolated copies.
- Use the running Steam session and installed Proton for authenticated isolated
  game tests; keep original saves/install untouched.
- Use `/tmp/opencode/going-under-coop` for local test runtime storage.

Do not publish, contact anyone, create GitHub issues/PRs, change account settings
or install other software without asking. Do not bypass Steam authentication.

## Storage and testing

Project storage does not support symlinks. Wine/Proton profiles need the local
temporary workspace, not the NAS. Use the sandbox launchers.

- `python tools/build_plugin.py --deploy-test`: compile our code only.
- `python -m unittest discover -s tests -v`: tooling/scorer tests.
- `python tools/smoke_test.py --suite`: authenticated real-game loader gates.
- `python tools/seat_test.py --windowed --suite`: rendered independent-actor gates.
- `python tools/test_pad.py`: production controller-frame mapping tests.
- `python tools/controller_test.py --suite`: native input-routing and ownership gates.
- `python tools/verify.py --integration`: full serial regression (uses Steam/game windows).

The plugin has a diagnostic default and an opt-in local actor prototype, not
online co-op. Passing a loader gate is not multiplayer QA. Only claim tests that
actually ran. Keep notes current and test negative controls. Capture only the
game framebuffer, not the desktop. Do not deploy over a running test's DLL.
P2 gamepad assignment uses Rewired templates and in-memory exclusive ownership;
do not change system device permissions or Steam Input configuration. No OS
virtual input devices or physical button injection have been authorized.
