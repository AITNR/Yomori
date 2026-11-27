# Verify MCP mouse input

Run these checks from the Yomori repository root on Windows with .NET 8 and Python installed. They do not connect to RDP or send real input.

```powershell
dotnet run --project winui3/tests/McpMouseChecks/McpMouseChecks.csproj
python winui3/tests/native_mouse_checks.py <built-runtime-directory>/ycn_rdp.dll
```

Replace the DLL path with the rebuilt runtime. Keep its matching FreeRDP dependencies in the same directory. The second command fails if tracked mouse exports are missing.

The C# executable links the production click sequencer and checks separate move/down/up ordering, waiting for the down receipt before the 120ms hold, movement failure, down failure/exception, cancellation during hold, release failure, and validation failure before down. It uses a fake transport and does not prove delivery to a game.

The Python checks cover invalid flags, null output pointers and disconnected-session errors in the additive Native ABI. They do not validate connected queue dispatch.

For supervised live acceptance, verify the Windows account/session, game process and foreground window before input. Check an observed target, issue one click, release the control lease, and inspect the next image. `transportDispatched` means FreeRDP accepted the event; only the observed application result proves the intended action.

The local controller may remain unfocused while the remote game is foreground. Do not generalize this to a remote minimized/covered game or a locked/disconnected session.
