#!/usr/bin/env dotnet
// Installs the checksum-pinned Go toolchain shared by the Tailscale builds into the machine-wide cache
// ($TEREDITOR_CACHE_DIR/tailscale-go/go<version>-<platform>) and prints its GOROOT.
// Usage: dotnet run --file scripts/tailscale-go.cs
#:include lib/Tasks.cs
#:include lib/Native.cs
#:include lib/Tailscale.cs
using static Tasks;

return Guard(() =>
{
    Console.WriteLine(TailscaleGo.Provision());
    return 0;
});
