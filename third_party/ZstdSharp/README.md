# ZstdSharp (vendored)

Zstandard decompressor from https://github.com/oleg-st/ZstdSharp (MIT, see LICENSE), included as source
so the app has no NuGet dependencies and builds as a single Native AOT binary.

Local changes:
- `CompressionStream.cs`: dropped a `ReadOnlySpan<byte> != null` check (a span is never null; CA2265).
- `Unsafe/ZstdDecompressBlock.cs`: `Thread.VolatileRead` -> `Volatile.Read` (same effect; SYSLIB0054).
