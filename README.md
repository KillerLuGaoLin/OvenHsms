# OvenHsms

Standalone .NET Framework 4.8 HSMS/SECS-II client for LabVIEW.

This project exposes `Hsms.Public.OvenClient` without Secs4Net or NuGet runtime dependencies. It was built for LabVIEW .NET calls where package binding is difficult.

## Build

```powershell
dotnet build OvenHsms.csproj
```

## LabVIEW entry points

- `CheckConnection()`
- `GetPpid()`
- `ReadParameters()`
- `ReadParametersData()`
- `ReadParametersStringArray()`
- `ReadParametersStringTable()`
- `ReadParametersText()`
- `StartProgram(string ppid)`

`ReadParametersStringTable()` returns a 9 x 2 string table: field name, value.

## Output

Use:

```text
bin\Debug\net48\OvenHsms.dll
```

Class:

```text
Hsms.Public.OvenClient
```
