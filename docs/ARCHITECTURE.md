# Utility architecture

- **Utility.Core** contains the profiles, draft validation, JSON state store, and leftover and package classification. Simulated providers exercise write sequencing as a separate test layer.
- **Utility.Platform** reads Windows inventory and hardware. On 83JJ / PTCN18WW, it also uses EnergyDrv to apply and read back charge policy and Lenovo WMI to apply and read back ten custom fan speed levels.
- **Utility.ElevatedWorker** accepts typed Lenovo operations. Both ends verify the named pipe peer's process ID. The pipe excludes network clients and uses a one-time token. CPU temperature and fan RPM use a separate read-only session that ends when the app closes.
- **Utility.App** runs as the invoking user. Profile and fan draft saves are serialized within the window. Telemetry takes one sample at a time and pauses when the dashboard is hidden. Graphics requests record the Windows boot session so the app can compare the requested route with the active route after restart.

On 83JJ / PTCN18WW, the power switch changes only the fields that differ between the two original profiles and applies reductions first. It verifies the entire power preset and fan table afterwards. Failure triggers a read-back of only the attempted changes and an original-state recovery attempt. The application keeps the journal open when the outcome cannot be verified. Windows and vendor installation links open their official workflows.

Read access, a recorded draft, and a passing simulated operation provide different evidence. Enable a write only after verifying target identity, field meanings and units, effective state, and recovery. [Named pipe APIs](https://learn.microsoft.com/en-us/windows/win32/ipc/pipe-functions) · [Windows trust API](https://learn.microsoft.com/en-us/windows/win32/api/wintrust/nf-wintrust-winverifytrust)
