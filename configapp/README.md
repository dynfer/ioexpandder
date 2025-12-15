# UsbCdcGui

WinForms .NET app that talks to your embedded USB CDC API:

- Send `0xAA` -> reads 21B data frame (`0x11` + 10x u16) and 21B volts frame (`0x22` + 10x u16)
- Send `0xBB` -> reads 4 calibration frames:
  - 25B (`0x33` + 6*(lowCal,highCal))
  - 25B (`0x44` + 6*(lowV,highV))
  - 49B (`0x55` + 4*(r1,r2,r3))
  - 25B (`0x66` + 4*(t1,t2,t3))  // t* are int16
- Send `0xCC` + 124B payload to write calibrations (no ACK; app re-reads to verify)

## Build

Requires Windows + .NET 8 SDK.

```bash
dotnet build -c Release
dotnet run
```

## Notes

- Voltage is shown as mV and converted to V.
- If you ever get out of sync (wrong frame IDs), disconnect/reconnect.
