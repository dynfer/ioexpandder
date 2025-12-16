# UsbCdcConfigApp

WinForms app that talks to your firmware over USB CDC (Serial/COM).

## Build
```powershell
dotnet restore
dotnet build -c Release
dotnet run
```

## Protocol used (binary)
- Host -> device:
  - `0xAA` : request live data (device responds with 0x11 + 0x22 packets)
  - `0xBB` : request config (device responds with 0x33, 0x44, 0x55, 0x66, optionally 0x77)
  - `0xCC` : write config (host sends one 131-byte buffer right after 0xCC)

## Notes
- Displayed value = `raw / factorDivisor`, where factorDivisor is 1/10/100/1000/10000.
- If your current firmware does not send the 0x77 factors packet during read-config, the app will keep factors at X1 until you set them and write the config.
